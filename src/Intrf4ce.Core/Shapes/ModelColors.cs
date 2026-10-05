namespace Intrf4ce.Core.Shapes;

/// <summary>
/// The colours a 3D model's parts may be, by name.
/// </summary>
/// <remarks>
/// Names rather than numbers, as everywhere the agent describes what and the app decides how: the
/// app's own accent colours, a few plain ones, and the materials an anatomy or an engineering
/// model is made of, each a shade that reads on the dark stage. <c>glass</c> is see-through, for a
/// skin over bones or a casing over gears.
/// </remarks>
public static class ModelColors
{
    private static readonly Dictionary<string, (string Hex, double Opacity)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["accent"] = ("#FFB547", 1),
        ["cool"] = ("#5CE1E6", 1),
        ["white"] = ("#E8ECF1", 1),
        ["grey"] = ("#9AA5B1", 1),
        ["red"] = ("#FF6B6B", 1),
        ["orange"] = ("#FF9F5A", 1),
        ["yellow"] = ("#F2D06B", 1),
        ["green"] = ("#7BE08A", 1),
        ["teal"] = ("#4FD1B5", 1),
        ["blue"] = ("#6AA8FF", 1),
        ["purple"] = ("#9D8CFF", 1),
        ["pink"] = ("#FF7A90", 1),
        ["bone"] = ("#E9DEC4", 1),
        ["cartilage"] = ("#BFD8E0", 1),
        ["muscle"] = ("#D9666F", 1),
        ["tendon"] = ("#F0E6D2", 1),
        ["ligament"] = ("#D8CFA8", 1),
        ["nerve"] = ("#F5D547", 1),
        ["artery"] = ("#E8434F", 1),
        ["vein"] = ("#5B7BE0", 1),
        ["organ"] = ("#C98B7A", 1),
        ["skin"] = ("#E8B999", 1),
        ["fat"] = ("#F2DFA0", 1),
        ["metal"] = ("#B8C4D0", 1),
        ["wood"] = ("#C49A6C", 1),
        ["glass"] = ("#9FD8F0", 0.22),
    };

    /// <summary>The colour a part is when nothing names one: the parts of a model cycle through these.</summary>
    public static readonly IReadOnlyList<string> Cycle = ["cool", "accent", "pink", "purple", "green", "yellow", "blue", "orange"];

    public static IEnumerable<string> Names => Named.Keys;

    public static bool IsKnown(string? name) => name is not null && Named.ContainsKey(name);

    /// <summary>The colour as #RRGGBB and how opaque its faces are; an unknown name is grey.</summary>
    public static (string Hex, double Opacity) Of(string? name) =>
        name is not null && Named.TryGetValue(name, out var colour) ? colour : Named["grey"];
}
