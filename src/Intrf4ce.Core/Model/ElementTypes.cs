using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intrf4ce.Core.Model;

/// <summary>
/// A type of element a plugin adds, as far as the deck model needs to know it: what to tell
/// the agent, and how to check what the agent sends. How it looks is the plugin's business.
/// </summary>
/// <param name="Plugin">The id of the plugin it comes from.</param>
/// <param name="Fields">Its own fields as JSON Schema property definitions, by name.</param>
/// <param name="Check">The plugin's own check of an element's fields, beyond the schema.</param>
public sealed record ExtraElementType(
    string Name,
    string Plugin,
    string Description,
    JsonObject Fields,
    IReadOnlyList<string> Required,
    bool IsVisual,
    Func<JsonElement, IEnumerable<ExtraElementIssue>>? Check = null);

/// <summary>A plugin's word on one field of one of its elements.</summary>
/// <param name="Field">The field, or empty for the element as a whole.</param>
public sealed record ExtraElementIssue(string Field, string Message, bool IsWarning);

/// <summary>Every type of element: intrf4ce's own, and the ones plugins have added in this run.</summary>
/// <remarks>
/// Process-wide, like the plugins themselves: a type is added when its plugin loads and stays
/// until the app closes, and the validator and the agent's tool descriptions both read it.
/// </remarks>
public static class ElementTypes
{
    private static ImmutableDictionary<string, ExtraElementType> _extra =
        ImmutableDictionary.Create<string, ExtraElementType>(StringComparer.Ordinal);

    /// <summary>intrf4ce's own types, in the order the agent is told them.</summary>
    public static IReadOnlyList<string> BuiltIn => ElementJsonConverter.BuiltInNames;

    /// <summary>The types plugins have added, by name.</summary>
    public static IReadOnlyList<ExtraElementType> Extra => [.. _extra.Values.OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>Every type there is now.</summary>
    public static IEnumerable<string> Names => BuiltIn.Concat(Extra.Select(t => t.Name));

    public static bool IsTaken(string name) => ElementJsonConverter.IsBuiltIn(name) || _extra.ContainsKey(name);

    public static ExtraElementType? Find(string name) => _extra.GetValueOrDefault(name);

    /// <exception cref="InvalidOperationException">The name is already a type.</exception>
    public static void Add(ExtraElementType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (ElementJsonConverter.IsBuiltIn(type.Name))
        {
            throw new InvalidOperationException($"'{type.Name}' is one of intrf4ce's own element types");
        }

        ImmutableInterlocked.Update(ref _extra, types => types.ContainsKey(type.Name)
            ? throw new InvalidOperationException($"there is already an element type called '{type.Name}'")
            : types.Add(type.Name, type));
    }

    /// <summary>Forgets the types a plugin added.</summary>
    public static void RemovePlugin(string plugin) =>
        ImmutableInterlocked.Update(ref _extra, types =>
            types.RemoveRange([.. types.Values.Where(t => t.Plugin == plugin).Select(t => t.Name)]));
}
