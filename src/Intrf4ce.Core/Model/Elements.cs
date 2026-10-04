using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intrf4ce.Core.Model;

/// <summary>
/// One thing on a slide. What the agent describes; how it looks is the app's business.
/// </summary>
/// <remarks>
/// Immutable records all the way down. The deck is replaced, never edited in place, so the
/// stage can hold a snapshot without locking while tool calls arrive on other threads.
/// <para>
/// Names follow the agent-facing JSON, which is camelCase. Enum-like fields stay strings so
/// that validation can answer a typo with the list of values that would have worked, which a
/// converter exception cannot. Which record a <c>"type"</c> becomes is
/// <see cref="ElementJsonConverter"/>'s to say, so that a plugin's types can exist at all.
/// </para>
/// </remarks>
public abstract record Element
{
    /// <summary>Unique within the slide; generated when the agent leaves it out.</summary>
    public string Id { get; init; } = "";

    /// <summary>Which region of the layout it goes in (left, right, main, side…).</summary>
    public string? Area { get; init; }

    /// <summary>Free layout only: [x, y, w, h] as fractions of the slide.</summary>
    public double[]? Rect { get; init; }

    /// <summary>A short line under the element.</summary>
    public string? Caption { get; init; }

    /// <summary>Starts invisible and appears on a <c>[[show id]]</c> cue.</summary>
    public bool Hidden { get; init; }

    /// <summary>The discriminator as written in JSON.</summary>
    [JsonIgnore]
    public abstract string TypeName { get; }

    /// <summary>True for elements that are pictures rather than words, which the auto layout gives more room.</summary>
    [JsonIgnore]
    public virtual bool IsVisual => false;
}

public sealed record TitleElement : Element
{
    public string Text { get; init; } = "";

    public string? Subtitle { get; init; }

    /// <summary>A small line above the title ("Lesson 1", "Chapter three").</summary>
    public string? Kicker { get; init; }

    public override string TypeName => "title";
}

public sealed record MarkdownElement : Element
{
    public string Text { get; init; } = "";

    public override string TypeName => "markdown";
}

public sealed record ImageElement : Element
{
    /// <summary>A file path or an http(s) URL. URLs are downloaded into the deck.</summary>
    public string Src { get; init; } = "";

    /// <summary><c>contain</c> (default) or <c>cover</c>.</summary>
    public string? Fit { get; init; }

    public IReadOnlyList<Callout>? Callouts { get; init; }

    /// <summary>What the image shows, for the agent's own later reference and for snapshots.</summary>
    public string? Alt { get; init; }

    public override string TypeName => "image";

    public override bool IsVisual => true;
}

/// <summary>A label with a leader line pointing at part of an image.</summary>
public sealed record Callout
{
    public string Id { get; init; } = "";

    /// <summary>[x, y] as fractions of the image: the point the leader touches.</summary>
    public double[]? At { get; init; }

    /// <summary>[x, y, w, h] as fractions of the image: an area to outline instead of a point.</summary>
    public double[]? Box { get; init; }

    public string Label { get; init; } = "";

    /// <summary>Shown on hover and when the callout is focused.</summary>
    public string? Detail { get; init; }

    /// <summary><c>auto</c> (default), <c>left</c> or <c>right</c>: which gutter the label sits in.</summary>
    public string? Side { get; init; }
}

public sealed record DiagramElement : Element
{
    public string Mermaid { get; init; } = "";

    /// <summary>Node ids in the order the narration walks them; enables stepping.</summary>
    public IReadOnlyList<string>? Steps { get; init; }

    public override string TypeName => "diagram";

    public override bool IsVisual => true;
}

public sealed record MathElement : Element
{
    public string Tex { get; init; } = "";

    public override string TypeName => "math";
}

public sealed record ChartElement : Element
{
    /// <summary>bar, line, area, pie, donut or scatter. Ignored when <see cref="VegaLite"/> is given.</summary>
    public string? Kind { get; init; }

    /// <summary>Rows of the data as objects, e.g. <c>[{"month":"Jan","sales":4}]</c>.</summary>
    public JsonElement? Data { get; init; }

    /// <summary>Field for the categories or x axis.</summary>
    public string? X { get; init; }

