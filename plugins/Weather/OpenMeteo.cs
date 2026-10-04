using System.Globalization;
using System.Text.Json;

namespace Intrf4ce.Plugin.Weather;

/// <summary>A forecast service said something this cannot use; the message is for the slide.</summary>
public sealed class WeatherException(string message) : Exception(message);

/// <summary>Where and how much forecast to ask for.</summary>
/// <param name="Imperial">Fahrenheit and mph rather than Celsius and km/h.</param>
internal sealed record WeatherRequest(double Latitude, double Longitude, string LocationName, int ForecastDays, bool Imperial)
{
    /// <summary>The same place to about a hundred metres: what the cache is keyed by.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.000},{Longitude:0.000},{ForecastDays},{Imperial}");
}

/// <summary>One day of the strip.</summary>
internal sealed record DailyForecast(
    DateOnly Date,
    WeatherCondition Condition,
    double High,
    double Low,
    int? PrecipitationChancePercent,
    double? UvIndexMax,
    DateTimeOffset? Sunrise,
    DateTimeOffset? Sunset);

/// <summary>One hour of the trend, in the place's own time.</summary>
internal sealed record HourPoint(DateTimeOffset Time, double? Temperature, int? PrecipitationChancePercent, double? UvIndex);

/// <summary>One reading of the weather somewhere.</summary>
internal sealed record WeatherReading(
    string LocationName,
    double Temperature,
    double ApparentTemperature,
    WeatherCondition Condition,
    bool IsDay,
    int? HumidityPercent,
    double? WindSpeed,
    string TemperatureUnit,
    string WindSpeedUnit,
    TimeSpan UtcOffset,
    IReadOnlyList<DailyForecast> Forecast,
    IReadOnlyList<HourPoint> Hourly)
{
    /// <summary>The hours from <paramref name="from"/> on, at most <paramref name="hours"/> of them.</summary>
    /// <remarks>Open-Meteo returns whole local days, so the first hours are already past by the time anyone looks.</remarks>
    public IReadOnlyList<HourPoint> HoursFrom(DateTimeOffset from, int hours) =>
        [.. Hourly.Where(h => h.Time >= from.AddHours(-1)).Take(hours)];

    /// <summary>The highest UV in the next <paramref name="hours"/> hours, and when it peaks.</summary>
    public (double Index, DateTimeOffset At)? PeakUv(DateTimeOffset from, int hours)
    {
        var peak = HoursFrom(from, hours).Where(h => h.UvIndex is > 0).OrderByDescending(h => h.UvIndex).FirstOrDefault();
        return peak is { UvIndex: { } uv } ? (uv, peak.Time) : null;
    }
}

/// <summary>
/// Builds Open-Meteo request URLs and parses the replies.
/// </summary>
/// <remarks>
/// From Kompas's OpenMeteo.cs (itself ported from wonky's OpenMeteo.kt), with sunrise and
/// sunset, and a choice of units, added. Open-Meteo needs no key and no account, which is why
/// it is the source: a slide should not stop showing the weather because a key expired. URL
/// building and parsing stay apart from the network, so both are tested against captured
/// replies with no HTTP in the picture.
/// </remarks>
internal static class OpenMeteo
{
    public const string BaseUrl = "https://api.open-meteo.com/v1/forecast";

    private static readonly string[] CurrentFields =
        ["temperature_2m", "apparent_temperature", "weather_code", "is_day", "relative_humidity_2m", "wind_speed_10m"];

    private static readonly string[] HourlyFields = ["temperature_2m", "precipitation_probability", "uv_index"];

    private static readonly string[] DailyFields =
        ["weather_code", "temperature_2m_max", "temperature_2m_min", "precipitation_probability_max", "uv_index_max", "sunrise", "sunset"];

    public static string ForecastUrl(WeatherRequest request)
    {
        var parameters = new List<(string Key, string Value)>
        {
            ("latitude", Coordinate(request.Latitude)),
            ("longitude", Coordinate(request.Longitude)),
            ("current", string.Join(',', CurrentFields)),
            ("hourly", string.Join(',', HourlyFields)),
            ("daily", string.Join(',', DailyFields)),

            // The place's own day, so "today's high" is the place's today.
            ("timezone", "auto"),

            // Open-Meteo rejects 0; ask for one day and drop it when no strip is wanted.
            ("forecast_days", Math.Clamp(Math.Max(request.ForecastDays, 3), 1, 7).ToString(CultureInfo.InvariantCulture)),
        };

        if (request.Imperial)
        {
            parameters.Add(("temperature_unit", "fahrenheit"));
            parameters.Add(("wind_speed_unit", "mph"));
        }

        return BaseUrl + "?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    /// <exception cref="WeatherException">The reply was not a forecast this can read.</exception>
    public static WeatherReading Parse(string body, WeatherRequest request)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException ex)
        {
            throw new WeatherException($"Open-Meteo answered with something that is not JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new WeatherException("Open-Meteo answered with something that is not a forecast");
        }

        // Open-Meteo reports its own errors with HTTP 400 and this shape, naming the parameter.
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
        {
            throw new WeatherException($"Open-Meteo: {String(root, "reason") ?? "unknown error"}");
        }

        if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
        {
            throw new WeatherException("Open-Meteo's answer had no current conditions");
        }

        var temperature = Double(current, "temperature_2m") ?? throw new WeatherException("Open-Meteo's answer had no temperature");
        var isDay = (Int(current, "is_day") ?? 1) == 1;
        var units = root.TryGetProperty("current_units", out var u) ? u : default;
        var offset = TimeSpan.FromSeconds(root.TryGetProperty("utc_offset_seconds", out var off) && off.ValueKind == JsonValueKind.Number ? off.GetInt32() : 0);

        return new WeatherReading(
            LocationName: request.LocationName,
            Temperature: temperature,
            ApparentTemperature: Double(current, "apparent_temperature") ?? temperature,
            Condition: WeatherCodes.Describe(Int(current, "weather_code") ?? -1, isDay),
            IsDay: isDay,
            HumidityPercent: Int(current, "relative_humidity_2m"),
            WindSpeed: Double(current, "wind_speed_10m"),
            TemperatureUnit: String(units, "temperature_2m") ?? (request.Imperial ? "°F" : "°C"),
            WindSpeedUnit: String(units, "wind_speed_10m") ?? (request.Imperial ? "mp/h" : "km/h"),
            UtcOffset: offset,
            Forecast: [.. ParseDaily(root, offset).Take(Math.Max(1, request.ForecastDays))],
            Hourly: ParseHourly(root, offset));
    }

