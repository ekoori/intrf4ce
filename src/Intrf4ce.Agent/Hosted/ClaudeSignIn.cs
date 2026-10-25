using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Intrf4ce.Core.Settings;

namespace Intrf4ce.Agent.Hosted;

/// <summary>Whether Claude Code is signed in, as <c>claude auth status</c> has it.</summary>
/// <param name="Account">Whose Claude account, when it says.</param>
public sealed record SignInStatus(bool SignedIn, string? Account);

/// <summary>How a sign-in ended.</summary>
/// <param name="Error">Why it did not work, mostly in Claude Code's words; null when it did, or when it was given up.</param>
/// <param name="Account">The Claude account it is signed in to now, when Claude Code says.</param>
public sealed record SignInOutcome(bool SignedIn, string? Error = null, string? Account = null)
{
    /// <summary>Whether what was said while it was signed out has gone to the agent again.</summary>
    public bool AskedAgain { get; init; }
}

/// <summary>
/// Claude Code's own sign-in, <c>claude auth login</c>, and its own answer to whether it is
/// signed in, <c>claude auth status</c>: both run the way the agent is, so they see the login
/// the agent will.
/// </summary>
/// <remarks>
/// intrf4ce never handles the login itself. Claude Code opens the browser, waits on a port of
/// its own for the browser to come back, and keeps the tokens where the agent reads them. All
/// that is done here is starting it, passing on the page to open when no browser came up, and
/// handing over the code that page shows — the one step that would otherwise need a terminal.
/// </remarks>
public sealed partial class ClaudeSignIn
{
    private static readonly TimeSpan StatusPatience = TimeSpan.FromSeconds(20);

    private readonly AgentSettings _settings;
    private readonly string _workingDirectory;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Process? _login;

    public ClaudeSignIn(AgentSettings settings, string workingDirectory, Action<string> log)
    {
        _settings = settings;
        _workingDirectory = workingDirectory;
        _log = log;
    }

    /// <summary>Whether a sign-in is waiting on the browser.</summary>
    public bool InProgress
    {
        get
        {
            lock (_gate)
            {
                return _login is { HasExited: false };
            }
        }
    }

