using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intrf4ce.Core.Model;

/// <summary>The one set of JSON options for everything the agent sends and every deck on disk.</summary>
public static class DeckJson
{
    /// <summary>
    /// Strict: an unknown property is an error rather than silently dropped.
    /// </summary>
    /// <remarks>
    /// An agent that writes <c>"callout"</c> for <c>"callouts"</c> must hear about it. Ignoring
    /// unknown members, the serializer default, would accept the slide and draw an image with
    /// no labels, and the agent would have no way to know why.
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    /// <summary>The same, indented, for deck.json so it diffs and reads well.</summary>
    public static JsonSerializerOptions File { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowOutOfOrderMetadataProperties = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            WriteIndented = indented,
            // Narration is full of apostrophes and quotes; escaping them makes deck.json unreadable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
