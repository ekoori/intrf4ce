using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Agent.Mcp;
using Intrf4ce.Agent.Tools;
using Intrf4ce.Core.Decks;
using Intrf4ce.Core.Events;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Settings;
using Intrf4ce.Core.Stage;

namespace Intrf4ce.Tests.Agent;

/// <summary>What a plugin adds reaches the agent: its element type, its tools, its guide.</summary>
public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly DeckService _decks;
    private readonly McpTests.FakeStage _stage = new();
    private readonly PresenterTools _tools;

    public PluginCatalogTests()
    {
        _decks = new DeckService(new DeckStore(_root));
        _tools = new PresenterTools(_decks, _stage, new EventHub(), new NoMedia(),
            new SettingsAccess(new AppSettings(), Path.Combine(_root, "settings.yaml")));
    }

    public void Dispose()
    {
        _decks.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void APluginsElementTypeToolsAndGuideAreWhatTheAgentIsToldNext()
    {
        var catalog = ToolCatalog.Load();
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        catalog.SetPlugins([Clock()]);

        var putSlide = catalog.Tools.OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == "put_slide");
        var element = putSlide["inputSchema"]!["properties"]!["slide"]!["properties"]!["elements"]!["items"]!;
        var types = element["properties"]!["type"]!["enum"]!.AsArray().Select(t => t!.GetValue<string>());

        Assert.Contains("clock", types);
        Assert.Equal("clock: An IANA zone id.", element["properties"]!["zone"]!["description"]!.GetValue<string>());
        Assert.Contains("clock (clocks plugin)", element["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("clocks_now", catalog.Names);
        Assert.Contains("Put a clock wherever", catalog.Instructions, StringComparison.Ordinal);
        Assert.Equal(1, changed);

        // And gone again when the plugin is.
        catalog.SetPlugins([]);
        Assert.DoesNotContain("clocks_now", catalog.Names);
        Assert.DoesNotContain("## Plugins", catalog.Instructions, StringComparison.Ordinal);
        Assert.Equal(catalog.CoreNames.Order(), catalog.Names.Order());
    }

    [Fact]
    public void AFieldTwoTypesShareIsDescribedForBoth()
    {
        var catalog = ToolCatalog.Load();
        var shared = Clock() with
        {
            Elements = [new ExtraElementType("alarm", "clocks", "An alarm.",
                new JsonObject { ["zone"] = new JsonObject { ["type"] = "string", ["description"] = "Where it rings." } }, [], IsVisual: false)],
            Tools = [],
        };

        catalog.SetPlugins([Clock(), shared with { Id = "alarms" }]);

        var putSlide = catalog.Tools.OfType<JsonObject>().Single(t => t["name"]!.GetValue<string>() == "put_slide");
        var zone = putSlide["inputSchema"]!["properties"]!["slide"]!["properties"]!["elements"]!["items"]!["properties"]!["zone"]!;

        Assert.Equal("clock: An IANA zone id. alarm: Where it rings.", zone["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task APluginToolIsCalledAndItsFailuresAreTheAgentsToHear()
    {
        _tools.SetPluginTools(new Dictionary<string, PluginToolHandler>
        {
            ["clocks_now"] = new("clocks", (arguments, _) => Task.FromResult($"12:00 in {arguments["zone"]}")),
            ["clocks_bad"] = new("clocks", (_, _) => throw new ArgumentException("zone is required")),
            ["clocks_broken"] = new("clocks", (_, _) => throw new InvalidOperationException("the clock fell off the wall")),
        });

        var ok = await _tools.CallAsync("clocks_now", new JsonObject { ["zone"] = "Europe/Zagreb" }, CancellationToken.None);
        var bad = await _tools.CallAsync("clocks_bad", [], CancellationToken.None);
        var broken = await _tools.CallAsync("clocks_broken", [], CancellationToken.None);

        Assert.Equal("12:00 in Europe/Zagreb", ok.Text);
        Assert.False(ok.IsError);
        Assert.Equal("zone is required", bad.Text);
        Assert.True(bad.IsError);
        Assert.Contains("the clocks plugin could not answer: the clock fell off the wall", broken.Text, StringComparison.Ordinal);
        Assert.True(broken.IsError);
    }

    [Fact]
    public async Task ReadElementSaysWhatALiveElementShowsNow()
    {
        _stage.State = _stage.State with { CurrentSlideId = "world", CurrentIndex = 0 };
        _stage.Readings["world.zones"] = new ElementReading("world", "zones", "timezones", Live: true, "Zagreb 06:05, Melbourne 14:05");

        var result = await _tools.CallAsync("read_element", new JsonObject { ["element"] = "zones" }, CancellationToken.None);
        var dotted = await _tools.CallAsync("read_element", new JsonObject { ["element"] = "world.zones" }, CancellationToken.None);
        var missing = await _tools.CallAsync("read_element", new JsonObject { ["element"] = "weather" }, CancellationToken.None);

        using var reading = JsonDocument.Parse(result.Text);
        Assert.Equal("Zagreb 06:05, Melbourne 14:05", reading.RootElement.GetProperty("shows").GetString());
        Assert.True(reading.RootElement.GetProperty("live").GetBoolean());
        Assert.False(dotted.IsError);
        Assert.True(missing.IsError);
    }

    private static CatalogPlugin Clock() => new(
        "clocks",
        "Clocks",
        "1.0.0",
        "Put a clock wherever a time matters.",
        [new ExtraElementType("clock", "clocks", "A clock somewhere.",
            new JsonObject { ["zone"] = new JsonObject { ["type"] = "string", ["description"] = "An IANA zone id." } }, ["zone"], IsVisual: false)],
        [new CatalogTool("clocks_now", "The time somewhere.", new JsonObject { ["type"] = "object" })]);

    private sealed class NoMedia : IMediaTools
    {
        public Task<InspectedImage> InspectAsync(string source, bool grid, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectedImage([1], 10, 10, "none"));

        public Task<IReadOnlyList<MediaItem>> ScanAsync(string folder, bool recursive, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaItem>>([]);
    }
}
