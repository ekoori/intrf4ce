using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// One place on a map: a dot where it is, and its name beside it.
/// </summary>
/// <remarks>
/// Drawn rather than composed, because the dot and the line to it hang outside the label's own
/// box and have to point at a coordinate rather than fill a rectangle. Lit is the focused
/// state — the same warm accent the rest of the app uses for "look here".
/// </remarks>
public sealed class MapMarker : Control
{
    private const double Radius = 9;
    private const double PadX = 9;
    private const double PadY = 4;
    private const double MaxLabel = 240;

    private static readonly IBrush Paper = new SolidColorBrush(Color.FromArgb(232, 0xF7, 0xF5, 0xF0));
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x28));
    private static readonly IBrush Edge = new SolidColorBrush(Color.FromArgb(120, 0x1A, 0x20, 0x28));

    private readonly Skin _skin;
    private readonly string _label;
    private readonly int _number;
    private readonly bool _numbered;
    private Point _at;
    private bool _lit;

    public MapMarker(Skin skin, string label, int number, bool numbered)
    {
        ArgumentNullException.ThrowIfNull(skin);

        _skin = skin;
        _label = label;
        _number = number;
        _numbered = numbered;
        ClipToBounds = false;
    }

    /// <summary>This is the place being talked about.</summary>
    public bool Lit
    {
        get => _lit;
        set
        {
            if (_lit == value)
            {
                return;
            }

            _lit = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Where the place itself is, in this marker's own coordinates.</summary>
    public void PointsAt(Point at)
    {
        _at = at;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var text = Text();
        var box = new RoundedRect(new Rect(Bounds.Size), 7);
        context.DrawRectangle(_lit ? _skin.Brush(_skin.Accent) : Paper, new Pen(_lit ? _skin.Brush(_skin.Ink, 0.7) : Edge, 1.5), box);
        context.DrawText(text, new Point(PadX, PadY));

        // From the edge of the label to the dot, so a label nudged aside still says which
        // place it names.
        var edge = new Point(Math.Clamp(_at.X, 0, Bounds.Width), Math.Clamp(_at.Y, 0, Bounds.Height));
        if (new Vector(edge.X - _at.X, edge.Y - _at.Y).Length > 2)
        {
            context.DrawLine(new Pen(_lit ? _skin.Brush(_skin.Accent) : _skin.Brush(_skin.Text, 0.85), 2), edge, _at);
        }

        context.DrawEllipse(
            _lit ? _skin.Brush(_skin.Accent) : _skin.Brush(_skin.Text, 0.95),
            new Pen(_skin.Brush(_skin.Ink, 0.8), 2),
            _at,
            Radius,
            Radius);

        if (!_numbered)
        {
            return;
        }

        var number = new FormattedText(
            _number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(_skin.UiFont, weight: FontWeight.Bold),
            13,
            _skin.Brush(_skin.Ink));

        context.DrawText(number, new Point(_at.X - (number.Width / 2), _at.Y - (number.Height / 2)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Text();
        return new Size(text.Width + (2 * PadX), text.Height + (2 * PadY));
    }

    private FormattedText Text() =>
        new(
            _label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(_skin.UiFont, weight: FontWeight.SemiBold),
            _lit ? 19 : 17,
            _lit ? _skin.Brush(_skin.Ink) : Ink)
        {
            MaxTextWidth = MaxLabel,
        };
}
