using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Intrf4ce.Plugin.Holidays;

/// <summary>Nager.Date could not say; the message is for the slide.</summary>
public sealed class HolidaysException(string message) : Exception(message);

/// <summary>One public holiday, and where it applies.</summary>
/// <param name="Name">In English, as Nager.Date names it.</param>
/// <param name="LocalName">As the country names it; the same as <see cref="Name"/> in an English-speaking one.</param>
/// <param name="Regions">
/// Empty when it is national. Short codes — VIC, NSW, BY — rather than the API's <c>AU-VIC</c>,
/// because every code in a country starts the same way and the column is narrow.
/// </param>
internal sealed record Holiday(DateOnly Date, string Name, string LocalName, IReadOnlyList<string> Regions)
{
    public bool IsNational => Regions.Count == 0;

    public bool AppliesIn(string? region) =>
        IsNational || region is null || Regions.Contains(region, StringComparer.OrdinalIgnoreCase);

    public int DaysUntil(DateOnly today) => Date.DayNumber - today.DayNumber;

    public string RegionLabel => IsNational ? "national" : string.Join(" ", Regions);
}

/// <summary>What to list: whose holidays, whose are "ours", from when, and how many.</summary>
internal sealed record HolidayQuery(string Country, string? Region, DateOnly From, int Count, bool OnlyRegion)
{
    public string CountryName
    {
        get
        {
            try
            {
                return new RegionInfo(Country).EnglishName;
            }
            catch (ArgumentException)
            {
                return Country;
            }
        }
    }
}

/// <summary>
/// Public holidays for any country Nager.Date covers, national and by region.
/// </summary>
/// <remarks>
/// <para>
/// From Kompas's HolidaySource, which knew only Australia, made to take any country. Nager.Date
/// needs no key and gives the regional breakdown, which is the part that matters: barely half of
/// Australia's holidays are national — Labour Day alone falls on four dates depending on the
/// state — and Germany's differ by Land as much.
/// </para>
/// <para>
/// Computing them locally was rejected in Kompas for good reason: substitute days for weekends,
/// one-off proclamations, legislation by region; a wrong answer looks exactly like a right one
/// until someone turns up to a closed office.
/// </para>
/// </remarks>
internal static class Holidays
{
    private const string BaseUrl = "https://date.nager.at/api/v3/PublicHolidays";

    /// <summary>Twelve hours: the lists change when a government gazettes a date, a few times a year.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(12);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, Task<IReadOnlyList<Holiday>> Year)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The query an element's fields make, falling back on the user's settings, then on this machine's region.</summary>
    public static HolidayQuery Resolve(JsonElement fields, JsonElement settings, DateOnly today)
    {
        var country = Text(fields, "country") ?? Text(settings, "country") ?? RegionInfo.CurrentRegion.TwoLetterISORegionName;
        var region = Text(fields, "region") ?? (Text(fields, "country") is null ? Text(settings, "region") : null);
        var from = Text(fields, "from") is { } text && DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var date) ? date : today;
        var count = (int)Math.Clamp(Number(fields, "count") ?? Number(settings, "count") ?? 8, 1, 30);
        var only = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("only_region", out var o) && o.ValueKind == JsonValueKind.True;

