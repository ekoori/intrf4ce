using System.Text.Json.Nodes;

namespace Intrf4ce.Agent.Mcp;

/// <summary>
/// The tool definitions and server instructions, as reviewed documents embedded in the assembly.
/// </summary>
/// <remarks>
/// tools.json keeps shared shapes (element, slide) under <c>definitions</c> and refers to them
/// with <c>$ref</c>. Refs are resolved here, once, because clients differ in whether they
/// resolve a ref against a tool's own schema, and an unresolved ref reaches the model as an
/// empty object with no fields described.
/// </remarks>
public sealed class ToolCatalog
{
    private ToolCatalog(JsonArray tools, string instructions, string hostedPrompt)
    {
        Tools = tools;
        Instructions = instructions;
        HostedPrompt = hostedPrompt;
        Names = [.. tools.Select(t => t!["name"]!.GetValue<string>())];
    }

    /// <summary>What <c>tools/list</c> returns, refs resolved.</summary>
    public JsonArray Tools { get; }

    public IReadOnlyList<string> Names { get; }

    /// <summary>Sent as the server's <c>instructions</c>: the presenter guide every client gets.</summary>
    public string Instructions { get; }

    /// <summary>Appended to the hosted agent's system prompt, after <see cref="Instructions"/>.</summary>
    public string HostedPrompt { get; }

    public static ToolCatalog Load()
    {
        var document = JsonNode.Parse(Read("tools.json"))!.AsObject();
        var definitions = document["definitions"]!.AsObject();
        var tools = new JsonArray();

        foreach (var tool in document["tools"]!.AsArray())
        {
            tools.Add(Resolve(tool!.DeepClone(), definitions, depth: 0));
        }

        return new ToolCatalog(tools, Read("instructions.md").Trim(), Read("hosted.md").Trim());
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
}
