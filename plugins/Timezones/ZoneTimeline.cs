using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Intrf4ce.Plugins;

namespace Intrf4ce.Plugin.Timezones;

/// <summary>
/// A day of hours, with one band per place.
/// </summary>
/// <remarks>
/// <para>
/// From Kompas's ZoneTimeline, sized for a slide instead of a dashboard panel. The whole thing is
/// drawn rather than laid out, because the only thing that matters is that every band shares
/// one axis: a column of labels beside a drawn strip would drift by a pixel at some size, and the
/// alignment is the entire point.
/// </para>
/// <para>
/// Each hour is shaded by what it means there — asleep, early, working, evening — because "is it
/// a reasonable time to call Zagreb" is the question, and reading it off two clocks and
/// subtracting is what this replaces. Four shades, not a gradient: the question is categorical,
/// and a smooth ramp makes 07:00 and 08:00 alike exactly where the answer changes.
/// </para>
/// </remarks>
internal sealed class ZoneTimeline : Control
{
    private readonly PluginTheme _theme;
    private IReadOnlyList<ZoneReading> _zones = [];
    private DateTimeOffset _now;
    private DateTimeOffset? _cursor;
    private bool _scrubbing;
    private int _highlighted = -1;

    public ZoneTimeline(PluginTheme theme)
    {
        _theme = theme;
        ClipToBounds = true;
    }

    private double Size => _theme.FontSize;

    private double LabelWidth => Math.Clamp(Bounds.Width * 0.2, Size * 5.5, Size * 9);

    private double AxisHeight => Size * 1.3;

    private double BandHeight => _zones.Count == 0 ? 0 : Math.Min((Bounds.Height - AxisHeight) / _zones.Count, Size * 4);