    /// <summary>Field for the values or y axis.</summary>
    public string? Y { get; init; }

    /// <summary>Optional field that splits the data into coloured series.</summary>
    public string? Series { get; init; }

    /// <summary>A complete Vega-Lite spec, for anything the shorthand cannot say.</summary>
    public JsonElement? VegaLite { get; init; }

    public override string TypeName => "chart";

    public override bool IsVisual => true;
}

public sealed record TableElement : Element
{
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Each row is an array of cells: strings, numbers, booleans or null.</summary>
    public IReadOnlyList<IReadOnlyList<JsonElement>> Rows { get; init; } = [];

    /// <summary>Column whose values name the rows for focus cues; defaults to the first.</summary>
    public string? Key { get; init; }

    public override string TypeName => "table";
}

public sealed record GalleryElement : Element
{
    public IReadOnlyList<GalleryItem>? Items { get; init; }

    /// <summary>A folder of images to show, alone or in addition to <see cref="Items"/>.</summary>
    public string? Folder { get; init; }

    /// <summary>scatter (default), grid or timeline.</summary>
    public string? Arrange { get; init; }

    public override string TypeName => "gallery";

    public override bool IsVisual => true;
}

public sealed record GalleryItem
{
    public string? Id { get; init; }

    public string Src { get; init; } = "";

    public string? Caption { get; init; }
}

/// <summary>
/// A map of somewhere, with the places the narration walks through marked on it.
/// </summary>
/// <remarks>
/// Drawn from OpenStreetMap's tiles. A place is a focus target like any other part, so
/// <c>[[focus journey.cairo]]</c> flies the map to Cairo as the voice says the word — which is
/// the whole reason a presentation wants a map rather than a picture of one.
/// </remarks>
public sealed record MapElement : Element
{
    /// <summary>The places to mark, in the order the narration visits them.</summary>
    public IReadOnlyList<Place>? Places { get; init; }

    /// <summary>[latitude, longitude] of the middle; omit to frame the places.</summary>
    public double[]? Center { get; init; }

    /// <summary>0 for the whole world, 19 for a doorway; omit to fit the places.</summary>
    public double? Zoom { get; init; }

    /// <summary>Draw a line from each place to the next: a journey, a route, a flight path.</summary>
    public bool Route { get; init; }

    public override string TypeName => "map";

    public override bool IsVisual => true;
}

/// <summary>Somewhere on a map, with a label and, if it earns one, a sentence.</summary>
public sealed record Place
{
    /// <summary>Focus it with <c>[[focus mapId.placeId]]</c>; made from the label when omitted.</summary>
    public string? Id { get; init; }

    public string Label { get; init; } = "";

    /// <summary>[latitude, longitude] in degrees: 51.5, -0.13 is London.</summary>
    public double[]? At { get; init; }

    /// <summary>A sentence shown when the place is focused.</summary>
    public string? Detail { get; init; }

    /// <summary>How close in to go when the narration focuses this place; omit for a sensible one.</summary>
    public double? Zoom { get; init; }
}

/// <summary>
/// An element of a type a plugin adds. Its own fields travel exactly as the agent wrote them,
/// whether or not the plugin is loaded, so a deck made with a plugin opens without it and is
/// whole again when it is back.
/// </summary>
public sealed record PluginElement : Element
{
    private static readonly JsonElement NoFields = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>The type, as written in <c>"type"</c>.</summary>
    [JsonIgnore]
    public string Kind { get; init; } = "";

    /// <summary>Its own fields, as one JSON object: everything but the ones every element has.</summary>
    [JsonIgnore]
    public JsonElement Fields { get; init; } = NoFields;

    public override string TypeName => Kind;

    public override bool IsVisual => ElementTypes.Find(Kind)?.IsVisual ?? true;
}

public sealed record QuizElement : Element
{
    public string Question { get; init; } = "";

    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>More than one option may be chosen.</summary>
    public bool Multiple { get; init; }

    /// <summary>Zero-based indexes of the correct options; without it the quiz is a poll.</summary>
    public IReadOnlyList<int>? Answer { get; init; }

    /// <summary>Shown once the user has answered.</summary>
    public string? Explanation { get; init; }

    public override string TypeName => "quiz";
}
