using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Core.Events;
using Intrf4ce.Core.Settings;

namespace Intrf4ce.Agent.Hosted;

public enum AgentStatus
{
    Stopped,
    Starting,
    Idle,
    Working,
    Failed,
}

public enum AgentActivityKind
{
    /// <summary>The session started; <see cref="AgentActivity.Text"/> is the model.</summary>
    Started,

    /// <summary>Something the agent said to the user.</summary>
    Said,

    /// <summary>The agent called a tool; <see cref="AgentActivity.Text"/> says which, in words.</summary>
    Tool,

    /// <summary>A turn finished; <see cref="AgentActivity.Cost"/> is the session total so far.</summary>
    TurnDone,

    Error,

    Exited,

    /// <summary>
    /// Claude Code is not signed in, so nothing said reaches the agent until it is.
    /// <see cref="AgentActivity.Text"/> is how Claude Code put it; empty when a check found it
    /// before anything was said.
    /// </summary>
    SignInNeeded,
}

public sealed record AgentActivity(AgentActivityKind Kind, string Text, double? Cost = null);

/// <summary>What the host needs to know to start Claude Code.</summary>
/// <param name="Executable">This program, so the agent can be given <c>intrf4ce --mcp --hosted</c> as its MCP server.</param>
/// <param name="Home">A folder of its own for the agent's working directory and config files.</param>
public sealed record HostedAgentOptions(
    AgentSettings Settings,
    string Executable,
    string Home,
    string SystemPrompt);

/// <summary>
/// Runs Claude Code headless, streaming JSON both ways, as the presenter behind the chat box.
/// </summary>
/// <remarks>
/// One long-lived process: <c>claude -p --input-format stream-json</c> keeps the session and
/// its context between messages and skips the three-second start-up on every chat. The
/// agent reaches the stage through the same MCP server as any attached agent, started as
/// <c>intrf4ce --mcp --hosted</c>, so there is only one set of tools to get right.
/// <para>
/// It is also an <see cref="IEventSink"/>: while the process runs, chat, quiz answers and
/// "ask about this" go to its stdin as user messages, and the attached agents' queue stays
/// empty.
/// </para>
/// </remarks>
public sealed class ClaudeCodeHost : IEventSink, IAsyncDisposable
{
    private readonly HostedAgentOptions _options;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    /// <summary>What was said since the last turn ended: what a turn that could not sign in leaves unanswered.</summary>
    private readonly List<string> _sent = [];

    /// <summary>What was said while signed out, to be asked again once signed in.</summary>
    private readonly List<string> _unanswered = [];
    private Process? _process;
    private StreamWriter? _stdin;
    private int _restarts;
    private int _controls;
    private volatile bool _stopping;

    /// <summary>The turn in flight failed for want of a sign-in: what Claude Code said about it.</summary>
    private string? _signInFailure;

    /// <summary>The turn in flight has had its failure reported already, as Claude Code put it.</summary>
    private bool _failureReported;

    public ClaudeCodeHost(HostedAgentOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
        Account = new ClaudeSignIn(options.Settings, WorkingDirectory, log);
    }

    /// <summary>Claude Code's own sign-in, run the way the agent is, so it signs in the login the agent uses.</summary>
    public ClaudeSignIn Account { get; }

    /// <summary>
    /// Claude Code is not signed in, as far as the last turn or check could tell; a turn that
    /// gets through, or <see cref="SignInAsync"/>, puts it right.
    /// </summary>
    public bool SignedOut { get; private set; }

