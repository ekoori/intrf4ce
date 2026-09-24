using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Intrf4ce.Agent.Mcp;
using Intrf4ce.Agent.Tools;
using Intrf4ce.Core.Decks;
using Intrf4ce.Core.Events;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Settings;
using Intrf4ce.Core.Stage;

namespace Intrf4ce.Tests.Agent;

public sealed class McpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-mcp-" + Guid.NewGuid().ToString("N"));
    private readonly DeckService _decks;
    private readonly FakeStage _stage = new();
    private readonly EventHub _events = new();
    private readonly PresenterTools _tools;

    public McpTests()
    {
        _decks = new DeckService(new DeckStore(_root));
        _tools = new PresenterTools(_decks, _stage, _events, new FakeMedia(), new SettingsAccess(new AppSettings(), Path.Combine(Path.GetTempPath(), "intrf4ce-tests-settings.yaml")));
    }

    public void Dispose()
    {
        _decks.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The test that earns its keep: a tool cannot be described to the agent without being
    /// implemented, or implemented without being described.
    /// </summary>
    [Fact]
    public void EveryToolInTheSchemaHasAHandlerAndEveryHandlerIsInTheSchema()
    {
        var catalog = ToolCatalog.Load();

        Assert.Equal(catalog.Names.Order(), _tools.Implemented.Order());
    }

    [Fact]
    public void SchemaRefsAreResolvedSoEveryToolIsSelfContained()
    {
        var catalog = ToolCatalog.Load();

        var json = catalog.Tools.ToJsonString();
        Assert.DoesNotContain("$ref", json, StringComparison.Ordinal);
        var putSlide = catalog.Tools.OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == "put_slide");
        Assert.NotNull(putSlide["inputSchema"]!["properties"]!["slide"]!["properties"]!["elements"]!["items"]!["properties"]!["callouts"]);
    }

    [Fact]
    public async Task HandshakeListsToolsAndCallsThem()
    {
        await using var connection = await Connection.OpenAsync(_tools);

        var init = await connection.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
        });

        Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("intrf4ce", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains("[[focus", init["result"]!["instructions"]!.GetValue<string>(), StringComparison.Ordinal);

        var list = await connection.RequestAsync("tools/list", []);
        Assert.Equal(20, list["result"]!["tools"]!.AsArray().Count);

        var call = await connection.RequestAsync("tools/call", new JsonObject
        {
            ["name"] = "new_deck",
            ["arguments"] = new JsonObject { ["title"] = "The knee" },
        });

        Assert.Contains("the-knee", Text(call), StringComparison.Ordinal);
        Assert.Equal("The knee", _decks.Current!.Title);
    }

    [Fact]
    public async Task UnknownMethodsAndToolsAreErrorsNotCrashes()
    {
        await using var connection = await Connection.OpenAsync(_tools);

        var method = await connection.RequestAsync("resources/subscribe", []);
        Assert.Equal(-32601, method["error"]!["code"]!.GetValue<int>());

        var tool = await connection.RequestAsync("tools/call", new JsonObject { ["name"] = "fly_to_moon", ["arguments"] = new JsonObject() });
        Assert.NotNull(tool["error"]);

        var ping = await connection.RequestAsync("ping", []);
        Assert.NotNull(ping["result"]);
    }

    [Fact]
    public async Task ASlideWithErrorsIsRejectedWithEveryProblemListed()
    {
        _decks.New("x");

        var result = await _tools.CallAsync("put_slide", JsonNode.Parse("""
            {"slide":{"id":"s","layout":"columns","elements":[{"type":"image","id":"k","src":"a.png","callouts":[{"id":"a","at":[2,3],"label":"A"}]}]}}
            """)!.AsObject(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("layout", result.Text, StringComparison.Ordinal);
        Assert.Contains("callouts[0].at", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, _decks.Current!.Count);
    }

    [Fact]
    public async Task PutSlideCreatesADeckWhenNoneIsOpenAndReportsParts()
    {
        _stage.Parts["flow"] = ["A", "B"];

        var result = await _tools.CallAsync("put_slide", JsonNode.Parse("""
            {"slide":{"title":"Flow","elements":[{"type":"diagram","id":"flow","mermaid":"flowchart LR\n A-->B"}],"narration":"First [[focus flow.A]] then B."}}
            """)!.AsObject(), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.Equal("Flow", _decks.Current!.Title);
        Assert.Contains("\"flow\":[\"A\",\"B\"]", result.Text, StringComparison.Ordinal);
        Assert.Equal("flow", _decks.Current.Slides[0].Id);
    }

    [Fact]
    public async Task UpdateSlideReplacesAddsAndRemovesElementsById()
    {
        _decks.New("x");
        await _tools.CallAsync("put_slide", JsonNode.Parse("""
            {"slide":{"id":"s","elements":[{"type":"markdown","id":"a","text":"one"},{"type":"markdown","id":"b","text":"two"}]}}
            """)!.AsObject(), CancellationToken.None);

        var result = await _tools.CallAsync("update_slide", JsonNode.Parse("""
            {"id":"s","title":"New","elements":[{"type":"markdown","id":"a","text":"ONE"},{"type":"title","text":"hi"}],"remove":["b"]}
            """)!.AsObject(), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var slide = _decks.Current!.Slides[0];
        Assert.Equal("New", slide.Title);
        Assert.Equal(["a", "title"], slide.Elements.Select(e => e.Id));
        Assert.Equal("ONE", ((MarkdownElement)slide.Elements[0]).Text);
    }

    /// <summary>
    /// The agent presenting what it has just built: the slides that answer the new question,
    /// and not the ones the user has already heard.
    /// </summary>
    [Fact]
    public async Task SetPlaybackPlaysARangeOfSlidesAndRefusesOnesThatAreNotThere()
    {
        _decks.New("x");
        _decks.PutSlide(new Slide { Id = "bones" }, null);
        _decks.PutSlide(new Slide { Id = "acl" }, null);
        _decks.PutSlide(new Slide { Id = "recovery" }, null);

        var result = await _tools.CallAsync(
            "set_playback",
            JsonNode.Parse("""{"playback":"play","from":"acl","through":"recovery","continuous":true}""")!.AsObject(),
            CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var asked = Assert.Single(_stage.Playback);
        Assert.Equal(("play", "acl", "recovery", true), (asked.Action, asked.From, asked.Through, asked.Continuous));

        var wrong = await Assert.ThrowsAsync<ToolInputException>(() => _tools.CallAsync(
            "set_playback", JsonNode.Parse("""{"from":"kneecap"}""")!.AsObject(), CancellationToken.None));

        Assert.Contains("bones, acl, recovery", wrong.Message, StringComparison.Ordinal);
        Assert.Single(_stage.Playback);
    }

    [Fact]
    public async Task ASettingIsChangedThroughTheSameDoorTheUserUses()
    {
        var wrong = await _tools.CallAsync(
            "set_setting", JsonNode.Parse("""{"key":"narration.volume","value":"very loud"}""")!.AsObject(), CancellationToken.None);

        Assert.True(wrong.IsError);
        Assert.Contains("narration.volume is a number", wrong.Text, StringComparison.Ordinal);

        var listed = await _tools.CallAsync("get_settings", [], CancellationToken.None);
        Assert.Contains("narration.subtitles", listed.Text, StringComparison.Ordinal);
        Assert.Contains("settings.yaml", listed.Text, StringComparison.Ordinal);
    }

    /// <summary>Looking at an earlier session must not take the user's presentation off the stage.</summary>
    [Fact]
    public async Task AnotherSessionCanBeReadWithoutOpeningIt()
    {
        _decks.New("Last week");
        _decks.PutSlide(new Slide { Id = "nile", Title = "The Nile", Narration = "The longest river in Africa." }, null);
        _decks.AddChat(fromUser: true, "explain the Nile");
        _decks.Flush();

        _decks.New("This week");
        _decks.PutSlide(new Slide { Id = "amazon", Title = "The Amazon" }, null);

        var result = await _tools.CallAsync(
            "describe_deck", JsonNode.Parse("""{"id":"last-week"}""")!.AsObject(), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("The Nile", result.Text, StringComparison.Ordinal);
        Assert.Contains("longest river", result.Text, StringComparison.Ordinal);
        Assert.Contains("user: explain the Nile", result.Text, StringComparison.Ordinal);

        // Still the one that was open.
        Assert.Equal("this-week", _decks.Current!.Id);

        var missing = await Assert.ThrowsAsync<ToolInputException>(() => _tools.CallAsync(
            "describe_deck", JsonNode.Parse("""{"id":"the-danube"}""")!.AsObject(), CancellationToken.None));

        Assert.Contains("last-week", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForUserReturnsQueuedEvents()
    {
        _events.Publish("chat", "show me the knee");

        var result = await _tools.CallAsync("wait_for_user", new JsonObject { ["timeout_seconds"] = 1 }, CancellationToken.None);

        Assert.Contains("show me the knee", result.Text, StringComparison.Ordinal);
    }

    private static string Text(JsonNode response) =>
        response["result"]!["content"]!.AsArray().OfType<JsonObject>().First(c => c["type"]!.GetValue<string>() == "text")["text"]!.GetValue<string>();

    /// <summary>An MCP session over in-memory pipes, driven like a client would.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly McpSession _session;
        private readonly Task _running;
        private readonly Stream _toServer;
        private readonly StreamReader _fromServer;
        private int _id;

        private Connection(McpSession session, Task running, Stream toServer, StreamReader fromServer)
        {
            _session = session;
            _running = running;
            _toServer = toServer;
            _fromServer = fromServer;
        }

        public static Task<Connection> OpenAsync(IToolDispatcher tools)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var serverSide = new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
            var session = new McpSession(serverSide, SessionKind.Attached, ToolCatalog.Load(), tools, _ => { });
            var running = Task.Run(session.RunAsync);
            return Task.FromResult(new Connection(session, running, clientToServer.Writer.AsStream(), new StreamReader(serverToClient.Reader.AsStream())));
        }

        public async Task<JsonNode> RequestAsync(string method, JsonObject parameters)
        {
            var id = ++_id;
            var line = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString() + "\n";
            await _toServer.WriteAsync(Encoding.UTF8.GetBytes(line));
            await _toServer.FlushAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var response = await _fromServer.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException();
                var node = JsonNode.Parse(response)!;
                if (node["id"]?.GetValue<int>() == id)
                {
                    return node;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _toServer.DisposeAsync();
            await _session.DisposeAsync();
            try
            {
                await _running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class FakeStage : IStage
    {
        public Dictionary<string, IReadOnlyList<string>> Parts { get; } = new(StringComparer.Ordinal);

        public StageState State { get; set; } = new(-1, null, "idle", null, false, "none", "none");

        public List<string> Replies { get; } = [];

        public Task<SlideReport> AwaitSlideAsync(string slideId, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new SlideReport(slideId, true, [], Parts));

        public Task GoToAsync(string slideId, bool play, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> FocusAsync(string? target, double[]? region, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task SayAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Reply(string text) => Replies.Add(text);

        public Task<byte[]> SnapshotAsync(string? slideId, int width, CancellationToken cancellationToken) => Task.FromResult(new byte[] { 1, 2, 3 });

        public List<PlaybackRequest> Playback { get; } = [];

        public Task<string> PlaybackAsync(PlaybackRequest request, CancellationToken cancellationToken)
        {
            Playback.Add(request);
            return Task.FromResult("speaking");
        }
    }

    private sealed class FakeMedia : IMediaTools
    {
        public Task<InspectedImage> InspectAsync(string source, bool grid, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectedImage([1], 10, 10, "fake"));

        public Task<IReadOnlyList<MediaItem>> ScanAsync(string folder, bool recursive, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaItem>>([]);
    }
}
