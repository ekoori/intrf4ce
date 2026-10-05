using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Intrf4ce.Plugins;

namespace Intrf4ce.Plugin.Timezones;

/// <summary>
/// The timeline on a slide, kept to the minute, with a line under it about clocks changing.
/// </summary>
/// <remarks>
/// Ticks on the minute rather than every sixty seconds from whenever it started, so the clocks it
/// shows turn over when the user's own clock does. The zones are read again on every tick: an
/// offset changes at a transition, and a slide left up across one would otherwise be an hour
/// wrong until something else made it redraw.
/// </remarks>
internal sealed class TimezonesElement : LiveElement
{
    private readonly ElementHost _host;
    private readonly ZoneTimeline _timeline;
    private readonly TextBlock _note;
    private readonly Grid _view;
    private readonly DispatcherTimer _tick;
    private IReadOnlyList<Place> _places = [];
    private IReadOnlyList<ZoneReading> _readings = [];
    private DateTimeOffset? _at;
    private int _offset;
    private string? _highlighted;

    public TimezonesElement(ElementHost host)
    {
        _host = host;
        _timeline = new ZoneTimeline(host.Theme);
        _note = new TextBlock
        {
            FontFamily = host.Theme.Font,
            FontSize = host.Theme.FontSize * 0.68,
            Foreground = host.Theme.Brush(host.Theme.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, host.Theme.FontSize * 0.5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // The note sits under the timeline, and the two together in the middle of the space: a
        // short list of places does not stretch to fill a tall element, and a long one is still
        // told how much room there is.
        Grid.SetRow(_note, 1);
        _view = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _timeline, _note },
        };

        _tick = new DispatcherTimer();
        _tick.Tick += (_, _) =>
        {
            _tick.Interval = UntilNextMinute();
            Redraw();
        };
    }

    public override Control View => _view;

    public override IReadOnlyList<string> Parts => [.. _places.Select(p => p.Part)];

    public override Task ShowAsync(JsonElement fields, CancellationToken cancellationToken)
    {
        _places = Zones.From(fields, _host.Settings());
        _at = fields.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String
            && DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? AtHome(moment)
            : null;

        Redraw();
        _tick.Interval = UntilNextMinute();
        _tick.Start();
        return Task.CompletedTask;
    }

    public override string Describe() =>
        Zones.Describe(_readings, _host.Clock.GetUtcNow(), Cursor(_host.Clock.GetUtcNow()));

    /// <summary>
    /// An hour at a time, from the agent's marked moment or from now: the clocks follow it and the
    /// timeline turns over into the next day when it gets there. Two weeks either way at most.
    /// </summary>
    public override bool MoveCursor(int steps)
    {
        _offset = Math.Clamp(_offset + steps, -24 * 14, 24 * 14);
        Redraw();
        return true;
    }

    public override void ResetCursor()
    {
        _offset = 0;
        Redraw();
    }

    public override Rect? PartBounds(string part)
    {
        var index = Index(part);
        return index < 0 || _timeline.TranslatePoint(_timeline.Band(index).TopLeft, _view) is not { } at
            ? null
            : new Rect(at, _timeline.Band(index).Size);
    }

    public override void Highlight(string? part)
    {
        _highlighted = part;
        Redraw();
    }

    protected override void Dispose(bool disposing)
    {
        _tick.Stop();
        base.Dispose(disposing);
    }

    private void Redraw()
    {
        var now = _host.Clock.GetUtcNow();
        _readings = [.. _places.Select(p => ZoneAlmanac.Read(p.Label, p.Zone, now))];
        var highlighted = _highlighted is null ? -1 : Index(_highlighted);
        _timeline.Show(_readings, now, Cursor(now), _offset != 0, highlighted);
        _note.Text = _offset != 0 && Cursor(now) is { } cursor ? AtCursor(cursor, now) : Note(now, highlighted);
        _note.Foreground = _host.Theme.Brush(_offset != 0 ? _host.Theme.Cool : _host.Theme.Muted);
    }

