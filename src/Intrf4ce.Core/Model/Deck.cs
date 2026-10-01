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

    /// <summary>
    /// [width, height] in slide units; a slide is 1600 by 900 when left out.
    /// </summary>
    /// <remarks>
    /// Larger is a canvas: one big surface — an architecture, a family tree, a mind map — that
    /// the conversation keeps adding to rather than a sequence of slides that each replace the
    /// last. The camera roams it, and focus zooms into its parts, as on any slide.
    /// </remarks>
    public double[]? Size { get; init; }

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

/// <summary>One turn of the conversation that built a presentation.</summary>
/// <param name="Who">user or agent.</param>
/// <param name="Files">What the user dropped on the conversation with it, by full path.</param>
public sealed record ChatLine(string Who, string Text, DateTimeOffset At, IReadOnlyList<string>? Files = null);

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

    /// <summary>
    /// The group it is filed under in the sessions list; null for none.
    /// </summary>
    /// <remarks>
    /// Kept in the deck itself, so a session copied elsewhere keeps its filing, and a group is
    /// simply the set of sessions that name it — there is no separate list to fall out of step.
    /// </remarks>
    public string? Group { get; init; }

    /// <summary>
    /// What was said while this presentation was built, kept with it.
    /// </summary>
    /// <remarks>
    /// A presentation is the answer to a conversation, and reopening it without the questions
    /// leaves the user reading someone else's slides. The agent's own memory of the session is
    /// its Claude Code conversation, resumed through <see cref="AgentSession"/>; this is the
    /// part a person can read, and what is shown again when the session is reopened.
    /// </remarks>
    public ImmutableList<ChatLine> Chat { get; init; } = [];

    /// <summary>
    /// Slides that have already been performed to the user.
    /// </summary>
    /// <remarks>
    /// So that an answer to a new question plays the slides that answer it and not the six
    /// before them, which the user has already sat through.
    /// </remarks>
    public ImmutableHashSet<string> Spoken { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

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
/// <param name="Subject">The first slide's title: what the presentation is about, in its own words.</param>
/// <param name="Group">Which group it is filed under, if any.</param>
public sealed record DeckInfo(
    string Id, string Title, int Slides, DateTimeOffset Modified, string Folder, string? Subject = null, string? Group = null);