        return new HolidayQuery(country.ToUpperInvariant(), region?.ToUpperInvariant(), from, count, only && region is not null);
    }

    /// <summary>The next holidays from the query's date: this year's and next, because in December the useful list is next year's.</summary>
    public static async Task<IReadOnlyList<Holiday>> UpcomingAsync(HttpClient http, HolidayQuery query, TimeProvider clock, CancellationToken cancellationToken)
    {
        var thisYear = await YearAsync(http, query.Country, query.From.Year, clock, cancellationToken).ConfigureAwait(false);
        var nextYear = await YearAsync(http, query.Country, query.From.Year + 1, clock, cancellationToken).ConfigureAwait(false);

        return
        [
            .. thisYear.Concat(nextYear)
                .Where(h => h.Date >= query.From && (!query.OnlyRegion || h.AppliesIn(query.Region)))
                .OrderBy(h => h.Date)
                .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .Take(query.Count),
        ];
    }

    /// <summary>Parses the API's array. Public so it is tested against a captured answer.</summary>
    public static IReadOnlyList<Holiday> Parse(string body, string country)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException ex)
        {
            throw new HolidaysException($"the holiday list for {country} was not JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new HolidaysException($"the holiday list for {country} was not a list");
        }

        var holidays = new List<Holiday>();
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("date", out var date) || !DateOnly.TryParse(date.GetString(), CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            var name = Text(item, "name") ?? Text(item, "localName") ?? "Holiday";
            holidays.Add(new Holiday(parsed, name, Text(item, "localName") ?? name, Regions(item, country)));
        }

        return holidays;
    }

    /// <summary>What a list says, a line a holiday, for the agent.</summary>
    public static string Describe(HolidayQuery query, IReadOnlyList<Holiday> holidays, DateOnly today)
    {
        var words = new StringBuilder($"Public holidays in {query.CountryName}");
        if (query.Region is { } region)
        {
            words.Append(CultureInfo.InvariantCulture, $" ({region} is home)");
        }

        words.Append(CultureInfo.InvariantCulture, $" from {query.From:d MMM yyyy}:");
        if (holidays.Count == 0)
        {
            return words.Append(" none known.").ToString();
        }

        foreach (var holiday in holidays)
        {
            words.Append(CultureInfo.InvariantCulture, $"\n{holiday.Date:ddd d MMM yyyy} {holiday.Name}");
            if (holiday.LocalName != holiday.Name)
            {
                words.Append(CultureInfo.InvariantCulture, $" ({holiday.LocalName})");
            }

            words.Append(CultureInfo.InvariantCulture, $"; {holiday.RegionLabel}; {Countdown(holiday, today, longForm: true)}");
        }

        return words.ToString();
    }

    /// <summary>
    /// Days to a fortnight, then weeks: "in 96 days" is a number to convert before it means
    /// anything, "14w" is already the answer to "is that soon".
    /// </summary>
    public static string Countdown(Holiday holiday, DateOnly today, bool longForm = false) =>
        holiday.DaysUntil(today) switch
        {
            < 0 => longForm ? "past" : "",
            0 => "today",
            1 => "tomorrow",
            < 14 and var d => longForm ? $"in {d} days" : $"{d}d",
            < 365 and var d => longForm ? $"in {d / 7} weeks" : $"{d / 7}w",
            var d => longForm ? $"in {d / 365} years" : $"{d / 365}y",
        };

    private static async Task<IReadOnlyList<Holiday>> YearAsync(HttpClient http, string country, int year, TimeProvider clock, CancellationToken cancellationToken)
    {
        var key = $"{country}/{year}";
        var now = clock.GetUtcNow();
        if (Cache.TryGetValue(key, out var cached) && now - cached.At < Fresh && !cached.Year.IsFaulted)
        {
            return await cached.Year.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var fetch = FetchAsync(http, country, year);
        Cache[key] = (now, fetch);
        try
        {
            return await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Cache.TryRemove(KeyValuePair.Create(key, (now, fetch)));
            throw;
        }
    }

    private static async Task<IReadOnlyList<Holiday>> FetchAsync(HttpClient http, string country, int year)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await http.GetAsync($"{BaseUrl}/{year}/{Uri.EscapeDataString(country)}", timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || body.Trim().Length == 0)
            {
                throw new HolidaysException(response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NoContent || body.Trim().Length == 0
                    ? $"Nager.Date has no public holidays for '{country}'; country is a two-letter code such as AU, HR or DE"
                    : $"Nager.Date answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return Parse(body, country);
        }
        catch (HttpRequestException ex)
        {
            throw new HolidaysException($"Nager.Date could not be reached: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw new HolidaysException("Nager.Date did not answer in time");
        }
    }

    /// <summary>
    /// The regions a holiday applies in. National ones say <c>global: true</c> and list none; one
    /// that is neither is taken as national rather than dropped — an unlabelled holiday is more
    /// likely a gap in the data than a holiday that applies nowhere.
    /// </summary>
    private static IReadOnlyList<string> Regions(JsonElement item, string country)
    {
        if ((item.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.True)
            || !item.TryGetProperty("counties", out var counties) || counties.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var prefix = country + "-";
        return
        [
            .. counties.EnumerateArray()
                .Select(c => c.GetString() ?? "")
                .Where(c => c.Length > 0)
                .Select(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? c[prefix.Length..] : c),
        ];
    }

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    internal static double? Number(JsonElement element, string name) =>
        element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null;
}
