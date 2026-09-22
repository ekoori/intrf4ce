using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private bool _free;
    private Point? _pressed;
    private CameraView _pressedView;
    private bool _dragging;

    public StageView(Skin skin)
    {
        ClipToBounds = true;
        _world.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        Spotlight = new SpotlightOverlay(skin);
        Children.Add(_world);
        Children.Add(Spotlight);
    }

    /// <summary>Dims everything but the focused part; drawn over the world, under the HUD.</summary>
    public SpotlightOverlay Spotlight { get; }

    /// <summary>Multiplies animation durations; 0 disables them.</summary>
    public double AnimationSpeed { get; set; } = 1;

    /// <summary>Screen space the HUD covers; slides are fitted into what is left.</summary>
    public Thickness SafeArea { get; set; } = new(24, 64, 24, 176);

    public CameraView View => _view;

    public bool IsMoving => _path is not null;

    /// <summary>The camera finished moving.</summary>
    public event EventHandler? Arrived;

    /// <summary>A click that was not a drag, at this point in the world.</summary>
    public event EventHandler<Point>? Clicked;

    /// <summary>The user moved the camera by hand (wheel or drag).</summary>
    public event EventHandler? MovedByHand;

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
        _free = false;

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

    public Point ScreenToWorld(Point screen) => _view.ToMatrix(Bounds.Size).Invert().Transform(screen);

    /// <summary>
    /// Zooms by <paramref name="factor"/> (below 1 zooms in) keeping the world point under
    /// <paramref name="screen"/> where it is, the way maps zoom.
    /// </summary>
    public void ZoomAt(Point screen, double factor)
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        _path = null;
        var world = ScreenToWorld(screen);
        var width = Math.Clamp(_view.Width * factor, SlideLayout.Width * 0.06, SlideLayout.Width * Columns * 2.5);
        var scale = Bounds.Width / width;
        var center = new Point(
            world.X - ((screen.X - (Bounds.Width / 2)) / scale),
            world.Y - ((screen.Y - (Bounds.Height / 2)) / scale));

        _free = true;
        Apply(new CameraView(center, width));
        MovedByHand?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled)
        {
            return;
        }

        ZoomAt(e.GetPosition(this), Math.Pow(0.87, e.Delta.Y));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressed = e.GetPosition(this);
        _pressedView = _view;
        _dragging = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressed is not { } start)
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (!_dragging && Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y)) < 5)
        {
            return;
        }

        _dragging = true;
        _path = null;
        _free = true;
        var scale = Bounds.Width / _pressedView.Width;
        Apply(new CameraView(_pressedView.Center - (delta / scale), _pressedView.Width));
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is not { } start)
        {
            return;
        }

        _pressed = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;

        if (_dragging)
        {
            MovedByHand?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Clicked?.Invoke(this, ScreenToWorld(start));
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _world.Arrange(new Rect(0, 0, 1, 1));
        Spotlight.Arrange(new Rect(finalSize));

        // A resize keeps the same thing in view rather than the same zoom; a view the user
        // steered by hand keeps its centre and zoom.
        if (_path is null && !_free && finalSize.Width > 0 && finalSize.Height > 0)
        {
            Apply(CameraView.Fit(_target, finalSize, SafeArea, _targetMargin), finalSize);
        }
        else if (_free && finalSize.Width > 0)
        {
            Apply(_view, finalSize);
        }

        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _world.Measure(Size.Infinity);
        Spotlight.Measure(availableSize);
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
        var matrix = view.ToMatrix(size ?? Bounds.Size);
        _world.RenderTransform = new MatrixTransform(matrix);
        Spotlight.Follow(matrix);
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow((-2 * t) + 2, 3) / 2);
}
