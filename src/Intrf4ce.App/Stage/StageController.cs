using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Intrf4ce.App.Stage.Elements;
using Intrf4ce.Core.Decks;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Stage;

namespace Intrf4ce.App.Stage;

/// <summary>
/// Keeps the stage in step with the deck and answers the agent's questions about it.
/// </summary>
/// <remarks>
/// Deck changes arrive on whatever thread made them; each is posted to the UI thread in
/// order. <see cref="AwaitSlideAsync"/> queues behind them at a lower priority, so by the
/// time it looks, the change the tool just made has been applied, and it reports on the
/// right version of the slide.
/// </remarks>
public sealed class StageController : IStage
{
    private readonly StageView _stage;
    private readonly ElementContext _context;
    private readonly DeckService _decks;
    private readonly Func<Control> _snapshotRoot;
    private readonly Dictionary<string, SlideView> _views = new(StringComparer.Ordinal);
    private Deck? _deck;
    private int _current = -1;
    private bool _overview;
    private string? _focus;
    private volatile StageState _state = new(-1, null, "idle", null, false, "", "");

    public StageController(StageView stage, ElementContext context, DeckService decks, Func<Control> snapshotRoot)
    {
        _stage = stage;
        _context = context;
        _decks = decks;
        _snapshotRoot = snapshotRoot;
        _decks.Changed += (_, e) => Dispatcher.UIThread.Post(() => OnDeckChanged(e), DispatcherPriority.Normal);
        _stage.DraggableAt = screen => _overview ? null : HitTest(_stage.ScreenToWorld(screen))?.Element as IStageDraggable;
    }

    /// <summary>The slide being shown changed, or the deck did.</summary>
    public event EventHandler? Changed;

    /// <summary>A chat message from the agent, for the chat bar to show.</summary>
    public event EventHandler<string>? AgentReplied;

    /// <summary>Something to speak and subtitle; the narrator picks it up.</summary>
    public event EventHandler<string>? SayRequested;

    /// <summary>A different slide is now the one being shown (null: none), and whether to narrate it.</summary>
    public event EventHandler<ShownSlide>? CurrentChanged;

    /// <summary>The slide being shown was replaced by the agent.</summary>
    public event EventHandler<Slide>? CurrentUpdated;

    /// <summary>Slides were added, removed or moved.</summary>
    public event EventHandler? SlidesChanged;

    public Slide? CurrentSlide => _deck is not null && _current >= 0 && _current < _deck.Count ? _deck.Slides[_current] : null;

    public Deck? Deck => _deck;

    public int CurrentIndex => _current;

    public SlideView? CurrentView => _deck is not null && _current >= 0 && _current < _deck.Count ? _views.GetValueOrDefault(_deck.Slides[_current].Id) : null;

    public bool IsOverview => _overview;

    /// <summary>Filled in by whoever owns narration and the agent, for <c>get_state</c>.</summary>
    public Func<string> Playback { get; set; } = () => "idle";

    /// <summary>
    /// Works the transport on the agent's behalf; supplied by the window, which owns the
    /// narrator. Returns what the performance is doing afterwards.
    /// </summary>
    public Func<PlaybackRequest, string> Perform { get; set; } = _ => "there is no narrator";

    public Func<string> Voice { get; set; } = () => "none";

    public Func<string> Agent { get; set; } = () => "none";

    public StageState State => _state with { Playback = Playback(), Voice = Voice(), Agent = Agent() };

    // ------------------------------------------------------------------------------------
    // Navigation (UI thread)

    public void Next() => Show(_current + 1);

    public void Previous() => Show(_current - 1);

    public void First() => Show(0);

    public void Last() => Show((_deck?.Count ?? 0) - 1);

    public void Show(int index, bool animate = true, bool play = true)
    {
        if (_deck is null || _deck.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _deck.Count - 1);
        var moved = index != _current;
        var changed = moved || _overview;
        ClearFocusMarks();
        _current = index;
        _overview = false;
        _focus = null;
        CurrentView?.Reset();
        _stage.FlyTo(_stage.SlideRect(index), animate);

        if (changed)
        {
            Publish();
        }

        if (moved)
        {
            CurrentChanged?.Invoke(this, new ShownSlide(CurrentSlide, play));
        }
    }

    /// <summary>Undoes what a focus left behind: the spotlight and highlighted parts.</summary>
    private void ClearFocusMarks()
    {
        _stage.Spotlight.Set([]);
        foreach (var element in CurrentView?.Elements ?? [])
        {
            element.Highlight(null);
        }
    }

