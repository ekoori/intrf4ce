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
    }

    /// <summary>The slide being shown changed, or the deck did.</summary>
    public event EventHandler? Changed;

    /// <summary>A chat message from the agent, for the chat bar to show.</summary>
    public event EventHandler<string>? AgentReplied;

    /// <summary>Something to speak and subtitle; the narrator picks it up.</summary>
    public event EventHandler<string>? SayRequested;

    public Deck? Deck => _deck;

    public int CurrentIndex => _current;

    public SlideView? CurrentView => _deck is not null && _current >= 0 && _current < _deck.Count ? _views.GetValueOrDefault(_deck.Slides[_current].Id) : null;

    public bool IsOverview => _overview;

    /// <summary>Filled in by whoever owns narration and the agent, for <c>get_state</c>.</summary>
    public Func<string> Playback { get; set; } = () => "idle";

    public Func<string> Voice { get; set; } = () => "none";

    public Func<string> Agent { get; set; } = () => "none";

    public StageState State => _state with { Playback = Playback(), Voice = Voice(), Agent = Agent() };

    // ------------------------------------------------------------------------------------
    // Navigation (UI thread)

    public void Next() => Show(_current + 1);

    public void Previous() => Show(_current - 1);

    public void First() => Show(0);

    public void Last() => Show((_deck?.Count ?? 0) - 1);

    public void Show(int index, bool animate = true)
    {
        if (_deck is null || _deck.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _deck.Count - 1);
        var changed = index != _current || _overview;
        _current = index;
        _overview = false;
        _focus = null;
        CurrentView?.Reset();
        _stage.FlyTo(StageView.SlideRect(index), animate);

        if (changed)
        {
            Publish();
        }
    }

    public void ToggleOverview()
    {
        if (_deck is null || _deck.Count == 0)
        {
            return;
        }

        _overview = !_overview;
        if (_overview)
        {
            _stage.FlyTo(StageView.DeckRect(_deck.Count), margin: 0.06);
        }
        else
        {
            _stage.FlyTo(StageView.SlideRect(Math.Max(0, _current)));
        }

        Publish();
    }

    /// <summary>In overview, clicking a slide flies to it.</summary>
    public void ShowSlideAt(Point world)
    {
        if (_deck is null)
        {
            return;
        }

        for (var i = 0; i < _deck.Count; i++)
        {
            if (StageView.SlideRect(i).Contains(world))
            {
                Show(i);
                return;
            }
        }
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
            _stage.FlyTo(StageView.SlideRect(_current), animate);
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

        Rect local;
        if (region is [var x, var y, var w, var h])
        {
            local = new Rect(x * element.Bounds.Width, y * element.Bounds.Height, w * element.Bounds.Width, h * element.Bounds.Height);
        }
        else if (part is null)
        {
            local = new Rect(element.Bounds.Size);
        }
        else if (element.PartBounds(part) is { } bounds)
        {
            local = bounds;
        }
        else
        {
            var parts = element.Parts;
            return $"'{elementId}' has no part '{part}'" + (parts.Count > 0 ? $" (parts: {string.Join(", ", parts.Take(40))})." : ".");
        }

        _focus = target;
        var origin = StageView.SlideRect(_current).Position + element.Bounds.Position;
        var world = new Rect(origin + local.Position, local.Size);

        // Keep some context around a small part; never zoom past a sensible magnification.
        var minimum = new Size(SlideLayout.Width * 0.3, SlideLayout.Height * 0.3);
        if (world.Width < minimum.Width || world.Height < minimum.Height)
        {
            world = new Rect(world.Center.X - (Math.Max(world.Width, minimum.Width) / 2), world.Center.Y - (Math.Max(world.Height, minimum.Height) / 2),
                Math.Max(world.Width, minimum.Width), Math.Max(world.Height, minimum.Height));
        }

        _stage.FlyTo(world, animate, margin: 0.08);
        if (element is ImageView image)
        {
            image.Highlight(part);
        }

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
                Show(index);
            }
        }).GetTask();

    public Task<string?> FocusAsync(string? target, double[]? region, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() => Focus(target, region)).GetTask();

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
            size = new Size(SlideLayout.Width, SlideLayout.Height);
        }
        else
        {
            // The stage alone, as framed for the user; the chat bar is the user's, not the slide's.
            target = _snapshotRoot();
            size = target.Bounds.Size;
        }

        var scale = width / Math.Max(1, size.Width);
        var pixels = new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(target);

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
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
                _deck = deck;
                _focus = null;
                _overview = false;
                foreach (var slide in deck.Slides)
                {
                    AddView(slide);
                }

                PlaceAll();
                _current = deck.Count > 0 ? 0 : -1;
                _stage.FlyTo(StageView.SlideRect(Math.Max(0, _current)), animate: false);
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
                    _stage.FlyTo(StageView.SlideRect(_current));
                }

                break;

            case DeckChangeKind.DeckUpdated:
                _deck = deck;
                break;
        }

        Publish();
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
            _stage.FlyTo(StageView.SlideRect(_current), animate: false);
        }
        else if (_overview)
        {
            _stage.FlyTo(StageView.DeckRect(e.Deck.Count), margin: 0.06);
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
            return;
        }

        for (var i = 0; i < _deck.Count; i++)
        {
            if (_views.TryGetValue(_deck.Slides[i].Id, out var view))
            {
                StageView.Place(view, i);
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
