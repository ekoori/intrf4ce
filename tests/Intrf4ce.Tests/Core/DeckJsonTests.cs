using System.Text.Json;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Tests.Core;

public class DeckJsonTests
{
    [Fact]
    public void ElementsRoundTripThroughTheirTypeDiscriminator()
    {
        var slide = new Slide
        {
            Id = "ligaments",
            Title = "Four ligaments",
            Elements =
            [
                new ImageElement
                {
                    Id = "knee",
                    Src = "https://example.org/knee.png",
                    Callouts = [new Callout { Id = "acl", At = [0.47, 0.44], Label = "ACL" }],
                },
                new MarkdownElement { Id = "notes", Text = "- **Cruciates** cross" },
            ],
            Narration = "Four ligaments. [[focus knee.acl]] The ACL.",
        };

        var json = JsonSerializer.Serialize(slide, DeckJson.Options);
        var back = JsonSerializer.Deserialize<Slide>(json, DeckJson.Options)!;

        Assert.Equal("ligaments", back.Id);
        var image = Assert.IsType<ImageElement>(back.Elements[0]);
        Assert.Equal([0.47, 0.44], image.Callouts![0].At!);
        Assert.IsType<MarkdownElement>(back.Elements[1]);
        Assert.Contains("\"type\":\"image\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTypeMayComeAnywhereInTheObject()
    {
        // Agents do not reliably put "type" first; the serializer default requires it.
        const string json = """{"id":"t","text":"Hello","type":"title"}""";

        var element = JsonSerializer.Deserialize<Element>(json, DeckJson.Options);

        Assert.Equal("Hello", Assert.IsType<TitleElement>(element).Text);
    }

    [Fact]
    public void AMisspelledPropertyIsAnErrorRatherThanSilentlyDropped()
    {
        const string json = """{"type":"image","id":"k","src":"a.png","callout":[]}""";

        var ex = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Element>(json, DeckJson.Options));

        Assert.Contains("callout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChartDataKeepsItsJsonShape()
    {
        const string json = """{"type":"chart","id":"c","kind":"bar","data":[{"k":"Mon","v":3}],"x":"k","y":"v"}""";

        var chart = Assert.IsType<ChartElement>(JsonSerializer.Deserialize<Element>(json, DeckJson.Options));

        Assert.Equal("bar", chart.Kind);
        Assert.Equal(JsonValueKind.Array, chart.Data!.Value.ValueKind);
        Assert.Equal(3, chart.Data.Value[0].GetProperty("v").GetInt32());
    }
}
