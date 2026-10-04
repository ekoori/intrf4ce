using System.Text.Json;
using System.Text.Json.Nodes;

namespace Intrf4ce.Plugins;

/// <summary>A kind of thing the agent can put on a slide, drawn by a plugin.</summary>
/// <param name="Name">
/// What the agent writes in <c>"type"</c>: lower case letters, digits and '-', and not a type
/// intrf4ce has of its own.
/// </param>
/// <param name="Description">A sentence or two for the agent: what it shows, and when it is the right thing.</param>
/// <param name="Create">Makes the live element for one element on one slide.</param>
public sealed record ElementType(string Name, string Description, Func<ElementHost, LiveElement> Create)
{
    /// <summary>
    /// Its own fields, as JSON Schema property definitions:
    /// <c>{"place": {"type": "string", "description": "…"}}</c>. The fields every element has —
    /// id, area, rect, caption, hidden — come from intrf4ce and are not repeated here.
    /// </summary>
    /// <remarks>
    /// The agent is shown these, and nothing else is accepted: a field the agent sends that is
    /// not here is an error it is told about, as it is for intrf4ce's own elements.
    /// </remarks>
    public JsonObject Fields { get; init; } = [];

    /// <summary>Fields that must be given.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>
    /// Checks one element's fields, beyond what the schema says, before the slide is accepted.
    /// Unknown and missing required fields are checked already.
    /// </summary>
    public Func<JsonElement, IEnumerable<FieldIssue>>? Check { get; init; }

    /// <summary>A picture rather than words: the automatic layout gives it more room.</summary>
    public bool IsVisual { get; init; } = true;
}

/// <summary>Something wrong with one field, said so that the agent can put it right.</summary>
/// <param name="Field">The field's name, or empty for the element as a whole.</param>
/// <param name="IsWarning">The slide is accepted anyway, and the agent told.</param>
public sealed record FieldIssue(string Field, string Message, bool IsWarning = false);

/// <summary>A tool the agent can call, answered by a plugin.</summary>
/// <param name="Name">Begins with the plugin's id and an underscore: <c>weather_now</c>.</param>
/// <param name="Description">For the agent: what it returns, and when to call it.</param>
/// <param name="InputSchema">JSON Schema for its arguments, an object schema.</param>
/// <param name="Run">
/// Answers a call with text for the agent, JSON or words. Throw <see cref="ArgumentException"/>
/// for arguments that are wrong, with a message saying how.
/// </param>
public sealed record PluginTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject, CancellationToken, Task<string>> Run);
