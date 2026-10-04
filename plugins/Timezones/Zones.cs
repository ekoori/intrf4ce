using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intrf4ce.Plugin.Timezones;

/// <summary>A place on the timeline: what to call it, and its zone.</summary>
internal sealed record Place(string Label, string Zone)
{
    /// <summary>Its id as a focus part: the label in lower case, with '-' between words.</summary>
    public string Part => Zones.PartOf(Label);
}

/// <summary>Reading the places from an element, from settings, or from a tool call; and saying them in words.</summary>
internal static class Zones
{
    public static bool Exists(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// The element's places, else the user's from settings, else this machine's own zone,
    /// called "Here" because its id names a city that may not be where the user is.
    /// </summary>
    public static IReadOnlyList<Place> From(JsonElement fields, JsonElement settings)
    {
        if (fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("zones", out var zones) && Parse(zones) is { Count: > 0 } given)
        {
            return given;
        }

        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("zones", out var mine) && Parse(mine) is { Count: > 0 } configured)
        {
            return configured;
        }

        var local = TimeZoneInfo.Local;
        return [new Place("Here", TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : local.Id)];
    }

    public static IReadOnlyList<Place> FromTool(JsonObject arguments)
    {
        if (arguments["zones"] is not JsonArray zones || zones.Count == 0)
        {
            throw new ArgumentException("zones is required: a list of IANA zone ids, home first");
        }

        var places = Parse(JsonSerializer.SerializeToElement(zones));
        foreach (var place in places.Where(p => !Exists(p.Zone)))
        {
            throw new ArgumentException($"'{place.Zone}' is not a time zone this machine knows; use an IANA id such as Europe/Zagreb");
        }

        return places;
    }

    /// <summary>What a place is called when the element does not say: the city in its id.</summary>
    public static string LabelOf(string zone) => zone.Split('/')[^1].Replace('_', ' ');

    public static string PartOf(string label)
    {
        var part = new StringBuilder();
        foreach (var c in label.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                part.Append(c);
            }
            else if (part.Length > 0 && part[^1] != '-')
            {
                part.Append('-');
            }
        }

        return part.ToString().Trim('-');
    }

    /// <summary>Every place's time now, its gap from home, and its next change of clocks, a line each.</summary>
    public static string Describe(IReadOnlyList<ZoneReading> zones, DateTimeOffset now, DateTimeOffset? at)
    {
        if (zones.Count == 0)
        {
            return "no places";
        }

        var home = zones[0];
        var lines = new List<string>();
        foreach (var zone in zones)
        {
            if (!zone.IsValid)
            {
                lines.Add($"{zone.Label}: {zone.Error}");
                continue;
            }

            var local = zone.At(now);
            var line = new StringBuilder($"{zone.Label}: {local.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}");
            line.Append(zone == home ? " (home)" : $" ({ZoneAlmanac.Describe(zone.Difference(home))} from {home.Label})");

            if (at is { } moment)
            {
                line.Append(CultureInfo.InvariantCulture, $"; at the marked time {zone.At(moment):ddd HH:mm}");
            }

            if (zone.Next(now) is { } next)
            {
                var days = (int)Math.Round((next.At - now).TotalDays);
                line.Append(CultureInfo.InvariantCulture, $"; daylight saving {(next.Starting ? "starts" : "ends")} {next.At:ddd d MMM} (in {days} days)");
            }

            lines.Add(line.ToString());
        }

        return string.Join("\n", lines);
    }

    private static List<Place> Parse(JsonElement zones)
    {
        var places = new List<Place>();
        if (zones.ValueKind != JsonValueKind.Array)
        {
            return places;
        }

        foreach (var zone in zones.EnumerateArray())
        {
            switch (zone.ValueKind)
            {
                case JsonValueKind.String when zone.GetString() is { Length: > 0 } id:
                    places.Add(new Place(LabelOf(id), id));
                    break;

                case JsonValueKind.Object when zone.TryGetProperty("zone", out var id) && id.GetString() is { Length: > 0 } zoneId:
                    var label = zone.TryGetProperty("label", out var named) && named.GetString() is { Length: > 0 } text ? text : LabelOf(zoneId);
                    places.Add(new Place(label, zoneId));
                    break;
            }
        }

        return places;
    }
}