    public void ToggleOverview()
    {
        if (_deck is null || _deck.Count == 0)
        {
            return;
        }

        _overview = !_overview;
        ClearFocusMarks();
        if (_overview)
        {
            _stage.FlyTo(_stage.DeckRect(_deck.Count), margin: 0.06);
        }
        else
        {
            _stage.FlyTo(_stage.SlideRect(Math.Max(0, _current)));
        }

        Publish();
    }

    /// <summary>Back to the whole current slide, after the user zoomed or panned by hand.</summary>
    public void Reframe()
    {
        if (_overview)
        {
            _stage.FlyTo(_stage.DeckRect(_deck?.Count ?? 0), margin: 0.06);
        }
        else if (_current >= 0)
        {
            ClearFocusMarks();
            _focus = null;
            _stage.FlyTo(_stage.SlideRect(_current));
            Publish();
        }
    }

    /// <summary>
    /// Focuses the next (or previous) part of the current slide: a diagram's steps in order,
    /// or failing that its nodes, an image's callouts, a table's rows.
    /// </summary>
    /// <returns>False when the slide has nothing to step through.</returns>
    public bool Step(int direction)
    {
        if (CurrentView is not { } slide || _overview)
        {
            return false;
        }

        var targets = new List<string>();
        foreach (var element in slide.Elements)
        {
            var parts = element.Element is DiagramElement { Steps: { Count: > 0 } steps } ? steps : element.Parts;
            targets.AddRange(parts.Select(p => $"{element.Id}.{p}"));
        }

        if (targets.Count == 0)
        {
            return false;
        }

        var index = _focus is null ? -1 : targets.IndexOf(_focus);
        var next = index + direction;
        if (next < 0 || next >= targets.Count)
        {
            // Past either end: back to the whole slide, and one more press leaves it.
            if (_focus is null)
            {
                return false;
            }

            Focus(null, null);
            return true;
        }

        Focus(targets[next], null);
        return true;
    }

    /// <summary>
    /// Moves the cursor inside an element (J and K): the element in focus when it has a cursor,
    /// otherwise the first on the slide that does — a clock, a forecast, a model.
    /// </summary>
    /// <returns>Whether anything had a cursor to move.</returns>
    public bool MoveCursor(int steps)
    {
        if (CurrentView is not { } slide || _overview)
        {
            return false;
        }

        var focused = _focus?.Split('.')[0] is { } id ? slide.Find(id) : null;
        if (focused?.MoveCursor(steps) == true)
        {
            return true;
        }

        return slide.Elements.Where(e => e != focused).Any(e => e.MoveCursor(steps));
    }

    /// <summary>Puts every cursor on the slide back (r): to now, to each model's own view.</summary>
    public void ResetCursors()
    {
        if (CurrentView is { } slide)
        {
            foreach (var element in slide.Elements)
            {
                element.ResetCursor();
            }
        }
    }

    /// <summary>
    /// A click on the stage: in overview it opens the slide; on a slide it focuses the part
    /// clicked (a callout, a node, a photo, a row), and a second click on it, or a click on
    /// nothing, goes back to the whole slide.
    /// </summary>
    public void Click(Point world)
    {
        if (_deck is null)
        {
            return;
        }

        var index = SlideIndexAt(world);
        if (_overview || (index >= 0 && index != _current))
        {
            // Also a slide the user panned into view by hand.
            if (index >= 0)
            {
                Show(index);
            }

            return;
        }

        if (HitTest(world) is { Part: { } part } hit)
        {
            var target = $"{hit.Element.Id}.{part}";
            Focus(target == _focus ? null : target, null);
        }
        else if (_focus is not null)
        {
            Focus(null, null);
        }
    }

    /// <summary>
    /// The picture under a point on the current slide, for a closer look at it; null if there
    /// is none there.
    /// </summary>
    public ImageView? ImageAt(Point world) =>
        _overview || SlideIndexAt(world) != _current ? null : HitTest(world)?.Element as ImageView;

    /// <summary>What a right-click would ask about: the part under the pointer, its element, or the slide.</summary>
    public AskSubject? SubjectAt(Point world)
    {
        if (_overview || CurrentView is not { } slide || SlideIndexAt(world) != _current)
        {
            return null;
        }

        if (HitTest(world) is not { } hit)
        {
            return new AskSubject(slide.SlideId, null, null, slide.Slide.Title is { Length: > 0 } title ? title : "this slide");
        }

        var element = hit.Element;
        return hit.Part is { } part
            ? new AskSubject(slide.SlideId, element.Id, $"{element.Id}.{part}", element.Describe(part))
            : new AskSubject(slide.SlideId, element.Id, element.Id, element.Describe(null));
    }

