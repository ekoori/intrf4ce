using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Intrf4ce.Plugin.Weather;

/// <summary>
/// Where an element or a tool call wants the weather for, and the forecast for it — fetched once
/// per place every quarter of an hour, however many slides show that place.
/// </summary>
/// <remarks>
/// Open-Meteo is free and asks only that it is not hammered; a deck with the same city on five
/// slides, each refreshing on its own clock, would ask five times. So the fetch is shared: the
/// first to ask starts it, the rest wait on the same task, and a failure is forgotten at once so
/// the next asks again.
/// </remarks>
internal static class Forecasts
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, Task<WeatherReading> Reading)> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<Found>> Places = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The request an element's fields make, falling back on the user's settings.</summary>
    /// <exception cref="WeatherException">It does not say where, or the place cannot be found.</exception>
    public static async Task<WeatherRequest> ResolveAsync(JsonElement fields, JsonElement settings, HttpClient http, CancellationToken cancellationToken)
    {
        var days = Number(fields, "days") ?? Number(settings, "days") ?? 5;
        var imperial = string.Equals(Text(fields, "units") ?? Text(settings, "units"), "imperial", StringComparison.OrdinalIgnoreCase);
        var place = Text(fields, "place");

        if (Coordinates(fields, "at") is { } at)
        {
            return new WeatherRequest(at.Latitude, at.Longitude, place ?? Format(at), (int)Math.Clamp(days, 1, 7), imperial);
        }

        if (place is null && Number(settings, "latitude") is { } latitude && Number(settings, "longitude") is { } longitude)
        {
            return new WeatherRequest(latitude, longitude, Text(settings, "place") ?? Format((latitude, longitude)), (int)Math.Clamp(days, 1, 7), imperial);
        }

        place ??= Text(settings, "place")
            ?? throw new WeatherException("where? give the element a \"place\" or \"at\", or set plugins.options.weather.place in settings.yaml");

        var found = await FindAsync(http, place, cancellationToken).ConfigureAwait(false);
        return new WeatherRequest(found.Latitude, found.Longitude, found.Name, (int)Math.Clamp(days, 1, 7), imperial);
    }

    public static async Task<WeatherReading> ForecastAsync(HttpClient http, WeatherRequest request, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (Cache.TryGetValue(request.Key, out var cached) && now - cached.At < Fresh && !cached.Reading.IsFaulted)
        {
            return await cached.Reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var fetch = FetchAsync(http, request);
        Cache[request.Key] = (now, fetch);
        try
        {
            return await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Cache.TryRemove(KeyValuePair.Create(request.Key, (now, fetch)));
            throw;
        }
    }

    private static Task<Found> FindAsync(HttpClient http, string name, CancellationToken cancellationToken)
    {
        var lookup = Places.GetOrAdd(name.Trim(), n => GeocodeAsync(http, n));
        _ = lookup.ContinueWith(t => Places.TryRemove(KeyValuePair.Create(name.Trim(), lookup)), CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        return lookup.WaitAsync(cancellationToken);
    }

    private static async Task<Found> GeocodeAsync(HttpClient http, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var body = await GetAsync(http, Geocoder.SearchUrl(name), "Open-Meteo's place search", timeout.Token).ConfigureAwait(false);
        return Geocoder.Parse(body, name);
    }

    private static async Task<WeatherReading> FetchAsync(HttpClient http, WeatherRequest request)
    {
        // Its own time limit, not the first asker's: the fetch is shared with whoever asks next.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var body = await GetAsync(http, OpenMeteo.ForecastUrl(request), "Open-Meteo", timeout.Token).ConfigureAwait(false);
        return OpenMeteo.Parse(body, request);
    }

    private static async Task<string> GetAsync(HttpClient http, string url, string who, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Open-Meteo explains a bad request in the body, which Parse reads out.
            if (!response.IsSuccessStatusCode && !body.Contains("\"reason\"", StringComparison.Ordinal))
            {
                throw new WeatherException($"{who} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new WeatherException($"{who} could not be reached: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WeatherException($"{who} did not answer in time");
        }
    }

    private static string Format((double Latitude, double Longitude) at) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(at.Latitude):0.##}°{(at.Latitude < 0 ? 'S' : 'N')} {Math.Abs(at.Longitude):0.##}°{(at.Longitude < 0 ? 'W' : 'E')}");

    private static (double Latitude, double Longitude)? Coordinates(JsonElement fields, string name) =>
        fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var at) && at.ValueKind == JsonValueKind.Array
        && at.GetArrayLength() == 2 && at[0].ValueKind == JsonValueKind.Number && at[1].ValueKind == JsonValueKind.Number
            ? (at[0].GetDouble(), at[1].GetDouble())
            : null;

    internal static string? Text(JsonElement fields, string name) =>
        fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    internal static double? Number(JsonElement fields, string name) =>
        fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null;
}
