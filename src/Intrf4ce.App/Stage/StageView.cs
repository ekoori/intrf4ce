using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Intrf4ce.Core.Layout;

namespace Intrf4ce.App.Stage;

/// <summary>
/// The world every slide lives on, and the camera looking at it.
/// </summary>
/// <remarks>
/// Slides sit on one large canvas in reading order, five to a row. Showing a slide moves the
/// camera, not the slide, which is what makes overview mode (zoom out to see the whole deck
/// growing) the same operation as any other navigation. The camera is a single matrix on the
/// world, animated on the render loop.
/// </remarks>
public sealed class StageView : Panel
{
    public const int Columns = 5;
    public const double GapX = 240;
    public const double GapY = 200;

    private readonly Canvas _world = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CameraView _view = new(new Point(SlideLayout.Width / 2, SlideLayout.Height / 2), SlideLayout.Width * 1.2);
    private Rect _target = new(0, 0, SlideLayout.Width, SlideLayout.Height);
    private double _targetMargin = 0.035;
    private CameraPath? _path;
    private TimeSpan _pathStart;
    private TimeSpan _pathDuration;
    private bool _frameRequested;

    public StageView()
    {
        ClipToBounds = true;
        _world.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        Children.Add(_world);
    }

    /// <summary>Multiplies animation durations; 0 disables them.</summary>
    public double AnimationSpeed { get; set; } = 1;

    /// <summary>Screen space the HUD covers; slides are fitted into what is left.</summary>
    public Thickness SafeArea { get; set; } = new(24, 64, 24, 118);

    public CameraView View => _view;

    public bool IsMoving => _path is not null;

    /// <summary>The camera finished moving.</summary>
    public event EventHandler? Arrived;

    public static Rect SlideRect(int index)
    {
        var column = index % Columns;
        var row = index / Columns;
        return new Rect(column * (SlideLayout.Width + GapX), row * (SlideLayout.Height + GapY), SlideLayout.Width, SlideLayout.Height);
    }

    /// <summary>The rectangle enclosing the first <paramref name="count"/> slides.</summary>
    public static Rect DeckRect(int count)
    {
        if (count <= 0)
        {
            return SlideRect(0);
        }

        var last = SlideRect(count - 1);
        var columns = Math.Min(count, Columns);
        var width = (columns * SlideLayout.Width) + ((columns - 1) * GapX);
        return new Rect(0, 0, width, last.Bottom);
    }

    public void Add(Control slide) => _world.Children.Add(slide);

    public void Remove(Control slide) => _world.Children.Remove(slide);

    public void Clear() => _world.Children.Clear();

    /// <summary>Moves a slide to its place in the world for <paramref name="index"/>.</summary>
    public static void Place(Control slide, int index)
    {
        var rect = SlideRect(index);
        Canvas.SetLeft(slide, rect.X);
        Canvas.SetTop(slide, rect.Y);
    }

    /// <summary>Flies the camera to show <paramref name="target"/> (in world units) whole.</summary>
    public void FlyTo(Rect target, bool animate = true, double margin = 0.035)
    {
        _target = target;
        _targetMargin = margin;

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var destination = CameraView.Fit(target, Bounds.Size, SafeArea, margin);

        if (!animate || AnimationSpeed <= 0)
        {
            _path = null;
            Apply(destination);
            Arrived?.Invoke(this, EventArgs.Empty);
            return;
        }

        _path = new CameraPath(_view, destination);
        _pathStart = _clock.Elapsed;
        _pathDuration = _path.Duration(AnimationSpeed, TimeSpan.FromMilliseconds(380), TimeSpan.FromMilliseconds(1700));
        RequestFrame();
    }

    /// <summary>Where a point in the world is on screen right now.</summary>
    public Point WorldToScreen(Point world) => _view.ToMatrix(Bounds.Size).Transform(world);

    protected override Size ArrangeOverride(Size finalSize)
    {
        _world.Arrange(new Rect(0, 0, 1, 1));

        // A resize keeps the same thing in view rather than the same zoom.
        if (_path is null && finalSize.Width > 0 && finalSize.Height > 0)
        {
            Apply(CameraView.Fit(_target, finalSize, SafeArea, _targetMargin), finalSize);
        }

        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _world.Measure(Size.Infinity);
        return availableSize;
    }

    private void RequestFrame()
    {
        if (_frameRequested)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            Frame();
        });
    }

    private void Frame()
    {
        if (_path is not { } path)
        {
            return;
        }

        var t = (_clock.Elapsed - _pathStart).TotalMilliseconds / Math.Max(1, _pathDuration.TotalMilliseconds);
        var eased = t >= 1 ? 1 : EaseInOut(t);

        // Re-aim at the live target on the last frame, in case the window was resized mid-flight.
        var view = t >= 1 ? CameraView.Fit(_target, Bounds.Size, SafeArea, _targetMargin) : path.At(eased);
        Apply(view);

        if (t >= 1)
        {
            _path = null;
            Arrived?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            RequestFrame();
        }
    }

    private void Apply(CameraView view, Size? size = null)
    {
        _view = view;
        _world.RenderTransform = new MatrixTransform(view.ToMatrix(size ?? Bounds.Size));
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow((-2 * t) + 2, 3) / 2);
}
