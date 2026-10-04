using System.Text.Json;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Core.Decks;

/// <summary>
/// Decks on disk: one folder each, holding deck.json, assets/ and audio/.
/// </summary>
/// <remarks>
/// A folder rather than a single file so a deck is transparent: its images can be looked at,
/// its JSON read and diffed, and the whole thing copied or zipped by hand. deck.json is
/// always replaced atomically, because the agent writes to it every few seconds while the
/// user may close the app at any moment.
/// </remarks>
public sealed class DeckStore
{
    public const string DeckFile = "deck.json";

    public DeckStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string FolderOf(string deckId) => Path.Combine(Root, deckId);

    public string AssetsOf(string deckId) => Path.Combine(FolderOf(deckId), "assets");

    /// <summary>
    /// Brings decks made before sessions lived beside the executable across.
    /// </summary>
    /// <remarks>
    /// Only when this library has none of its own, and by moving rather than copying: two
    /// copies of a presentation would drift apart, and the user would have no way of telling
    /// which one an agent was changing.
    /// </remarks>
    /// <returns>How many were moved.</returns>
    public int AdoptFrom(string legacy, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(legacy) || Path.GetFullPath(legacy) == Path.GetFullPath(Root)
            || !Directory.Exists(legacy) || List().Count > 0)
        {
            return 0;
        }

        var moved = 0;
        foreach (var folder in Directory.EnumerateDirectories(legacy))
        {
            if (!File.Exists(Path.Combine(folder, DeckFile)))
            {
                continue;
            }

            var target = Path.Combine(Root, Path.GetFileName(folder));
            if (Directory.Exists(target))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Root);
                Directory.Move(folder, target);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"could not move {folder} into {Root}: {ex.Message}");
            }
        }

        if (moved > 0)
        {
            log?.Invoke($"moved {moved} deck(s) from {legacy} into {Root}");
        }

        return moved;
    }

    public IReadOnlyList<DeckInfo> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var decks = new List<DeckInfo>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var file = Path.Combine(folder, DeckFile);
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var deck = Read(file);
                decks.Add(new DeckInfo(deck.Id, deck.Title, deck.Count, deck.Modified, folder, deck.Slides.FirstOrDefault()?.Title, deck.Group));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A deck that cannot be read is listed by its folder so it is not silently lost.
                decks.Add(new DeckInfo(Path.GetFileName(folder), Path.GetFileName(folder) + " (unreadable)", 0,
                    File.GetLastWriteTimeUtc(file), folder));
            }
        }

        return [.. decks.OrderByDescending(d => d.Modified)];
    }

    public Deck Create(string title, DateTimeOffset now)
    {
        var stem = Slug.From(title, "deck");
        var id = stem;
        for (var n = 2; Directory.Exists(FolderOf(id)); n++)
        {
            id = $"{stem}-{n}";
        }

        var deck = new Deck
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim(),
            Created = now,
            Modified = now,
        };

        Save(deck);
        return deck;
    }

    public Deck Load(string deckId)
    {
        if (!SpecValidator.IsValidId(deckId))
        {
            throw new ArgumentException($"'{deckId}' is not a deck id");
        }

        var file = Path.Combine(FolderOf(deckId), DeckFile);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"There is no deck '{deckId}' in {Root}.", file);
        }

        // The folder is the authority on the id: a copied folder must not claim the original's.
        return Read(file) with { Id = deckId };
    }

    public void Save(Deck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);

        var folder = FolderOf(deck.Id);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, DeckFile);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(deck, DeckJson.File));
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>
    /// Files a closed session under a group, or takes it out of one, without disturbing when
    /// it was last changed — filing is not editing, and the list is ordered by the latter.
    /// </summary>
    public void SetGroup(string deckId, string? group)
    {
        var deck = Load(deckId);
        Save(deck with { Group = Group(group) });
    }

    /// <summary>A group name as stored: trimmed, and null for nothing.</summary>
    public static string? Group(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>
    /// Where deleted sessions wait until the app closes. A dot folder, so it can never be
    /// mistaken for a session: ids are letters, digits, '_' and '-'.
    /// </summary>
    public string TrashFolder => Path.Combine(Root, ".trash");

    /// <summary>
    /// Takes a session off the list by moving its folder into the trash, whole.
    /// </summary>
    /// <returns>The trash entry it went into, for <see cref="RestoreFromTrash"/>.</returns>
    /// <exception cref="FileNotFoundException">There is no such session.</exception>
    public string MoveToTrash(string deckId)
    {
        var folder = FolderOf(deckId);
        if (!SpecValidator.IsValidId(deckId) || !File.Exists(Path.Combine(folder, DeckFile)))
        {
            throw new FileNotFoundException($"There is no session '{deckId}' in {Root}.", folder);
        }

        var entry = Path.Combine(TrashFolder, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{deckId}");
        Directory.CreateDirectory(entry);
        Directory.Move(folder, Path.Combine(entry, "deck"));
        return entry;
    }

    /// <summary>
    /// Puts a session back from the trash, under its old id or, if a new session has taken
    /// that since, the next free one.
    /// </summary>
    /// <returns>The id it is back under.</returns>
    public string RestoreFromTrash(string entry)
    {
        var name = Path.GetFileName(entry);
        var stem = name[(name.IndexOf('-', StringComparison.Ordinal) + 1)..];
        var id = stem;
        for (var n = 2; Directory.Exists(FolderOf(id)); n++)
        {
            id = $"{stem}-{n}";
        }

        Directory.Move(Path.Combine(entry, "deck"), FolderOf(id));
        return id;
    }

    /// <summary>
    /// Moves a file or folder from anywhere into <paramref name="folder"/>, across drives if
    /// need be: something that belongs with a session but lives elsewhere, like the agent's
    /// transcript of it.
    /// </summary>
    /// <returns>Where it is now.</returns>
    public static string MoveInto(string path, string folder)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        Move(path, target);
        return target;
    }

    /// <summary>Moves a file or a folder, copying and deleting when a plain move cannot cross drives.</summary>
    public static void Move(string from, string to)
    {
        if (File.Exists(from))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            return;
        }

        try
        {
            Directory.Move(from, to);
        }
        catch (IOException) when (Path.GetPathRoot(Path.GetFullPath(from)) != Path.GetPathRoot(Path.GetFullPath(to)))
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }

            Directory.Delete(from, recursive: true);
        }
    }

    /// <summary>Deletes everything in the trash for good: on the way out, and on the way in after a crash.</summary>
    public void EmptyTrash()
    {
        try
        {
            if (Directory.Exists(TrashFolder))
            {
                Directory.Delete(TrashFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file held open by something else; the next start tries again.
        }
    }

    /// <summary>The absolute path of an asset recorded in the deck, if the file is there.</summary>
    public string? ResolveAsset(Deck deck, string source)
    {
        ArgumentNullException.ThrowIfNull(deck);

        if (!deck.Assets.TryGetValue(source, out var relative))
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(FolderOf(deck.Id), relative));
        return File.Exists(path) ? path : null;
    }

    private static Deck Read(string file) =>
        JsonSerializer.Deserialize<Deck>(File.ReadAllText(file), DeckJson.File)
        ?? throw new JsonException($"{file} is empty");
}