    /// <param name="cursor">A moment to mark with a dashed line: the agent's, or the user's with J and K.</param>
    /// <param name="scrubbing">The user is moving it: the clocks show its time, and the day drawn is its day.</param>
    public void Show(IReadOnlyList<ZoneReading> zones, DateTimeOffset now, DateTimeOffset? cursor, bool scrubbing, int highlighted)
    {
        _zones = zones;
        _now = now;
        _cursor = cursor;
        _scrubbing = scrubbing && cursor is not null;
        _highlighted = highlighted;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Where a place's band is, for the stage's light when it is focused.</summary>
    public Rect Band(int index) => new(0, index * BandHeight, Bounds.Width, BandHeight);

    /// <summary>As wide as it is given, and as tall as its bands want, up to what it is given.</summary>
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? Size * 40 : availableSize.Width,
        Math.Min(availableSize.Height, (Math.Max(1, _zones.Count) * Size * 4) + AxisHeight));

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_zones.Count == 0 || Bounds.Width <= LabelWidth + 40 || BandHeight <= 0)
        {
            return;
        }

        var left = LabelWidth;
        var hourWidth = (Bounds.Width - left) / 24;

        // The window is home's day, midnight to midnight — the day of the cursor while the user moves
        // it, so it can be taken into tomorrow. Every other band is the same twenty-four hours seen
        // from somewhere else, which makes the offsets legible as a shift rather than as four
        // unrelated clocks.
        var origin = Midnight(_zones[0], _scrubbing ? _cursor!.Value : _now);

        DrawHourAxis(context, left, hourWidth, origin);
        for (var i = 0; i < _zones.Count; i++)
        {
            DrawBand(context, i, left, hourWidth, origin);
        }

        DrawMarker(context, left, hourWidth, origin, _now, _theme.Accent, dashed: false);
        if (_cursor is { } cursor)
        {
            DrawMarker(context, left, hourWidth, origin, cursor, _theme.Cool, dashed: true);
        }
    }

    /// <summary>
    /// Midnight at home on the day of <paramref name="instant"/> — with the offset it had at
    /// midnight, not then: on the day the clocks change they differ, and the whole timeline would
    /// start an hour out.
    /// </summary>
    private static DateTimeOffset Midnight(ZoneReading home, DateTimeOffset instant)
    {
        var midnight = DateTime.SpecifyKind(home.At(instant).Date, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(home.ZoneId);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    private void DrawHourAxis(DrawingContext context, double left, double hourWidth, DateTimeOffset origin)
    {
        var bottom = _zones.Count * BandHeight;
        var pen = new Pen(_theme.Brush(_theme.Line), 1);

        for (var hour = 0; hour <= 24; hour += 3)
        {
            var x = left + (hour * hourWidth);
            context.DrawLine(pen, new Point(x, 0), new Point(x, bottom));

            if (hour < 24)
            {
                var label = Text(origin.AddHours(hour).ToString("HH", CultureInfo.InvariantCulture), Size * 0.62, _theme.Muted);
                context.DrawText(label, new Point(x + (Size * 0.15), bottom + (Size * 0.2)));
            }
        }
    }

    private void DrawBand(DrawingContext context, int index, double left, double hourWidth, DateTimeOffset origin)
    {
        var zone = _zones[index];
        var top = index * BandHeight;
        var home = _zones[0];

        if (index == _highlighted)
        {
            context.FillRectangle(_theme.Brush(_theme.Surface2), new Rect(0, top, Bounds.Width, BandHeight));
        }

        // The label column: the name, and the time it is there now — with the day when it is not
        // home's, because "Tuesday 9am here" being Monday somewhere else is the thing people miss.
        var name = Text(zone.Label, Size * 0.82, index == _highlighted ? _theme.Text : _theme.Muted);
        context.DrawText(name, new Point(Size * 0.3, top + (BandHeight * 0.12)));

        if (!zone.IsValid)
        {
            context.DrawText(Text("unknown zone", Size * 0.7, _theme.Danger), new Point(Size * 0.3, top + (BandHeight * 0.5)));
            return;
        }

        // While the user moves the cursor, the clocks are its time — "what time is the 3pm call
        // there" belongs beside the name, not in a legend — and in its colour, so they read as then.
        var shown = _scrubbing ? _cursor!.Value : _now;
        var local = zone.At(shown);
        var clock = Text(local.ToString("HH:mm", CultureInfo.InvariantCulture), Size * 1.05, _scrubbing ? _theme.Cool : _theme.Text, bold: true);
        var clockAt = new Point(Size * 0.3, top + (BandHeight * 0.12) + name.Height);
        context.DrawText(clock, clockAt);

        if (local.Date != home.At(shown).Date)
        {
            var day = Text(local.ToString("ddd", CultureInfo.InvariantCulture), Size * 0.62, _theme.Accent);
            context.DrawText(day, new Point(clockAt.X + clock.Width + (Size * 0.25), clockAt.Y + (clock.Height - day.Height) * 0.7));
        }

        var cellTop = top + (BandHeight * 0.14);
        var cellHeight = BandHeight * 0.72;
        for (var hour = 0; hour < 24; hour++)
        {
            var at = zone.At(origin.AddHours(hour));
            var cell = new Rect(left + (hour * hourWidth) + 1, cellTop, hourWidth - 2, cellHeight);
            context.FillRectangle(Shade(at.Hour), cell, (float)(Size * 0.12));

            // Local midnight is where the date rolls over there: the one boundary worth seeing.
            if (at.Hour == 0)
            {
                context.DrawLine(new Pen(_theme.Brush(_theme.Text, 0.8), 2), new Point(cell.X - 1, cellTop - 3), new Point(cell.X - 1, cellTop + cellHeight + 3));
            }
        }
    }

    private IBrush Shade(int hour) => hour switch
    {
        >= 9 and < 18 => _theme.Brush(_theme.Success, 0.5),
        >= 7 and < 9 => _theme.Brush(_theme.Accent, 0.38),
        >= 18 and < 22 => _theme.Brush(_theme.Accent, 0.26),
        _ => _theme.Brush(_theme.Line, 0.75),
    };

    private void DrawMarker(DrawingContext context, double left, double hourWidth, DateTimeOffset origin, DateTimeOffset at, Color color, bool dashed)
    {
        var hours = (at - origin).TotalHours;
        if (hours < 0 || hours > 24)
        {
            return;
        }

        var x = left + (hours * hourWidth);
        var pen = new Pen(_theme.Brush(color), Size * 0.12, dashed ? new DashStyle([3, 2], 0) : null, PenLineCap.Round);
        context.DrawLine(pen, new Point(x, 0), new Point(x, _zones.Count * BandHeight));
    }

    private FormattedText Text(string text, double size, Color color, bool bold = false) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(_theme.Font, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
        size,
        _theme.Brush(color));
}
