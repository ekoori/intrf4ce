using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Intrf4ce.App.Stage.Elements;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Stage;

namespace Intrf4ce.App.Stage;

/// <summary>
/// One slide on the world canvas: a 1600×900 card holding its title and element views.
/// </summary>
/// <remarks>
/// Updates reconcile by element id rather than rebuilding, so when the agent adds one callout
/// to a slide the user is watching, only that element changes and the rest does not flicker.
/// New elements rise and fade in; removed ones fade out.
/// </remarks>
public sealed class SlideView : Border
{
    private readonly ElementContext _context;
    private readonly Canvas _canvas = new() { Width = SlideLayout.Width, Height = SlideLayout.Height };
    private readonly TextBlock _title;
    private readonly Dictionary<string, ElementView> _views = new(StringComparer.Ordinal);
    private Slide _slide;
    private int _version;

    public SlideView(ElementContext context, Slide slide)
    {
        _context = context;
        _slide = slide;
        var theme = context.Skin;

        Width = SlideLayout.Width;
        Height = SlideLayout.Height;
        CornerRadius = new CornerRadius(22);
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(theme.Surface, 0),
                new GradientStop(Color.FromRgb(0x0F, 0x14, 0x1B), 1),
            },
        };
        BorderBrush = theme.Brush(theme.Line, 0.9);
        BorderThickness = new Thickness(1.5);
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 60, Spread = 4, Color = Color.FromArgb(150, 0, 0, 0), OffsetY = 18 });
        ClipToBounds = false;

        _title = new TextBlock
        {
            Foreground = theme.Brush(theme.Text),
            FontSize = 46,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = SlideLayout.Width - (2 * SlideLayout.PaddingX),
        };

        Canvas.SetLeft(_title, SlideLayout.PaddingX);
        Canvas.SetTop(_title, SlideLayout.PaddingTop - 6);
        _canvas.Children.Add(_title);
        Child = _canvas;

        Apply(slide, animate: false);
    }

    public string SlideId => _slide.Id;

    public Slide Slide => _slide;

    public IEnumerable<ElementView> Elements => _views.Values;

    public ElementView? Find(string elementId) => _views.GetValueOrDefault(elementId);

    /// <summary>Completes when every element of the current version has loaded (or failed).</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    public void Update(Slide slide) => Apply(slide, animate: true);

    public SlideReport Report()
    {
        var issues = _views.Values.SelectMany(v => v.Issues).ToList();
        var parts = _views.Values
            .Where(v => v.Parts.Count > 0)
            .ToDictionary(v => v.Id, v => (IReadOnlyList<string>)v.Parts, StringComparer.Ordinal);

        return new SlideReport(_slide.Id, Ready.IsCompleted, issues, parts);
    }

    private void Apply(Slide slide, bool animate)
    {
        _slide = slide;
        var version = ++_version;

        _title.Text = slide.Title;
        _title.IsVisible = !string.IsNullOrWhiteSpace(slide.Title);

        var boxes = SlideLayout.Arrange(slide);
        var keep = new HashSet<string>(slide.Elements.Select(e => e.Id), StringComparer.Ordinal);

        foreach (var gone in _views.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            var view = _views[gone];
            _views.Remove(gone);
            view.Cancel();
            FadeOut(view);
        }

        foreach (var element in slide.Elements)
        {
            var box = boxes[element.Id];
            var isNew = false;

            if (!_views.TryGetValue(element.Id, out var view) || !ElementViews.CanReuse(view, element))
            {
                if (view is not null)
                {
                    view.Cancel();
                    _canvas.Children.Remove(view);
                }

                view = ElementViews.Create(_context, element);
                _views[element.Id] = view;
                _canvas.Children.Add(view);
                isNew = true;
            }

            var resized = view.Width != box.Width || view.Height != box.Height;
            view.Width = box.Width;
            view.Height = box.Height;
            Canvas.SetLeft(view, box.X);
            Canvas.SetTop(view, box.Y);

            // A chart's spec depends on its size, so a resize is a reload for it.
            view.Update(element, force: isNew || (resized && element is ChartElement));

            view.IsVisible = !element.Hidden;
            if (isNew && animate)
            {
                Enter(view);
            }
        }

        var pending = _views.Values.Select(v => v.Ready).ToArray();
        Ready = Task.WhenAll(pending);
        _ = Ready.ContinueWith(_ =>
        {
            if (version == _version)
            {
                Dispatcher.UIThread.Post(InvalidateArrange);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Reveals or hides an element, for show and hide cues.</summary>
    public void SetVisible(string elementId, bool visible)
    {
        if (_views.TryGetValue(elementId, out var view))
        {
            if (visible && !view.IsVisible)
            {
                view.IsVisible = true;
                Enter(view);
            }
            else if (!visible)
            {
                view.IsVisible = false;
            }
        }
    }

    /// <summary>Puts every element back to how the slide describes it: hidden ones hidden again.</summary>
    public void Reset()
    {
        foreach (var element in _slide.Elements)
        {
            if (_views.TryGetValue(element.Id, out var view))
            {
                view.IsVisible = !element.Hidden;
            }
        }
    }

    private static void Enter(Control view)
    {
        view.Opacity = 0;
        view.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(26px)");
        view.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(420), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(520), Easing = new CubicEaseOut() },
        ];

        Dispatcher.UIThread.Post(() =>
        {
            view.Opacity = 1;
            view.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Background);
    }

    private void FadeOut(Control view)
    {
        view.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(260) }];
        view.Opacity = 0;
        DispatcherTimer.RunOnce(() => _canvas.Children.Remove(view), TimeSpan.FromMilliseconds(300));
    }
}
