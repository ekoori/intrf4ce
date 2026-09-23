using Intrf4ce.Core.Decks;
using Intrf4ce.Core.Events;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Settings;

namespace Intrf4ce.Tests.Core;

public sealed class DeckServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Slide S(string id) => new() { Id = id, Title = id };

    [Fact]
    public void PutAppendsReplacesInPlaceAndInsertsAfter()
    {
        using var service = new DeckService(new DeckStore(_root));
        service.New("Knee");

        service.PutSlide(S("a"), null);
        service.PutSlide(S("b"), null);
        service.PutSlide(S("c"), "a");
        var (index, replaced) = service.PutSlide(S("b") with { Title = "B2" }, null);

        Assert.Equal(["a", "c", "b"], service.Current!.Slides.Select(s => s.Id));
        Assert.True(replaced);
        Assert.Equal(2, index);
        Assert.Equal("B2", service.Current.Find("b")!.Title);
    }

    [Fact]
    public void MoveAndRemove()
    {
        using var service = new DeckService(new DeckStore(_root));
        service.New("Knee");
        foreach (var id in new[] { "a", "b", "c" })
        {
            service.PutSlide(S(id), null);
        }

        service.MoveSlide("c", "start");
        service.RemoveSlide("a");

        Assert.Equal(["c", "b"], service.Current!.Slides.Select(s => s.Id));
    }

    [Fact]
    public void UnknownSlidesProduceMessagesThatListTheRealOnes()
    {
        using var service = new DeckService(new DeckStore(_root));
        service.New("Knee");
        service.PutSlide(S("a"), null);

        var ex = Assert.Throws<KeyNotFoundException>(() => service.PutSlide(S("b"), "zzz"));

        Assert.Contains("slides: a", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangesAreSavedAndReopenIntact()
    {
        string id;
        using (var service = new DeckService(new DeckStore(_root)))
        {
            id = service.New("Knee anatomy").Id;
            service.PutSlide(S("a") with { Elements = [new MarkdownElement { Id = "m", Text = "Hello" }] }, null);
            service.AddAsset("https://x/y.png", "assets/y.png");
        }

        using var reopened = new DeckService(new DeckStore(_root));
        var deck = reopened.Open(id);

        Assert.Equal("knee-anatomy", deck.Id);
        Assert.Equal("Hello", Assert.IsType<MarkdownElement>(deck.Slides[0].Elements[0]).Text);
        Assert.Equal("assets/y.png", deck.Assets["https://x/y.png"]);
        Assert.Single(new DeckStore(_root).List());
    }

    [Fact]
    public void EveryChangeIsPublishedWithASnapshot()
    {
        using var service = new DeckService(new DeckStore(_root));
        var kinds = new List<DeckChangeKind>();
        service.Changed += (_, e) => kinds.Add(e.Kind);

        service.New("x");
        service.PutSlide(S("a"), null);
        service.PutSlide(S("a"), null);
        service.RemoveSlide("a");

        Assert.Equal([DeckChangeKind.Opened, DeckChangeKind.SlideAdded, DeckChangeKind.SlideReplaced, DeckChangeKind.SlideRemoved], kinds);
    }

    [Fact]
    public void DeckIdsAreUniqueSlugs()
    {
        var store = new DeckStore(_root);

        var first = store.Create("Ćevapi & Friends!", DateTimeOffset.Now);
        var second = store.Create("Ćevapi & Friends!", DateTimeOffset.Now);

        Assert.Equal("cevapi-friends", first.Id);
        Assert.Equal("cevapi-friends-2", second.Id);
    }

    private sealed class Sink(bool live) : IEventSink
    {
        public List<UserEvent> Received { get; } = [];

        public bool TryDeliver(UserEvent userEvent)
        {
            if (live)
            {
                Received.Add(userEvent);
            }

            return live;
        }
    }

    [Fact]
    public async Task EventsGoToTheFirstLiveSinkOrTheQueue()
    {
        var hub = new EventHub();
        var dead = new Sink(false);
        var live = new Sink(true);

        hub.AddSink(dead);
        hub.Publish("chat", "one");
        hub.AddSink(live);
        hub.Publish("chat", "two");

        var queued = await hub.TakeAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.Equal("one", Assert.Single(queued).Text);
        Assert.Equal("two", Assert.Single(live.Received).Text);
    }

    [Fact]
    public async Task TakingWaitsForTheNextEvent()
    {
        var hub = new EventHub();

        var waiting = hub.TakeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        hub.Publish("quiz", "picked B", new Dictionary<string, string> { ["slide"] = "s4" });

        var events = await waiting;
        Assert.Equal("<intrf4ce-event type=\"quiz\" slide=\"s4\">picked B</intrf4ce-event>", Assert.Single(events).ToMessage());
        Assert.Empty(await hub.TakeAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Theory]
    [InlineData("sk_abcdef0123456789", "sk_abcdef0123456789")]
    [InlineData("ELEVENLABS_API_KEY=sk_abc\n", "sk_abc")]
    [InlineData("# comment\nKEY=\"sk_quoted\"", "sk_quoted")]
    [InlineData("PORT=8080\n", null)]
    public void KeyFilesInTheDotEnvFormatsAreRead(string text, string? expected)
    {
        Assert.Equal(expected, SettingsFile.KeyFromFile(text));
    }

    [Fact]
    public void RememberingTheAgentSessionBeforeThereIsADeckDoesNothing()
    {
        using var service = new DeckService(new DeckStore(_root));

        // The agent can finish a turn having only talked, or having failed, before any deck
        // exists; that must not throw, because the window has nowhere to catch it.
        Assert.False(service.TrySetAgentSession("session-1"));

        service.New("A deck");
        Assert.True(service.TrySetAgentSession("session-1"));
        Assert.Equal("session-1", service.Current!.AgentSession);
    }
}
