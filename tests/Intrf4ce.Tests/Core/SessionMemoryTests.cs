using Intrf4ce.Core.Decks;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// A presentation is a session: the conversation that built it, and what the user has already
/// heard, are kept with the slides and come back when it is reopened.
/// </summary>
public sealed class SessionMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Slide S(string id) => new() { Id = id, Title = id, Narration = "Something about " + id };

    [Fact]
    public void TheConversationIsKeptWithThePresentationAndComesBackWithIt()
    {
        var store = new DeckStore(_root);
        using (var service = new DeckService(store))
        {
            service.New("The knee");
            service.AddChat(fromUser: true, "explain the knee");
            service.PutSlide(S("bones"), null);
            service.AddChat(fromUser: false, "Building a tour of the knee.");
            service.AddChat(fromUser: true, "   ");
        }

        var reopened = store.Load("the-knee");

        Assert.Equal(["user", "agent"], reopened.Chat.Select(c => c.Who));
        Assert.Equal("explain the knee", reopened.Chat[0].Text);

        // Nothing is kept for a message that was only whitespace.
        Assert.Equal(2, reopened.Chat.Count);
    }

    [Fact]
    public void FilesDroppedOnTheConversationAreKeptWithTheLineTheyWentWith()
    {
        var store = new DeckStore(_root);
        using (var service = new DeckService(store))
        {
            service.New("Photos");
            service.AddChat(fromUser: true, "what is this?", [@"C:\photos\harbour.jpg"]);
            service.AddChat(fromUser: true, "", [@"C:\photos\a.jpg", @"C:\photos\b.jpg"]);
            service.AddChat(fromUser: false, "A harbour at dusk.");
        }

        var chat = store.Load("photos").Chat;

        Assert.Equal(3, chat.Count);
        Assert.Equal([@"C:\photos\harbour.jpg"], chat[0].Files!);

        // A picture sent with nothing typed is still a turn of the conversation.
        Assert.Equal("", chat[1].Text);
        Assert.Equal(2, chat[1].Files!.Count);

        // Lines without files say nothing about files, in the file or in the record.
        Assert.Null(chat[2].Files);
        Assert.DoesNotContain("\"files\"", File.ReadAllText(Path.Combine(_root, "photos", "deck.json")).Split("A harbour at dusk")[1]);
    }

    [Fact]
    public void NothingIsKeptBeforeThereIsAPresentationToKeepItWith()
    {
        using var service = new DeckService(new DeckStore(_root));

        service.AddChat(fromUser: true, "explain the knee");

        Assert.Null(service.Current);
    }

    [Fact]
    public void WhatHasBeenPlayedIsRememberedSoItIsNotPlayedAgain()
    {
        var store = new DeckStore(_root);
        using (var service = new DeckService(store))
        {
            service.New("The knee");
            service.PutSlide(S("bones"), null);
            service.PutSlide(S("ligaments"), null);
            service.PutSlide(S("meniscus"), null);

            service.MarkSpoken("bones");
            service.MarkSpoken("bones");
            service.MarkSpoken("ligaments");

            // A slide that is not in the deck cannot have been played.
            service.MarkSpoken("kneecap");

            Assert.Equal(["meniscus"], service.Unspoken());
        }

        var reopened = store.Load("the-knee");
        Assert.Equal(["bones", "ligaments"], reopened.Spoken.OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>
    /// A new question adds slides at the end, and only those are left to play: the answer plays
    /// without the user sitting through everything that came before it again.
    /// </summary>
    [Fact]
    public void SlidesAddedToAnsweringANewQuestionAreTheOnesLeftToPlay()
    {
        using var service = new DeckService(new DeckStore(_root));
        service.New("The knee");
        service.PutSlide(S("bones"), null);
        service.PutSlide(S("ligaments"), null);
        service.MarkSpoken("bones");
        service.MarkSpoken("ligaments");

        Assert.Empty(service.Unspoken());

        service.PutSlide(S("acl-tears"), null);
        service.PutSlide(S("recovery"), null);

        Assert.Equal(["acl-tears", "recovery"], service.Unspoken());
    }

    [Fact]
    public void ThePresentationIsListedByWhatItIsAbout()
    {
        var store = new DeckStore(_root);
        using (var service = new DeckService(store))
        {
            service.New("the-knee");
            service.PutSlide(S("bones") with { Title = "The bones of the knee" }, null);
        }

        var listed = Assert.Single(store.List());
        Assert.Equal("The bones of the knee", listed.Subject);
    }

    [Fact]
    public void ClosingAPresentationKeepsItButLeavesNothingOpen()
    {
        var store = new DeckStore(_root);
        using var service = new DeckService(store);
        service.New("The knee");
        service.PutSlide(S("bones"), null);

        var closed = 0;
        service.Changed += (_, e) => closed += e.Kind == DeckChangeKind.Closed ? 1 : 0;
        service.Close();

        Assert.Null(service.Current);
        Assert.Equal(1, closed);
        Assert.Single(store.Load("the-knee").Slides);

        // And closing again says nothing, because nothing changed.
        service.Close();
        Assert.Equal(1, closed);
    }
}