    /// <summary>The element of the current slide under a point in the world, and its part there, if any.</summary>
    private (ElementView Element, string? Part)? HitTest(Point world)
    {
        if (CurrentView is not { } slide)
        {
            return null;
        }

        var origin = _stage.SlideRect(_current).Position;
        foreach (var element in slide.Elements.Where(e => e.IsVisible).Reverse())
        {
            var local = new Point(world.X - origin.X - element.Bounds.X, world.Y - origin.Y - element.Bounds.Y);
            if (new Rect(element.Bounds.Size).Contains(local))
            {
                return (element, element.PartAt(local));
            }
        }

        return null;
    }

    private int SlideIndexAt(Point world)
    {
        for (var i = 0; i < (_deck?.Count ?? 0); i++)
        {
            if (_stage.SlideRect(i).Contains(world))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Zooms to an element or part of the current slide; null returns to the whole slide.
    /// </summary>
    /// <returns>An error message for the agent, or null.</returns>
    public string? Focus(string? target, double[]? region, bool animate = true)
    {
        if (CurrentView is not { } slide)
        {
            return "No slide is showing.";
        }

        if (target is null)
        {
            _focus = null;
            ClearFocusMarks();
            _stage.FlyTo(_stage.SlideRect(_current), animate);
            Publish();
            return null;
        }

        var dot = target.IndexOf('.', StringComparison.Ordinal);
        var elementId = dot < 0 ? target : target[..dot];
        var part = dot < 0 ? null : target[(dot + 1)..];

        if (slide.Find(elementId) is not { } element)
        {
            return $"There is no element '{elementId}' on slide '{slide.SlideId}' (elements: {string.Join(", ", slide.Elements.Select(e => e.Id))}).";
        }

        if (part is not null && region is null && element.PartBounds(part) is null)
        {
            var parts = element.Parts;
            return $"'{elementId}' has no part '{part}'" + (parts.Count > 0 ? $" (parts: {string.Join(", ", parts.Take(40))})." : ".");
        }

        // Marked first: a part in a scrolling list scrolls into view, and is measured where it lands.
        ClearFocusMarks();
        element.Highlight(part);
        element.UpdateLayout();

        var local = region is [var x, var y, var w, var h] ? element.RegionBounds(x, y, w, h)
            : part is not null && element.PartBounds(part) is { } bounds ? bounds
            : new Rect(element.Bounds.Size);

        _focus = target;
        var origin = _stage.SlideRect(_current).Position + element.Bounds.Position;
        var world = new Rect(origin + local.Position, local.Size);

        // The spotlight lights the part itself (and a callout's label), not the whole framing.
        var holes = new List<Rect> { world };
        if (part is not null && element.LabelBounds(part) is { } label)
        {
            holes.Add(new Rect(origin + label.Position, label.Size));
        }

        if (part is not null || region is not null)
        {
            _stage.Spotlight.Set(holes);
        }

        // Keep some context around a small part; never zoom past a sensible magnification.
        var minimum = new Size(SlideLayout.Width * 0.3, SlideLayout.Height * 0.3);
        if (world.Width < minimum.Width || world.Height < minimum.Height)
        {
            world = new Rect(world.Center.X - (Math.Max(world.Width, minimum.Width) / 2), world.Center.Y - (Math.Max(world.Height, minimum.Height) / 2),
                Math.Max(world.Width, minimum.Width), Math.Max(world.Height, minimum.Height));
        }

        // Frame the lit parts together, so a callout's label is on screen with its anchor.
        var frame = holes.Aggregate(world, (all, hole) => all.Union(hole));
        _stage.FlyTo(frame, animate, margin: 0.08);
        Publish();
        return null;
    }

    // ------------------------------------------------------------------------------------
    // IStage (any thread)

    public async Task<SlideReport> AwaitSlideAsync(string slideId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Background priority: runs after the Normal-priority post of the change being awaited.
        // A tuple rather than the task itself: InvokeAsync would otherwise await it on the UI thread.
        var (ready, found) = await Dispatcher.UIThread.InvokeAsync(
            () => _views.TryGetValue(slideId, out var view) ? (view.Ready, true) : (Task.CompletedTask, false),
            DispatcherPriority.Background);

        if (!found)
        {
            return new SlideReport(slideId, false,
                [new SpecIssue(IssueSeverity.Warning, "slide", "the slide is not on the stage")], new Dictionary<string, IReadOnlyList<string>>());
        }

        await Task.WhenAny(ready, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_views.TryGetValue(slideId, out var view))
            {
                return new SlideReport(slideId, false, [], new Dictionary<string, IReadOnlyList<string>>());
            }

            var report = view.Report();
            if (!report.Ready)
            {
                return report with
                {
                    Issues = [.. report.Issues, new SpecIssue(IssueSeverity.Warning, "slide", $"still loading after {timeout.TotalSeconds:0} s (a slow download?); it will appear when ready")],
                };
            }

            return report;
        });
    }

