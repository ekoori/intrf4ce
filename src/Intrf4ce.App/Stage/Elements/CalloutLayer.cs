using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// Callout labels, leaders and anchors over an image, drawn rather than composed.
/// </summary>
/// <remarks>
/// Leaders have a dark halo under a light line so they read on any photograph, the way
/// technical illustrations do it. Only the labels and anchors take the pointer; the picture
/// underneath stays clickable everywhere else.
/// </remarks>
public sealed class CalloutLayer : Control, ICustomHitTest
{
    private const double LabelSize = 22;
    private const double PadX = 12;
    private const double PadY = 6;
    private const double DetailWidth = 380;

    private readonly Skin _theme;
    private readonly Typeface _typeface;
    private readonly Typeface _detailFace;
    private IReadOnlyList<Callout> _callouts = [];
    private readonly Dictionary<string, FormattedText> _texts = new(StringComparer.Ordinal);
    private IReadOnlyList<CalloutPlacement> _placements = [];
    private Rect _image;
    private string? _highlight;
    private string? _hover;

    public CalloutLayer(Skin theme)
    {
        _theme = theme;
        _typeface = new Typeface(theme.UiFont, FontStyle.Normal, FontWeight.SemiBold);
        _detailFace = new Typeface(theme.UiFont);
        ClipToBounds = false;
    }

    public void Set(IReadOnlyList<Callout> callouts)
    {
        _callouts = callouts;
        _texts.Clear();
        foreach (var callout in callouts)
        {
            _texts[callout.Id] = new FormattedText(
                callout.Label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _typeface,
                LabelSize,
                _theme.Brush(_theme.Text));
        }

        InvalidateVisual();
    }

    public void Highlight(string? calloutId)
    {
        _highlight = calloutId;
        InvalidateVisual();
    }

