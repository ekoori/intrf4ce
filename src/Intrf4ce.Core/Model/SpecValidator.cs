using System.Text.Json;
using System.Text.RegularExpressions;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Narration;

namespace Intrf4ce.Core.Model;

public enum IssueSeverity
{
    /// <summary>The slide is rejected.</summary>
    Error,

    /// <summary>The slide is accepted; the agent is told what will not work as it hoped.</summary>
    Warning,
}

/// <summary>One problem with what the agent sent, located by JSON path.</summary>
public sealed record SpecIssue(IssueSeverity Severity, string Path, string Message)
{
    public override string ToString() => $"{(Severity == IssueSeverity.Error ? "error" : "warning")} {Path}: {Message}";
}

/// <summary>
/// Checks a slide the way a careful reviewer would, and says exactly what to change.
/// </summary>
/// <remarks>
/// Every message is written for the agent that will read it: the path to the value, what was
/// wrong, and what would have been right. A vague error costs a whole round trip of guessing.
/// Problems the stage can live with (a cue naming a part that does not exist) are warnings,
/// so one slip does not throw away an otherwise good slide.
/// </remarks>
public static partial class SpecValidator
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    private static readonly string[] Fits = ["contain", "cover"];
    private static readonly string[] Sides = ["auto", "left", "right"];
    private static readonly string[] ChartKinds = ["bar", "line", "area", "pie", "donut", "scatter"];
    private static readonly string[] Arrangements = ["scatter", "grid", "timeline"];
    private static readonly string[] Advances = ["auto", "manual"];

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Fills in missing ids: slide ids from the title, element ids from the type.</summary>
    public static Slide WithIds(Slide slide, Func<string, bool> slideIdTaken)
    {
        ArgumentNullException.ThrowIfNull(slide);
        ArgumentNullException.ThrowIfNull(slideIdTaken);

        var id = slide.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            var stem = Slug.From(slide.Title, "slide");
            id = stem;
            for (var n = 2; slideIdTaken(id); n++)
            {
                id = $"{stem}-{n}";
            }
        }

        var used = new HashSet<string>(slide.Elements.Select(e => e.Id).Where(e => e.Length > 0), StringComparer.Ordinal);
        var elements = new List<Element>(slide.Elements.Count);
        foreach (var element in slide.Elements)
        {
            if (!string.IsNullOrWhiteSpace(element.Id))
            {
                elements.Add(element);
                continue;
            }

            var generated = element.TypeName;
            for (var n = 2; used.Contains(generated); n++)
            {
                generated = $"{element.TypeName}{n}";
            }

            used.Add(generated);
            elements.Add(element with { Id = generated });
        }

        return slide with { Id = id, Elements = elements };
    }

    public static IReadOnlyList<SpecIssue> Validate(Slide slide)
    {
        ArgumentNullException.ThrowIfNull(slide);

        var issues = new List<SpecIssue>();

        if (!IsValidId(slide.Id))
        {
            Error(issues, "id", $"'{slide.Id}' is not a valid id: use 1-64 letters, digits, '_' or '-'");
        }

        var layout = slide.Layout;
        if (layout is not null && !SlideLayout.Kinds.Contains(layout))
        {
            Error(issues, "layout", $"unknown layout '{layout}'; use one of {List(SlideLayout.Kinds)}, or leave it out to have one chosen");
        }

        if (slide.Advance is { } advance && !Advances.Contains(advance))
        {
            Error(issues, "advance", $"'{advance}' is not a way to advance; use auto or manual");
        }

        var resolved = SlideLayout.Resolve(slide);
        var areas = SlideLayout.AreasOf(resolved);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < slide.Elements.Count; i++)
        {
            var element = slide.Elements[i];
            var path = $"elements[{i}]";

            if (!IsValidId(element.Id))
            {
                Error(issues, path + ".id", $"'{element.Id}' is not a valid id: use 1-64 letters, digits, '_' or '-'");
            }
            else if (!seen.Add(element.Id))
            {
                Error(issues, path + ".id", $"'{element.Id}' is used by another element on this slide; ids must be unique");
            }

            if (element.Area is { } area && areas.Count > 0 && !areas.Contains(area))
            {
                Warn(issues, path + ".area", $"'{area}' is not an area of the {resolved} layout ({List(areas)}); placed in order instead");
            }

            if (element.Rect is { } rect)
            {
                CheckFractions(issues, path + ".rect", rect, 4, "[x, y, w, h]");
                if (resolved != "free")
                {
                    Warn(issues, path + ".rect", "rect only applies to the free layout; ignored");
                }
            }

            ValidateElement(element, path, issues);
        }

        ValidateSize(slide, issues);
        ValidateNarration(slide, issues);
        return issues;
    }

    public static bool HasErrors(IEnumerable<SpecIssue> issues) =>
        issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>Checks a narration's cues against the slide's elements and their known parts.</summary>
    /// <param name="knownParts">Parts learned from rendering (Mermaid node ids), by element id.</param>
    public static IEnumerable<SpecIssue> CheckCueTargets(
        Slide slide, ParsedNarration narration, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? knownParts = null)
    {
        ArgumentNullException.ThrowIfNull(slide);
        ArgumentNullException.ThrowIfNull(narration);

        foreach (var cue in narration.Cues)
        {
            if (cue.ElementId is not { } elementId)
            {
                continue;
            }

            var element = slide.Find(elementId);
            if (element is null)
            {
                yield return new SpecIssue(IssueSeverity.Warning, "narration",
                    $"cue targets '{cue.Target}' but there is no element '{elementId}' (elements: {List(slide.Elements.Select(e => e.Id))})");
                continue;
            }

            if (cue.PartId is not { } part)
            {
                continue;
            }

            var parts = PartsOf(element, knownParts);
            if (parts is not null && !parts.Contains(part))
            {
                yield return new SpecIssue(IssueSeverity.Warning, "narration",
                    $"cue targets '{cue.Target}' but '{elementId}' has no part '{part}'"
                    + (parts.Count > 0 ? $" (parts: {List(parts.Take(30))})" : ""));
            }
        }
    }

    /// <summary>What can follow the dot in a cue for this element, or null when it cannot be known yet.</summary>
    public static IReadOnlyCollection<string>? PartsOf(
        Element element, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? knownParts = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (knownParts is not null && knownParts.TryGetValue(element.Id, out var known))
        {
            return known;
        }

        return element switch
        {
            ImageElement image => image.Callouts?.Select(c => c.Id).ToList() ?? [],
            // A folder's photos are named after files nobody has listed yet.
            GalleryElement gallery => string.IsNullOrWhiteSpace(gallery.Folder)
                ? ElementParts.GalleryItems(gallery.Items ?? [], new HashSet<string>(StringComparer.Ordinal)).ToList()
                : null,
            TableElement table => ElementParts.TableRows(table).ToList(),
            MapElement map => ElementParts.Places(map.Places ?? []).ToList(),
            MarkdownElement => null,
            DiagramElement => null,
            _ => [],
        };
    }

    private static void ValidateSize(Slide slide, List<SpecIssue> issues)
    {
        if (slide.Size is null)
        {
            return;
        }

        if (slide.Size is not [var width, var height] || width <= 0 || height <= 0)
        {
            Error(issues, "size", "size is [width, height] in slide units; a slide is [1600, 900]");
            return;
        }

        if (width < SlideLayout.MinWidth || height < SlideLayout.MinHeight || width > SlideLayout.MaxWidth || height > SlideLayout.MaxHeight)
        {
            Warn(issues, "size",
                $"[{width:0}, {height:0}] is outside {SlideLayout.MinWidth:0}x{SlideLayout.MinHeight:0} to {SlideLayout.MaxWidth:0}x{SlideLayout.MaxHeight:0}; it is drawn at the nearest size within that");
        }

        if (SlideLayout.IsCanvas(slide) && SlideLayout.Resolve(slide) != "free" && slide.Elements.Count > 1)
        {
            Warn(issues, "layout", "a canvas is laid out best by hand: layout free, with a rect for each element");
        }
    }

    private static void ValidateNarration(Slide slide, List<SpecIssue> issues)
    {
        if (slide.Narration is null)
        {
            return;
        }

        var parsed = CueParser.Parse(slide.Narration);
        foreach (var error in parsed.Errors)
        {
            Error(issues, "narration", error);
        }

        issues.AddRange(CheckCueTargets(slide, parsed));

        foreach (var cue in parsed.Cues.Where(c => c.Kind is CueKind.Show or CueKind.Hide))
        {
            if (slide.Find(cue.ElementId!) is { Hidden: false } visible && cue.Kind == CueKind.Show)
            {
                Warn(issues, "narration", $"[[show {visible.Id}]] reveals an element that is not hidden; set \"hidden\": true on it");
            }
        }
    }

    private static void ValidateElement(Element element, string path, List<SpecIssue> issues)
    {
        switch (element)
        {
            case TitleElement title:
                Required(issues, path + ".text", title.Text);
                break;

            case MarkdownElement markdown:
                Required(issues, path + ".text", markdown.Text);
                break;

            case ImageElement image:
                ValidateImage(image, path, issues);
                break;

            case DiagramElement diagram:
                Required(issues, path + ".mermaid", diagram.Mermaid);
                if (diagram.Steps is { } steps && steps.Any(string.IsNullOrWhiteSpace))
                {
                    Error(issues, path + ".steps", "steps are Mermaid node ids and cannot be empty");
                }

                break;

            case MathElement math:
                Required(issues, path + ".tex", math.Tex);
                if (math.Tex.TrimStart().StartsWith('$'))
                {
                    Warn(issues, path + ".tex", "tex is TeX source without $ delimiters; they will be stripped");
                }

                break;

            case ChartElement chart:
                ValidateChart(chart, path, issues);
                break;

            case TableElement table:
                ValidateTable(table, path, issues);
                break;

            case GalleryElement gallery:
                ValidateGallery(gallery, path, issues);
                break;

            case QuizElement quiz:
                ValidateQuiz(quiz, path, issues);
                break;

            case MapElement map:
                ValidateMap(map, path, issues);
                break;
        }
    }

    private static void ValidateImage(ImageElement image, string path, List<SpecIssue> issues)
    {
        Required(issues, path + ".src", image.Src);

        if (image.Fit is { } fit && !Fits.Contains(fit))
        {
            Error(issues, path + ".fit", $"'{fit}' is not a fit; use contain or cover");
        }

        if (image.Callouts is not { } callouts)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 0; c < callouts.Count; c++)
        {
            var callout = callouts[c];
            var cpath = $"{path}.callouts[{c}]";

            if (!IsValidId(callout.Id))
            {
                Error(issues, cpath + ".id", $"'{callout.Id}' is not a valid id: use 1-64 letters, digits, '_' or '-'");
            }
            else if (!ids.Add(callout.Id))
            {
                Error(issues, cpath + ".id", $"'{callout.Id}' is used by another callout on this image");
            }

            Required(issues, cpath + ".label", callout.Label);

            if (callout.At is null == callout.Box is null)
            {
                Error(issues, cpath, "give exactly one of at: [x, y] (a point) or box: [x, y, w, h] (an area), as fractions of the image");
            }

            if (callout.At is { } at)
            {
                CheckFractions(issues, cpath + ".at", at, 2, "[x, y]");
            }

            if (callout.Box is { } box)
            {
                CheckFractions(issues, cpath + ".box", box, 4, "[x, y, w, h]");
            }

            if (callout.Side is { } side && !Sides.Contains(side))
            {
                Error(issues, cpath + ".side", $"'{side}' is not a side; use auto, left or right");
            }

            if (callout.Label.Length > 40)
            {
                Warn(issues, cpath + ".label", "labels read best under 40 characters; put the rest in detail");
            }
        }
    }

    private static void ValidateChart(ChartElement chart, string path, List<SpecIssue> issues)
    {
        if (chart.VegaLite is { } spec)
        {
            if (spec.ValueKind != JsonValueKind.Object)
            {
                Error(issues, path + ".vegaLite", "must be a Vega-Lite spec object");
            }

            return;
        }

        if (chart.Kind is null)
        {
            Error(issues, path, $"give kind ({List(ChartKinds)}) with data, x and y, or a complete vegaLite spec");
            return;
        }

        if (!ChartKinds.Contains(chart.Kind))
        {
            Error(issues, path + ".kind", $"'{chart.Kind}' is not a chart kind; use one of {List(ChartKinds)}, or give a vegaLite spec");
        }

        if (chart.Data is not { ValueKind: JsonValueKind.Array } data || data.GetArrayLength() == 0)
        {
            Error(issues, path + ".data", "must be a non-empty array of objects, e.g. [{\"month\":\"Jan\",\"sales\":4}]");
            return;
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                Error(issues, $"{path}.data[{index}]", "each row must be an object of field: value");
                return;
            }

            foreach (var property in row.EnumerateObject())
            {
                fields.Add(property.Name);
            }

            index++;
        }

        Required(issues, path + ".x", chart.X);
        Required(issues, path + ".y", chart.Y);

        foreach (var (name, field) in new[] { ("x", chart.X), ("y", chart.Y), ("series", chart.Series) })
        {
            if (field is not null && !fields.Contains(field))
            {
                Error(issues, $"{path}.{name}", $"field '{field}' is not in the data (fields: {List(fields)})");
            }
        }
    }

    private static void ValidateTable(TableElement table, string path, List<SpecIssue> issues)
    {
        if (table.Columns.Count == 0)
        {
            Error(issues, path + ".columns", "a table needs at least one column");
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            if (row.Count > table.Columns.Count)
            {
                Warn(issues, $"{path}.rows[{r}]", $"has {row.Count} cells but there are {table.Columns.Count} columns; extra cells are dropped");
            }

            for (var c = 0; c < row.Count; c++)
            {
                if (row[c].ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    Error(issues, $"{path}.rows[{r}][{c}]", "cells must be strings, numbers, booleans or null");
                }
            }
        }

        if (table.Key is { } key && !table.Columns.Contains(key))
        {
            Error(issues, path + ".key", $"'{key}' is not a column (columns: {List(table.Columns)})");
        }
    }

    private static void ValidateGallery(GalleryElement gallery, string path, List<SpecIssue> issues)
    {
        if ((gallery.Items is null || gallery.Items.Count == 0) && string.IsNullOrWhiteSpace(gallery.Folder))
        {
            Error(issues, path, "give items: [{src, caption}] or a folder");
        }

        if (gallery.Arrange is { } arrange && !Arrangements.Contains(arrange))
        {
            Error(issues, path + ".arrange", $"'{arrange}' is not an arrangement; use one of {List(Arrangements)}");
        }

        if (gallery.Items is { } items)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                Required(issues, $"{path}.items[{i}].src", items[i].Src);
                if (items[i].Id is { } id && (!IsValidId(id) || !ids.Add(id)))
                {
                    Error(issues, $"{path}.items[{i}].id", $"'{id}' must be a unique id of letters, digits, '_' or '-'");
                }
            }
        }
    }

    private static void ValidateMap(MapElement map, string path, List<SpecIssue> issues)
    {
        var places = map.Places ?? [];
        if (places.Count == 0 && map.Center is null)
        {
            Error(issues, path, "a map needs places, or a center to show");
        }

        if (map.Center is { } centre && !IsPlace(centre))
        {
            Error(issues, path + ".center", "center is [latitude, longitude]: latitude -90 to 90, longitude -180 to 180");
        }

        if (map.Zoom is { } zoom && (zoom < WebMercator.MinZoom || zoom > WebMercator.MaxZoom))
        {
            Error(issues, path + ".zoom", $"zoom is {WebMercator.MinZoom} (the world) to {WebMercator.MaxZoom} (a doorway)");
        }

        for (var i = 0; i < places.Count; i++)
        {
            var where = $"{path}.places[{i}]";
            Required(issues, where + ".label", places[i].Label);

            if (places[i].At is not { } at)
            {
                Error(issues, where + ".at", "at is [latitude, longitude]; every place needs one");
            }
            else if (!IsPlace(at))
            {
                Error(issues, where + ".at",
                    $"[{string.Join(", ", at)}] is not a place: at is [latitude, longitude], latitude -90 to 90, longitude -180 to 180"
                    + (at.Length == 2 && Math.Abs(at[0]) > 90 && Math.Abs(at[1]) <= 90 ? " (the wrong way round?)" : ""));
            }

            if (places[i].Zoom is { } close && (close < WebMercator.MinZoom || close > WebMercator.MaxZoom))
            {
                Error(issues, where + ".zoom", $"zoom is {WebMercator.MinZoom} to {WebMercator.MaxZoom}");
            }
        }
    }

    private static bool IsPlace(double[] at) =>
        at is [var latitude, var longitude] && Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180
        && !double.IsNaN(latitude) && !double.IsNaN(longitude);

    private static void ValidateQuiz(QuizElement quiz, string path, List<SpecIssue> issues)
    {
        Required(issues, path + ".question", quiz.Question);

        if (quiz.Options.Count < 2)
        {
            Error(issues, path + ".options", "a quiz needs at least two options");
        }

        if (quiz.Answer is { } answer)
        {
            foreach (var index in answer)
            {
                if (index < 0 || index >= quiz.Options.Count)
                {
                    Error(issues, path + ".answer", $"{index} is not an option index (0 to {quiz.Options.Count - 1})");
                }
            }

            if (!quiz.Multiple && answer.Count > 1)
            {
                Error(issues, path + ".answer", "several correct answers need \"multiple\": true");
            }
        }
    }

    private static void CheckFractions(List<SpecIssue> issues, string path, double[] values, int count, string shape)
    {
        if (values.Length != count)
        {
            Error(issues, path, $"expected {shape}, got {values.Length} numbers");
            return;
        }

        if (values.Any(v => double.IsNaN(v) || v < 0 || v > 1))
        {
            Error(issues, path, $"expected {shape} as fractions within 0..1 (0,0 is the top-left), got [{string.Join(", ", values)}]");
            return;
        }

        if (count == 4)
        {
            if (values[2] <= 0 || values[3] <= 0)
            {
                Error(issues, path, "width and height must be greater than 0");
            }
            else if (values[0] + values[2] > 1.0001 || values[1] + values[3] > 1.0001)
            {
                Warn(issues, path, "extends past the edge; x + w and y + h should be at most 1");
            }
        }
    }

    private static void Required(List<SpecIssue> issues, string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Error(issues, path, "is required and cannot be empty");
        }
    }

    private static void Error(List<SpecIssue> issues, string path, string message) =>
        issues.Add(new SpecIssue(IssueSeverity.Error, path, message));

    private static void Warn(List<SpecIssue> issues, string path, string message) =>
        issues.Add(new SpecIssue(IssueSeverity.Warning, path, message));

    private static string List(IEnumerable<string> values) => string.Join(", ", values);
}
