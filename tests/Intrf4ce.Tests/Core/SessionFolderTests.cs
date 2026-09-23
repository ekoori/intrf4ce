using Intrf4ce.Core.Decks;
using Intrf4ce.Media.Images;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// A session is a folder that holds a presentation and everything it needs, so it can be
/// copied to another machine and still work.
/// </summary>
public class SessionFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void DecksMadeBeforeSessionsExistedComeAcrossOnce()
    {
        var legacy = Folder("documents");
        var sessions = Folder("sessions");
        var old = new DeckStore(legacy);
        old.Save(old.Create("The knee", DateTimeOffset.Now));

        var store = new DeckStore(sessions);
        var moved = store.AdoptFrom(legacy);

        Assert.Equal(1, moved);
        Assert.Equal("The knee", Assert.Single(store.List()).Title);
        Assert.Empty(old.List());

        // A second run has decks of its own, so it leaves whatever is left in the old folder alone.
        old.Save(old.Create("Another", DateTimeOffset.Now));
        Assert.Equal(0, store.AdoptFrom(legacy));
        Assert.Single(old.List());
    }

    [Fact]
    public async Task APictureOnThisMachineIsPointedAtWhereItLives()
    {
        var sessions = Folder("sessions");
        var elsewhere = Folder("pictures");
        var photo = Path.Combine(elsewhere, "holiday.jpg");
        await File.WriteAllBytesAsync(photo, [1, 2, 3, 4]);

        var store = new DeckStore(sessions);
        var deck = store.Create("Holiday", DateTimeOffset.Now);
        store.Save(deck);

        using var http = new HttpClient();
        var (path, relative) = await new AssetFetcher(http).ResolveAsync(photo, null, store.FolderOf(deck.Id), CancellationToken.None);

        // A folder of photographs belongs to the user, in one place.
        Assert.Null(relative);
        Assert.Equal(photo, path);
        Assert.False(Directory.Exists(Path.Combine(store.FolderOf(deck.Id), "assets")));
    }

    [Fact]
    public async Task MediaOnASlideIsCopiedIntoTheSessionWhenAskedFor()
    {
        var sessions = Folder("sessions");
        var elsewhere = Folder("pictures");
        var photo = Path.Combine(elsewhere, "holiday.jpg");
        await File.WriteAllBytesAsync(photo, [1, 2, 3, 4]);

        var store = new DeckStore(sessions);
        var deck = store.Create("Holiday", DateTimeOffset.Now);
        store.Save(deck);
        var deckFolder = store.FolderOf(deck.Id);

        using var http = new HttpClient();
        var fetcher = new AssetFetcher(http) { CopyMedia = true };
        var (path, relative) = await fetcher.ResolveAsync(photo, null, deckFolder, CancellationToken.None);

        Assert.NotNull(relative);
        Assert.StartsWith("assets/", relative, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.StartsWith(deckFolder, path, StringComparison.OrdinalIgnoreCase);

        // The original is untouched, and the copy is found again without copying twice.
        Assert.True(File.Exists(photo));
        var again = await fetcher.ResolveAsync(photo, relative, deckFolder, CancellationToken.None);
        Assert.Equal(path, again.Path);
    }

    [Fact]
    public async Task AFileAlreadyInTheSessionStaysWhereItIs()
    {
        var sessions = Folder("sessions");
        var store = new DeckStore(sessions);
        var deck = store.Create("Holiday", DateTimeOffset.Now);
        store.Save(deck);
        var deckFolder = store.FolderOf(deck.Id);

        var inside = Path.Combine(deckFolder, "assets", "already.png");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        await File.WriteAllBytesAsync(inside, [9]);

        using var http = new HttpClient();
        var (path, relative) = await new AssetFetcher(http).ResolveAsync(inside, null, deckFolder, CancellationToken.None);

        Assert.Null(relative);
        Assert.Equal(inside, path);
        Assert.Single(Directory.GetFiles(Path.Combine(deckFolder, "assets")));
    }

    [Fact]
    public async Task ACopyIsUsedOnlyWhenTheOriginalHasGone()
    {
        var sessions = Folder("sessions");
        var elsewhere = Folder("pictures");
        var photo = Path.Combine(elsewhere, "holiday.jpg");
        await File.WriteAllBytesAsync(photo, [1, 2, 3, 4]);

        var store = new DeckStore(sessions);
        var deck = store.Create("Holiday", DateTimeOffset.Now);
        store.Save(deck);
        var deckFolder = store.FolderOf(deck.Id);

        using var http = new HttpClient();
        var copied = await new AssetFetcher(http) { CopyMedia = true }.ResolveAsync(photo, null, deckFolder, CancellationToken.None);

        // With copying off again, the picture where the user keeps it is the one shown.
        var plain = new AssetFetcher(http);
        Assert.Equal(photo, (await plain.ResolveAsync(photo, copied.Relative, deckFolder, CancellationToken.None)).Path);

        // Unless it has gone, and then the session still has what it needs.
        File.Delete(photo);
        Assert.Equal(copied.Path, (await plain.ResolveAsync(photo, copied.Relative, deckFolder, CancellationToken.None)).Path);
    }
}