    /// <summary>Label sizes and anchors against a provisional image box, to size the gutters.</summary>
    public IReadOnlyList<CalloutInput> Inputs(Box bounds) => [.. _callouts.Select(c => Input(c, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height)))];

    public void Place(Rect image, Box bounds)
    {
        _image = image;
        var inputs = _callouts.Select(c => Input(c, image)).ToList();
        _placements = CalloutLayout.Arrange(new Box(image.X, image.Y, image.Width, image.Height), bounds, inputs).Placements;
        InvalidateVisual();
    }

    public Rect? LabelOf(string id) =>
        _placements.FirstOrDefault(p => p.Id == id) is { } p ? new Rect(p.Label.X, p.Label.Y, p.Label.Width, p.Label.Height) : null;

    public bool HitTest(Point point) => Find(point) is not null;

    /// <summary>The callout whose label or anchor is at <paramref name="point"/>, for clicks the stage passes on.</summary>
    public string? IdAt(Point point) => Find(point);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = Find(e.GetPosition(this));
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_placements.Count == 0 || _image.Width <= 0)
        {
            return;
        }

        var halo = new Pen(new SolidColorBrush(Colors.Black, 0.55), 4.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var byId = _callouts.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var dimmed = _highlight is not null;

        foreach (var placement in _placements)
        {
            var active = placement.Id == _highlight || placement.Id == _hover;
            var opacity = dimmed && !active ? 0.35 : 1.0;
            var color = active ? _theme.Accent : _theme.Text;
            var line = new Pen(new SolidColorBrush(color, 0.95 * opacity), active ? 2.4 : 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

            var points = placement.Leader.Select(p => new Point(p.X, p.Y)).ToArray();
            var geometry = new PolylineGeometry(points, false);

            using (context.PushOpacity(opacity))
            {
                context.DrawGeometry(null, halo, geometry);
            }

            context.DrawGeometry(null, line, geometry);

            var callout = byId[placement.Id];
            if (callout.Box is [var bx, var by, var bw, var bh])
            {
                var area = new Rect(_image.X + (bx * _image.Width), _image.Y + (by * _image.Height), bw * _image.Width, bh * _image.Height);
                context.DrawRectangle(new SolidColorBrush(color, 0.10 * opacity), halo, area, 8, 8);
                context.DrawRectangle(null, line, area, 8, 8);
            }
            else
            {
                var anchor = points[^1];
                context.DrawEllipse(new SolidColorBrush(Colors.Black, 0.6 * opacity), null, anchor, 8, 8);
                context.DrawEllipse(new SolidColorBrush(active ? _theme.Accent : _theme.Accent, opacity), null, anchor, active ? 6.5 : 5, active ? 6.5 : 5);
            }

            DrawLabel(context, placement, active, opacity);
        }

        // Detail last, so it sits over every other label.
        var detailFor = _hover ?? _highlight;
        if (detailFor is not null && byId.TryGetValue(detailFor, out var focused) && !string.IsNullOrWhiteSpace(focused.Detail))
        {
            DrawDetail(context, _placements.First(p => p.Id == detailFor), focused.Detail!);
        }
    }

    private void DrawLabel(DrawingContext context, CalloutPlacement placement, bool active, double opacity)
    {
        var text = _texts[placement.Id];
        var box = new Rect(placement.Label.X, placement.Label.Y, placement.Label.Width, placement.Label.Height);
        var background = active ? _theme.Accent : _theme.Surface2;

        using (context.PushOpacity(opacity))
        {
            context.DrawRectangle(new SolidColorBrush(background, active ? 1 : 0.92), new Pen(_theme.Brush(active ? _theme.Accent : _theme.Line), 1.2), box, 8, 8);
        }

        text.SetForegroundBrush(active ? new SolidColorBrush(_theme.Ink) : _theme.Brush(_theme.Text, opacity));
        context.DrawText(text, new Point(box.X + PadX, box.Y + PadY));
    }

    private void DrawDetail(DrawingContext context, CalloutPlacement placement, string detail)
    {
        var text = new FormattedText(detail, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _detailFace, 19, _theme.Brush(_theme.Text))
        {
            MaxTextWidth = DetailWidth - 28,
        };

        var label = placement.Label;
        var width = Math.Min(DetailWidth, text.Width + 28);
        var height = text.Height + 22;
        var x = placement.Side == "left" ? label.X : label.Right - width;
        var y = label.Bottom + 8;
        if (y + height > Bounds.Height)
        {
            y = label.Y - height - 8;
        }

        x = Math.Clamp(x, 0, Math.Max(0, Bounds.Width - width));
        var box = new Rect(x, Math.Max(0, y), width, height);

        context.DrawRectangle(new SolidColorBrush(_theme.Ink, 0.94), new Pen(_theme.Brush(_theme.Accent, 0.7), 1.2), box, 10, 10);
        context.DrawText(text, new Point(box.X + 14, box.Y + 11));
    }

    private string? Find(Point point)
    {
        foreach (var placement in _placements)
        {
            var label = new Rect(placement.Label.X, placement.Label.Y, placement.Label.Width, placement.Label.Height);
            if (label.Inflate(4).Contains(point))
            {
                return placement.Id;
            }

            var anchor = placement.Leader[^1];
            if (Math.Abs(anchor.X - point.X) < 12 && Math.Abs(anchor.Y - point.Y) < 12)
            {
                return placement.Id;
            }
        }

        return null;
    }

    private CalloutInput Input(Callout callout, Rect image)
    {
        var text = _texts[callout.Id];
        var size = (text.Width + (2 * PadX), text.Height + (2 * PadY));

        Box? area = callout.Box is [var bx, var by, var bw, var bh]
            ? new Box(image.X + (bx * image.Width), image.Y + (by * image.Height), bw * image.Width, bh * image.Height)
            : null;

        var anchor = callout.At is [var ax, var ay]
            ? new Pt(image.X + (ax * image.Width), image.Y + (ay * image.Height))
            : area?.Center ?? new Pt(image.X, image.Y);

        return new CalloutInput(callout.Id, anchor, area, size, callout.Side is "left" or "right" ? callout.Side : null);
    }
}
