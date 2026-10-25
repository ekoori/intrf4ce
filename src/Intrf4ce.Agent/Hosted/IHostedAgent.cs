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

    /// <summary>A turn finished; <see cref="AgentActivity.Cost"/> is the session total so far, when it is known.</summary>
    TurnDone,

    Error,

    Exited,

    /// <summary>
    /// The agent is not signed in, so nothing said reaches it until it is.
    /// <see cref="AgentActivity.Text"/> is how the agent put it; empty when a check found it
    /// before anything was said.
    /// </summary>
    SignInNeeded,
}

public sealed record AgentActivity(AgentActivityKind Kind, string Text, double? Cost = null);

/// <summary>What the host needs to know to start the agent.</summary>
/// <param name="Executable">This program, so the agent can be given <c>intrf4ce --mcp --hosted</c> as its MCP server.</param>
/// <param name="Home">A folder of its own for the agent's working directory and config files.</param>
public sealed record HostedAgentOptions(
    AgentSettings Settings,
    string Executable,
    string Home,
    string SystemPrompt);

/// <summary>Who the agent is, in the words the conversation uses for it.</summary>
/// <param name="Name">What it is called: "Claude Code".</param>
/// <param name="Account">Whose account it works through: "Claude".</param>
/// <param name="CodeToPaste">
/// Whether its sign-in page, opened by hand, ends with a code to paste back, rather than
/// finishing by itself.
/// </param>
public sealed record AgentIdentity(string Name, string Account, bool CodeToPaste);

/// <summary>
/// The agent intrf4ce runs for the user, whichever it is: what the window starts, talks to,
/// stops, and signs in.
/// </summary>
/// <remarks>
/// It is also an <see cref="IEventSink"/>: while it runs, chat, quiz answers and "ask about
/// this" go to it as messages, and the attached agents' queue stays empty.
/// </remarks>
public interface IHostedAgent : IEventSink, IAsyncDisposable
{
    event EventHandler<AgentActivity>? Activity;

    event EventHandler<AgentStatus>? StatusChanged;

    AgentIdentity Identity { get; }

    AgentStatus Status { get; }

    /// <summary>The conversation it is in, as the deck records it to come back to.</summary>
    string? SessionId { get; }

    /// <summary>Why the last start failed, in words for the chat.</summary>
    string? LastError { get; }

    bool IsRunning { get; }

    /// <summary>Whether there is a turn in flight to stop.</summary>
    bool IsWorking { get; }

    /// <summary>
    /// Not signed in, as far as the last turn or check could tell; a turn that gets through,
    /// or <see cref="SignInAsync"/>, puts it right.
    /// </summary>
    bool SignedOut { get; }

    /// <summary>Starts the agent, resuming <paramref name="resumeSession"/> when given.</summary>
    void Start(string? resumeSession);

    /// <summary>Continues <paramref name="session"/> instead of the one running: the deck just opened was built in it.</summary>
    Task SwitchToAsync(string? session);

    /// <summary>Stops the turn in flight, leaving the conversation intact.</summary>
    Task InterruptAsync();

    /// <summary>Sends a user message, starting the agent first if it is not running.</summary>
    void Send(string text);

    Task StopAsync();

    /// <summary>Forgets the conversation, for a new deck: the next message starts a fresh one.</summary>
    Task ResetAsync(string? resumeSession);

    /// <summary>The files the agent keeps of <paramref name="session"/>, whichever exist.</summary>
    IReadOnlyList<string> TranscriptOf(string? session);

    /// <summary>Asks whether it is signed in, so that an agent nobody can reach is reported before anyone asks it anything.</summary>
    Task CheckSignInAsync();

    /// <summary>
    /// Signs the agent in, in the browser, and carries on: whatever was said while it was
    /// signed out is asked again.
    /// </summary>
    /// <param name="page">Told the sign-in page's address, for when no browser came up.</param>
    Task<SignInOutcome> SignInAsync(Action<string> page, CancellationToken cancellation = default);

    /// <summary>A code from the sign-in page, for a sign-in waiting for one; false when it is not taken.</summary>
    bool SubmitSignInCode(string code);
}

/// <summary>The agent the settings ask for.</summary>
public static class HostedAgent
{
    public static IHostedAgent Create(HostedAgentOptions options, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Settings.IsCodex ? new CodexHost(options, log) : new ClaudeCodeHost(options, log);
    }
}
