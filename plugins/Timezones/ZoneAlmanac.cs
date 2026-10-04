using System.Globalization;

namespace Intrf4ce.Plugin.Timezones;

// From Kompas (MIT, the same author): its Sources/Time/ZoneAlmanac.cs, unchanged but for the
// namespace.

/// <summary>One daylight-saving transition.</summary>
/// <param name="At">The instant, in the zone's own local time — the wall clock that jumps.</param>
/// <param name="Starting">True when clocks go forward into daylight time.</param>
public sealed record DaylightTransition(DateTimeOffset At, bool Starting, TimeSpan Shift);

/// <summary>A clock in one place.</summary>
public sealed record ZoneReading(
    string Label,
    string ZoneId,
    DateTimeOffset Now,
    bool IsDaylightSaving,
    TimeSpan Offset,
    IReadOnlyList<DaylightTransition> Transitions,
    string? Error)
{
    /// <summary>Whether the zone id resolved at all.</summary>
    public bool IsValid => Error is null;

    /// <summary>Hours ahead of, or behind, the reference zone.</summary>
    public TimeSpan Difference(ZoneReading reference) => Offset - reference.Offset;

    /// <summary>The next transition after <paramref name="now"/>, or null when the zone has none.</summary>
    public DaylightTransition? Next(DateTimeOffset now) =>
        Transitions.FirstOrDefault(t => t.At > now);

    /// <summary>The local time at a given instant.</summary>
    public DateTimeOffset At(DateTimeOffset instant) =>
        IsValid ? TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(ZoneId)) : instant;
}

/// <summary>
/// Local times and daylight-saving transitions for a set of zones.
/// </summary>
/// <remarks>
/// Everything here comes from the machine's own zone database, so it needs no network and
/// cannot be stale in the way a service can — though it is exactly as current as the host's
/// tzdata, which is worth remembering when a country changes its rules at short notice.
/// </remarks>
public static class ZoneAlmanac
{
    /// <summary>
    /// Reads a zone, including its transitions for the next couple of years.
    /// </summary>
    /// <param name="label">
    /// What to call it. A zone id is a location, not a name anyone says — "Australia/Sydney"
    /// is where Melbourne's clock comes from half the year and is still the wrong word for it.
    /// </param>
    public static ZoneReading Read(string label, string zoneId, DateTimeOffset now)
    {
        TimeZoneInfo zone;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Reported on the row rather than thrown: one mistyped zone in a config list
            // must not take the whole panel down.
            return new ZoneReading(label, zoneId, now, false, TimeSpan.Zero, [], $"unknown zone '{zoneId}'");
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);

        return new ZoneReading(
            Label: label,
            ZoneId: zoneId,
            Now: local,
            IsDaylightSaving: zone.IsDaylightSavingTime(now),
            Offset: zone.GetUtcOffset(now),
            Transitions: Transitions(zone, now),
            Error: null);
    }

    /// <summary>
    /// The daylight-saving flips within a window around <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// Found by scanning rather than by interpreting <see cref="TimeZoneInfo.GetAdjustmentRules"/>.
    /// The rules describe transitions as floating expressions — "the first Sunday of October"
    /// — with their own edge cases for fixed-date rules, southern-hemisphere years that wrap,
    /// and rules that expire mid-window. Asking the same API the rest of the app trusts
    /// whether a given instant is in daylight time, and noticing where the answer changes,
    /// cannot disagree with itself.
    /// <para>
    /// A day at a time to find the day, then an hour at a time inside it. Two years of daily
    /// checks is seven hundred calls, which is nothing, and it keeps the search honest for
    /// zones whose transition is not at the usual hour.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<DaylightTransition> Transitions(TimeZoneInfo zone, DateTimeOffset now)
    {
        if (!zone.SupportsDaylightSavingTime)
        {
            return [];
        }

        var found = new List<DaylightTransition>();

        // From six months back, so "when did it last change" is answerable too.
        var start = now.AddMonths(-6).UtcDateTime.Date;
        var end = now.AddMonths(18).UtcDateTime.Date;

        var previous = zone.IsDaylightSavingTime(new DateTimeOffset(start, TimeSpan.Zero));

        for (var day = start.AddDays(1); day <= end; day = day.AddDays(1))
        {
            var current = zone.IsDaylightSavingTime(new DateTimeOffset(day, TimeSpan.Zero));

            if (current == previous)
            {
                continue;
            }

            if (Pinpoint(zone, day.AddDays(-1), previous) is { } transition)
            {
                found.Add(transition);
            }

            previous = current;
        }

        return found;
    }

    /// <summary>Narrows a flip to the hour it happened, within the day starting <paramref name="from"/>.</summary>
    private static DaylightTransition? Pinpoint(TimeZoneInfo zone, DateTime from, bool before)
    {
        for (var hour = 1; hour <= 48; hour++)
        {
            var instant = new DateTimeOffset(from, TimeSpan.Zero).AddHours(hour);

            if (zone.IsDaylightSavingTime(instant) == before)
            {
                continue;
            }

            var previous = instant.AddHours(-1);

            return new DaylightTransition(
                At: TimeZoneInfo.ConvertTime(instant, zone),
                Starting: !before,
                Shift: zone.GetUtcOffset(instant) - zone.GetUtcOffset(previous));
        }

        return null;
    }

    /// <summary>
    /// How an offset difference reads on a row.
    /// </summary>
    /// <remarks>
    /// Half-hour and forty-five-minute offsets are real — Adelaide, Kathmandu, Chatham
    /// Island — so this cannot print whole hours and hope.
    /// </remarks>
    public static string Describe(TimeSpan difference)
    {
        if (difference == TimeSpan.Zero)
        {
            return "same";
        }

        var sign = difference < TimeSpan.Zero ? "-" : "+";
        var magnitude = difference.Duration();

        var text = magnitude.Minutes == 0
            ? magnitude.TotalHours.ToString("0", CultureInfo.InvariantCulture)
            : $"{(int)magnitude.TotalHours}:{magnitude.Minutes:00}";

        return $"{sign}{text}h";
    }
}
