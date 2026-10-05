using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Intrf4ce.Plugins;

namespace Intrf4ce.Plugin.Weather;

/// <summary>
/// Temperature and the chance of rain over the next three days, drawn directly.
/// </summary>
/// <remarks>
/// From Kompas's TrendChart, sized for a slide and without its cursor. Seventy-two hours as
/// laid-out controls would be a few hundred elements to say one line and a row of bars. Rain is
/// bars from the baseline and temperature a line above them, because the two answer different
/// questions — "will I need a coat" reads off a curve, "will I get wet at three" off a column —
/// and one shared axis would leave neither legible.
/// </remarks>
internal sealed class TrendChart : Control
{
    private readonly PluginTheme _theme;
    private IReadOnlyList<HourPoint> _hours = [];
    private string _unit = "°";
    private int _selected = -1;

    public TrendChart(PluginTheme theme)
    {
        _theme = theme;
        MinHeight = theme.FontSize * 4;
        ClipToBounds = true;
    }

    private double Size => _theme.FontSize;

    public void Show(IReadOnlyList<HourPoint> hours, string unit)
    {
        _hours = hours;
        _unit = unit.StartsWith('°') ? "°" : unit;
        _selected = Math.Min(_selected, hours.Count - 1);
        InvalidateVisual();
    }

    /// <summary>How many hours it shows.</summary>
    public int Count => _hours.Count;

    /// <summary>The hour under the cursor, or null.</summary>
    public HourPoint? Selected => _selected >= 0 && _selected < _hours.Count ? _hours[_selected] : null;

    /// <summary>Puts the cursor on an hour, by index; -1 takes it away.</summary>
    public void Select(int index)
    {
        _selected = _hours.Count == 0 ? -1 : Math.Clamp(index, -1, _hours.Count - 1);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var temperatures = _hours.Where(h => h.Temperature is not null).Select(h => h.Temperature!.Value).ToList();
        if (_hours.Count < 2 || temperatures.Count == 0 || Bounds.Width <= 1 || Bounds.Height <= Size * 2)
        {
            return;
        }

        // Room under the plot for the day names, and to the right for the high and the low.
        var axis = Size * 1.1;
        var labels = Size * 2.2;
        var plotHeight = Bounds.Height - axis;
        var plotWidth = Bounds.Width - labels;
        var step = plotWidth / (_hours.Count - 1);

        var low = temperatures.Min();
        var high = temperatures.Max();
        var span = Math.Max(1.0, high - low);

        DrawRain(context, plotHeight, step);
        DrawDays(context, plotHeight, step);
        DrawTemperature(context, plotHeight, step, low, span);
        DrawSelection(context, plotHeight, step, low, span);

        var top = Label($"{Math.Round(high)}{_unit}", Size * 0.8, _theme.Text);
        var bottom = Label($"{Math.Round(low)}{_unit}", Size * 0.8, _theme.Muted);
        context.DrawText(top, new Point(plotWidth + (Size * 0.3), Y(high, plotHeight, low, span) - (top.Height / 2)));
        context.DrawText(bottom, new Point(plotWidth + (Size * 0.3), Y(low, plotHeight, low, span) - (bottom.Height / 2)));
    }

    /// <remarks>
    /// Translucent, so the line over them still reads; nothing at all for a dry hour, so a dry
    /// week does not look like drizzle; and three quarters of the plot at most, because a bar to
    /// the top reads as "the chart is broken" before it reads as "certain".
    /// </remarks>
    private void DrawRain(DrawingContext context, double plotHeight, double step)
    {
        var brush = _theme.Brush(_theme.Cool, 0.32);
        for (var i = 0; i < _hours.Count; i++)
        {
            if (_hours[i].PrecipitationChancePercent is { } chance && chance > 0)
            {
                var height = plotHeight * 0.75 * (chance / 100.0);
                context.FillRectangle(brush, new Rect((i * step) - (step / 2), plotHeight - height, Math.Max(1, step - 1), height));
            }
        }
    }

    /// <summary>A line at each midnight, the day's name after it; a name that would run into the last is left out.</summary>
    private void DrawDays(DrawingContext context, double plotHeight, double step)
    {
        var pen = new Pen(_theme.Brush(_theme.Line), Math.Max(1, Size * 0.05));
        var lastEnd = double.NegativeInfinity;

        for (var i = 0; i < _hours.Count; i++)
        {
            var midnight = i > 0 && _hours[i].Time.Date != _hours[i - 1].Time.Date;
            if (i > 0 && !midnight)
            {
                continue;
            }

            var x = i * step;
            if (midnight)
            {
                context.DrawLine(pen, new Point(x, 0), new Point(x, plotHeight));
            }

            var label = Label(i == 0 ? "now" : _hours[i].Time.ToString("ddd", CultureInfo.InvariantCulture), Size * 0.62, _theme.Muted);
            if (x + (Size * 0.2) >= lastEnd + (Size * 0.3))
            {
                context.DrawText(label, new Point(x + (Size * 0.2), plotHeight + (Size * 0.15)));
                lastEnd = x + (Size * 0.2) + label.Width;
            }
        }
    }

    private void DrawTemperature(DrawingContext context, double plotHeight, double step, double low, double span)
    {
        var line = new StreamGeometry();
        using (var draw = line.Open())
        {
            var started = false;
            for (var i = 0; i < _hours.Count; i++)
            {
                if (_hours[i].Temperature is not { } value)
                {
                    continue;
                }

                var point = new Point(i * step, Y(value, plotHeight, low, span));
                if (started)
                {
                    draw.LineTo(point);
                }
                else
                {
                    draw.BeginFigure(point, isFilled: false);
                    started = true;
                }
            }

            if (started)
            {
                draw.EndFigure(false);
            }
        }

        context.DrawGeometry(null, new Pen(_theme.Brush(_theme.Accent), Size * 0.11, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), line);
    }

    /// <summary>
    /// The cursor: a line the height of the plot and a dot on the curve. Both, as in Kompas,
    /// because either alone is ambiguous — the line says which hour, the dot which temperature.
    /// </summary>
    private void DrawSelection(DrawingContext context, double plotHeight, double step, double low, double span)
    {
        if (Selected is not { } hour)
        {
            return;
        }

        var x = _selected * step;
        context.DrawLine(new Pen(_theme.Brush(_theme.Cool), Size * 0.07), new Point(x, 0), new Point(x, plotHeight));
        if (hour.Temperature is { } value)
        {
            context.DrawEllipse(_theme.Brush(_theme.Cool), null, new Point(x, Y(value, plotHeight, low, span)), Size * 0.2, Size * 0.2);
        }
    }

    /// <summary>Inset, so the warmest and coldest hours are not cut by the edges.</summary>
    private double Y(double value, double plotHeight, double low, double span)
    {
        var inset = Size * 0.5;
        return inset + ((plotHeight - (inset * 2)) * (1 - ((value - low) / span)));
    }

    private FormattedText Label(string text, double size, Color color) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(_theme.Font), size, _theme.Brush(color));
}
