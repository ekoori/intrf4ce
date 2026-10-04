using System.Text.Json;
using Avalonia.Media;

namespace Intrf4ce.Plugins;

/// <summary>What intrf4ce shares with every plugin: the network, the clock, its settings, the log.</summary>
public sealed class PluginServices
{
    /// <summary>One client for every plugin, with intrf4ce's user agent. Do not dispose it.</summary>
    public required HttpClient Http { get; init; }

    /// <summary>The time; a test can give another.</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>
    /// This plugin's settings from settings.yaml (<c>plugins.options.id</c>), as an object; empty
    /// when there are none. A function, because the user can change them while it runs.
    /// </summary>
    public required Func<JsonElement> Settings { get; init; }

    /// <summary>Writes a line to intrf4ce's log.</summary>
    public required Action<string> Log { get; init; }
}

/// <summary>What intrf4ce gives a live element to work with.</summary>
public sealed class ElementHost
{
    public required PluginTheme Theme { get; init; }

    /// <summary>One client for every plugin, with intrf4ce's user agent. Do not dispose it.</summary>
    public required HttpClient Http { get; init; }

    /// <summary>The time; a test can give another.</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>
    /// This plugin's settings from settings.yaml (<c>plugins.options.id</c>), as an object;
    /// empty when there are none. A function, because the user can change them while it runs.
    /// </summary>
    public required Func<JsonElement> Settings { get; init; }

    /// <summary>Writes a line to intrf4ce's log.</summary>
    public required Action<string> Log { get; init; }

    /// <summary>Which element this is, and on which slide, for messages.</summary>
    public required string ElementId { get; init; }

    public required string SlideId { get; init; }
}

/// <summary>The stage's colours and type, so a plugin's elements look like the rest of the slide.</summary>
/// <param name="Ink">The stage behind the slides.</param>
/// <param name="Surface">A slide.</param>
/// <param name="Surface2">Something raised on a slide: a card, a band.</param>
/// <param name="Line">Hairlines and borders.</param>
/// <param name="Accent">Warm: "look here" — what is focused, what is being said.</param>
/// <param name="Cool">Cool: secondary emphasis, and anything about the agent.</param>
/// <param name="Palette">Series colours for charts, in order.</param>
/// <param name="FontSize">Body text on a slide, in slide units: what a paragraph of markdown is set in.</param>
public sealed record PluginTheme(
    Color Ink,
    Color Surface,
    Color Surface2,
    Color Line,
    Color Text,
    Color Muted,
    Color Faint,
    Color Accent,
    Color Cool,
    Color Danger,
    Color Success,
    IReadOnlyList<Color> Palette,
    FontFamily Font,
    FontFamily MonoFont,
    double FontSize)
{
    /// <summary>A brush of one of the colours.</summary>
    public IBrush Brush(Color color, double opacity = 1) => new SolidColorBrush(color, opacity);
}
