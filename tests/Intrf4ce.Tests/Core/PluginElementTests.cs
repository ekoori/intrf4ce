using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// An element of a type a plugin adds is kept exactly as written, loaded or not, and checked
/// against the plugin when the agent sends it.
/// </summary>
public class PluginElementTests
{
    [Fact]
    public void AnElementOfATypeNobodyHasKeepsItsFieldsThroughDeckJson()
    {
        const string json = """
            {"id":"s","title":"Weather","elements":[
              {"type":"forecast-test","id":"wx","caption":"Today","place":"Zagreb","days":4,"at":[45.8,15.97]}
            ]}
            """;

        var slide = JsonSerializer.Deserialize<Slide>(json, DeckJson.Options)!;
        var element = Assert.IsType<PluginElement>(Assert.Single(slide.Elements));

        Assert.Equal("forecast-test", element.TypeName);
        Assert.Equal("wx", element.Id);
        Assert.Equal("Today", element.Caption);
        Assert.Equal("Zagreb", element.Fields.GetProperty("place").GetString());
        Assert.False(element.Fields.TryGetProperty("id", out _));

        var again = JsonSerializer.Serialize(slide, DeckJson.File);
        var back = Assert.IsType<PluginElement>(JsonSerializer.Deserialize<Slide>(again, DeckJson.Options)!.Elements[0]);

        Assert.Equal(4, back.Fields.GetProperty("days").GetInt32());
        Assert.Equal(15.97, back.Fields.GetProperty("at")[1].GetDouble());
        Assert.Contains("\"type\": \"forecast-test\"", again, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatEarlierDecksWroteAboutTheTypeIsReadPastAndNotWrittenAgain()
    {
        // Before the converter, an element's computed TypeName and IsVisual went to disk.
        const string json = """{"type":"map","places":[],"route":false,"typeName":"map","isVisual":true,"id":"m","hidden":false}""";

        var map = Assert.IsType<MapElement>(JsonSerializer.Deserialize<Element>(json, DeckJson.Options));
        var written = JsonSerializer.Serialize<Element>(map, DeckJson.Options);

        Assert.DoesNotContain("typeName", written, StringComparison.Ordinal);
        Assert.DoesNotContain("isVisual", written, StringComparison.Ordinal);
        Assert.StartsWith("{\"type\":\"map\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void AMistakeInsideAnElementSaysWhichElementAndWhereInIt()
    {
        const string json = """{"id":"s","elements":[{"type":"markdown","text":"x"},{"type":"image","src":"a.png","callouts":[{"id":"a","label":"A","lable":"B"}]}]}""";

        var ex = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Slide>(json, DeckJson.Options));
        var inside = Assert.IsType<ElementJsonException>(ex);

        Assert.Equal("$.elements[1]", inside.Path);
        Assert.Equal("callouts[0].lable", inside.Inner);
        Assert.Contains("lable", inside.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnElementWithoutATypeIsRefusedWithTheTypesThereAre()
    {
        var ex = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Element>("""{"id":"x","text":"hi"}""", DeckJson.Options));

        Assert.Contains("\"type\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("markdown", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeNoLoadedPluginHasIsAnErrorTheAgentCanAct()
    {
        var slide = SlideWith("""{"type":"no-such-thing","id":"x"}""");

        var issue = Assert.Single(SpecValidator.Validate(slide), i => i.Severity == IssueSeverity.Error);

        Assert.Equal("elements[0].type", issue.Path);
        Assert.Contains("markdown", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APluginElementIsCheckedAgainstWhatItsPluginSaid()
    {
        var name = "clock-" + Guid.NewGuid().ToString("n")[..8];
        ElementTypes.Add(new ExtraElementType(
            name,
            "clock-test",
            "A clock.",
            new JsonObject
            {
                ["zone"] = new JsonObject { ["type"] = "string" },
                ["seconds"] = new JsonObject { ["type"] = "boolean" },
            },
            ["zone"],
            IsVisual: false,
            Check: fields => fields.TryGetProperty("zone", out var zone) && zone.GetString() == "Mars/Olympus"
                ? [new ExtraElementIssue("zone", "Mars has no time zone yet", IsWarning: true)]
                : []));

        try
        {
            var missing = SpecValidator.Validate(SlideWith($$"""{"type":"{{name}}","id":"c","second":true}"""));
            Assert.Contains(missing, i => i.Path == "elements[0].second" && i.Message.Contains("seconds", StringComparison.Ordinal));
            Assert.Contains(missing, i => i.Path == "elements[0].zone" && i.Severity == IssueSeverity.Error);

            var warned = SpecValidator.Validate(SlideWith($$"""{"type":"{{name}}","id":"c","zone":"Mars/Olympus"}"""));
            var warning = Assert.Single(warned);
            Assert.Equal(IssueSeverity.Warning, warning.Severity);
            Assert.Equal("elements[0].zone", warning.Path);

            Assert.Empty(SpecValidator.Validate(SlideWith($$"""{"type":"{{name}}","id":"c","zone":"Europe/Zagreb","seconds":true}""")));
            Assert.False(SlideWith($$"""{"type":"{{name}}","id":"c"}""").Elements[0].IsVisual);
        }
        finally
        {
            ElementTypes.RemovePlugin("clock-test");
        }
    }

    [Fact]
    public void APluginsCheckThatThrowsIsAWarningNotALostSlide()
    {
        var name = "broken-" + Guid.NewGuid().ToString("n")[..8];
        ElementTypes.Add(new ExtraElementType(name, "broken-test", "Broken.", [], [], IsVisual: true,
            Check: _ => throw new InvalidOperationException("it fell over")));

        try
        {
            var issue = Assert.Single(SpecValidator.Validate(SlideWith($$"""{"type":"{{name}}","id":"b"}""")));

            Assert.Equal(IssueSeverity.Warning, issue.Severity);
            Assert.Contains("it fell over", issue.Message, StringComparison.Ordinal);
        }
        finally
        {
            ElementTypes.RemovePlugin("broken-test");
        }
    }

    [Fact]
    public void APluginCannotTakeANameIntrf4ceAlreadyHas()
    {
        Assert.True(ElementTypes.IsTaken("image"));
        Assert.Throws<InvalidOperationException>(() =>
            ElementTypes.Add(new ExtraElementType("image", "thief", "No.", [], [], IsVisual: true)));
    }

    private static Slide SlideWith(string element) =>
        JsonSerializer.Deserialize<Slide>($$"""{"id":"s","elements":[{{element}}]}""", DeckJson.Options)!;
}
