using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Intrf4ce.Plugins;

namespace Intrf4ce.Plugin.Holidays;

/// <summary>
/// The next public holidays on a slide, with where each applies and how soon it is.
/// </summary>
/// <remarks>
/// <para>
/// From Kompas's HolidaysPanel. The region column is the point: "there is a public holiday on
/// the ninth" is not a fact anyone can act on without knowing whose. The home region's holidays
/// are in full colour and everyone else's dimmed, so the ones that close your office read first
/// without the rest being hidden — they are why a colleague two states away is not answering.
/// </para>
/// <para>
/// The countdowns are re-read every half hour so "tomorrow" becomes "today" at midnight; the
/// list itself is fetched again twice a day.
/// </para>
/// </remarks>
internal sealed class HolidaysElement : LiveElement
{
    private static readonly TimeSpan Refetch = TimeSpan.FromHours(12);

    private readonly ElementHost _host;
    private readonly TextBlock _heading;
    private readonly Grid _rows;
    private readonly Viewbox _view;
    private readonly DispatcherTimer _tick;
    private readonly List<(string Part, Rectangle Band, int Row)> _parts = [];
    private JsonElement _fields;
    private HolidayQuery? _query;
    private IReadOnlyList<Holiday> _holidays = [];
    private DateTimeOffset _fetched;
    private string? _stale;
    private string? _highlighted;

