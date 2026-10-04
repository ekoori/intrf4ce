using Intrf4ce.Core.Decks;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// Deleting a session takes it off the list at once, can be undone until the app closes, and
/// then leaves nothing behind.
/// </summary>
public class TrashTests : IDisposable
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

    [Fact]
    public void ADeletedSessionLeavesTheListAndComesBackWhole()
    {
        var store = new DeckStore(Path.Combine(_root, "sessions"));
        var deck = store.Create("The knee", DateTimeOffset.Now);
        File.WriteAllText(Path.Combine(store.FolderOf(deck.Id), "picture.png"), "not really a picture");

        var entry = store.MoveToTrash(deck.Id);

        Assert.Empty(store.List());
        Assert.False(Directory.Exists(store.FolderOf(deck.Id)));

        var back = store.RestoreFromTrash(entry);

        Assert.Equal(deck.Id, back);
        Assert.Equal("The knee", Assert.Single(store.List()).Title);
        Assert.True(File.Exists(Path.Combine(store.FolderOf(back), "picture.png")));
    }

    [Fact]
    public void ASessionComesBackUnderAnotherIdWhenItsOwnHasBeenTaken()
    {
        var store = new DeckStore(Path.Combine(_root, "sessions"));
        var deck = store.Create("The knee", DateTimeOffset.Now);
        var entry = store.MoveToTrash(deck.Id);

        // Made while the first was in the trash, and given the free name.
        var newer = store.Create("The knee", DateTimeOffset.Now);
        Assert.Equal(deck.Id, newer.Id);

        var back = store.RestoreFromTrash(entry);

        Assert.Equal(deck.Id + "-2", back);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void EmptyingTheTrashDeletesForGoodAndFilesFromElsewhereGoWithIt()
    {
        var store = new DeckStore(Path.Combine(_root, "sessions"));
        var deck = store.Create("The knee", DateTimeOffset.Now);

        // What the hosted agent kept of the conversation lives outside the session folder.
        var transcripts = Path.Combine(_root, "claude", "projects", "work");
        Directory.CreateDirectory(transcripts);
        var transcript = Path.Combine(transcripts, "5d1f6c1e-0000-4000-8000-000000000001.jsonl");
        File.WriteAllText(transcript, "{}");

        var entry = store.MoveToTrash(deck.Id);
        var kept = DeckStore.MoveInto(transcript, Path.Combine(entry, "agent"));

        Assert.False(File.Exists(transcript));
        Assert.True(File.Exists(kept));

        store.EmptyTrash();

        Assert.False(Directory.Exists(store.TrashFolder));
        Assert.False(File.Exists(kept));
        Assert.Throws<FileNotFoundException>(() => store.MoveToTrash(deck.Id));
    }
}
