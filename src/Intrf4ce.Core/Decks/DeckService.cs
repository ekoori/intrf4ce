using Intrf4ce.Core.Model;

namespace Intrf4ce.Core.Decks;

public enum DeckChangeKind
{
    /// <summary>A different deck is now open.</summary>
    Opened,

    SlideAdded,

    SlideReplaced,

    SlideRemoved,

    SlidesMoved,

    /// <summary>Title, assets or agent session; nothing the stage draws differently.</summary>
    DeckUpdated,

    /// <summary>The deck was put away; the stage is empty and nothing is open.</summary>
    Closed,

    /// <summary>A session that is not open changed on disk (its group); only the list cares.</summary>
    Library,
}

public sealed class DeckChangedEventArgs(DeckChangeKind kind, Deck deck, string? slideId) : EventArgs
{
    public DeckChangeKind Kind { get; } = kind;

    /// <summary>The deck after the change: a snapshot that will never change under the reader.</summary>
    public Deck Deck { get; } = deck;

    public string? SlideId { get; } = slideId;
}

/// <summary>
/// The open deck, and the only way to change it.
/// </summary>
/// <remarks>
/// Tool calls arrive on pipe threads, the stage lives on the UI thread, and the store writes
/// from a timer. Every change therefore builds a new immutable <see cref="Deck"/> under one
/// lock and publishes it; readers take the snapshot and never lock. Events are raised after
/// the lock is released, so a handler that calls back in cannot deadlock.
/// <para>
/// Saving is debounced: an agent building a slide element by element changes the deck several
/// times a second, and writing each one would be wasted work. <see cref="Flush"/> writes at once.
/// </para>
/// </remarks>
public sealed class DeckService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Lines of conversation kept with a presentation.</summary>
    private const int MaxChat = 200;

    private readonly DeckStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private Deck? _current;
    private bool _dirty;

    public DeckService(DeckStore store, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.Now);
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<DeckChangedEventArgs>? Changed;

    public DeckStore Store => _store;

    public Deck? Current => Volatile.Read(ref _current);

    public Deck New(string title)
    {
        Flush();
        var deck = _store.Create(title, _now());
        Publish(DeckChangeKind.Opened, deck, null, save: false);
        return deck;
    }

    /// <summary>
    /// Puts the open deck away, saving it first: an empty stage, ready for a new subject.
    /// </summary>
    /// <remarks>
    /// Not the same as deleting it. The session stays on disk with everything in it; the next
    /// <c>put_slide</c> starts a new one rather than adding to whatever happened to be open.
    /// </remarks>
    public void Close()
    {
        Flush();
        Deck? had;
        lock (_gate)
        {
            had = _current;
            _current = null;
            _dirty = false;
        }

        if (had is not null)
        {
            Changed?.Invoke(this, new DeckChangedEventArgs(DeckChangeKind.Closed, had, null));
        }
    }

    public Deck Open(string deckId)
    {
        Flush();
        var deck = _store.Load(deckId);
        Publish(DeckChangeKind.Opened, deck, null, save: false);
        return deck;
    }

    /// <summary>The open deck, creating one if the agent started without asking for a deck.</summary>
    public Deck Ensure(string? titleIfNew)
    {
        var current = Current;
        return current ?? New(string.IsNullOrWhiteSpace(titleIfNew) ? "Untitled" : titleIfNew);
    }

    /// <summary>Adds a slide or replaces the one with the same id.</summary>
    /// <param name="after">Put it after this slide; "start" for first. Null appends, or keeps a replaced slide where it was.</param>
    /// <returns>The index it ended up at and whether it replaced an existing slide.</returns>
    public (int Index, bool Replaced) PutSlide(Slide slide, string? after)
    {
        ArgumentNullException.ThrowIfNull(slide);

        int index;
        bool replaced;
        Deck next;

        lock (_gate)
        {
            var deck = RequireOpen();
            var existing = deck.IndexOf(slide.Id);
            replaced = existing >= 0;
            var slides = replaced ? deck.Slides.RemoveAt(existing) : deck.Slides;

            index = after switch
            {
                null => replaced ? existing : slides.Count,
                "start" => 0,
                _ => AfterIndex(slides, after),
            };

            next = deck with { Slides = slides.Insert(index, slide), Modified = _now() };
            _current = next;
        }

        Publish(replaced ? DeckChangeKind.SlideReplaced : DeckChangeKind.SlideAdded, next, slide.Id);
        return (index, replaced);
    }

    /// <summary>Applies <paramref name="change"/> to one slide.</summary>
    /// <exception cref="KeyNotFoundException">There is no such slide.</exception>
    public Slide UpdateSlide(string slideId, Func<Slide, Slide> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        Slide updated;
        Deck next;

        lock (_gate)
        {
            var deck = RequireOpen();
            var index = deck.IndexOf(slideId);
            if (index < 0)
            {
                throw new KeyNotFoundException(NoSuchSlide(deck, slideId));
            }

            updated = change(deck.Slides[index]);
            next = deck with { Slides = deck.Slides.SetItem(index, updated), Modified = _now() };
            _current = next;
        }

        Publish(DeckChangeKind.SlideReplaced, next, slideId);
        return updated;
    }

    public void RemoveSlide(string slideId)
    {
        Deck next;
        lock (_gate)
        {
            var deck = RequireOpen();
            var index = deck.IndexOf(slideId);
            if (index < 0)
            {
                throw new KeyNotFoundException(NoSuchSlide(deck, slideId));
            }

            next = deck with { Slides = deck.Slides.RemoveAt(index), Modified = _now() };
            _current = next;
        }

        Publish(DeckChangeKind.SlideRemoved, next, slideId);
    }

    /// <param name="after">The slide it should follow, or "start".</param>
    public int MoveSlide(string slideId, string after)
    {
        Deck next;
        int index;

        lock (_gate)
        {
            var deck = RequireOpen();
            var from = deck.IndexOf(slideId);
            if (from < 0)
            {
                throw new KeyNotFoundException(NoSuchSlide(deck, slideId));
            }

            var slide = deck.Slides[from];
            var without = deck.Slides.RemoveAt(from);
            index = after == "start" ? 0 : AfterIndex(without, after);
            next = deck with { Slides = without.Insert(index, slide), Modified = _now() };
            _current = next;
        }

        Publish(DeckChangeKind.SlidesMoved, next, slideId);
        return index;
    }

    public void Rename(string title) => UpdateDeck(d => d with { Title = title });

    /// <summary>
    /// Remembers the agent conversation that built the open deck, if one is open.
    /// </summary>
    /// <remarks>
    /// Called whenever the agent finishes a turn, which it may well do before any deck exists —
    /// it can answer a question, or fail, without building anything — so having nothing to
    /// write it on is an ordinary outcome and not an error.
    /// </remarks>
    /// <returns>False when no deck is open.</returns>
    public bool TrySetAgentSession(string? sessionId)
    {
        if (Current is null)
        {
            return false;
        }

        UpdateDeck(d => d with { AgentSession = sessionId });
        return true;
    }

    /// <summary>
    /// Keeps a line of the conversation with the presentation it is about.
    /// </summary>
    /// <remarks>
    /// Silently nothing when no deck is open: the user may chat before there is anything to
    /// chat about, and the agent decides what the subject is by making a deck for it. A picture
    /// dropped with nothing typed is a turn of its own.
    /// </remarks>
    public void AddChat(bool fromUser, string text, IReadOnlyList<string>? files = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var withFiles = files is { Count: > 0 };
        if (Current is null || (string.IsNullOrWhiteSpace(text) && !withFiles))
        {
            return;
        }

        var line = new ChatLine(fromUser ? "user" : "agent", text, _now(), withFiles ? [.. files!] : null);
        UpdateDeck(d => d with { Chat = Recent(d.Chat.Add(line)) });
    }

    /// <summary>
    /// Files a session under a group — the open one through the live snapshot, any other on
    /// disk — or, with null, takes it out of its group.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no such session.</exception>
    public void SetGroup(string deckId, string? group)
    {
        if (Current is { } open && open.Id == deckId)
        {
            Deck next;
            lock (_gate)
            {
                next = RequireOpen() with { Group = DeckStore.Group(group) };
                _current = next;
            }

            Publish(DeckChangeKind.DeckUpdated, next, null);
            return;
        }

        _store.SetGroup(deckId, group);
        Changed?.Invoke(this, new DeckChangedEventArgs(DeckChangeKind.Library, Current ?? new Deck { Id = deckId }, null));
    }

    /// <summary>Renames a group across every session filed under it.</summary>
    /// <returns>How many sessions moved.</returns>
    public int RenameGroup(string from, string to)
    {
        var moved = 0;
        foreach (var info in _store.List().Where(d => string.Equals(d.Group, from, StringComparison.Ordinal)))
        {
            SetGroup(info.Id, to);
            moved++;
        }

        return moved;
    }

    /// <summary>Remembers that a slide has been performed, so it is not performed again.</summary>
    public void MarkSpoken(string slideId)
    {
        if (Current is not { } deck || deck.Spoken.Contains(slideId) || deck.IndexOf(slideId) < 0)
        {
            return;
        }

        UpdateDeck(d => d with { Spoken = d.Spoken.Add(slideId) });
    }

    /// <summary>Slides of the open deck the user has not heard yet, in order.</summary>
    public IReadOnlyList<string> Unspoken() =>
        Current is { } deck ? [.. deck.Slides.Where(s => !deck.Spoken.Contains(s.Id)).Select(s => s.Id)] : [];

    /// <summary>The last of a long conversation; a session lasting days need not keep all of it.</summary>
    private static System.Collections.Immutable.ImmutableList<ChatLine> Recent(System.Collections.Immutable.ImmutableList<ChatLine> chat) =>
        chat.Count <= MaxChat ? chat : chat.RemoveRange(0, chat.Count - MaxChat);

    /// <summary>Records where an image source was saved, relative to the deck folder.</summary>
    public void AddAsset(string source, string relativePath) =>
        UpdateDeck(d => d with { Assets = d.Assets.SetItem(source, relativePath) });

    /// <summary>Writes the deck now if anything changed since the last save.</summary>
    public void Flush()
    {
        Deck? deck;
        lock (_gate)
        {
            if (!_dirty || _current is null)
            {
                return;
            }

            deck = _current;
            _dirty = false;
        }

        try
        {
            _store.Save(deck);
        }
        catch (IOException)
        {
            // A locked file (antivirus, a sync client) is transient; the next change retries.
            lock (_gate)
            {
                _dirty = true;
            }

            _saveTimer.Change(SaveDelay * 5, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    private void UpdateDeck(Func<Deck, Deck> change)
    {
        Deck next;
        lock (_gate)
        {
            next = change(RequireOpen()) with { Modified = _now() };
            _current = next;
        }

        Publish(DeckChangeKind.DeckUpdated, next, null);
    }

    private void Publish(DeckChangeKind kind, Deck deck, string? slideId, bool save = true)
    {
        if (kind == DeckChangeKind.Opened)
        {
            lock (_gate)
            {
                _current = deck;
                _dirty = false;
            }
        }
        else if (save)
        {
            lock (_gate)
            {
                _dirty = true;
            }

            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, new DeckChangedEventArgs(kind, deck, slideId));
    }

    private Deck RequireOpen() =>
        _current ?? throw new InvalidOperationException("No deck is open. Call new_deck or open_deck first.");

    private static int AfterIndex(System.Collections.Immutable.ImmutableList<Slide> slides, string after)
    {
        var index = slides.FindIndex(s => string.Equals(s.Id, after, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new KeyNotFoundException(
                $"after: there is no slide '{after}' (slides: {string.Join(", ", slides.Select(s => s.Id))}; or use \"start\")");
        }

        return index + 1;
    }

    private static string NoSuchSlide(Deck deck, string slideId) =>
        $"There is no slide '{slideId}' in '{deck.Title}' (slides: {string.Join(", ", deck.Slides.Select(s => s.Id))}).";
}
