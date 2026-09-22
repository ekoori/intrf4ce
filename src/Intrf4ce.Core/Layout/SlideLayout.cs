using Intrf4ce.Core.Model;

namespace Intrf4ce.Core.Layout;

/// <summary>
/// Where each element goes on a slide.
/// </summary>
/// <remarks>
/// The agent names a layout and an order, or neither, and this decides the geometry. Keeping
/// it here rather than in the view means the agent's <c>get_state</c> and the stage agree on
/// where things are, and it is testable without a window.
/// </remarks>
public static class SlideLayout
{
    public const double Width = 1600;
    public const double Height = 900;
    public const double PaddingX = 72;
    public const double PaddingTop = 60;
    public const double PaddingBottom = 64;
    public const double TitleBand = 92;
    public const double Gap = 40;

    public static readonly IReadOnlyList<string> Kinds = ["full", "split", "wide", "stack", "grid", "free"];

    public static Box Slide => new(0, 0, Width, Height);

    /// <summary>The part of the slide elements may use, below the title when there is one.</summary>
    public static Box ContentArea(bool hasTitle)
    {
        var top = PaddingTop + (hasTitle ? TitleBand : 0);
        return new Box(PaddingX, top, Width - (2 * PaddingX), Height - top - PaddingBottom);
    }

    /// <summary>The areas an element may name under each layout.</summary>
    public static IReadOnlyList<string> AreasOf(string layout) => layout switch
    {
        "split" => ["left", "right"],
        "wide" => ["main", "side"],
        _ => [],
    };

    /// <summary>The layout that will actually be used: the slide's own, or one chosen for its elements.</summary>
    public static string Resolve(Slide slide)
    {
        ArgumentNullException.ThrowIfNull(slide);

        if (slide.Layout is { } named && Kinds.Contains(named))
        {
            return named;
        }

        var elements = slide.Elements;
        switch (elements.Count)
        {
            case <= 1:
                return "full";

            case 2:
                var visuals = elements.Count(e => e.IsVisual);
                return visuals == 1 ? "wide" : "split";

            case 3 when elements.Count(e => e.IsVisual) >= 1:
                return "wide";

            case 3:
                return "stack";

            default:
                return "grid";
        }
    }

    /// <summary>A box for every element, in slide units.</summary>
    public static IReadOnlyDictionary<string, Box> Arrange(Slide slide)
    {
        ArgumentNullException.ThrowIfNull(slide);

        var layout = Resolve(slide);
        var content = ContentArea(!string.IsNullOrWhiteSpace(slide.Title));
        var elements = slide.Elements;
        var result = new Dictionary<string, Box>(StringComparer.Ordinal);

        if (elements.Count == 0)
        {
            return result;
        }

        switch (layout)
        {
            case "free":
                foreach (var element in elements)
                {
                    result[element.Id] = element.Rect is [var x, var y, var w, var h]
                        ? Slide.Fraction(x, y, w, h)
                        : content;
                }

                break;

            case "split":
                Columns(content, [0.5, 0.5], ["left", "right"], elements, result);
                break;

            case "wide":
                // The visual goes in the wide column even when the agent listed the text first.
                var hero = elements.FirstOrDefault(e => e.IsVisual);
                IReadOnlyList<Element> ordered = hero is null || elements.Any(e => e.Area is not null)
                    ? elements
                    : [hero, .. elements.Where(e => !ReferenceEquals(e, hero))];
                Columns(content, [0.62, 0.38], ["main", "side"], ordered, result);
                break;

            case "grid":
                Grid(content, elements, result);
                break;

            default:
                Stack(content, elements, result);
                break;
        }

        return result;
    }

    private static void Columns(
        Box content, double[] shares, string[] areas, IReadOnlyList<Element> elements, Dictionary<string, Box> result)
    {
        var usable = content.Width - (Gap * (shares.Length - 1));
        var columns = new List<Box>();
        var x = content.X;
        foreach (var share in shares)
        {
            columns.Add(new Box(x, content.Y, usable * share, content.Height));
            x += (usable * share) + Gap;
        }

        // Named areas first; then the rest fill columns in order, the first taking the first column.
        var buckets = areas.Select(_ => new List<Element>()).ToArray();
        var unplaced = new List<Element>();
        foreach (var element in elements)
        {
            var index = element.Area is null ? -1 : Array.IndexOf(areas, element.Area);
            if (index >= 0)
            {
                buckets[index].Add(element);
            }
            else
            {
                unplaced.Add(element);
            }
        }

        foreach (var element in unplaced)
        {
            var empty = Array.FindIndex(buckets, b => b.Count == 0);
            buckets[empty >= 0 ? empty : buckets.Length - 1].Add(element);
        }

        for (var i = 0; i < buckets.Length; i++)
        {
            Stack(columns[i], buckets[i], result);
        }
    }

    private static void Stack(Box area, IReadOnlyList<Element> elements, Dictionary<string, Box> result)
    {
        if (elements.Count == 0)
        {
            return;
        }

        var weights = elements.Select(WeightOf).ToArray();
        var total = weights.Sum();
        var usable = area.Height - (Gap * (elements.Count - 1));
        var y = area.Y;

        for (var i = 0; i < elements.Count; i++)
        {
            var height = usable * weights[i] / total;
            result[elements[i].Id] = new Box(area.X, y, area.Width, height);
            y += height + Gap;
        }
    }

    private static void Grid(Box area, IReadOnlyList<Element> elements, Dictionary<string, Box> result)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(elements.Count));
        var rows = (int)Math.Ceiling(elements.Count / (double)columns);
        var cellWidth = (area.Width - (Gap * (columns - 1))) / columns;
        var cellHeight = (area.Height - (Gap * (rows - 1))) / rows;

        for (var i = 0; i < elements.Count; i++)
        {
            var row = i / columns;
            var column = i % columns;

            // A short last row is centred rather than left-aligned.
            var inRow = row == rows - 1 ? elements.Count - (row * columns) : columns;
            var offset = (columns - inRow) * (cellWidth + Gap) / 2;

            result[elements[i].Id] = new Box(
                area.X + offset + (column * (cellWidth + Gap)),
                area.Y + (row * (cellHeight + Gap)),
                cellWidth,
                cellHeight);
        }
    }

    private static double WeightOf(Element element) => element switch
    {
        TitleElement => 0.8,
        MathElement => 0.6,
        QuizElement => 1.1,
        TableElement => 1.2,
        MarkdownElement => 1,
        _ => 1.6,
    };
}
