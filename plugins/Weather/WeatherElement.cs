using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Intrf4ce.Plugins;

namespace Intrf4ce.Plugin.Weather;

/// <summary>
/// The weather somewhere, on a slide: now, the next days, and the trend, kept current.
/// </summary>
/// <remarks>
/// <para>
/// Kompas's Weather and Today panels made one: what it is doing now, a strip of days, the
/// seventy-two hours as a line and bars, and the UV peak and the light. <c>show</c> trims it to
/// one of those for a slide that has room for less.
/// </para>
/// <para>
/// Refetched every quarter of an hour while the slide is up, through a cache shared with every
/// other weather element and the tool. A refresh that fails leaves the last forecast up and says
/// how old it is, rather than replacing a good slide with an error.
/// </para>
/// </remarks>
internal sealed class WeatherElement : LiveElement
{
    private static readonly FontFamily Emoji = new("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji");
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    private readonly ElementHost _host;
    private readonly Grid _view;
    private readonly Control _now;
    private readonly TextBlock _glyph;
    private readonly TextBlock _temperature;
    private readonly TextBlock _condition;
    private readonly TextBlock _place;
    private readonly Grid _days;
    private readonly TrendChart _trend;
    private readonly TextBlock _footer;
    private readonly DispatcherTimer _refresh;
    private readonly List<(string Part, Border Column)> _columns = [];
    private JsonElement _fields;
    private WeatherRequest? _request;
    private WeatherReading? _reading;
    private DateTimeOffset _fetched;
    private string? _stale;
    private string? _highlighted;

    // The cursor J and K move: an hour along the trend when it is shown, otherwise a day along
    // the strip; -1 for none.
    private int _hour = -1;
    private int _day = -1;