    /// <summary>The dashed line: the user's cursor while they move it, otherwise the agent's moment.</summary>
    private DateTimeOffset? Cursor(DateTimeOffset now) =>
        _offset == 0 ? _at : (_at ?? Hour(now)).AddHours(_offset);

    /// <summary>Now, to the hour, so the cursor steps from 15:00 to 16:00 rather than from 15:37.</summary>
    private static DateTimeOffset Hour(DateTimeOffset now) =>
        now.AddMinutes(-now.Minute).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);

    /// <summary>What the line under the timeline says while the cursor moves: every place's time then.</summary>
    private string AtCursor(DateTimeOffset cursor, DateTimeOffset now)
    {
        if (_readings.Count == 0 || !_readings[0].IsValid)
        {
            return "";
        }

        var home = _readings[0];
        var then = home.At(cursor);
        var hours = (int)Math.Round((cursor - now).TotalHours);
        var others = _readings.Skip(1).Where(r => r.IsValid).Select(r =>
        {
            var there = r.At(cursor);
            var day = there.Date != then.Date ? " " + there.ToString("ddd", CultureInfo.InvariantCulture) : "";
            return $"{r.Label} {there.ToString("HH:mm", CultureInfo.InvariantCulture)}{day}";
        });

        var from = hours >= 0 ? $"+{hours}h" : $"{hours}h";
        return $"{home.Label} {then.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)} ({from} from now):   {string.Join("  ·  ", others)}   —   r for now";
    }

    /// <summary>
    /// The line under the timeline: about the focused place when there is one — its zone, its gap
    /// from home, its daylight saving — and otherwise what the shades mean and the next change
    /// of clocks anywhere on it.
    /// </summary>
    private string Note(DateTimeOffset now, int highlighted)
    {
        if (_readings.Count == 0)
        {
            return "";
        }

        if (highlighted >= 0 && _readings[highlighted] is { IsValid: true } zone)
        {
            var parts = new List<string> { zone.ZoneId };
            if (highlighted > 0)
            {
                parts.Add($"{ZoneAlmanac.Describe(zone.Difference(_readings[0]))} from {_readings[0].Label}");
            }

            if (zone.Transitions.Count == 0)
            {
                parts.Add("no daylight saving");
            }
            else
            {
                parts.Add(zone.IsDaylightSaving ? "on daylight saving" : "on standard time");
                if (zone.Next(now) is { } next)
                {
                    parts.Add($"{(next.Starting ? "starts" : "ends")} {next.At.ToString("ddd d MMM", CultureInfo.InvariantCulture)}, in {(int)Math.Round((next.At - now).TotalDays)} days");
                }
            }

            return $"{zone.Label}: {string.Join(" · ", parts)}";
        }

        var legend = "green working · amber early and evening · grey asleep";
        var soonest = _readings
            .Where(r => r.IsValid)
            .Select(r => (Zone: r, Next: r.Next(now)))
            .Where(r => r.Next is not null && (r.Next.At - now).TotalDays <= 60)
            .OrderBy(r => r.Next!.At)
            .FirstOrDefault();

        return soonest.Next is { } change
            ? $"{legend}   ·   {soonest.Zone.Label}'s clocks go {(change.Starting ? "forward" : "back")} {change.At.ToString("ddd d MMM", CultureInfo.InvariantCulture)}"
            : legend;
    }

    private int Index(string part)
    {
        for (var i = 0; i < _places.Count; i++)
        {
            if (string.Equals(_places[i].Part, part, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A wall-clock time at home as an instant.</summary>
    private DateTimeOffset? AtHome(DateTime local)
    {
        if (_places.Count == 0 || !Zones.Exists(_places[0].Zone))
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(_places[0].Zone);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    private TimeSpan UntilNextMinute()
    {
        var now = _host.Clock.GetUtcNow();
        return TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
    }
}
