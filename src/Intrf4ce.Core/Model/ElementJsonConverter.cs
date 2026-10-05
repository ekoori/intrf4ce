using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Intrf4ce.Core.Model;

/// <summary>
/// Reads and writes an element by its <c>"type"</c>: intrf4ce's own types as their records,
/// and anything else as a <see cref="PluginElement"/> that keeps its fields as written.
/// </summary>
/// <remarks>
/// In place of System.Text.Json's own polymorphism, which refuses a type it was not told about
/// — and the types plugins add are not known until the plugins load, nor at all when a deck made
/// with one is opened without it. Whether a type is one that exists is the validator's question,
/// asked of what the agent sends; a deck on disk is read whatever it holds.
/// </remarks>
public sealed class ElementJsonConverter : JsonConverter<Element>
{
    private static readonly Dictionary<string, Type> BuiltIn = new(StringComparer.Ordinal)
    {
        ["title"] = typeof(TitleElement),
        ["markdown"] = typeof(MarkdownElement),
        ["image"] = typeof(ImageElement),
        ["diagram"] = typeof(DiagramElement),
        ["math"] = typeof(MathElement),
        ["chart"] = typeof(ChartElement),
        ["table"] = typeof(TableElement),
        ["gallery"] = typeof(GalleryElement),
        ["quiz"] = typeof(QuizElement),
        ["map"] = typeof(MapElement),
        ["model3d"] = typeof(ModelElement),
    };

    /// <summary>Worked out from the type, not kept; decks written before this converter have them, and they are read past.</summary>
    private static readonly string[] Computed = ["typeName", "isVisual"];

    /// <summary>The fields every element has, which a plugin element keeps as typed properties.</summary>
    private static readonly string[] Common = ["id", "area", "rect", "caption", "hidden"];

    internal static IReadOnlyList<string> BuiltInNames { get; } = [.. BuiltIn.Keys];

    internal static bool IsBuiltIn(string name) => BuiltIn.ContainsKey(name);

    public override Element Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonObject element;
        try
        {
            element = JsonNode.Parse(ref reader) as JsonObject
                ?? throw new JsonException("an element is an object: {\"type\": …, …}");
        }
        catch (ArgumentException ex)
        {
            // Two properties with one name.
            throw new JsonException(ex.Message, ex);
        }

        var type = element["type"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new JsonException($"every element needs \"type\": one of {string.Join(", ", ElementTypes.Names)}");

        element.Remove("type");
        foreach (var computed in Computed)
        {
            element.Remove(computed);
        }

        if (BuiltIn.TryGetValue(type, out var record))
        {
            return (Element)Deserialize(element, record, options);
        }

        var common = new JsonObject();
        foreach (var name in Common)
        {
            if (element.TryGetPropertyValue(name, out var field))
            {
                element.Remove(name);
                common[name] = field;
            }
        }

        var plugin = (PluginElement)Deserialize(common, typeof(PluginElement), options);
        return plugin with { Kind = type, Fields = JsonSerializer.SerializeToElement(element) };
    }

    public override void Write(Utf8JsonWriter writer, Element value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        var body = JsonSerializer.SerializeToNode(value, value.GetType(), options)!.AsObject();
        var element = new JsonObject { ["type"] = value.TypeName };

        foreach (var (name, field) in body.ToList())
        {
            body.Remove(name);
            if (!Computed.Contains(name))
            {
                element[name] = field;
            }
        }

        if (value is PluginElement plugin && plugin.Fields.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in plugin.Fields.EnumerateObject())
            {
                element[field.Name] = JsonSerializer.SerializeToNode(field.Value);
            }
        }

        element.WriteTo(writer, options);
    }

    private static object Deserialize(JsonObject element, Type type, JsonSerializerOptions options)
    {
        try
        {
            return element.Deserialize(type, options) ?? throw new JsonException("an element cannot be null");
        }
        catch (JsonException ex) when (ex.Path is { Length: > 1 } path)
        {
            // Thrown on without a path, so the serializer gives it the element's own —
            // elements[2] — and the part within the element is kept beside it.
            throw new ElementJsonException(path[1..].TrimStart('.'), Message(ex), ex);
        }
    }

    /// <summary>The serializer's message without the location it appends, which is said separately.</summary>
    private static string Message(JsonException ex)
    {
        var cut = ex.Message.IndexOf(" Path:", StringComparison.Ordinal);
        return cut > 0 ? ex.Message[..cut] : ex.Message;
    }
}

/// <summary>Something wrong inside an element; <see cref="Inner"/> says where in it, as <c>callouts[0].lable</c>.</summary>
public sealed class ElementJsonException : JsonException
{
    public ElementJsonException()
    {
    }

    public ElementJsonException(string message)
        : base(message)
    {
    }

    public ElementJsonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ElementJsonException(string inner, string message, Exception innerException)
        : base(message, innerException)
    {
        Inner = inner;
    }

    /// <summary>Where in the element, relative to it; empty for the element itself.</summary>
    public string Inner { get; } = "";
}