    public HolidaysElement(ElementHost host)
    {
        _host = host;
        var size = host.Theme.FontSize;

        _heading = new TextBlock
        {
            FontFamily = host.Theme.Font,
            FontSize = size * 0.72,
            Foreground = host.Theme.Brush(host.Theme.Muted),
            Margin = new Thickness(0, 0, 0, size * 0.5),
        };

        _rows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

        // Scaled to the space it is given: a long list shrinks rather than running off the slide,
        // a short one grows to be read from the back of the room — no more than its minimum width
        // allows, so three holidays do not become a poster.
        _view = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { MinWidth = size * 26, Children = { _heading, _rows } },
        };

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _tick.Tick += async (_, _) => await TickAsync();
    }

    public override Control View => _view;

    public override IReadOnlyList<string> Parts => [.. _parts.Select(p => p.Part)];

    public override async Task ShowAsync(JsonElement fields, CancellationToken cancellationToken)
    {
        _fields = fields.Clone();
        await LoadAsync(cancellationToken);
        _tick.Start();
    }

    public override string Describe() =>
        _query is null ? "" : Holidays.Describe(_query, _holidays, Today()) + (_stale is null ? "" : $"\n({_stale})");

    /// <remarks>Both corners through the visual tree, so the Viewbox's scale, whichever way, is in it.</remarks>
    public override Rect? PartBounds(string part) =>
        _parts.FirstOrDefault(p => p.Part == part).Band is { } band
        && band.TranslatePoint(default, _view) is { } topLeft
        && band.TranslatePoint(new Point(band.Bounds.Width, band.Bounds.Height), _view) is { } bottomRight
            ? new Rect(topLeft, bottomRight)
            : null;

    public override void Highlight(string? part)
    {
        _highlighted = part;
        foreach (var (name, band, _) in _parts)
        {
            band.Fill = name == part ? _host.Theme.Brush(_host.Theme.Surface2) : Brushes.Transparent;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _tick.Stop();
        base.Dispose(disposing);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        _query = Holidays.Resolve(_fields, _host.Settings(), Today());
        _holidays = await Holidays.UpcomingAsync(_host.Http, _query, _host.Clock, cancellationToken);
        _fetched = _host.Clock.GetUtcNow();
        _stale = null;
        Draw();
    }

    private async Task TickAsync()
    {
        if (_host.Clock.GetUtcNow() - _fetched < Refetch && (_query is null || _query.From >= Today() || _fields.TryGetProperty("from", out _)))
        {
            Draw();
            return;
        }

        try
        {
            await LoadAsync(CancellationToken.None);
        }
        catch (HolidaysException ex)
        {
            _stale = $"could not refresh: {ex.Message}";
            _host.Log(_stale);
            Draw();
        }
    }

    private void Draw()
    {
        if (_query is not { } query)
        {
            return;
        }

        var theme = _host.Theme;
        var size = theme.FontSize;
        var today = Today();

        var heading = new StringBuilder(query.CountryName);
        if (query.Region is { } region)
        {
            heading.Append(CultureInfo.InvariantCulture, $" · {region}");
        }

        if (_stale is not null)
        {
            heading.Append(CultureInfo.InvariantCulture, $" · {_stale}");
        }

        _heading.Text = heading.ToString();
        _rows.Children.Clear();
        _rows.RowDefinitions.Clear();
        _parts.Clear();

        if (_holidays.Count == 0)
        {
            _rows.Children.Add(Text("No public holidays known from then on.", size, theme.Muted));
            return;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var row = 0; row < _holidays.Count; row++)
        {
            var holiday = _holidays[row];
            var mine = query.Region is null || holiday.AppliesIn(query.Region);
            var soon = holiday.DaysUntil(today) <= 14;
            _rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var part = Part(holiday.Name, used);
            var band = new Rectangle
            {
                Fill = part == _highlighted ? theme.Brush(theme.Surface2) : Brushes.Transparent,
                RadiusX = size * 0.25,
                RadiusY = size * 0.25,
                Margin = new Thickness(-size * 0.3, 0),
            };
            Place(band, row, 0, columnSpan: 4);
            _parts.Add((part, band, row));

            Place(Text(holiday.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture), size * 0.82, mine ? theme.Text : theme.Faint, margin: new Thickness(0, size * 0.22, size * 0.8, size * 0.22)), row, 0);

            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = size * 0.35, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Text(holiday.Name, size * 0.95, mine ? theme.Text : theme.Faint, weight: mine && soon ? FontWeight.SemiBold : FontWeight.Normal));
            if (holiday.LocalName != holiday.Name)
            {
                name.Children.Add(Text(holiday.LocalName, size * 0.7, theme.Faint));
            }

            Place(name, row, 1);

            // The regions coloured only when one is yours: a column of codes in accent would shout.
            var regions = Text(holiday.RegionLabel, size * 0.66, holiday.IsNational ? theme.Success : mine ? theme.Cool : theme.Faint, margin: new Thickness(size * 0.8, 0, 0, 0));
            Place(regions, row, 2);

            var countdown = Text(Holidays.Countdown(holiday, today), size * 0.72, !mine ? theme.Faint : holiday.DaysUntil(today) <= 1 ? theme.Success : soon ? theme.Accent : theme.Muted, margin: new Thickness(size * 0.8, 0, 0, 0));
            countdown.HorizontalAlignment = HorizontalAlignment.Right;
            Place(countdown, row, 3);
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(_host.Clock.GetLocalNow().DateTime);

    private void Place(Control control, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
        _rows.Children.Add(control);
    }

    private TextBlock Text(string text, double size, Color color, FontWeight weight = FontWeight.Normal, Thickness margin = default) => new()
    {
        Text = text,
        FontFamily = _host.Theme.Font,
        FontSize = size,
        FontWeight = weight,
        Foreground = _host.Theme.Brush(color),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = margin,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>A holiday's id for focus cues: its name in lower case with '-' between words, made unique.</summary>
    private static string Part(string name, HashSet<string> used)
    {
        var part = new StringBuilder();
        foreach (var c in name.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                part.Append(c);
            }
            else if (c is '\'' or '’')
            {
                // King's Birthday is kings-birthday, not king-s-birthday.
            }
            else if (char.IsWhiteSpace(c) || c is '-' or '.')
            {
                if (part.Length > 0 && part[^1] != '-')
                {
                    part.Append('-');
                }
            }
        }

        var stem = part.ToString().Trim('-');
        var id = stem.Length > 0 ? stem : "holiday";
        for (var n = 2; !used.Add(id); n++)
        {
            id = $"{stem}-{n}";
        }

        return id;
    }
}
