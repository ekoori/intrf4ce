using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Agent.Mcp;

/// <summary>What a tool returns: text, and sometimes an image the agent can look at.</summary>
public sealed record ToolResult(string Text, bool IsError = false, byte[]? Png = null)
{
    private static readonly JsonSerializerOptions Compact = new(DeckJson.Options) { WriteIndented = false };

    public static ToolResult Ok(string text) => new(text);

    /// <summary>A JSON object as the text, which is what most tools answer with.</summary>
    public static ToolResult Json(object value) => new(JsonSerializer.Serialize(value, Compact));

    public static ToolResult Error(string message) => new(message, IsError: true);

    public static ToolResult Image(byte[] png, string caption) => new(caption, Png: png);

    public JsonObject ToJson()
    {
        var content = new JsonArray();
        if (Png is not null)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(Png),
                ["mimeType"] = "image/png",
            });
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = Text });

        var result = new JsonObject { ["content"] = content };
        if (IsError)
        {
            result["isError"] = true;
        }

        return result;
    }
}

/// <summary>A tool call the handler could not make sense of; becomes an error result, not a protocol error.</summary>
public sealed class ToolInputException(string message) : Exception(message);
