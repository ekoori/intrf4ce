using Intrf4ce.Core.Model;

namespace Intrf4ce.Core.Stage;

/// <summary>What the agent is told about the stage when it asks.</summary>
/// <param name="CurrentIndex">Zero-based; -1 when the deck is empty.</param>
/// <param name="Playback">idle, speaking, paused or waiting (for a slide the agent has not built yet).</param>
/// <param name="Focus">The current focus target, if any.</param>
/// <param name="UserAtNewest">The user is looking at the last slide, so the next one is what they wait for.</param>
/// <param name="Voice">Which voice narration is using and, for ElevenLabs, what is left of the budget.</param>
public sealed record StageState(
    int CurrentIndex,
    string? CurrentSlideId,
    string Playback,
    string? Focus,
    bool UserAtNewest,
    string Voice,
    string Agent);

/// <summary>What became of a slide once the stage tried to show it.</summary>
/// <param name="Ready">Everything loaded and rendered (or failed, which also counts as settled).</param>
/// <param name="Issues">Render and load problems: a Mermaid parse error, a 404, a cue at a missing node.</param>
/// <param name="Parts">Parts discovered by rendering, such as Mermaid node ids, by element id.</param>
public sealed record SlideReport(
    string SlideId,
    bool Ready,
    IReadOnlyList<SpecIssue> Issues,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Parts);

/// <summary>
/// The parts of the app only the window can do, as the agent's tools see them.
/// </summary>
/// <remarks>
/// Declared in Core so the tools can be tested against a fake and the agent layer never
/// references Avalonia. Implementations marshal to the UI thread themselves.
/// </remarks>
public interface IStage
{
    StageState State { get; }

    /// <summary>Waits until the stage has loaded and rendered a slide, or the timeout passes.</summary>
    Task<SlideReport> AwaitSlideAsync(string slideId, TimeSpan timeout, CancellationToken cancellationToken);

    Task GoToAsync(string slideId, bool play, CancellationToken cancellationToken);

    /// <summary>Focuses an element or part now; null clears. Returns an error message, or null.</summary>
    Task<string?> FocusAsync(string? target, double[]? region, CancellationToken cancellationToken);

    /// <summary>Speaks and subtitles <paramref name="text"/> now, without saving it to the deck.</summary>
    Task SayAsync(string text, CancellationToken cancellationToken);

    /// <summary>Shows a message from the agent in the chat.</summary>
    void Reply(string text);

    /// <summary>A PNG of the stage as the user sees it, or of one slide rendered on its own.</summary>
    Task<byte[]> SnapshotAsync(string? slideId, int width, CancellationToken cancellationToken);
}
