using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace Intrf4ce.Plugins;

/// <summary>
/// One plugin element on one slide: a control, what it shows in words, and its own clock.
/// </summary>
/// <remarks>
/// <para>
/// Made on the UI thread and only ever touched there; work that finishes elsewhere — a fetch —
/// comes back with <c>Dispatcher.UIThread.Post</c>. The control is laid out in slide units, a
/// slide being 1600 wide, and the stage's camera scales it: size text from
/// <see cref="PluginTheme.FontSize"/> rather than in pixels.
/// </para>
/// <para>
/// It is given its fields once to start with and again whenever the agent changes them, and
/// keeps itself current in between — a clock ticks, a forecast refetches — for as long as its
/// slide exists. It is disposed when the slide goes: stop timers there.
/// </para>
/// </remarks>
public abstract class LiveElement : IDisposable
{
    /// <summary>What goes on the slide.</summary>
    public abstract Control View { get; }

    /// <summary>Ids that can follow the dot in a focus cue: the <c>zagreb</c> of <c>[[focus zones.zagreb]]</c>.</summary>
    public virtual IReadOnlyList<string> Parts => [];

    /// <summary>
    /// Shows these fields: the first time, and every time the agent changes them. An exception
    /// is shown in the element's place, its message for the user and the agent.
    /// </summary>
    public abstract Task ShowAsync(JsonElement fields, CancellationToken cancellationToken);

    /// <summary>
    /// What it shows right now, in words: what the agent reads back to say it — the time in
    /// Zagreb, today's high — so the narration and the slide agree.
    /// </summary>
    public virtual string Describe() => "";

    /// <summary>Where a part is, in <see cref="View"/>'s coordinates; null for no such part.</summary>
    public virtual Rect? PartBounds(string part) => null;

    /// <summary>Marks a part as the one being talked about, beyond the light the stage puts on it; null clears it.</summary>
    public virtual void Highlight(string? part)
    {
    }

    /// <summary>
    /// Moves the element's own cursor by <paramref name="steps"/> and shows what is under it — an
    /// hour along a timeline or a forecast, a day along a strip. The user's J and K, with a count
    /// as in vim. False when it has no cursor, so the keys go to something that has.
    /// </summary>
    public virtual bool MoveCursor(int steps) => false;

    /// <summary>Puts the cursor away: back to now, back to the element's own view. The user's r.</summary>
    public virtual void ResetCursor()
    {
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