    public Task GoToAsync(string slideId, bool play, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_deck?.IndexOf(slideId) is { } index and >= 0)
            {
                Show(index, play: play);
            }
        }).GetTask();

    public Task<string?> FocusAsync(string? target, double[]? region, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() => Focus(target, region)).GetTask();

    public Task<string> PlaybackAsync(PlaybackRequest request, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() => Perform(request)).GetTask();

    public Task<ElementReading?> ReadElementAsync(string? slideId, string elementId, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync<ElementReading?>(() =>
        {
            var id = slideId ?? (_deck is not null && _current >= 0 && _current < _deck.Count ? _deck.Slides[_current].Id : null);
            if (id is null || !_views.TryGetValue(id, out var slide) || slide.Find(elementId) is not { } view)
            {
                return null;
            }

            return view is PluginElementView plugin
                ? new ElementReading(id, elementId, view.Element.TypeName, plugin.IsLive, plugin.Reading)
                : new ElementReading(id, elementId, view.Element.TypeName, Live: false, view.Describe(null));
        }).GetTask();

    public Task SayAsync(string text, CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.Post(() => SayRequested?.Invoke(this, text));
        return Task.CompletedTask;
    }

    public void Reply(string text) => Dispatcher.UIThread.Post(() => AgentReplied?.Invoke(this, text));

    public async Task<byte[]> SnapshotAsync(string? slideId, int width, CancellationToken cancellationToken)
    {
        if (slideId is not null)
        {
            var (ready, _) = await Dispatcher.UIThread.InvokeAsync(() => _views.TryGetValue(slideId, out var v) ? (v.Ready, true) : (Task.CompletedTask, false));
            await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            // Let an in-flight camera move land before taking the picture.
            await WaitForCameraAsync(cancellationToken).ConfigureAwait(false);
        }

        return await Dispatcher.UIThread.InvokeAsync(() => Render(slideId, width));
    }

    private async Task WaitForCameraAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 40; i++)
        {
            var moving = await Dispatcher.UIThread.InvokeAsync(() => _stage.IsMoving);
            if (!moving)
            {
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private byte[] Render(string? slideId, int width)
    {
        Control target;
        Size size;

        if (slideId is not null && _views.TryGetValue(slideId, out var slide))
        {
            target = slide;
            var (w, h) = SlideLayout.SizeOf(slide.Slide);
            size = new Size(w, h);
        }
        else
        {
            // The stage alone, as framed for the user; the chat bar is the user's, not the slide's.
            target = _snapshotRoot();
            size = target.Bounds.Size;
        }

        // Rendered at 96 DPI, one pixel per layout unit, and scaled afterwards. At any other
        // DPI RenderTargetBitmap applies the scale a second time to content nested inside a
        // transformed panel, and the slide's text came out smaller than its card.
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width)), Math.Max(1, (int)Math.Ceiling(size.Height)));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
        bitmap.Render(target);

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return Resize(stream.ToArray(), width);
    }

    private static byte[] Resize(byte[] png, int width)
    {
        using var source = SkiaSharp.SKBitmap.Decode(png);
        if (source is null || source.Width <= width)
        {
            return png;
        }

        var height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
        using var resized = source.Resize(new SkiaSharp.SKImageInfo(width, height), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        using var image = SkiaSharp.SKImage.FromBitmap(resized);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ------------------------------------------------------------------------------------
    // Deck changes (UI thread)

    private void OnDeckChanged(DeckChangedEventArgs e)
    {
        var deck = e.Deck;

        switch (e.Kind)
        {
            case DeckChangeKind.Opened:
                _stage.Clear();
                _views.Clear();
                _stage.Spotlight.Set([]);
                _deck = deck;
                _focus = null;
                _overview = false;
                foreach (var slide in deck.Slides)
                {
                    AddView(slide);
                }

                PlaceAll();
                _current = deck.Count > 0 ? 0 : -1;
                _stage.FlyTo(_stage.SlideRect(Math.Max(0, _current)), animate: false);
                CurrentChanged?.Invoke(this, new ShownSlide(CurrentSlide, true));
                break;

            case DeckChangeKind.SlideAdded:
                _deck = deck;
                AddView(deck.Find(e.SlideId!)!);
                PlaceAll();
                KeepCurrent(e);
                break;

            case DeckChangeKind.SlideReplaced:
                _deck = deck;
                if (_views.TryGetValue(e.SlideId!, out var existing))
                {
                    existing.Update(deck.Find(e.SlideId!)!);
                    if (deck.IndexOf(e.SlideId!) == _current)
                    {
                        CurrentUpdated?.Invoke(this, deck.Find(e.SlideId!)!);
                    }
                }
                else
                {
                    AddView(deck.Find(e.SlideId!)!);
                }

                PlaceAll();
                KeepCurrent(e);
                break;

            case DeckChangeKind.SlideRemoved:
                var removedIndex = _deck?.IndexOf(e.SlideId!) ?? -1;
                if (_views.Remove(e.SlideId!, out var gone))
                {
                    _stage.Remove(gone);
                }

                var wasCurrent = removedIndex == _current;
                _deck = deck;
                PlaceAll();
                if (deck.Count == 0)
                {
                    _current = -1;
                }
                else if (wasCurrent || _current >= deck.Count)
                {
                    Show(Math.Min(Math.Max(0, removedIndex), deck.Count - 1));
                }
                else if (removedIndex < _current)
                {
                    _current--;
                }

                break;

            case DeckChangeKind.SlidesMoved:
                var currentId = _deck is not null && _current >= 0 && _current < _deck.Count ? _deck.Slides[_current].Id : null;
                _deck = deck;
                PlaceAll();
                if (currentId is not null)
                {
                    _current = deck.IndexOf(currentId);
                    _stage.FlyTo(_stage.SlideRect(_current));
                }

                break;

            case DeckChangeKind.DeckUpdated:
                _deck = deck;
                break;

            case DeckChangeKind.Closed:
                _stage.Clear();
                _views.Clear();
                _stage.Spotlight.Set([]);
                _deck = null;
                _current = -1;
                _focus = null;
                _overview = false;
                CurrentChanged?.Invoke(this, new ShownSlide(null, false));
                break;
        }

        Publish();
        if (e.Kind is DeckChangeKind.SlideAdded or DeckChangeKind.SlideRemoved or DeckChangeKind.SlidesMoved)
        {
            SlidesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>After a slide arrives, decide whether the camera follows it.</summary>
    private void KeepCurrent(DeckChangedEventArgs e)
    {
        var index = e.Deck.IndexOf(e.SlideId!);

        if (_current < 0)
        {
            // The first slide of a deck: the user has been looking at an empty stage.
            Show(index);
            return;
        }

        if (e.Kind == DeckChangeKind.SlideAdded && index <= _current)
        {
            // Inserted before the current slide: the current one moved along.
            _current++;
        }

        if (FollowAgent && e.Kind == DeckChangeKind.SlideAdded)
        {
            Show(index);
            return;
        }

        if (!_overview && _focus is null)
        {
            _stage.FlyTo(_stage.SlideRect(_current), animate: false);
        }
        else if (_overview)
        {
            _stage.FlyTo(_stage.DeckRect(e.Deck.Count), margin: 0.06);
        }
    }

    /// <summary>Jump to every new slide as it arrives (a setting).</summary>
    public bool FollowAgent { get; set; }

    private void AddView(Slide slide)
    {
        var view = new SlideView(_context, slide);
        _views[slide.Id] = view;
        _stage.Add(view);
    }

    private void PlaceAll()
    {
        if (_deck is null)
        {
            _stage.Layout = WorldLayout.Standard(0);
            return;
        }

        // Every slide's size, so a canvas gets the room it needs and nothing overlaps it.
        _stage.Layout = new WorldLayout([.. _deck.Slides.Select(SlideLayout.SizeOf)]);

        for (var i = 0; i < _deck.Count; i++)
        {
            if (_views.TryGetValue(_deck.Slides[i].Id, out var view))
            {
                _stage.Place(view, i);
            }
        }
    }

    private void Publish()
    {
        var count = _deck?.Count ?? 0;
        var currentId = _deck is not null && _current >= 0 && _current < count ? _deck.Slides[_current].Id : null;
        _state = new StageState(_current, currentId, "idle", _focus, count > 0 && _current == count - 1, "", "");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