    /// <summary>
    /// Asks Claude Code whether it is signed in. Null when it cannot say: not installed, too old
    /// to have <c>auth status</c>, or too slow to answer.
    /// </summary>
    public async Task<SignInStatus?> StatusAsync(CancellationToken cancellation = default)
    {
        if (Start("status", "--json") is not { } process)
        {
            return null;
        }

        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            patience.CancelAfter(StatusPatience);
            try
            {
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                _log("claude auth status did not answer");
                return null;
            }

            var status = ParseStatus(await output.ConfigureAwait(false));
            if (status is null)
            {
                _log("claude auth status said nothing intrf4ce could read: " + (await errors.ConfigureAwait(false)).Trim());
            }

            return status;
        }
    }

    /// <summary>
    /// Runs <c>claude auth login</c> until it is done: the browser opens, the user signs in, and
    /// Claude Code keeps the login. Starting one while another waits gives the other up.
    /// </summary>
    /// <param name="page">
    /// Told the sign-in page's address as soon as Claude Code prints it, for when no browser came
    /// up. That page ends by showing a code, which <see cref="SubmitCode"/> passes on.
    /// </param>
    /// <param name="cancellation">Gives the sign-in up; the outcome is then neither success nor an error.</param>
    public async Task<SignInOutcome> SignInAsync(Action<string> page, CancellationToken cancellation = default)
    {
        Process process;
        lock (_gate)
        {
            Kill(_login);
            if (Start("login") is not { } started)
            {
                return new SignInOutcome(false, ClaudeCodeHost.ResolveCommand(_settings.CommandToRun) is null
                    ? $"Could not find '{_settings.CommandToRun}' on the PATH. Install Claude Code, or set agent.command in settings.yaml."
                    : $"Could not start {_settings.CommandToRun} to sign in; the log says why.");
            }

            process = started;
            _login = process;
        }

        _log("signing Claude Code in");
        var complaints = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                if (PageIn(line) is { } address)
                {
                    page(address);
                }
            }
        }, CancellationToken.None);

        var complaining = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                if (line.Trim().Length > 0)
                {
                    _log("claude auth login: " + line.Trim());
                    lock (complaints)
                    {
                        complaints.AppendLine(line.Trim());
                    }
                }
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            await Task.WhenAll(reading, complaining).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            _log("sign-in given up");
            return new SignInOutcome(false);
        }
        catch (IOException)
        {
            // Its output went with it; the exit code still says how it ended.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_login, process))
                {
                    _login = null;
                }
            }
        }

        var code = process.ExitCode;
        process.Dispose();
        if (code == 0)
        {
            _log("Claude Code is signed in");
            var status = await StatusAsync(CancellationToken.None).ConfigureAwait(false);
            return new SignInOutcome(true, Account: status?.Account);
        }

        string said;
        lock (complaints)
        {
            said = complaints.ToString();
        }

        _log($"claude auth login ended with {code}");
        return new SignInOutcome(false, Reason(said) ?? $"Claude Code stopped before it was signed in (exit code {code}).");
    }

    /// <summary>
    /// Hands Claude Code the code a sign-in page shows when it cannot send the browser back by
    /// itself: two parts with a <c>#</c> between them.
    /// </summary>
    /// <returns>False when nothing is waiting for one, or that is not such a code.</returns>
    public bool SubmitCode(string code)
    {
        code = code.Trim();
        if (!LooksLikeCode(code))
        {
            return false;
        }

        lock (_gate)
        {
            if (_login is not { HasExited: false } login)
            {
                return false;
            }

            try
            {
                login.StandardInput.WriteLine(code);
                login.StandardInput.Flush();
                _log("passed a sign-in code to Claude Code");
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>What <c>claude auth status --json</c> printed, read; null when it is not that.</summary>
    internal static SignInStatus? ParseStatus(string output)
    {
        // Only the object: anything printed around it is not the answer.
        var (from, to) = (output.IndexOf('{', StringComparison.Ordinal), output.LastIndexOf('}'));
        if (from < 0 || to < from)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(output[from..(to + 1)]) is JsonObject status
                && status["loggedIn"] is JsonValue loggedIn && loggedIn.TryGetValue<bool>(out var signedIn))
            {
                var account = status["email"] is JsonValue email && email.TryGetValue<string>(out var who) && who.Length > 0 ? who : null;
                return new SignInStatus(signedIn, account);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// The address in a line of <c>claude auth login</c>'s output — "If the browser didn't open,
    /// visit: …" — out of whatever terminal codes it is wrapped in to make it a link.
    /// </summary>
    internal static string? PageIn(string line) => Address().Match(line) is { Success: true } match ? match.Value : null;

    /// <summary>
    /// Whether <paramref name="text"/> has the shape of a sign-in code: two long parts with a
    /// <c>#</c> between them, and nothing a sentence or an address would have. Strict, because
    /// while a sign-in waits, whatever passes goes to Claude Code instead of the agent.
    /// </summary>
    internal static bool LooksLikeCode(string text) =>
        text.Split('#') is [{ Length: >= 8 }, { Length: >= 8 }]
        && !text.Any(c => char.IsWhiteSpace(c) || c is '/' or ':');

    /// <summary>Why a sign-in failed: from Claude Code's "Login failed: …" on, or everything it complained of.</summary>
    internal static string? Reason(string complaints)
    {
        var text = complaints.Trim();
        var failed = text.IndexOf("failed:", StringComparison.OrdinalIgnoreCase);
        if (failed >= 0)
        {
            var start = text.LastIndexOf('\n', failed) + 1;
            text = text[start..];
        }

        text = string.Join(' ', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text.Length <= 300 ? text : text[..299] + "…";
    }

    /// <summary>
    /// <c>claude auth</c> with <paramref name="arguments"/>, started; null when it could not be.
    /// </summary>
    private Process? Start(params string[] arguments)
    {
        if (ClaudeCodeHost.ResolveCommand(_settings.CommandToRun) is not { } executable)
        {
            return null;
        }

        var start = ClaudeCodeHost.StartInfo(executable, _settings, _workingDirectory);
        start.ArgumentList.Add("auth");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(start) ?? throw new InvalidOperationException("no process was started");
            if (OperatingSystem.IsWindows())
            {
                ChildProcessGuard.Adopt(process);
            }

            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            _log($"could not start {executable} auth {string.Join(' ', arguments)}: {ex.Message}");
            return null;
        }
    }

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    // Stops at a space or any control character: the end of the link a terminal would draw.
    [GeneratedRegex(@"https://[^\s\x00-\x1F\x7F]+")]
    private static partial Regex Address();
}
