using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Intrf4ce.Core.Model;

/// <summary>One page of a presentation: elements arranged by a layout, and what is said over them.</summary>
public sealed record Slide
{
    public string Id { get; init; } = "";

    /// <summary>Shown as the slide's heading; also how the slide is listed to the agent and the user.</summary>
    public string? Title { get; init; }

    /// <summary>full, split, wide, stack, grid or free. Chosen from the elements when left out.</summary>
    public string? Layout { get; init; }

    public IReadOnlyList<Element> Elements { get; init; } = [];

    /// <summary>What is spoken and subtitled, with <c>[[cue]]</c> markers inline.</summary>
    public string? Narration { get; init; }

    /// <summary>auto or manual: whether the slide moves on by itself when the narration ends.</summary>
    public string? Advance { get; init; }

    /// <summary>The agent's own notes; never shown.</summary>
    public string? Notes { get; init; }

    public Element? Find(string elementId) =>
        Elements.FirstOrDefault(e => string.Equals(e.Id, elementId, StringComparison.Ordinal));
}

/// <summary>A presentation: an ordered list of slides and where their files live.</summary>
public sealed record Deck
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The folder name, derived from the title when the deck is created.</summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public DateTimeOffset Created { get; init; }

    public DateTimeOffset Modified { get; init; }

    /// <summary>The hosted agent's conversation, resumed when the deck is reopened.</summary>
    public string? AgentSession { get; init; }

    public ImmutableList<Slide> Slides { get; init; } = [];

    /// <summary>
    /// Where each image source the agent named was saved, relative to the deck folder.
    /// </summary>
    /// <remarks>
    /// The element keeps the source exactly as the agent wrote it, so a later update naming
    /// the same URL finds the same file instead of downloading it again, and the agent reads
    /// back what it wrote rather than a path it never chose.
    /// </remarks>
    public ImmutableDictionary<string, string> Assets { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    [JsonIgnore]
    public int Count => Slides.Count;

    public int IndexOf(string slideId) => Slides.FindIndex(s => string.Equals(s.Id, slideId, StringComparison.Ordinal));

    public Slide? Find(string slideId)
    {
        var index = IndexOf(slideId);
        return index < 0 ? null : Slides[index];
    }
}

/// <summary>What the library lists without loading every deck.</summary>
public sealed record DeckInfo(string Id, string Title, int Slides, DateTimeOffset Modified, string Folder);
