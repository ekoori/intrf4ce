using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// A table: numbers right-aligned, headers that sort, rows that can be focused by their key.
/// </summary>
/// <remarks>
/// Like markdown, a table that does not fit steps its type down a few sizes before it
/// scrolls. Sorting is the viewer's own business: it is not saved, and row names follow the
/// key column wherever the row ends up.
/// </remarks>
public sealed partial class TableView : ElementView
{
    private const int MaxRows = 200;
    private static readonly double[] Scales = [1.0, 0.9, 0.8, 0.7, 0.6];

    private readonly ScrollViewer _scroller;
    private readonly Dictionary<string, Border> _rows = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _rowIds = [];
    private double _scale = 1;
    private double _fittedFor = -1;
    private int _sortColumn = -1;
    private bool _descending;
    private string? _highlight;

    public TableView(ElementContext context, TableElement element)
        : base(context, element)
    {
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Children.Add(_scroller);
    }

    private TableElement Table => (TableElement)Element;

    public override IReadOnlyList<string> Parts => _rowIds;

    public override Rect? PartBounds(string part) =>
        Row(part) is { } row && row.TranslatePoint(default, this) is { } origin ? new Rect(origin, row.Bounds.Size) : null;

    public override string Describe(string? part)
    {
        var index = part is null ? -1 : IndexOf(part);
        if (index < 0)
        {
            return Table.Caption ?? $"this table ({string.Join(", ", Table.Columns.Take(4))})";
        }

        var key = Table.Key is { } name ? Math.Max(0, Table.Columns.ToList().IndexOf(name)) : 0;
        var row = Table.Rows[index];
        return key < row.Count ? ElementParts.CellText(row[key]) ?? part! : part!;
    }

    public override void Highlight(string? part)
    {
        _highlight = part is null ? null : _rowIds.ElementAtOrDefault(IndexOf(part));
        foreach (var (id, row) in _rows)
        {
            Style(row, id == _highlight, IsStriped(row));
        }

        // A long table scrolls; the row being talked about has to be on it.
        if (_highlight is not null && _rows.TryGetValue(_highlight, out var focused))
        {
            focused.BringIntoView();
        }
    }

    protected override Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        var table = (TableElement)element;
        _rowIds = ElementParts.TableRows(table);
        _scale = 1;
        _fittedFor = -1;

        if (_sortColumn >= table.Columns.Count)
        {
            _sortColumn = -1;
        }

        if (table.Rows.Count > MaxRows)
        {
            Warn($"{table.Rows.Count} rows is more than a slide can show; showing the first {MaxRows}. Summarise, or split the table.");
        }

        Build();
        return Task.CompletedTask;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        // Step down once per size: if the table overflows at this scale, try the next.
        if (_fittedFor != finalSize.Height && _scroller.Content is Control content)
        {
            content.Measure(new Size(finalSize.Width, double.PositiveInfinity));
            if (content.DesiredSize.Height > finalSize.Height + 1)
            {
                var next = Array.FindIndex(Scales, s => s < _scale - 0.001);
                if (next >= 0)
                {
                    _scale = Scales[next];
                    Build();
                    return result;
                }
            }

            _fittedFor = finalSize.Height;
        }

