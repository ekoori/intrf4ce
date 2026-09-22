using Avalonia.Media;
using Intrf4ce.App.Render;

namespace Intrf4ce.App;

/// <summary>
/// Every colour and font the app draws with. The agent never sees any of this.
/// </summary>
/// <remarks>
/// Deliberately small: an ink stage, one warm accent that means "look here" (focus,
/// callouts, the spoken word) and one cool accent that means "the agent" (its orb, its
/// messages). A presentation with six accents has none.
/// </remarks>
public sealed class Theme
{
    public static Theme Default { get; } = new();

    public Color Ink { get; init; } = Color.Parse("#0A0E13");

    public Color Surface { get; init; } = Color.Parse("#121820");

    public Color Surface2 { get; init; } = Color.Parse("#1B2430");

    public Color Line { get; init; } = Color.Parse("#2E3A49");

    public Color Text { get; init; } = Color.Parse("#E8ECF1");

    public Color Muted { get; init; } = Color.Parse("#9AA5B1");

    public Color Faint { get; init; } = Color.Parse("#5E6B7A");

    public Color Accent { get; init; } = Color.Parse("#FFB547");

    public Color Cool { get; init; } = Color.Parse("#5CE1E6");

    public Color Danger { get; init; } = Color.Parse("#FF6B6B");

    public Color Success { get; init; } = Color.Parse("#7BE08A");

    public IReadOnlyList<string> Palette { get; init; } =
        ["#5CE1E6", "#FFB547", "#FF7A90", "#9D8CFF", "#7BE08A", "#F2D06B", "#6AA8FF", "#E58CF0"];

    public FontFamily UiFont { get; init; } = new("fonts:Inter#Inter");

    /// <summary>
    /// The font diagrams are laid out and drawn with. It must be installed system-wide: the
    /// hidden browser measures with it and Svg.Skia draws with it, and neither can see the
    /// Inter that Avalonia carries as a resource.
    /// </summary>
    public string DiagramFont { get; init; } =
        OperatingSystem.IsWindows() ? "Segoe UI"
        : OperatingSystem.IsMacOS() ? "Helvetica Neue"
        : "DejaVu Sans";

    public double DiagramFontSize { get; init; } = 16;

    public RendererTheme Renderer => new(
        DiagramFont,
        DiagramFontSize,
        Hex(Text),
        Hex(Muted),
        Hex(Accent),
        Hex(Cool),
        Hex(Surface),
        Hex(Surface2),
        Hex(Line),
        Palette,
        Dark: true);

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public IBrush Brush(Color color, double opacity = 1) =>
        new SolidColorBrush(color, opacity);
}
