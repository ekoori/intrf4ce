using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Plugins;

namespace Intrf4ce.Plugin.Holidays;

/// <summary>Adds the <c>holidays</c> element and the <c>holidays_upcoming</c> tool.</summary>
public sealed class HolidaysPlugin : IPlugin
{
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        var services = registrar.Services;

        registrar.AddElement(new ElementType(
            "holidays",
            "The next public holidays in a country, with the regions each applies in and how soon it is; the home region's in full colour. From Nager.Date; kept current.",
            host => new HolidaysElement(host))
        {
            Fields = Fields(),
            Check = Check,
            IsVisual = false,
        });

        registrar.AddTool(new PluginTool(
            "holidays_upcoming",
            "The next public holidays in a country, with where each applies and how soon: what a holidays element would show. With no country, the user's own from settings.",
            new JsonObject { ["type"] = "object", ["properties"] = Fields() },
            async (arguments, cancellationToken) =>
            {
                var fields = JsonSerializer.SerializeToElement(arguments);
                if (Check(fields).FirstOrDefault() is { } wrong)
                {
                    throw new ArgumentException($"{wrong.Field}: {wrong.Message}");
                }

                var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
                var query = Holidays.Resolve(fields, services.Settings(), today);
                try
                {
                    return Holidays.Describe(query, await Holidays.UpcomingAsync(services.Http, query, services.Clock, cancellationToken), today);
                }
                catch (HolidaysException ex)
                {
                    throw new ArgumentException(ex.Message, ex);
                }
            }));
    }

    private static JsonObject Fields() => new()
    {
        ["country"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Two-letter country code: AU, HR, DE, US. Omit for the user's own, from settings or this machine.",
        },
        ["region"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "The home region within it, as the country's code for it without the country: VIC, NSW, BY, CA. Its holidays are in full colour, the rest dimmed.",
        },
        ["only_region"] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "Only the holidays that apply in the region: national ones and its own.",
        },
        ["count"] = new JsonObject { ["type"] = "integer", ["description"] = "How many, 1 to 30 (default 8)." },
        ["from"] = new JsonObject { ["type"] = "string", ["description"] = "List from this date instead of today: \"2026-12-01\"." },
    };

    private static IEnumerable<FieldIssue> Check(JsonElement fields)
    {
        if (Holidays.Text(fields, "country") is { } country && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
        {
            yield return new FieldIssue("country", "country is a two-letter code such as AU, HR or DE");
        }

        if (Holidays.Number(fields, "count") is { } count && (count < 1 || count > 30 || count != Math.Floor(count)))
        {
            yield return new FieldIssue("count", "count is a whole number from 1 to 30");
        }

        if (Holidays.Text(fields, "from") is { } from && !DateOnly.TryParse(from, CultureInfo.InvariantCulture, out _))
        {
            yield return new FieldIssue("from", "from is a date: \"2026-12-01\"");
        }

        if (fields.TryGetProperty("only_region", out var only) && only.ValueKind == JsonValueKind.True && Holidays.Text(fields, "region") is null)
        {
            yield return new FieldIssue("only_region", "only_region needs a region to be only about", IsWarning: true);
        }
    }
}
