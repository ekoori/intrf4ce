using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Intrf4ce.App.Render;
using Intrf4ce.App.Stage.Elements;

namespace Intrf4ce.App.Stage;

/// <summary>A question about part of a picture: the element it is on, and where, as fractions.</summary>
public sealed record ImageQuestion(string SlideId, string ElementId, string Label, double[]? Region);

/// <summary>
/// One picture, filling the window, with a box the user can draw on it.
/// </summary>
/// <remarks>
/// A slide gives a picture the room the slide can spare. Double-clicking gives it the room the
/// window has, which is what someone leaning in to look at a detail wants; and once they can
/// see the detail, the obvious next thing is to ask about it. Dragging a box and asking sends
/// the agent the region in the same coordinates it writes callouts in — fractions of the
/// picture — so it can zoom the slide there, read that part of the image, or answer about it.
/// <para>
/// It closes on Escape or a click on the surround, the way a lightbox should: nothing here is
/// a mode the user can get stuck in.
/// </para>
/// </remarks>
public sealed class ImageInspector : Panel
{
    private readonly Border _backdrop;
    private readonly Image _bitmap = new() { Stretch = Stretch.Fill };
    private readonly SvgPictureControl _vector = new() { MaxScale = 100 };
    private readonly Border _box;
    private readonly Border _caption;
    private readonly TextBlock _captionText;
    private readonly TextBlock _hint;
    private Size _natural = new(16, 9);
    private Rect _frame;
    private Rect? _selection;
    private Point? _from;
    private ImageView? _showing;

    public ImageInspector(Skin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);

        IsVisible = false;
        Focusable = true;

        _backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(242, 0x06, 0x09, 0x0D)) };

        _box = new Border
        {
            IsVisible = false,
            BorderBrush = skin.Brush(skin.Accent),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Background = skin.Brush(skin.Accent, 0.12),
            IsHitTestVisible = false,
        };

        _captionText = new TextBlock
        {
            FontSize = 15,
            Foreground = skin.Brush(skin.Text, 0.9),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 900,
            TextAlignment = TextAlignment.Center,
        };

        _caption = new Border
        {
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(10),
            Background = skin.Brush(skin.Surface, 0.8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 0, 0),
            Child = _captionText,
            IsHitTestVisible = false,
        };

        _hint = new TextBlock
        {
            FontSize = 13,
            Foreground = skin.Brush(skin.Faint),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 22),
            IsHitTestVisible = false,
        };

        Children.Add(_backdrop);
        Children.Add(_bitmap);
        Children.Add(_vector);
        Children.Add(_box);
        Children.Add(_caption);
        Children.Add(_hint);

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The user asked about the picture, or about the box they drew on it.</summary>
    public event EventHandler<ImageQuestion>? Asked;

    /// <summary>Which picture is open, if any.</summary>
    public ImageView? Showing => _showing;

    /// <summary>Fills the window with <paramref name="view"/>'s picture.</summary>
    public void Show(ImageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        _showing = view;
        _selection = null;
        _box.IsVisible = false;
        _from = null;

        _bitmap.Source = view.Picture;
        _bitmap.IsVisible = view.Picture is not null;
        _vector.Picture = view.Vector;
        _vector.IsVisible = view.Vector is not null;
        _natural = view.Picture is Bitmap map && map.Size.Width > 0
            ? map.Size
            : view.ImageRect.Width > 0 ? view.ImageRect.Size : new Size(16, 9);

        _captionText.Text = view.Describe(null);
        _caption.IsVisible = _captionText.Text is { Length: > 0 };
        _hint.Text = "Drag a box over anything, then right-click to ask about it  ·  Esc to close";

        IsVisible = true;
        Focus();
        InvalidateArrange();
    }

    public void Close()
    {
        IsVisible = false;
        _showing = null;
        _selection = null;
        _box.IsVisible = false;
        _bitmap.Source = null;
        _vector.Picture = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Closed;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        // Outside the picture is "away": a lightbox closes when you click off it.
        if (!_frame.Contains(point))
        {
            Close();
            e.Handled = true;
            return;
        }

        _from = point;
        _selection = null;
        _box.IsVisible = false;
        _hint.Text = "Let go, then right-click the box to ask about it  ·  Esc to close";
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_from is not { } start)
        {
            return;
        }

        var now = Clamp(e.GetPosition(this));
        var box = new Rect(start, now);
        if (box.Width < 6 || box.Height < 6)
        {
            return;
        }

        _selection = box;
        _box.IsVisible = true;
        InvalidateArrange();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_from is null)
        {
            return;
        }

        _from = null;
        e.Pointer.Capture(null);
        _hint.Text = _selection is null
            ? "Drag a box over anything, then right-click to ask about it  ·  Esc to close"
            : "Right-click the box to ask about it  ·  drag again to move it  ·  Esc to close";
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // The picture already has the whole window; the wheel must not scroll the stage behind.
        e.Handled = true;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _backdrop.Arrange(new Rect(finalSize));

        // Room for the caption above and the hint below, and never edge to edge.
        var area = new Rect(finalSize).Deflate(new Thickness(48, 74, 48, 64));
        var aspect = _natural.Width > 0 && _natural.Height > 0 ? _natural.Width / _natural.Height : 16.0 / 9;
        var width = Math.Min(area.Width, area.Height * aspect);
        var height = width / aspect;
        _frame = new Rect(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), Math.Max(1, width), Math.Max(1, height));

        _bitmap.Arrange(_frame);
        _vector.Arrange(_frame);
        _caption.Arrange(new Rect(finalSize));
        _hint.Arrange(new Rect(finalSize));
        _box.Arrange(_selection ?? new Rect(0, 0, 0, 0));

        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(availableSize);
        }

        return availableSize;
    }

    /// <summary>What a question would be about: the box drawn on the picture, or all of it.</summary>
    public ImageQuestion? Question()
    {
        if (_showing is not { } view)
        {
            return null;
        }

        if (_selection is not { } box || _frame.Width <= 0)
        {
            return new ImageQuestion(view.SlideId, view.Id, view.Describe(null), null);
        }

        var region = new[]
        {
            (box.X - _frame.X) / _frame.Width,
            (box.Y - _frame.Y) / _frame.Height,
            box.Width / _frame.Width,
            box.Height / _frame.Height,
        };

        return new ImageQuestion(view.SlideId, view.Id, "the marked area of " + view.Describe(null), region);
    }

    /// <summary>Raises <see cref="Asked"/> for whatever is marked, and closes.</summary>
    public void Ask()
    {
        if (Question() is { } question)
        {
            Asked?.Invoke(this, question);
        }

        Close();
    }

    private Point Clamp(Point point) =>
        new(Math.Clamp(point.X, _frame.X, _frame.Right), Math.Clamp(point.Y, _frame.Y, _frame.Bottom));
}