    public WeatherElement(ElementHost host)
    {
        _host = host;
        var theme = host.Theme;
        var size = theme.FontSize;

        _glyph = new TextBlock { FontFamily = Emoji, FontSize = size * 2.6, VerticalAlignment = VerticalAlignment.Center };
        _temperature = Text(size * 2.6, theme.Text, FontWeight.SemiBold);
        _condition = Text(size * 0.85, theme.Muted);
        _place = Text(size * 0.8, theme.Muted);
        _place.HorizontalAlignment = HorizontalAlignment.Right;
        _place.VerticalAlignment = VerticalAlignment.Top;
        _place.TextAlignment = TextAlignment.Right;

        var reading = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _temperature, _condition },
        };

        var now = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(reading, 1);
        Grid.SetColumn(_place, 2);
        reading.Margin = new Thickness(size * 0.5, 0, 0, 0);
        now.Children.Add(_glyph);
        now.Children.Add(reading);
        now.Children.Add(_place);
        _now = now;

        _days = new Grid { Margin = new Thickness(0, size * 0.6, 0, 0) };
        _trend = new TrendChart(theme) { Margin = new Thickness(0, size * 0.8, 0, 0) };
        _footer = Text(size * 0.68, theme.Muted);
        _footer.Margin = new Thickness(0, size * 0.6, 0, 0);
        _footer.TextWrapping = TextWrapping.Wrap;

        Grid.SetRow(_days, 1);
        Grid.SetRow(_trend, 2);
        Grid.SetRow(_footer, 3);
        _view = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children = { _now, _days, _trend, _footer },
        };

        _refresh = new DispatcherTimer { Interval = Every };
        _refresh.Tick += async (_, _) => await RefreshAsync();
    }

    public override Control View => _view;

    public override IReadOnlyList<string> Parts => ["now", "trend", .. _columns.Select(c => c.Part)];

    public override async Task ShowAsync(JsonElement fields, CancellationToken cancellationToken)
    {
        _fields = fields.Clone();
        var show = Forecasts.Text(fields, "show") ?? "all";
        _days.IsVisible = show is "all" or "days";
        _trend.IsVisible = show is "all" or "trend";

        _request = await Forecasts.ResolveAsync(_fields, _host.Settings(), _host.Http, cancellationToken);
        _reading = await Forecasts.ForecastAsync(_host.Http, _request, _host.Clock, cancellationToken);
        _fetched = _host.Clock.GetUtcNow();
        _stale = null;
        Draw();

        _refresh.Stop();
        _refresh.Start();
    }

    public override string Describe() => _reading is null ? "" : Words(_reading, _fetched) + (_stale is null ? "" : $" ({_stale})");

    public override Rect? PartBounds(string part)
    {
        Control? target = part switch
        {
            "now" => _now,
            "trend" when _trend.IsVisible => _trend,
            _ => _columns.FirstOrDefault(c => c.Part == part).Column,
        };

        return target is null || !target.IsVisible || target.TranslatePoint(default, _view) is not { } at
            ? null
            : new Rect(at, target.Bounds.Size);
    }

    public override void Highlight(string? part)
    {
        _highlighted = part;
        foreach (var (name, column) in _columns)
        {
            column.Background = name == part ? _host.Theme.Brush(_host.Theme.Surface2) : Brushes.Transparent;
        }
    }

    /// <summary>
    /// An hour at a time along the trend, as Kompas's j and k scrub its chart, or a day at a time
    /// along the strip when the trend is not shown; what is under the cursor takes the footer's
    /// line, so nothing below it moves.
    /// </summary>
    public override bool MoveCursor(int steps)
    {
        if (_reading is null)
        {
            return false;
        }

        if (_trend.IsVisible && _trend.Count > 0)
        {
            _hour = Math.Clamp((_hour < 0 ? -1 : _hour) + steps, 0, _trend.Count - 1);
            _trend.Select(_hour);
        }
        else if (_days.IsVisible && _columns.Count > 0)
        {
            _day = Math.Clamp((_day < 0 ? -1 : _day) + steps, 0, _columns.Count - 1);
            Highlight(_columns[_day].Part);
        }
        else
        {
            return false;
        }

        Footer();
        return true;
    }

    public override void ResetCursor()
    {
        _hour = -1;
        _day = -1;
        _trend.Select(-1);
        Highlight(null);
        Footer();
    }

    protected override void Dispose(bool disposing)
    {
        _refresh.Stop();
        base.Dispose(disposing);
    }

    /// <summary>In words, for the agent to say: now, each day, the UV and the light, and when it was fetched.</summary>
    internal static string Words(WeatherReading reading, DateTimeOffset fetched)
    {
        var words = new StringBuilder();
        var unit = reading.TemperatureUnit;
        var local = fetched.ToOffset(reading.UtcOffset);

        words.Append(CultureInfo.InvariantCulture, $"{reading.LocationName}, at {local:HH:mm} there: {Round(reading.Temperature)}{unit}, {reading.Condition.Label.ToLowerInvariant()}");
        if (Math.Abs(reading.ApparentTemperature - reading.Temperature) >= 1.5)
        {
            words.Append(CultureInfo.InvariantCulture, $", feels like {Round(reading.ApparentTemperature)}{unit}");
        }

        if (reading.WindSpeed is { } wind)
        {
            words.Append(CultureInfo.InvariantCulture, $", wind {Round(wind)} {reading.WindSpeedUnit}");
        }

        if (reading.HumidityPercent is { } humidity)
        {
            words.Append(CultureInfo.InvariantCulture, $", humidity {humidity}%");
        }

        words.Append('.');
        foreach (var (day, i) in reading.Forecast.Select((d, i) => (d, i)))
        {
            words.Append(CultureInfo.InvariantCulture, $" {(i == 0 ? "Today" : day.Date.ToString("dddd", CultureInfo.InvariantCulture))} {day.Condition.Label.ToLowerInvariant()}, {Round(day.High)}/{Round(day.Low)}{unit}");
            if (day.PrecipitationChancePercent is { } rain && rain >= 20)
            {
                words.Append(CultureInfo.InvariantCulture, $", {rain}% chance of rain");
            }

            words.Append(';');
        }

        if (reading.PeakUv(fetched, 24) is { } uv)
        {
            words.Append(CultureInfo.InvariantCulture, $" UV peaks at {uv.Index:0.#} ({UvBand(uv.Index)}) at {uv.At:HH:mm}.");
        }

        if (reading.Forecast.FirstOrDefault() is { Sunrise: { } sunrise, Sunset: { } sunset })
        {
            words.Append(CultureInfo.InvariantCulture, $" Sunrise {sunrise:HH:mm}, sunset {sunset:HH:mm}.");
        }

        return words.ToString().TrimEnd(';');
    }

    /// <summary>The WHO bands: named, because "7" means nothing without the scale, and the bands carry the advice.</summary>
    internal static string UvBand(double index) => index switch
    {
        < 3 => "low",
        < 6 => "moderate",
        < 8 => "high",
        < 11 => "very high",
        _ => "extreme",
    };

    private async Task RefreshAsync()
    {
        if (_request is null)
        {
            return;
        }

        try
        {
            _reading = await Forecasts.ForecastAsync(_host.Http, _request, _host.Clock, CancellationToken.None);
            _fetched = _host.Clock.GetUtcNow();
            _stale = null;
        }
        catch (WeatherException ex)
        {
            _stale = $"could not refresh: {ex.Message}";
            _host.Log(_stale);
        }

        Draw();
    }

    private void Draw()
    {
        if (_reading is not { } reading)
        {
            return;
        }

        var theme = _host.Theme;
        var unit = reading.TemperatureUnit;
        _glyph.Text = reading.Condition.Glyph;
        _temperature.Text = $"{Round(reading.Temperature)}{unit}";
        _place.Text = reading.LocationName;

        var detail = new List<string> { reading.Condition.Label };
        if (Math.Abs(reading.ApparentTemperature - reading.Temperature) >= 1.5)
        {
            detail.Add($"feels {Round(reading.ApparentTemperature)}°");
        }

        if (reading.WindSpeed is { } wind && wind >= 15)
        {
            detail.Add($"wind {Round(wind)} {reading.WindSpeedUnit}");
        }

        _condition.Text = string.Join(" · ", detail);

        DrawDays(reading);
        _trend.Show(reading.HoursFrom(_fetched, 72), unit);
        if (_day >= 0 && _day < _columns.Count)
        {
            Highlight(_columns[_day].Part);
        }

        Footer();
    }

    /// <summary>
    /// The line at the foot: the UV, the light and how fresh it is — or, while the cursor is out,
    /// what is under it.
    /// </summary>
    private void Footer()
    {
        if (_reading is not { } reading)
        {
            return;
        }

        var theme = _host.Theme;
        var unit = reading.TemperatureUnit.StartsWith('°') ? "°" : reading.TemperatureUnit;

        if (_hour >= 0 && _trend.Selected is { } hour)
        {
            var parts = new List<string> { hour.Time.ToString("ddd HH:mm", CultureInfo.InvariantCulture) };
            if (hour.Temperature is { } temperature)
            {
                parts.Add($"{Round(temperature)}{unit}");
            }

            if (hour.PrecipitationChancePercent is { } rain)
            {
                parts.Add($"{rain}% chance of rain");
            }

            if (hour.UvIndex is { } index && index > 0)
            {
                parts.Add($"UV {index.ToString("0.#", CultureInfo.InvariantCulture)} {UvBand(index)}");
            }

            _footer.Text = string.Join("   ·   ", parts) + "   —   r for now";
            _footer.Foreground = theme.Brush(theme.Cool);
            return;
        }

        if (_day >= 0 && _day < reading.Forecast.Count)
        {
            var day = reading.Forecast[_day];
            var parts = new List<string>
            {
                $"{(_day == 0 ? "Today" : day.Date.ToString("dddd", CultureInfo.InvariantCulture))}: {day.Condition.Label.ToLowerInvariant()}",
                $"{Round(day.High)}{unit} / {Round(day.Low)}{unit}",
            };

            if (day.PrecipitationChancePercent is { } rain)
            {
                parts.Add($"{rain}% chance of rain");
            }

            if (day.UvIndexMax is { } uvMax && uvMax > 0)
            {
                parts.Add($"UV {uvMax.ToString("0.#", CultureInfo.InvariantCulture)} {UvBand(uvMax)}");
            }

            if (day is { Sunrise: { } rises, Sunset: { } sets })
            {
                parts.Add($"sunrise {rises:HH:mm} · sunset {sets:HH:mm}");
            }

            _footer.Text = string.Join("   ·   ", parts) + "   —   r for now";
            _footer.Foreground = theme.Brush(theme.Cool);
            return;
        }

        var footer = new List<string>();
        if (reading.PeakUv(_fetched, 24) is { } uv)
        {
            footer.Add($"UV {uv.Index.ToString("0.#", CultureInfo.InvariantCulture)} {UvBand(uv.Index)}, peaks {uv.At:HH:mm}");
        }

        if (reading.Forecast.FirstOrDefault() is { Sunrise: { } sunrise, Sunset: { } sunset })
        {
            footer.Add($"sunrise {sunrise:HH:mm} · sunset {sunset:HH:mm}");
        }

        footer.Add(_stale ?? $"as of {_fetched.ToOffset(reading.UtcOffset):HH:mm} there");
        _footer.Text = string.Join("   ·   ", footer);
        _footer.Foreground = theme.Brush(_stale is null ? theme.Muted : theme.Danger);
    }

    /// <summary>
    /// A column a day. The rain chance only when it is worth planning around: a "5%" under every
    /// dry day trains the eye to skip the row.
    /// </summary>
    private void DrawDays(WeatherReading reading)
    {
        var theme = _host.Theme;
        var size = theme.FontSize;
        _days.Children.Clear();
        _days.ColumnDefinitions.Clear();
        _columns.Clear();

        foreach (var (day, i) in reading.Forecast.Select((d, i) => (d, i)))
        {
            var name = i == 0 ? "Today" : day.Date.ToString("ddd", CultureInfo.InvariantCulture);
            var column = new StackPanel { Spacing = size * 0.1, HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(Centered(Text(size * 0.72, theme.Muted, text: name)));
            column.Children.Add(new TextBlock { Text = day.Condition.Glyph, FontFamily = Emoji, FontSize = size * 1.35, HorizontalAlignment = HorizontalAlignment.Center });
            column.Children.Add(Centered(Text(size * 0.95, theme.Text, FontWeight.SemiBold, $"{Round(day.High)}°")));
            column.Children.Add(Centered(Text(size * 0.78, theme.Muted, text: $"{Round(day.Low)}°")));
            if (day.PrecipitationChancePercent is { } rain && rain >= 20)
            {
                column.Children.Add(Centered(Text(size * 0.68, theme.Cool, text: $"{rain}%")));
            }

            var part = i == 0 ? "today" : name.ToLowerInvariant();
            var border = new Border
            {
                CornerRadius = new CornerRadius(size * 0.35),
                Padding = new Thickness(size * 0.2, size * 0.25),
                Margin = new Thickness(size * 0.1, 0),
                Background = part == _highlighted ? theme.Brush(theme.Surface2) : Brushes.Transparent,
                Child = column,
            };

            _days.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(border, i);
            _days.Children.Add(border);
            _columns.Add((part, border));
        }
    }

    private TextBlock Text(double size, Color color, FontWeight weight = FontWeight.Normal, string text = "") => new()
    {
        Text = text,
        FontFamily = _host.Theme.Font,
        FontSize = size,
        FontWeight = weight,
        Foreground = _host.Theme.Brush(color),
    };

    private static TextBlock Centered(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return text;
    }

    private static string Round(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}