        return result;
    }

    private void Build()
    {
        var table = Table;
        _rows.Clear();

        var columns = table.Columns.Count;
        var shown = Enumerable.Range(0, Math.Min(table.Rows.Count, MaxRows)).ToList();
        var numeric = Enumerable.Range(0, columns).Select(c => IsNumeric(table, c)).ToArray();
        var grouped = Enumerable.Range(0, columns).Select(c => NeedsSeparators(table, c)).ToArray();

        if (_sortColumn >= 0)
        {
            var comparer = Comparer<int>.Create((a, b) => Compare(Cell(table, a, _sortColumn), Cell(table, b, _sortColumn)));
            shown = [.. _descending ? shown.OrderByDescending(i => i, comparer) : shown.OrderBy(i => i, comparer)];
        }

        var grid = new Grid();
        foreach (var weight in Weights(table))
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(weight, GridUnitType.Star));
        }

        var size = 22 * _scale;

        // Header
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var headerLine = new Border
        {
            BorderBrush = Skin.Brush(Skin.Line),
            BorderThickness = new Thickness(0, 0, 0, 2),
            [Grid.ColumnSpanProperty] = Math.Max(1, columns),
        };
        grid.Children.Add(headerLine);

        for (var c = 0; c < columns; c++)
        {
            grid.Children.Add(Header(table.Columns[c], c, numeric[c], size));
        }

        // Rows
        for (var r = 0; r < shown.Count; r++)
        {
            var index = shown[r];
            var id = _rowIds[index];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var background = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Tag = r % 2 == 1,
                [Grid.RowProperty] = r + 1,
                [Grid.ColumnSpanProperty] = Math.Max(1, columns),
            };

            Style(background, id == _highlight, r % 2 == 1);
            _rows[id] = background;
            grid.Children.Add(background);

            for (var c = 0; c < columns; c++)
            {
                var cell = Cell(table, index, c);
                var text = CellView(cell, numeric[c], grouped[c], size);
                text[Grid.RowProperty] = r + 1;
                text[Grid.ColumnProperty] = c;
                grid.Children.Add(text);
            }
        }

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(grid);
        if (CaptionFor(table, Skin) is { } caption)
        {
            root.Children.Add(caption);
        }

        _scroller.Content = root;
    }

    private Control Header(string name, int column, bool numeric, double size)
    {
        var arrow = column == _sortColumn ? (_descending ? " ▼" : " ▲") : "";
        var header = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(14 * _scale, 10 * _scale),
            Cursor = new Cursor(StandardCursorType.Hand),
            [Grid.ColumnProperty] = column,
            Child = new TextBlock
            {
                Text = name.ToUpperInvariant() + arrow,
                FontSize = size * 0.72,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 1.2,
                Foreground = Skin.Brush(column == _sortColumn ? Skin.Accent : Skin.Muted),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
            },
        };

        // Handled on press, so the stage does not take it for the start of a drag.
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;
            if (_sortColumn == column)
            {
                // Ascending, descending, then back to the order the agent gave.
                _sortColumn = _descending ? -1 : column;
                _descending = !_descending && _sortColumn >= 0;
            }
            else
            {
                _sortColumn = column;
                _descending = false;
            }

            Build();
        };

        header.DoubleTapped += (_, e) => e.Handled = true;
        return header;
    }

    private TextBlock CellView(JsonElement? cell, bool numeric, bool grouped, double size)
    {
        var block = new TextBlock
        {
            FontSize = size,
            Margin = new Thickness(14 * _scale, 9 * _scale),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Skin.Brush(Skin.Text),
            TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
            FontFeatures = numeric ? [FontFeature.Parse("tnum")] : null,
        };

        switch (cell?.ValueKind)
        {
            case null or JsonValueKind.Null or JsonValueKind.Undefined:
                block.Text = "—";
                block.Foreground = Skin.Brush(Skin.Faint);
                break;

            case JsonValueKind.True:
                block.Text = "✓";
                block.Foreground = Skin.Brush(Skin.Success);
                break;

            case JsonValueKind.False:
                block.Text = "✗";
                block.Foreground = Skin.Brush(Skin.Danger);
                break;

            case JsonValueKind.Number:
                block.Text = FormatNumber(cell.Value, grouped);
                break;

            default:
                block.Text = ElementParts.CellText(cell.Value) ?? "";
                break;
        }

        return block;
    }

    private void Style(Border row, bool focused, bool striped)
    {
        row.Background = focused ? Skin.Brush(Skin.Accent, 0.16) : striped ? Skin.Brush(Skin.Surface2, 0.55) : Brushes.Transparent;
        row.BorderBrush = focused ? Skin.Brush(Skin.Accent) : Brushes.Transparent;
    }

    private static bool IsStriped(Border row) => row.Tag is true;

    private Border? Row(string part) => _rowIds.ElementAtOrDefault(IndexOf(part)) is { } id ? _rows.GetValueOrDefault(id) : null;

    /// <summary>The row a part names: its id, or failing that the same words as a slug ("Knee joint").</summary>
    private int IndexOf(string part)
    {
        var index = _rowIds.ToList().IndexOf(part);
        return index >= 0 ? index : _rowIds.ToList().IndexOf(Slug.From(part, part));
    }

    private static JsonElement? Cell(TableElement table, int row, int column) =>
        column < table.Rows[row].Count ? table.Rows[row][column] : null;

    /// <summary>Numbers, and strings that are numbers with a unit or currency ("12 %", "$4.50"), align right.</summary>
    private static bool IsNumeric(TableElement table, int column)
    {
        var any = false;
        foreach (var row in table.Rows.Take(MaxRows))
        {
            if (column >= row.Count || row[column].ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            any = true;
            if (row[column].ValueKind != JsonValueKind.Number
                && !(row[column].ValueKind == JsonValueKind.String && Quantity().IsMatch(row[column].GetString() ?? "")))
            {
                return false;
            }
        }

        return any;
    }

    /// <summary>Numbers compare as numbers (so 9 sorts before 10), everything else as words; blanks last.</summary>
    private static int Compare(JsonElement? a, JsonElement? b)
    {
        var x = Value(a);
        var y = Value(b);
        if (x is null || y is null)
        {
            return (x is null ? 1 : 0) - (y is null ? 1 : 0);
        }

        if (x is double dx && y is double dy)
        {
            return dx.CompareTo(dy);
        }

        return string.Compare(x.ToString(), y.ToString(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
    }

    private static object? Value(JsonElement? cell)
    {
        switch (cell?.ValueKind)
        {
            case JsonValueKind.Number:
                return cell.Value.GetDouble();

            case JsonValueKind.String:
                var text = cell.Value.GetString() ?? "";
                var match = Quantity().Match(text);
                return match.Success && double.TryParse(match.Groups["n"].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? (match.Groups["neg"].Success ? -number : number)
                    : text;

            case JsonValueKind.True or JsonValueKind.False:
                return cell.Value.GetBoolean() ? 1.0 : 0.0;

            default:
                return null;
        }
    }

    /// <summary>
    /// A column with numbers of five digits or more gets thousands separators throughout, so
    /// its digits line up; a column of years (1999, 2004) stays as written.
    /// </summary>
    private static bool NeedsSeparators(TableElement table, int column) =>
        table.Rows.Take(MaxRows).Any(row =>
            column < row.Count && row[column].ValueKind == JsonValueKind.Number
            && row[column].TryGetDouble(out var value) && Math.Abs(value) >= 10_000);

    private static string FormatNumber(JsonElement number, bool grouped)
    {
        if (!number.TryGetDouble(out var value))
        {
            return number.GetRawText();
        }

        if (!grouped)
        {
            return value.ToString("0.##########", CultureInfo.CurrentCulture);
        }

        var raw = number.GetRawText();
        var decimals = raw.Contains('.', StringComparison.Ordinal) && !raw.Contains('e', StringComparison.OrdinalIgnoreCase)
            ? raw.Length - raw.IndexOf('.', StringComparison.Ordinal) - 1
            : 0;
        return value.ToString("N" + Math.Min(decimals, 6), CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Column widths in proportion to their longest text, within limits, so a notes column gets
    /// room; never so narrow that a heading has to break inside a word.
    /// </summary>
    private static IEnumerable<double> Weights(TableElement table) =>
        table.Columns.Select((name, c) =>
        {
            var longest = table.Rows.Take(MaxRows)
                .Select(row => c < row.Count ? (ElementParts.CellText(row[c]) ?? "").Length : 0)
                .DefaultIfEmpty(0)
                .Max();
            var word = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length).DefaultIfEmpty(0).Max();
            return Math.Clamp(Math.Max(Math.Max(longest, word * 1.25), name.Length * 0.7), 4, 42);
        });

    [GeneratedRegex(@"^\s*(?<neg>[-−])?\s*[$€£¥]?\s*(?<n>\d[\d,]*(\.\d+)?)\s*(%|[A-Za-zµ°]{1,4})?\s*$")]
    private static partial Regex Quantity();
}
