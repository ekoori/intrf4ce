namespace Intrf4ce.Core.Rendering;

/// <summary>What the hidden renderer knows how to turn into SVG.</summary>
public enum RenderKind
{
    Mermaid,
    Tex,
    VegaLite,
}

/// <summary>A box in the rendered SVG's own coordinates, origin at its top-left.</summary>
public readonly record struct RenderBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>An addressable piece of a rendered diagram, such as a Mermaid node.</summary>
public sealed record RenderPart(string Id, RenderBox Box);

/// <summary>Options that change the output, and so belong in its cache key.</summary>
/// <param name="Width">Target width for layouts that fill space (charts); ignored by Mermaid.</param>
/// <param name="Height">Target height, likewise.</param>
/// <param name="Display">TeX: display style rather than inline.</param>
/// <param name="FontSize">TeX: the size of one em, in the same units as the result.</param>
/// <param name="Color">TeX: the ink colour, since the SVG cannot inherit one.</param>
/// <param name="KeepOriginal">Self-test only: also return the renderer's untouched output.</param>
public sealed record RenderOptions(
    double? Width = null,
    double? Height = null,
    bool Display = true,
    double? FontSize = null,
    string? Color = null,
    bool KeepOriginal = false)
{
    public static RenderOptions Default { get; } = new();
}

/// <summary>A finished render: self-contained SVG plus what the stage needs to address it.</summary>
/// <param name="Svg">Serialised SVG with every style written as attributes. Null on failure.</param>
/// <param name="Width">Natural width of <paramref name="Svg"/>.</param>
/// <param name="Height">Natural height of <paramref name="Svg"/>.</param>
/// <param name="Depth">TeX only: distance from the baseline to the bottom edge.</param>
/// <param name="Parts">Addressable parts, in SVG coordinates relative to the top-left.</param>
/// <param name="Error">Why it failed, in the renderer's own words (a Mermaid parse error, say).</param>
/// <param name="Warning">A problem that still produced output.</param>
/// <param name="Original">The renderer's own output before it was made self-contained, when asked for.</param>
public sealed record RenderResult(
    string? Svg,
    double Width,
    double Height,
    double Depth,
    IReadOnlyList<RenderPart> Parts,
    string? Error = null,
    string? Warning = null,
    string? Original = null)
{
    public bool Ok => Svg is not null && Error is null;

    public static RenderResult Failed(string error) => new(null, 0, 0, 0, [], error);
}

/// <summary>Turns diagram, formula and chart source into SVG.</summary>
/// <remarks>
/// Declared here rather than beside its implementation so that tools can validate a slide
/// (a Mermaid syntax error has to reach the agent that wrote it) without the agent layer
/// knowing a web view exists.
/// </remarks>
public interface IRenderService
{
    /// <summary>Whether a web engine exists on this machine. False means every render fails fast.</summary>
    bool IsAvailable { get; }

    Task<RenderResult> RenderAsync(
        RenderKind kind,
        string source,
        RenderOptions? options = null,
        CancellationToken cancellationToken = default);
}
