using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Intrf4ce.Core;
using Intrf4ce.Core.Events;

namespace Intrf4ce.Agent.Hosted;

/// <summary>
/// Runs OpenAI's Codex as the presenter behind the chat box: one <c>codex app-server</c>,
/// spoken to in JSON-RPC over stdio, holding one thread per presentation.
/// </summary>
/// <remarks>
/// The app server is what Codex's own editor extensions talk to: a long-lived process that
/// keeps threads, streams what happens in a turn, takes an interrupt and signs in — the way
/// intrf4ce already runs Claude Code. Everything intrf4ce sets goes with the thread as JSON,
/// rather than on a command line a .cmd shim would have to carry intact: intrf4ce as an MCP
/// server, the presenter's instructions, the sandbox, and, with strict_mcp, the user's own MCP
/// servers, plugins and apps turned off for this thread alone.
/// <para>
/// A thread is made, or resumed, with the first message, not at start-up: a conversation
/// nobody spoke in is not worth keeping.
/// </para>
/// </remarks>
public sealed partial class CodexHost : IHostedAgent
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly HostedAgentOptions _options;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _calls = new();

    /// <summary>What was said since the last turn ended: what a turn that could not sign in leaves unanswered.</summary>
    private readonly List<string> _sent = [];

    /// <summary>What was said while signed out, to be asked again once signed in.</summary>
    private readonly List<string> _unanswered = [];
    private Process? _process;
    private StreamWriter? _stdin;

    /// <summary>Messages waiting their turn: one at a time, each after the one before has ended.</summary>
    private Channel<string>? _outbox;

    /// <summary>The app server answered <c>initialize</c>; false when it never will.</summary>
    private Task<bool>? _ready;

    /// <summary>Done whenever no turn is in flight: set by the pump, finished by whatever ends the turn.</summary>
    private volatile TaskCompletionSource _idle = Done();

    /// <summary>The thread to come back to with the first message, rather than starting one.</summary>
    private string? _resume;
    private bool _inThread;
    private string? _turnId;
    private long _nextId;
    private int _restarts;
    private volatile bool _stopping;

    /// <summary>The turn in flight failed for want of a sign-in: what Codex said about it.</summary>
    private string? _signInFailure;

    /// <summary>Why the turn in flight failed, if Codex said so before it ended.</summary>
    private string? _failure;

    /// <summary>The sign-in waiting on the browser, and what ends it: null when it worked, or why not.</summary>
    private (string Id, TaskCompletionSource<string?> Done)? _login;

    public CodexHost(HostedAgentOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
    }

    public event EventHandler<AgentActivity>? Activity;

    public event EventHandler<AgentStatus>? StatusChanged;

    public AgentIdentity Identity { get; } = new("Codex", "ChatGPT", CodeToPaste: false);

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

    public string? SessionId { get; private set; }

    public string? LastError { get; private set; }

    public string? Model { get; private set; }

    public bool SignedOut { get; private set; }

    /// <summary>Where the agent runs: the settings' folder, or one of intrf4ce's own.</summary>
    public string WorkingDirectory =>
        _options.Settings.WorkingDirectory is { Length: > 0 } folder ? folder : Path.Combine(_options.Home, "work");

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

    public bool IsWorking => Status is AgentStatus.Working && IsRunning;

    public void Start(string? resumeSession)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            SetStatus(AgentStatus.Starting);
            var command = _options.Settings.CommandToRun;
            if (ClaudeCodeHost.ResolveCommand(command) is not { } executable)
            {
                LastError = $"Could not find '{command}' on the PATH. Install Codex (npm install -g @openai/codex), or set agent.command in settings.yaml.";
                SetStatus(AgentStatus.Failed);
                Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, LastError));
                return;
            }

            var start = ClaudeCodeHost.StartInfo(executable, _options.Settings, WorkingDirectory);
            start.ArgumentList.Add("app-server");
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
                _resume = resumeSession;
                SessionId = resumeSession;
                _inThread = false;
                _turnId = null;
                _idle = Done();
                _outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

                _ = Task.Run(() => ReadOutputAsync(process));
                _ = Task.Run(() => ReadErrorsAsync(process));
                _ready = InitializeAsync();
                _ = Task.Run(() => PumpAsync(_outbox.Reader, _ready));
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                LastError = $"Could not start {executable}: {ex.Message}";
                SetStatus(AgentStatus.Failed);
                Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, LastError));
            }
        }
    }

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
    /// Stops the turn in flight, leaving the thread intact. An app server that does not end the
    /// turn within a few seconds is restarted on the same thread instead, because a stop button
    /// that leaves the agent talking is worse than none.
    /// </summary>
    public async Task InterruptAsync()
    {
        if (!IsRunning || SessionId is not { } thread || _turnId is not { } turn)
        {
            return;
        }

        _stopping = true;
        try
        {
            await CallAsync("turn/interrupt", new JsonObject { ["threadId"] = thread, ["turnId"] = turn }).ConfigureAwait(false);
            _log("asked Codex to stop");
        }
        catch (CodexException ex)
        {
            _log("Codex would not stop: " + ex.Message);
        }

        await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        if (Status is AgentStatus.Working)
        {
            _log("Codex did not stop; restarting it on the same thread");
            var session = SessionId;
            await StopAsync().ConfigureAwait(false);
            Start(session);
        }
    }

    public void Send(string text)
    {
        if (!IsRunning)
        {
            Start(SessionId);
        }

        Channel<string>? outbox;
        lock (_gate)
        {
            outbox = _outbox;
        }

        if (outbox is null || !outbox.Writer.TryWrite(text))
        {
            return;
        }

        _stopping = false;
        lock (_gate)
        {
            _sent.Add(text);
        }

        SetStatus(AgentStatus.Working);
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

    public async Task CheckSignInAsync()
    {
        if (await AccountAsync().ConfigureAwait(false) is { SignedIn: false } && !SignedOut)
        {
            SignedOut = true;
            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.SignInNeeded, ""));
        }
    }

    /// <summary>
    /// Signs Codex in to a ChatGPT account: the app server gives the page, intrf4ce opens it, and
    /// Codex waits on a port of its own for the browser to come back. Then whatever was said
    /// while signed out is asked again.
    /// </summary>
    public async Task<SignInOutcome> SignInAsync(Action<string> page, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!IsRunning)
        {
            Start(SessionId);
        }

        Task<bool>? ready;
        lock (_gate)
        {
            ready = _ready;
        }

        if (ready is null || !await ready.ConfigureAwait(false))
        {
            return new SignInOutcome(false, LastError ?? "Codex did not start.");
        }

        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string id;
        try
        {
            var started = await CallAsync("account/login/start", new JsonObject { ["type"] = "chatgpt" }, cancellation).ConfigureAwait(false);
            id = Text(started?["loginId"]) ?? throw new CodexException("Codex gave no sign-in to wait for");
            var address = Text(started?["authUrl"]) ?? throw new CodexException("Codex gave no page to sign in on");
            lock (_gate)
            {
                _login = (id, done);
            }

            _log("signing Codex in");
            OpenInBrowser(address);
            page(address);
        }
        catch (CodexException ex)
        {
            return new SignInOutcome(false, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new SignInOutcome(false);
        }

        string? error;
        try
        {
            error = await done.Task.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await CallAsync("account/login/cancel", new JsonObject { ["loginId"] = id }).ConfigureAwait(false);
            }
            catch (CodexException)
            {
            }

            _log("sign-in given up");
            return new SignInOutcome(false);
        }
        finally
        {
            lock (_gate)
            {
                _login = null;
            }
        }

        if (error is not null)
        {
            _log("Codex sign-in failed: " + error);
            return new SignInOutcome(false, error);
        }

        _log("Codex is signed in");
        SignedOut = false;
        var account = await AccountAsync().ConfigureAwait(false);
        string[] unanswered;
        lock (_gate)
        {
            unanswered = [.. _unanswered];
            _unanswered.Clear();
        }

        var outcome = new SignInOutcome(true, Account: account?.Account);
        if (unanswered.Length == 0)
        {
            return outcome;
        }

        _log($"signed in; asking again what {unanswered.Length} message(s) left unanswered");
        Send(string.Join("\n\n", unanswered));
        return outcome with { AskedAgain = true };
    }

    /// <summary>Codex's sign-in finishes in the browser; there is never a code to paste.</summary>
    public bool SubmitSignInCode(string code) => false;

    public async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _outbox?.Writer.TryComplete();
            _outbox = null;
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
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
        }
        finally
        {
            process.Dispose();
            Abandon("Codex stopped");
            SetStatus(AgentStatus.Stopped);
        }
    }

    public async Task ResetAsync(string? resumeSession)
    {
        await StopAsync().ConfigureAwait(false);
        ForgetUnanswered();
        SessionId = resumeSession;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// The rollout Codex keeps of <paramref name="session"/>, in its sessions folder or among
    /// the archived ones. Only a thread id that is a GUID is looked for, so a hand-edited deck
    /// cannot point this anywhere else.
    /// </summary>
    public IReadOnlyList<string> TranscriptOf(string? session)
    {
        if (!Guid.TryParse(session, out var id))
        {
            return [];
        }

        var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var name = id.ToString("D", CultureInfo.InvariantCulture);
        var found = new List<string>();
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(home, folder);
            try
            {
                if (Directory.Exists(root))
                {
                    found.AddRange(Directory.EnumerateFiles(root, $"rollout-*{name}.jsonl", SearchOption.AllDirectories));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"could not look through {root}: {ex.Message}");
            }
        }

        return found;
    }

    /// <summary>
    /// What Codex does in the sandbox it is given: write in its own folder, unless the settings
    /// ask for a presenter that only reads, or for none at all.
    /// </summary>
    internal static string Sandbox(string permissionMode) => permissionMode switch
    {
        "plan" => "read-only",
        "bypassPermissions" => "danger-full-access",
        _ => "workspace-write",
    };

    /// <summary>Whether an error Codex reported is its way of saying it is not signed in.</summary>
    internal static bool IsUnauthorized(JsonNode? error)
    {
        var info = error?["codexErrorInfo"];
        if (Text(info) == "unauthorized")
        {
            return true;
        }

        // The other kinds carry the HTTP status of whatever refused: { "kind": { "httpStatusCode": 401 } }.
        return info is JsonObject kinds
            && kinds.Select(k => k.Value is JsonObject kind ? kind["httpStatusCode"] : null)
                .OfType<JsonValue>()
                .Any(v => v.TryGetValue<int>(out var code) && code == 401);
    }

    /// <summary>
    /// A failed turn's reason as a sentence. Codex passes some refusals on as the JSON body they
    /// came in, and the sentence is inside it.
    /// </summary>
    internal static string Readable(string failure)
    {
        var text = failure.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject body
                    && Text(body["error"] is JsonObject error ? error["message"] : body["message"]) is { Length: > 0 } message)
                {
                    text = message;
                }
            }
            catch (JsonException)
            {
            }
        }

        // The model in Codex's own settings belongs to a newer Codex than the one on the PATH.
        if (text.Contains("requires a newer version of Codex", StringComparison.OrdinalIgnoreCase))
        {
            text += " Or give intrf4ce a model this Codex has, with agent.model in settings.yaml.";
        }

        return text;
    }

    /// <summary>An error in a sentence: its details, without the address and request ids after them.</summary>
    internal static string Words(JsonNode? error)
    {
        var details = Text(error?["additionalDetails"]) ?? Text(error?["message"]) ?? "the request failed";
        var cut = details.IndexOf(", url:", StringComparison.Ordinal);
        return (cut > 0 ? details[..cut] : details).Trim();
    }

    /// <summary>One message from the app server: an answer, something that happened, or a question for intrf4ce.</summary>
    internal void Handle(JsonObject message)
    {
        var method = Text(message["method"]);
        var parameters = message["params"] as JsonObject;

        if (method is null)
        {
            OnReply(message);
            return;
        }

        if (message["id"] is { } request)
        {
            OnRequest(request, method, parameters);
            return;
        }

        var item = parameters?["item"] as JsonObject;
        switch (method)
        {
            case "turn/started":
                _turnId = Text(parameters?["turn"]?["id"]) ?? _turnId;
                break;

            case "item/started" when item is not null:
                if (Describe(item) is { Length: > 0 } doing)
                {
                    Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Tool, doing));
                }

                break;

            case "item/completed" when item is not null && Text(item["type"]) == "agentMessage":
                if (Text(item["text"])?.Trim() is { Length: > 0 } said)
                {
                    Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Said, said));
                }

                break;

            case "error":
                OnError(parameters);
                break;

            case "turn/completed":
                OnTurnCompleted(parameters?["turn"]);
                break;

            case "account/login/completed":
                OnLoginCompleted(parameters);
                break;

            case "mcpServer/startupStatus/updated"
                when Text(parameters?["name"]) == "intrf4ce" && Text(parameters?["status"]) == "failed":
                Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error,
                    $"The agent could not connect to intrf4ce's tools: {Text(parameters?["error"]) ?? "it did not start"}."));
                break;
        }
    }

    /// <summary>A step of a turn as a status line, or empty for one not worth a word.</summary>
    internal static string Describe(JsonObject item)
    {
        string? Field(string key) => Text(item[key]) is { Length: > 0 } text ? text : null;

        return Field("type") switch
        {
            "mcpToolCall" when Field("server") == "intrf4ce" => ToolStatus.Describe(Field("tool") ?? "a tool", item["arguments"] as JsonObject),
            "mcpToolCall" => $"Using {Field("server")}: {Field("tool")?.Replace('_', ' ')}",
            "commandExecution" => "Running " + ToolStatus.Short(Field("command") ?? "a command"),
            "webSearch" when (Field("query") ?? Text(item["action"]?["query"])) is { Length: > 0 } query => $"Searching the web for {query}",
            "webSearch" => "Searching the web",
            "fileChange" => "Writing files",
            "imageView" => "Looking at a picture",
            _ => "",
        };
    }

    private void OnReply(JsonObject message)
    {
        if (message["id"] is not JsonValue id || !id.TryGetValue<long>(out var number) || !_calls.TryRemove(number, out var reply))
        {
            return;
        }

        if (message["error"] is JsonObject error)
        {
            reply.TrySetException(new CodexException(Text(error["message"]) ?? "Codex refused"));
        }
        else
        {
            reply.TrySetResult(message["result"]?.DeepClone());
        }
    }

    /// <summary>
    /// Something Codex asks of intrf4ce mid-turn. With approvals set to never it asks little;
    /// what it does ask is answered at once, because a request left unanswered stops the turn
    /// for ever — approvals allowed, as for Claude Code, and anything else refused in words.
    /// </summary>
    private void OnRequest(JsonNode id, string method, JsonObject? parameters)
    {
        JsonObject? answer = method switch
        {
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" => new JsonObject { ["decision"] = "accept" },
            "execCommandApproval" or "applyPatchApproval" => new JsonObject { ["decision"] = "approved" },
            "mcpServer/elicitation/request" when Text(parameters?["mode"]) == "form" =>
                new JsonObject { ["action"] = "accept", ["content"] = new JsonObject(), ["_meta"] = null },
            "mcpServer/elicitation/request" => new JsonObject { ["action"] = "decline", ["content"] = null, ["_meta"] = null },
            _ => null,
        };

        if (answer is not null)
        {
            _log($"allowing what Codex asked ({method})");
            TryWrite(new JsonObject { ["id"] = id.DeepClone(), ["result"] = answer });
            return;
        }

        _log($"Codex asked intrf4ce for {method}, which it cannot do");
        TryWrite(new JsonObject
        {
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"intrf4ce does not handle '{method}'" },
        });
    }

    /// <summary>
    /// Codex reports a refused request as it retries it, for several seconds. Refused for want
    /// of a login, there is nothing to wait for: the turn is stopped at the first refusal, and
    /// ends as one that could not sign in.
    /// </summary>
    private void OnError(JsonObject? parameters)
    {
        var error = parameters?["error"];
        if (IsUnauthorized(error))
        {
            if (_signInFailure is null)
            {
                _signInFailure = Words(error);
                if (SessionId is { } thread && (Text(parameters?["turnId"]) ?? _turnId) is { } turn)
                {
                    _ = CallQuietlyAsync("turn/interrupt", new JsonObject { ["threadId"] = thread, ["turnId"] = turn });
                }
            }

            return;
        }

        if (parameters?["willRetry"]?.GetValueKind() is JsonValueKind.True)
        {
            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Tool, "Reconnecting"));
            return;
        }

        _failure = Text(error?["message"]);
    }

    private void OnTurnCompleted(JsonNode? turn)
    {
        var status = Text(turn?["status"]);
        SetStatus(AgentStatus.Idle);

        // A turn the user stopped is not a failure, whatever Codex calls it.
        var stopped = _stopping;
        _stopping = false;

        var signIn = _signInFailure ?? (status == "failed" && IsUnauthorized(turn?["error"]) ? Words(turn?["error"]) : null);
        var failure = Text(turn?["error"]?["message"]) ?? _failure;
        (_signInFailure, _failure, _turnId) = (null, null, null);

        lock (_gate)
        {
            // A turn that got through means it is signed in, and whatever went unanswered
            // before has been overtaken by what was asked since.
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
            // A process that could not sign in may hold on to the login it had; the next
            // message starts a fresh one, which reads whatever login there is by then.
            _ = StopAsync();
            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.SignInNeeded, signIn));
        }
        else if (status == "failed" && !stopped)
        {
            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, failure is null ? "the turn failed" : Readable(failure)));
        }

        Activity?.Invoke(this, new AgentActivity(AgentActivityKind.TurnDone, ""));
        _idle.TrySetResult();
    }

    private void OnLoginCompleted(JsonObject? parameters)
    {
        (string Id, TaskCompletionSource<string?> Done)? login;
        lock (_gate)
        {
            login = _login;
        }

        if (login is not { } waiting || (Text(parameters?["loginId"]) is { } id && id != waiting.Id))
        {
            return;
        }

        var worked = parameters?["success"]?.GetValueKind() is JsonValueKind.True;
        waiting.Done.TrySetResult(worked ? null : Text(parameters?["error"]) ?? "the sign-in did not finish");
    }

    private async Task<bool> InitializeAsync()
    {
        try
        {
            await CallAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "intrf4ce",
                    ["title"] = "intrf4ce",
                    ["version"] = typeof(CodexHost).Assembly.GetName().Version?.ToString(3) ?? "0",
                },
                ["capabilities"] = null,
            }).ConfigureAwait(false);

            TryWrite(new JsonObject { ["method"] = "initialized" });
            return true;
        }
        catch (CodexException ex)
        {
            LastError = "Codex did not start: " + ex.Message;
            SetStatus(AgentStatus.Failed);
            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, LastError));
            return false;
        }
    }

    /// <summary>
    /// The messages, one turn each, in the order they were said: the first opens the thread,
    /// and each waits for the turn before it to end.
    /// </summary>
    private async Task PumpAsync(ChannelReader<string> outbox, Task<bool> ready)
    {
        try
        {
            if (!await ready.ConfigureAwait(false))
            {
                return;
            }

            await foreach (var text in outbox.ReadAllAsync().ConfigureAwait(false))
            {
                await _idle.Task.ConfigureAwait(false);
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    var thread = await ThreadAsync().ConfigureAwait(false);
                    var started = await CallAsync("turn/start", new JsonObject
                    {
                        ["threadId"] = thread,
                        ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["text_elements"] = new JsonArray() }),
                    }).ConfigureAwait(false);

                    _turnId = Text(started?["turn"]?["id"]) ?? _turnId;
                }
                catch (CodexException ex)
                {
                    // A turn that never started ends here, so the next message is not left waiting for it.
                    Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Error, "Codex would not take the message: " + ex.Message));
                    lock (_gate)
                    {
                        _sent.Clear();
                    }

                    SetStatus(AgentStatus.Idle);
                    Activity?.Invoke(this, new AgentActivity(AgentActivityKind.TurnDone, ""));
                    _idle.TrySetResult();
                }
            }
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The thread to talk in: the one this presentation was built in when Codex still has it, a new one otherwise.</summary>
    private async Task<string> ThreadAsync()
    {
        if (_inThread && SessionId is { } current)
        {
            return current;
        }

        var config = await ThreadConfigAsync().ConfigureAwait(false);
        JsonNode? opened = null;
        if (_resume is { } resume)
        {
            try
            {
                opened = await CallAsync("thread/resume", ThreadParams(config, resume)).ConfigureAwait(false);
            }
            catch (CodexException ex)
            {
                // Made with another agent, or cleared away: the deck tells a new thread what is there.
                _log($"Codex has no thread {resume} ({ex.Message}); starting a new one");
            }
        }

        opened ??= await CallAsync("thread/start", ThreadParams(config, null)).ConfigureAwait(false);
        var id = Text(opened?["thread"]?["id"]) ?? throw new CodexException("Codex started a thread without an id");
        SessionId = id;
        _resume = null;
        _inThread = true;

        var model = Text(opened?["model"]);
        if (model != Model)
        {
            Model = model;
            Activity?.Invoke(this, new AgentActivity(AgentActivityKind.Started, model ?? "codex"));
        }

        return id;
    }

    private JsonObject ThreadParams(JsonObject config, string? resume)
    {
        var settings = _options.Settings;
        var parameters = new JsonObject
        {
            ["cwd"] = WorkingDirectory,
            ["approvalPolicy"] = "never",
            ["sandbox"] = Sandbox(settings.PermissionMode),
            ["config"] = config,
            ["developerInstructions"] = _options.SystemPrompt,
        };

        if (settings.Model.Length > 0)
        {
            parameters["model"] = settings.Model;
        }

        if (resume is not null)
        {
            parameters["threadId"] = resume;
        }

        return parameters;
    }

    /// <summary>
    /// What this thread needs from Codex's configuration: intrf4ce as an MCP server, the web,
    /// and — strict — none of the user's own servers, plugins or apps, which are for their own
    /// work and only crowd a presenter's tools.
    /// </summary>
    private async Task<JsonObject> ThreadConfigAsync()
    {
        var config = new JsonObject
        {
            ["mcp_servers.intrf4ce"] = McpServer(_options.Executable, Environment.GetEnvironmentVariable(AppPaths.HomeVariable)),
            ["web_search"] = "live",
        };

        if (!_options.Settings.StrictMcp)
        {
            return config;
        }

        config["features.plugins"] = false;
        config["features.apps"] = false;
        try
        {
            var effective = await CallAsync("config/read", new JsonObject { ["cwd"] = WorkingDirectory }).ConfigureAwait(false);
            if (effective?["config"]?["mcp_servers"] is JsonObject servers)
            {
                foreach (var name in servers.Select(s => s.Key).Where(n => n != "intrf4ce"))
                {
                    config[$"mcp_servers.{name}.enabled"] = false;
                }
            }
        }
        catch (CodexException ex)
        {
            _log("could not read Codex's settings to leave its other MCP servers out: " + ex.Message);
        }

        return config;
    }

    /// <summary>intrf4ce as Codex's MCP server: the shim, and how Codex is to treat its tools.</summary>
    /// <param name="executable">This program, run as <c>--mcp --hosted</c>.</param>
    /// <param name="home">The window's own <c>INTRF4CE_HOME</c>, when it has one.</param>
    internal static JsonObject McpServer(string executable, string? home)
    {
        var server = new JsonObject
        {
            ["command"] = executable,
            ["args"] = new JsonArray("--mcp", "--hosted"),

            // put_slide comes back once the slide has rendered, which can take a while.
            ["tool_timeout_sec"] = 300,

            // The stage is what the presenter is for: its tools go ahead without asking, as
            // Claude Code's do when intrf4ce answers its permission questions. Codex 0.164
            // refuses outright a call that wants approval under approvalPolicy never, where
            // 0.145 still asked; one that asks is accepted, in OnRequest.
            ["default_tools_approval_mode"] = "approve",
        };

        // Codex passes an MCP server only a few of its own environment variables, and a window
        // with a home of its own listens on a pipe of its own: the shim must be told which.
        if (home is { Length: > 0 })
        {
            server["env"] = new JsonObject { [AppPaths.HomeVariable] = home };
        }

        return server;
    }

    /// <summary>The account Codex is signed in to, or null when it cannot say.</summary>
    private async Task<SignInStatus?> AccountAsync()
    {
        Task<bool>? ready;
        lock (_gate)
        {
            ready = _ready;
        }

        if (ready is null || !await ready.ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var read = await CallAsync("account/read", new JsonObject { ["refreshToken"] = false }).ConfigureAwait(false);
            if (read?["account"] is JsonObject account)
            {
                return new SignInStatus(true, Text(account["email"]));
            }

            // No account is signed out only when Codex needs one: a local model does not.
            return new SignInStatus(read?["requiresOpenaiAuth"]?.GetValueKind() is not JsonValueKind.True, null);
        }
        catch (CodexException ex)
        {
            _log("could not ask Codex whether it is signed in: " + ex.Message);
            return null;
        }
    }

    private async Task<JsonNode?> CallAsync(string method, JsonObject parameters, CancellationToken cancellation = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = reply;
        if (!TryWrite(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }))
        {
            _calls.TryRemove(id, out _);
            throw new CodexException($"Codex is not running to ask for {method}");
        }

        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        patience.CancelAfter(Patience);
        try
        {
            return await reply.Task.WaitAsync(patience.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new CodexException($"Codex did not answer {method}");
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    private async Task CallQuietlyAsync(string method, JsonObject parameters)
    {
        try
        {
            await CallAsync(method, parameters).ConfigureAwait(false);
        }
        catch (CodexException ex)
        {
            _log($"Codex refused {method}: {ex.Message}");
        }
    }

    /// <summary>One line of JSON down the app server's stdin; false when it has stopped listening.</summary>
    private bool TryWrite(JsonObject message)
    {
        lock (_gate)
        {
            if (_stdin is null)
            {
                return false;
            }

            try
            {
                _stdin.WriteLine(message.ToJsonString());
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                LastError = "Codex stopped accepting messages: " + ex.Message;
                return false;
            }
        }
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

    private async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (Colours().Replace(line, "").Trim() is { Length: > 0 } plain)
                {
                    _log("hosted agent stderr: " + plain);
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
                _outbox?.Writer.TryComplete();
                _outbox = null;
            }
        }

        if (!wasCurrent)
        {
            return;
        }

        _log($"hosted agent exited with {code.ToString(CultureInfo.InvariantCulture)}");
        Abandon("Codex stopped");
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

    /// <summary>Whatever was waiting on the app server that is gone: it will not answer now.</summary>
    private void Abandon(string why)
    {
        foreach (var id in _calls.Keys)
        {
            if (_calls.TryRemove(id, out var reply))
            {
                reply.TrySetException(new CodexException(why));
            }
        }

        (string Id, TaskCompletionSource<string?> Done)? login;
        lock (_gate)
        {
            login = _login;
        }

        login?.Done.TrySetResult(why);
        _idle.TrySetResult();
    }

    private void ForgetUnanswered()
    {
        lock (_gate)
        {
            _sent.Clear();
            _unanswered.Clear();
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

    private void OpenInBrowser(string address)
    {
        try
        {
            using var opened = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log("could not open the sign-in page: " + ex.Message);
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static TaskCompletionSource Done()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    // Codex colours its log lines for a terminal; the log file is not one.
    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex Colours();
}

/// <summary>The app server said no, or said nothing.</summary>
public sealed class CodexException(string message) : Exception(message);
