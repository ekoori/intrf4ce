using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Plugins;

namespace Intrf4ce.Plugin.Timezones;

/// <summary>Adds the <c>timezones</c> element and the <c>timezones_now</c> tool.</summary>
public sealed class TimezonesPlugin : IPlugin
{
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        var clock = registrar.Services.Clock;

        registrar.AddElement(new ElementType(
            "timezones",
            "A 24-hour timeline across places: each one's time now, its hours shaded asleep, early, working or evening, and when its clocks change. Keeps time by itself.",
            host => new TimezonesElement(host))
        {
            Fields = new JsonObject
            {
                ["zones"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "The places, home first: IANA zone ids (\"Europe/Zagreb\") or {\"zone\": \"America/New_York\", \"label\": \"New York\"}. Omit for the user's own.",
                    ["items"] = new JsonObject
                    {
                        ["anyOf"] = new JsonArray(
                            new JsonObject { ["type"] = "string" },
                            new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["zone"] = new JsonObject { ["type"] = "string" },
                                    ["label"] = new JsonObject { ["type"] = "string" },
                                },
                            }),
                    },
                },
                ["at"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A moment to mark with a dashed line, as local time at home: \"2026-10-06T15:00\".",
                },
            },
            Check = Check,
        });

        registrar.AddTool(new PluginTool(
            "timezones_now",
            "The time now in each of several places, the gap from the first, and when each one's clocks next change — from this machine's zone database, so it agrees with a timezones element.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("zones"),
                ["properties"] = new JsonObject
                {
                    ["zones"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "IANA zone ids, home first.",
                    },
                },
            },
            (arguments, _) =>
            {
                var zones = Zones.FromTool(arguments);
                var now = clock.GetUtcNow();
                var readings = zones.Select(z => ZoneAlmanac.Read(z.Label, z.Zone, now)).ToList();
                return Task.FromResult(Zones.Describe(readings, now, at: null));
            }));
    }

    private static IEnumerable<FieldIssue> Check(JsonElement fields)
    {
        if (fields.TryGetProperty("zones", out var zones))
        {
            if (zones.ValueKind != JsonValueKind.Array)
            {
                yield return new FieldIssue("zones", "zones is a list: [\"Europe/Zagreb\", {\"zone\": \"America/New_York\", \"label\": \"New York\"}]");
                yield break;
            }

            var i = 0;
            foreach (var zone in zones.EnumerateArray())
            {
                var id = zone.ValueKind == JsonValueKind.String ? zone.GetString()
                    : zone.ValueKind == JsonValueKind.Object && zone.TryGetProperty("zone", out var z) ? z.GetString()
                    : null;

                if (id is null)
                {
                    yield return new FieldIssue($"zones[{i}]", "each place is a zone id, or {\"zone\": …, \"label\": …}");
                }
                else if (!Zones.Exists(id))
                {
                    yield return new FieldIssue($"zones[{i}]", $"'{id}' is not a time zone this machine knows; use an IANA id such as Europe/Zagreb or America/New_York");
                }

                i++;
            }
        }

        if (fields.TryGetProperty("at", out var at)
            && (at.ValueKind != JsonValueKind.String || !DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
        {
            yield return new FieldIssue("at", "at is a local time at home, like \"2026-10-06T15:00\"");
        }
    }
}
