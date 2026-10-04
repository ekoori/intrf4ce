using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Plugins;

namespace Intrf4ce.Plugin.Weather;

/// <summary>Adds the <c>weather</c> element and the <c>weather_now</c> tool.</summary>
public sealed class WeatherPlugin : IPlugin
{
    private static readonly string[] Shows = ["all", "now", "days", "trend"];
    private static readonly string[] Units = ["metric", "imperial"];

    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        var services = registrar.Services;

        registrar.AddElement(new ElementType(
            "weather",
            "The weather somewhere, kept current: conditions now, a strip of days, the 72-hour temperature and rain trend, the UV peak and sunrise and sunset. From Open-Meteo.",
            host => new WeatherElement(host))
        {
            Fields = Fields(),
            Check = Check,
        });

        registrar.AddTool(new PluginTool(
            "weather_now",
            "The weather somewhere, now and for the coming days, in words: what a weather element there would show. Give a place or coordinates; with neither, the user's own place from settings.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = Fields(),
            },
            async (arguments, cancellationToken) =>
            {
                var fields = JsonSerializer.SerializeToElement(arguments);
                if (Check(fields).FirstOrDefault() is { } wrong)
                {
                    throw new ArgumentException($"{wrong.Field}: {wrong.Message}");
                }

                try
                {
                    var request = await Forecasts.ResolveAsync(fields, services.Settings(), services.Http, cancellationToken);
                    var reading = await Forecasts.ForecastAsync(services.Http, request, services.Clock, cancellationToken);
                    return WeatherElement.Words(reading, services.Clock.GetUtcNow());
                }
                catch (WeatherException ex)
                {
                    throw new ArgumentException(ex.Message, ex);
                }
            }));
    }

    private static JsonObject Fields() => new()
    {
        ["place"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Where, by name: \"Zagreb\", or \"Melbourne, Australia\" to choose among places of one name. Omit for the user's own place from settings.",
        },
        ["at"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "number" },
            ["description"] = "[latitude, longitude] instead of looking the place up; \"place\" is then only its label.",
        },
        ["days"] = new JsonObject { ["type"] = "integer", ["description"] = "Days in the strip, 1 to 7 (default 5)." },
        ["show"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray([.. Shows.Select(s => JsonValue.Create(s))]),
            ["description"] = "all (default); now: just the conditions; days: now and the strip; trend: now and the 72-hour line.",
        },
        ["units"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray([.. Units.Select(s => JsonValue.Create(s))]),
            ["description"] = "metric (°C, km/h, the default) or imperial (°F, mph).",
        },
    };

    private static IEnumerable<FieldIssue> Check(JsonElement fields)
    {
        if (fields.TryGetProperty("place", out var place) && place.ValueKind != JsonValueKind.String)
        {
            yield return new FieldIssue("place", "place is a name: \"Zagreb\"");
        }

        if (fields.TryGetProperty("at", out var at)
            && (at.ValueKind != JsonValueKind.Array || at.GetArrayLength() != 2
                || at[0].ValueKind != JsonValueKind.Number || at[1].ValueKind != JsonValueKind.Number
                || Math.Abs(at[0].GetDouble()) > 90 || Math.Abs(at[1].GetDouble()) > 180))
        {
            yield return new FieldIssue("at", "at is [latitude, longitude] in degrees: [45.81, 15.98] is Zagreb");
        }

        if (Forecasts.Number(fields, "days") is { } days && (days < 1 || days > 7 || days != Math.Floor(days)))
        {
            yield return new FieldIssue("days", "days is a whole number from 1 to 7");
        }

        if (Forecasts.Text(fields, "show") is { } show && !Shows.Contains(show))
        {
            yield return new FieldIssue("show", $"show is one of {string.Join(", ", Shows)}");
        }

        if (Forecasts.Text(fields, "units") is { } units && !Units.Contains(units))
        {
            yield return new FieldIssue("units", "units is metric or imperial");
        }
    }
}