    /// <summary>
    /// The agent signs in with an API key from the environment, which no sign-in in the browser
    /// replaces: Claude Code prefers a key whenever it has one.
    /// </summary>
    private bool UsesApiKey =>
        _options.Settings.UseApiKey && Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 };

    public event EventHandler<AgentActivity>? Activity;

    public event EventHandler<AgentStatus>? StatusChanged;

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

    public string? SessionId { get; private set; }

    /// <summary>Where the agent runs: the settings' folder, or one of intrf4ce's own.</summary>
    public string WorkingDirectory =>
        _options.Settings.WorkingDirectory is { Length: > 0 } folder ? folder : Path.Combine(_options.Home, "work");

    /// <summary>
    /// What Claude Code keeps of <paramref name="session"/>: its transcript, and the folder
    /// beside it that some versions keep a session's side conversations in — whichever exist.
    /// </summary>
    /// <remarks>
    /// Claude Code files a session under its working folder's path with every character that is
    /// not a letter or digit made a hyphen. Only a session id that is a GUID is looked for, so a
    /// hand-edited deck cannot point this anywhere else.
    /// </remarks>
    public IReadOnlyList<string> TranscriptOf(string? session)
    {
        if (!Guid.TryParse(session, out var id))
        {
            return [];
        }

        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var project = string.Concat(Path.GetFullPath(WorkingDirectory).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        var folder = Path.Combine(config, "projects", project);
        var name = id.ToString("D", CultureInfo.InvariantCulture);

        return [.. new[] { Path.Combine(folder, name + ".jsonl"), Path.Combine(folder, name) }
            .Where(p => File.Exists(p) || Directory.Exists(p))];
    }

    public string? Model { get; private set; }

    public double Cost { get; private set; }

    /// <summary>Why the last start failed, in words for the chat.</summary>
    public string? LastError { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>Starts the agent, resuming <paramref name="resumeSession"/> when given.</summary>
    public void Start(string? resumeSession)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            SetStatus(AgentStatus.Starting);

            var executable = ResolveCommand(_options.Settings.Command);
            if (executable is null)
            {
                LastError = $"Could not find '{_options.Settings.Command}' on the PATH. Install Claude Code, or set agent.command in settings.yaml.";
                SetStatus(AgentStatus.Failed);
                Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, LastError));
                return;
            }

            var start = BuildStart(executable, resumeSession);
            _log($"starting hosted agent: {start.FileName} {string.Join(' ', start.ArgumentList)}");

            try
            {
                var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                process.Exited += (_, _) => OnExited(process);
                process.Start();
                if (OperatingSystem.IsWindows())
                {
                    ChildProcessGuard.Adopt(process);
                }

                _process = process;
                _stdin = process.StandardInput;
                _stdin.AutoFlush = true;

                _ = Task.Run(() => ReadOutputAsync(process));
                _ = Task.Run(() => ReadErrorsAsync(process));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                LastError = $"Could not start {executable}: {ex.Message}";
                SetStatus(AgentStatus.Failed);
                Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, LastError));
            }
        }
    }

    /// <summary>
    /// Continues <paramref name="session"/> instead of the one running: the deck the user just
    /// opened was built in that conversation.
    /// </summary>
    public async Task SwitchToAsync(string? session)
    {
        if (session is null || session == SessionId)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        ForgetUnanswered();
        Start(session);
    }

    /// <summary>
    /// Whether there is a turn in flight to stop. Not while the process is merely starting:
    /// nothing has been asked yet, and a stop button with nothing to stop is noise.
    /// </summary>
    public bool IsWorking => Status is AgentStatus.Working && IsRunning;

    /// <summary>
    /// Stops the turn in flight, leaving the conversation intact.
    /// </summary>
    /// <remarks>
    /// Claude Code takes an interrupt as a control request on the same stream the messages go
    /// down, which is how its own SDK does it: the turn ends, the session keeps everything
    /// said so far, and the next message carries on from there. A process that does not answer
    /// within a few seconds is restarted onto the same session instead, because a stop button
    /// that leaves the agent talking is worse than no stop button.
    /// </remarks>
    public async Task InterruptAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _stopping = true;
        var request = new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = $"intrf4ce-{Interlocked.Increment(ref _controls)}",
            ["request"] = new JsonObject { ["subtype"] = "interrupt" },
        };

        if (!TryWrite(request))
        {
            return;
        }

        _log("asked the hosted agent to stop");

        await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        if (Status is AgentStatus.Working)
        {
            _log("the hosted agent did not stop; restarting it on the same session");
            var session = SessionId;
            await StopAsync().ConfigureAwait(false);
            Start(session);
        }
    }

    /// <summary>Sends a user message, starting the agent first if it is not running.</summary>
    public void Send(string text)
    {
        if (!IsRunning)
        {
            Start(SessionId);
        }

        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };

        _stopping = false;
        if (TryWrite(message))
        {
            lock (_gate)
            {
                _sent.Add(text);
            }

            SetStatus(AgentStatus.Working);
        }
    }

    /// <summary>
    /// Asks Claude Code whether it is signed in, so that an agent nobody can reach is reported
    /// before anyone has asked it anything.
    /// </summary>
    public async Task CheckSignInAsync()
    {
        if (await Account.StatusAsync().ConfigureAwait(false) is { SignedIn: false } && !SignedOut)
        {
            SignedOut = true;
            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.SignInNeeded, ""));
        }
    }

    /// <summary>
    /// Signs Claude Code in, in the browser, and carries on: a fresh agent, which reads the new
    /// login, and whatever was said while signed out asked again.
    /// </summary>
    /// <param name="page">Told the sign-in page's address, for when no browser came up.</param>
    public async Task<SignInOutcome> SignInAsync(Action<string> page, CancellationToken cancellation = default)
    {
        var outcome = await Account.SignInAsync(page, cancellation).ConfigureAwait(false);
        if (!outcome.SignedIn)
        {
            return outcome;
        }

        SignedOut = false;
        string[] unanswered;
        lock (_gate)
        {
            unanswered = [.. _unanswered];
            _unanswered.Clear();
        }

        // The process running now started with the old login, or with none.
        var session = SessionId;
        await StopAsync().ConfigureAwait(false);
        if (unanswered.Length == 0)
        {
            Start(session);
            return outcome;
        }

        _log($"signed in; asking again what {unanswered.Length} message(s) left unanswered");
        Send(string.Join("\n\n", unanswered));
        return outcome with { AskedAgain = true };
    }

    /// <summary>One line of JSON down the agent's stdin; false when it has stopped listening.</summary>
    private bool TryWrite(JsonObject message)
    {
        StreamWriter? stdin;
        lock (_gate)
        {
            stdin = _stdin;
        }

        if (stdin is null)
        {
            return false;
        }

        try
        {
            lock (_gate)
            {
                stdin.WriteLine(message.ToJsonString());
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LastError = "The agent stopped accepting messages: " + ex.Message;
            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, LastError));
            return false;
        }
    }

    public bool TryDeliver(UserEvent userEvent)
    {
        if (!string.Equals(_options.Settings.Mode, "hosted", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Send(userEvent.ToMessage());
        return true;
    }

    /// <summary>Stops the agent. Ending stdin lets it finish cleanly; after a grace period it is killed.</summary>
    public async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            try
            {
                _stdin?.Close();
            }
            catch (IOException)
            {
            }

            _stdin = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
        finally
        {
            process.Dispose();
            SetStatus(AgentStatus.Stopped);
        }
    }

    /// <summary>Forgets the conversation, for a new deck: the next message starts a fresh session.</summary>
    public async Task ResetAsync(string? resumeSession)
    {
        await StopAsync().ConfigureAwait(false);
        ForgetUnanswered();
        SessionId = resumeSession;
        Cost = 0;
    }

    /// <summary>
    /// What a signed-out agent was asked belongs to the conversation it was asked in; asked again
    /// in another, it would be an answer to nobody.
    /// </summary>
    private void ForgetUnanswered()
    {
        lock (_gate)
        {
            _sent.Clear();
            _unanswered.Clear();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Claude Code started for anything intrf4ce runs it for — the agent, its sign-in, the
    /// question whether it is signed in: no window, UTF-8 both ways, and an API key left in the
    /// environment hidden unless the settings ask for it. The caller adds the arguments.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string executable, AgentSettings settings, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        // An API key left in the environment for something else would bill the presentation to
        // API credit instead of the account the agent is signed in with, and fail outright when
        // that credit has run out. The user asks for it in settings or it does not happen.
        if (!settings.UseApiKey)
        {
            start.Environment.Remove("ANTHROPIC_API_KEY");
        }

        // A .cmd shim from npm cannot be started directly without a shell.
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(executable);
        }

        return start;
    }

    private ProcessStartInfo BuildStart(string executable, string? resumeSession)
    {
        var settings = _options.Settings;

        // The first time, nothing is there yet: not even the folder these two files go in.
        Directory.CreateDirectory(_options.Home);
        var mcpConfig = Path.Combine(_options.Home, "mcp.json");
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["intrf4ce"] = new JsonObject
                {
                    ["command"] = _options.Executable,
                    ["args"] = new JsonArray("--mcp", "--hosted"),
                },
            },
        };
        File.WriteAllText(mcpConfig, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var prompt = Path.Combine(_options.Home, "presenter.md");
        File.WriteAllText(prompt, _options.SystemPrompt);

        var start = StartInfo(executable, settings, WorkingDirectory);

        // Load intrf4ce's tools up front. Claude Code otherwise defers MCP tool schemas and the
        // first thing every session does is a tool search, a round trip before any slide
        // appears; with one small server there is nothing to save by deferring.
        start.Environment["ENABLE_TOOL_SEARCH"] = "false";

        foreach (var argument in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose" })
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--mcp-config");
        start.ArgumentList.Add(mcpConfig);
        if (settings.StrictMcp)
        {
            start.ArgumentList.Add("--strict-mcp-config");
        }

        start.ArgumentList.Add("--append-system-prompt-file");
        start.ArgumentList.Add(prompt);

        if (settings.PermissionMode.Length > 0)
        {
            start.ArgumentList.Add("--permission-mode");
            start.ArgumentList.Add(settings.PermissionMode);
        }

        if (settings.AllowedTools.Count > 0)
        {
            start.ArgumentList.Add("--allowedTools");
            start.ArgumentList.Add(string.Join(",", settings.AllowedTools));
        }

        if (settings.Model.Length > 0)
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(settings.Model);
        }

        if (resumeSession is { Length: > 0 })
        {
            start.ArgumentList.Add("--resume");
            start.ArgumentList.Add(resumeSession);
        }

        foreach (var extra in settings.ExtraArgs)
        {
            start.ArgumentList.Add(extra);
        }

        return start;
    }

    private async Task ReadOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    _log("hosted agent (not json): " + line);
                    continue;
                }

                if (message is not null)
                {
                    Handle(message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away; Exited reports it.
        }
    }

    /// <summary>One message from the agent's stdout.</summary>
    internal void Handle(JsonObject message)
    {
        var type = message["type"]?.GetValue<string>();
        var subtype = message["subtype"]?.GetValue<string>();

        switch (type)
        {
            case "system" when subtype == "init":
                SessionId = message["session_id"]?.GetValue<string>() ?? SessionId;
                var model = message["model"]?.GetValue<string>();
                if (model != Model)
                {
                    Model = model;
                    Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Started, model ?? "claude"));
                }

                ReportMcpFailure(message);
                break;

            case "assistant" when (message["message"]?["content"] is JsonArray content):
                // Only the top-level conversation; a subagent's chatter is not addressed to the user.
                if (message["parent_tool_use_id"] is not null && message["parent_tool_use_id"]!.GetValueKind() != JsonValueKind.Null)
                {
                    break;
                }

                // Claude Code's own word that a request failed, dressed as a reply no model
                // wrote: an error, not the agent talking, and not a line of the conversation.
                if (message["is_api_error_message"]?.GetValueKind() is JsonValueKind.True)
                {
                    OnApiError(message, content);
                    break;
                }

                foreach (var block in content.OfType<JsonObject>())
                {
                    switch (block["type"]?.GetValue<string>())
                    {
                        case "text" when block["text"]?.GetValue<string>() is { Length: > 0 } text:
                            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Said, text.Trim()));
                            break;

                        case "tool_use":
                            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Tool, Describe(block)));
                            break;
                    }
                }

                break;

            case "control_request":
                OnControlRequest(message);
                break;

            case "control_response":
                break;

            case "result":
                Cost = message["total_cost_usd"]?.GetValue<double>() ?? Cost;
                SessionId = message["session_id"]?.GetValue<string>() ?? SessionId;
                SetStatus(AgentStatus.Idle);

                // A turn the user stopped ends as an error as far as the CLI is concerned
                // ("error_during_execution"); saying so in red would be calling the button a
                // failure.
                var stopped = _stopping;
                _stopping = false;

                string? signIn;
                bool reported;
                lock (_gate)
                {
                    (signIn, reported) = (_signInFailure, _failureReported);
                    (_signInFailure, _failureReported) = (null, false);

                    // A turn that got through means it is signed in, and whatever went
                    // unanswered before has been overtaken by what was asked since.
                    if (signIn is null)
                    {
                        _unanswered.Clear();
                    }
                    else
                    {
                        _unanswered.AddRange(_sent);
                    }

                    _sent.Clear();
                }

                SignedOut = signIn is not null;
                if (signIn is not null)
                {
                    // A process that failed to sign in may hold on to that; the next message
                    // starts a fresh one, which reads whatever login there is by then.
                    _ = StopAsync();
                    Activity?.Invoke(this, new AgentActivity(AgentActivityKind.SignInNeeded, signIn));
                }
                else if (!stopped && !reported && (message["is_error"]?.GetValue<bool>() == true || subtype is not "success"))
                {
                    var error = message["result"]?.GetValue<string>() ?? subtype ?? "the turn failed";
                    Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, error));
                }

                Activity?.Invoke(this, new AgentActivity(AgentActivityKind.TurnDone, "", Cost));
                break;
        }
    }

    /// <summary>
    /// A request Claude Code could not make. Signed out, it waits for the end of the turn,
    /// which is when what was asked can be put aside to ask again; anything else is said now.
    /// </summary>
    private void OnApiError(JsonObject message, JsonArray content)
    {
        var said = string.Join("\n", content.OfType<JsonObject>().Select(b => b["text"]?.GetValue<string>()).OfType<string>()).Trim();
        var kind = message["error"] is JsonValue error && error.TryGetValue<string>(out var named) ? named : null;

        if (kind == "authentication_failed" && !UsesApiKey)
        {
            _signInFailure = said;
            return;
        }

        if (kind == "authentication_failed")
        {
            said += " The agent signs in with ANTHROPIC_API_KEY because agent.use_api_key is on: fix that key,"
                + " or turn the setting off to use your Claude account instead.";
        }

        // The turn's result says the same again; once is enough.
        _failureReported = true;
        Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, said.Length > 0 ? said : kind ?? "the request failed"));
    }

    /// <summary>
    /// Something the agent asks intrf4ce for mid-turn. Today that is permission to use a tool.
    /// </summary>
    /// <remarks>
    /// intrf4ce answers rather than leaving the turn hanging on a prompt nobody can see: the
    /// user chose the permission mode when they set the app up, the tool call itself is
    /// reported in the chat as it happens, and the stop button is there for an answer going the
    /// wrong way. Anything else is refused in words, because a control request left unanswered
    /// stops the agent for ever.
    /// </remarks>
    private void OnControlRequest(JsonObject message)
    {
        var request = message["request"];
        var subtype = request?["subtype"]?.GetValue<string>() ?? "something";

        if (subtype == "can_use_tool")
        {
            _log("allowing " + (request?["tool_name"]?.GetValue<string>() ?? "a tool"));
            Answer(message["request_id"], new JsonObject
            {
                ["behavior"] = "allow",
                ["updatedInput"] = request?["input"]?.DeepClone() ?? new JsonObject(),
            });

            return;
        }

        _log($"hosted agent asked intrf4ce for {subtype}, which it cannot do");
        Refuse(message["request_id"], $"intrf4ce does not handle '{subtype}'");
    }

    private void Answer(JsonNode? requestId, JsonObject response) =>
        TryWrite(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId?.DeepClone(),
                ["response"] = response,
            },
        });

    private void Refuse(JsonNode? requestId, string why) =>
        TryWrite(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "error",
                ["request_id"] = requestId?.DeepClone(),
                ["error"] = why,
            },
        });

    private void ReportMcpFailure(JsonObject init)
    {
        if (init["mcp_servers"] is not JsonArray servers)
        {
            return;
        }

        foreach (var server in servers.OfType<JsonObject>())
        {
            if (server["name"]?.GetValue<string>() == "intrf4ce" && server["status"]?.GetValue<string>() is { } status && status is not ("connected" or "pending"))
            {
                Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error,
                    $"The agent could not connect to intrf4ce's tools (status: {status})."));
            }
        }
    }

    /// <summary>A tool call as a status line: "Searching the web for knee anatomy".</summary>
    internal static string Describe(JsonObject toolUse)
    {
        var name = toolUse["name"]?.GetValue<string>() ?? "a tool";
        var input = toolUse["input"] as JsonObject;
        string? Arg(string key) => input?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        var shortName = name.StartsWith("mcp__intrf4ce__", StringComparison.Ordinal) ? name["mcp__intrf4ce__".Length..] : name;

        return shortName switch
        {
            "WebSearch" => $"Searching the web for {Arg("query") ?? "…"}",
            "WebFetch" => $"Reading {Host(Arg("url"))}",
            "Read" => $"Reading {Path.GetFileName(Arg("file_path") ?? "a file")}",
            "Glob" or "Grep" => "Looking through files",
            "Bash" => "Running " + Short(Arg("command") ?? "a command"),
            "Task" => "Asking a helper to " + Short(Arg("description") ?? "look into something"),
            "TodoWrite" => "Planning",
            "put_slide" => $"Building slide “{input?["slide"]?["title"]?.GetValue<string>() ?? input?["slide"]?["id"]?.GetValue<string>() ?? "…"}”",
            "update_slide" => $"Updating slide {Arg("id")}",
            "new_deck" => $"Starting “{Arg("title")}”",
            "inspect_image" => "Studying an image",
            "scan_media" => "Looking through photos",
            "snapshot" => "Checking the stage",
            "wait_for_user" => "Listening",
            "set_playback" => "",
            "describe_deck" => "Looking at another session",
            "group_decks" or "rename_group" => "Filing sessions",
            "get_settings" or "set_setting" => "Checking the settings",
            "reply" or "say" or "get_state" or "go_to" or "focus" => "",
            _ => shortName.Replace('_', ' '),
        };
    }

    /// <summary>A command line short enough for a status line.</summary>
    private static string Short(string text) =>
        text.Length <= 48 ? text : text[..47].TrimEnd() + "…";

    private static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "a page";

    private async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                {
                    _log("hosted agent stderr: " + line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void OnExited(Process process)
    {
        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = ReferenceEquals(_process, process);
            if (wasCurrent)
            {
                _process = null;
                _stdin = null;
            }
        }

        if (!wasCurrent)
        {
            return;
        }

        _log($"hosted agent exited with {code.ToString(CultureInfo.InvariantCulture)}");
        Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Exited, $"exit code {code}"));

        if (code != 0 && _restarts < 3)
        {
            // A crash mid-conversation is resumed rather than lost, a few times at most.
            _restarts++;
            SetStatus(AgentStatus.Failed);
            Start(SessionId);
        }
        else
        {
            SetStatus(code == 0 ? AgentStatus.Stopped : AgentStatus.Failed);
        }
    }

    private void SetStatus(AgentStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Finds a command on the PATH the way a shell would, including Windows' extensions.</summary>
    internal static string? ResolveCommand(string command)
    {
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Claude Code's native installer puts it here, and GUI apps do not always inherit a login PATH.
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));

        foreach (var directory in directories)
        {
            foreach (var extension in extensions.Prepend(""))
            {
                var candidate = Path.Combine(directory.Trim('"'), command + extension);
                if (File.Exists(candidate) && (extension.Length > 0 || !OperatingSystem.IsWindows()))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
