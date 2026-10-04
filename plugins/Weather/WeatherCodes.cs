namespace Intrf4ce.Plugin.Weather;

// From Kompas (MIT, the same author): its Sources/Weather/WeatherCodes.cs, unchanged but for
// the namespace and its visibility.

/// <summary>A WMO weather code resolved into something renderable.</summary>
/// <param name="Glyph">A single emoji; there are no icon assets to ship or scale.</param>
internal readonly record struct WeatherCondition(int Code, string Label, string Glyph);

/// <summary>
/// Maps WMO weather interpretation codes — what Open-Meteo returns — to a label and a glyph.
/// </summary>
/// <remarks>
/// Ported from wonky's <c>WeatherCodes.kt</c>. Emoji rather than bundled icons: they render
/// at any size with the system font and there is nothing to ship.
/// <para>
/// The codes are sparse and not contiguous, which is why this is a switch rather than a
/// table — 58 and 59 do not exist, and a lookup array would invent them.
/// </para>
/// </remarks>
internal static class WeatherCodes
{
    public static WeatherCondition Describe(int code, bool isDay = true) => code switch
    {
        0 => new(code, "Clear", isDay ? "☀️" : "🌙"),
        1 => new(code, "Mainly clear", isDay ? "🌤️" : "🌙"),
        2 => new(code, "Partly cloudy", isDay ? "⛅" : "☁️"),
        3 => new(code, "Overcast", "☁️"),

        45 => new(code, "Fog", "🌫️"),
        48 => new(code, "Freezing fog", "🌫️"),

        51 => new(code, "Light drizzle", "🌦️"),
        53 => new(code, "Drizzle", "🌦️"),
        55 => new(code, "Heavy drizzle", "🌧️"),
        56 or 57 => new(code, "Freezing drizzle", "🌧️"),

        61 => new(code, "Light rain", "🌦️"),
        63 => new(code, "Rain", "🌧️"),
        65 => new(code, "Heavy rain", "🌧️"),
        66 or 67 => new(code, "Freezing rain", "🌧️"),

        71 => new(code, "Light snow", "🌨️"),
        73 => new(code, "Snow", "🌨️"),
        75 => new(code, "Heavy snow", "❄️"),
        77 => new(code, "Snow grains", "🌨️"),

        80 => new(code, "Light showers", "🌦️"),
        81 => new(code, "Showers", "🌧️"),
        82 => new(code, "Heavy showers", "⛈️"),

        85 => new(code, "Snow showers", "🌨️"),
        86 => new(code, "Heavy snow showers", "❄️"),

        95 => new(code, "Thunderstorm", "⛈️"),
        96 => new(code, "Storm with hail", "⛈️"),
        99 => new(code, "Severe storm", "⛈️"),

        _ => new(code, "Unknown", "❓"),
    };
}