    private static List<DailyForecast> ParseDaily(JsonElement root, TimeSpan offset)
    {
        var result = new List<DailyForecast>();
        if (!root.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object
            || !daily.TryGetProperty("time", out var dates) || dates.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var codes = Array(daily, "weather_code");
        var highs = Array(daily, "temperature_2m_max");
        var lows = Array(daily, "temperature_2m_min");
        var rain = Array(daily, "precipitation_probability_max");
        var uv = Array(daily, "uv_index_max");
        var sunrise = Array(daily, "sunrise");
        var sunset = Array(daily, "sunset");

        for (var i = 0; i < dates.GetArrayLength(); i++)
        {
            // A day missing its high or low is skipped, not defaulted: 0° for a day Open-Meteo
            // did not answer for is a lie, and a shorter strip is merely shorter.
            if (!DateOnly.TryParse(dates[i].GetString(), CultureInfo.InvariantCulture, out var date)
                || At(highs, i) is not { } high || At(lows, i) is not { } low)
            {
                continue;
            }

            result.Add(new DailyForecast(
                date,
                WeatherCodes.Describe((int?)At(codes, i) ?? -1, isDay: true),
                high,
                low,
                (int?)At(rain, i),
                At(uv, i),
                Time(sunrise, i, offset),
                Time(sunset, i, offset)));
        }

        return result;
    }

    /// <remarks>
    /// Parallel arrays keyed by one time array, any of which may run short; each is read by
    /// index with a bounds check rather than zipped, so one short series cannot cut the trend.
    /// </remarks>
    private static List<HourPoint> ParseHourly(JsonElement root, TimeSpan offset)
    {
        var result = new List<HourPoint>();
        if (!root.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Object
            || !hourly.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var temperature = Array(hourly, "temperature_2m");
        var rain = Array(hourly, "precipitation_probability");
        var uv = Array(hourly, "uv_index");

        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (Time(times, i, offset) is { } time)
            {
                result.Add(new HourPoint(time, At(temperature, i), (int?)At(rain, i), At(uv, i)));
            }
        }

        return result;
    }

    /// <summary>A local time without an offset, as Open-Meteo writes them when asked for a zone, with the zone's offset.</summary>
    private static DateTimeOffset? Time(JsonElement? array, int index, TimeSpan offset) =>
        array is { } a && index < a.GetArrayLength() && a[index].ValueKind == JsonValueKind.String
        && DateTime.TryParse(a[index].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive)
            ? new DateTimeOffset(naive, offset)
            : null;

    /// <summary>Six decimal places is about 10 cm; more only bloats the URL and hurts caching.</summary>
    private static string Coordinate(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static JsonElement? Array(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;

    private static double? At(JsonElement? array, int index) =>
        array is { } a && index < a.GetArrayLength() && a[index].ValueKind == JsonValueKind.Number ? a[index].GetDouble() : null;

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Double(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int? Int(JsonElement parent, string name) => Double(parent, name) is { } d ? (int)d : null;
}

/// <summary>A place found by name.</summary>
internal sealed record Found(string Name, double Latitude, double Longitude);

/// <summary>
/// Turns a place name into coordinates with Open-Meteo's own geocoder, which, like its
/// forecast, needs no key.
/// </summary>
internal static class Geocoder
{
    public static string SearchUrl(string name)
    {
        // "Melbourne, Australia": the geocoder takes the name alone; the rest picks among matches.
        var parts = name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name=" + Uri.EscapeDataString(parts.Length > 0 ? parts[0] : name);
    }

    /// <exception cref="WeatherException">No such place.</exception>
    public static Found Parse(string body, string name)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            throw new WeatherException($"no place called '{name}' was found; give its latitude and longitude in \"at\"");
        }

        // The rest of what was written — a country, a state — chooses among places of that name;
        // otherwise the most populous, which is the order the geocoder answers in.
        var hints = name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        var best = results.EnumerateArray()
            .FirstOrDefault(r => hints.Count > 0 && hints.All(h => Mentions(r, h)), results[0]);

        var label = string.Join(", ", new[] { Text(best, "name"), Text(best, "admin1"), Text(best, "country") }
            .Where(t => !string.IsNullOrEmpty(t)).Distinct());

        return new Found(label, best.GetProperty("latitude").GetDouble(), best.GetProperty("longitude").GetDouble());
    }

    private static bool Mentions(JsonElement result, string hint) =>
        new[] { "country", "country_code", "admin1", "admin2" }
            .Any(field => string.Equals(Text(result, field), hint, StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
