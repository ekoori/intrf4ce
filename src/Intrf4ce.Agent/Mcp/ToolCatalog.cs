using System.Text.Json.Nodes;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Agent.Mcp;

/// <summary>What a plugin adds to what the agent is told.</summary>
/// <param name="Guide">Its prompt.md: how to use what it adds, appended to the instructions.</param>
public sealed record CatalogPlugin(
    string Id,
    string Name,
    string Version,
    string? Guide,
    IReadOnlyList<ExtraElementType> Elements,
    IReadOnlyList<CatalogTool> Tools);

/// <summary>A tool a plugin adds, as it is described to the agent.</summary>
public sealed record CatalogTool(string Name, string Description, JsonObject InputSchema);

/// <summary>
/// The tool definitions and server instructions: reviewed documents embedded in the assembly,
/// and what the loaded plugins add to them.
/// </summary>
/// <remarks>
/// <para>
/// tools.json keeps shared shapes (element, slide) under <c>definitions</c> and refers to them
/// with <c>$ref</c>. Refs are resolved here, once per change, because clients differ in whether
/// they resolve a ref against a tool's own schema, and an unresolved ref reaches the model as an
/// empty object with no fields described.
/// </para>
/// <para>
/// Plugins extend the definitions before they are resolved: each plugin's element types join
/// the element's <c>type</c> list and its fields join the element's properties, described with
/// the type's name first exactly as the built-in fields are. Sessions hold this one object and
/// read it when asked, so a plugin installed while they run is in the next <c>tools/list</c>;
/// <see cref="Changed"/> is their cue to tell their client so.
/// </para>
/// </remarks>
public sealed class ToolCatalog
{
    private readonly JsonObject _document;
    private readonly string _instructions;
    private Snapshot _current;

    private ToolCatalog(JsonObject document, string instructions, string hostedPrompt)
    {
        _document = document;
        _instructions = instructions;
        HostedPrompt = hostedPrompt;
        _current = Build(document, instructions, []);
        CoreNames = _current.Names;
    }

    /// <summary>The plugins have changed what the agent is told.</summary>
    public event EventHandler? Changed;

    /// <summary>What <c>tools/list</c> returns, refs resolved.</summary>
    public JsonArray Tools => Volatile.Read(ref _current).Tools;

    public IReadOnlyList<string> Names => Volatile.Read(ref _current).Names;

    /// <summary>The tools intrf4ce has of its own, without any a plugin added.</summary>
    public IReadOnlyList<string> CoreNames { get; }

    /// <summary>Sent as the server's <c>instructions</c>: the presenter guide every client gets.</summary>
    public string Instructions => Volatile.Read(ref _current).Instructions;

    /// <summary>Appended to the hosted agent's system prompt, after <see cref="Instructions"/>.</summary>
    public string HostedPrompt { get; }

    public static ToolCatalog Load()
    {
        var document = JsonNode.Parse(Read("tools.json"))!.AsObject();
        return new ToolCatalog(document, Read("instructions.md").Trim(), Read("hosted.md").Trim());
    }

    /// <summary>Replaces what plugins add with what these plugins add.</summary>
    public void SetPlugins(IReadOnlyList<CatalogPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        Volatile.Write(ref _current, Build(_document, _instructions, plugins));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Snapshot Build(JsonObject document, string instructions, IReadOnlyList<CatalogPlugin> plugins)
    {
        var definitions = document["definitions"]!.DeepClone().AsObject();
        Extend(definitions["element"]!.AsObject(), plugins.SelectMany(p => p.Elements));

        var tools = new JsonArray();
        foreach (var tool in document["tools"]!.AsArray())
        {
            tools.Add(Resolve(tool!.DeepClone(), definitions, depth: 0));
        }

        foreach (var tool in plugins.SelectMany(p => p.Tools))
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return new Snapshot(tools, [.. tools.Select(t => t!["name"]!.GetValue<string>())], Guide(instructions, plugins));
    }

    /// <summary>Adds plugin element types to the element schema, in the same style as the built-in ones.</summary>
    private static void Extend(JsonObject element, IEnumerable<ExtraElementType> types)
    {
        var properties = element["properties"]!.AsObject();
        var names = properties["type"]!["enum"]!.AsArray();
        var added = new List<string>();

        foreach (var type in types)
        {
            names.Add(type.Name);
            added.Add($"{type.Name} ({type.Plugin} plugin): {type.Description}");

            foreach (var (field, schema) in type.Fields)
            {
                var about = $"{type.Name}: {schema?["description"]?.GetValue<string>() ?? field}";
                if (properties[field] is JsonObject shared)
                {
                    // Two types with a field of one name: one description saying both.
                    shared["description"] = $"{shared["description"]?.GetValue<string>()} {about}".Trim();
                    continue;
                }

                var described = schema?.DeepClone() as JsonObject ?? [];
                described["description"] = about;
                properties[field] = described;
            }
        }

        if (added.Count > 0)
        {
            element["description"] = $"{element["description"]?.GetValue<string>()} Types plugins add — {string.Join(" ", added)}";
        }
    }

    /// <summary>The instructions, with each plugin's own guide after them.</summary>
    private static string Guide(string instructions, IReadOnlyList<CatalogPlugin> plugins)
    {
        if (plugins.Count == 0)
        {
            return instructions;
        }

        var guides = plugins.Select(p =>
        {
            var adds = string.Join(", ", p.Elements.Select(e => $"`{e.Name}`").Concat(p.Tools.Select(t => $"`{t.Name}`")));
            return $"### {p.Name} — plugin `{p.Id}` {p.Version}\n\nAdds {(adds.Length > 0 ? adds : "nothing yet")}.\n\n{p.Guide?.Trim()}".TrimEnd();
        });

        return instructions + "\n\n## Plugins\n\nThese are loaded now; each adds element types or tools, and says how to use them.\n\n"
            + string.Join("\n\n", guides);
    }

    private static JsonNode? Resolve(JsonNode? node, JsonObject definitions, int depth)
    {
        if (depth > 16)
        {
            throw new InvalidOperationException("tools.json: $ref nesting too deep (a cycle?)");
        }

        switch (node)
        {
            case JsonObject obj when obj["$ref"]?.GetValue<string>() is { } reference:
                const string Prefix = "#/definitions/";
                if (!reference.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"tools.json: unsupported $ref {reference}");
                }

                var target = definitions[reference[Prefix.Length..]]
                    ?? throw new InvalidOperationException($"tools.json: no definition for {reference}");
                return Resolve(target.DeepClone(), definitions, depth + 1);

            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = Resolve(value?.DeepClone(), definitions, depth + 1);
                }

                return copy;

            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Resolve(item?.DeepClone(), definitions, depth + 1));
                }

                return items;

            default:
                return node;
        }
    }

    private static string Read(string name)
    {
        using var stream = typeof(ToolCatalog).Assembly.GetManifestResourceStream("Intrf4ce.Agent." + name)
            ?? throw new InvalidOperationException($"embedded resource {name} is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Snapshot(JsonArray Tools, IReadOnlyList<string> Names, string Instructions);
}
