using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Rendering;

namespace Intrf4ce.Tests.Core;

public class ChartSpecTests
{
    private static ChartElement Chart(string kind, string data, string x, string y, string? series = null)
    {
        using var document = JsonDocument.Parse(data);
        return new ChartElement { Id = "c", Kind = kind, Data = document.RootElement.Clone(), X = x, Y = y, Series = series };
    }

    [Fact]
    public void BarsKeepTheDataOrderAndGroupSeriesSideBySide()
    {
        var chart = Chart("bar", """[{"m":"Jan","v":1,"s":"a"},{"m":"Feb","v":2,"s":"b"}]""", "m", "v", "s");

        var spec = JsonNode.Parse(ChartSpec.ToVegaLite(chart, 800, 500))!;

        Assert.Equal("bar", spec["mark"]!["type"]!.GetValue<string>());
        Assert.Null(spec["encoding"]!["x"]!["sort"]);
        Assert.Equal("s", spec["encoding"]!["xOffset"]!["field"]!.GetValue<string>());
        Assert.Equal(800, spec["width"]!.GetValue<double>());
    }

    [Fact]
    public void DatesGetATimeAxis()
    {
        var chart = Chart("line", """[{"d":"2024-01-01","v":1},{"d":"2024-02-01","v":3}]""", "d", "v");

        var spec = JsonNode.Parse(ChartSpec.ToVegaLite(chart, 800, 500))!;

        Assert.Equal("temporal", spec["encoding"]!["x"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void DonutsAreArcsWithAHole()
    {
        var chart = Chart("donut", """[{"k":"a","v":1},{"k":"b","v":3}]""", "k", "v");

        var spec = JsonNode.Parse(ChartSpec.ToVegaLite(chart, 600, 400))!;

        Assert.Equal("arc", spec["mark"]!["type"]!.GetValue<string>());
        Assert.True(spec["mark"]!["innerRadius"]!.GetValue<double>() > 0);
        Assert.Equal("v", spec["encoding"]!["theta"]!["field"]!.GetValue<string>());
    }

    [Fact]
    public void AFullSpecKeepsItsContentButTakesTheStagesSize()
    {
        using var document = JsonDocument.Parse("""{"mark":"point","width":10,"height":10,"encoding":{}}""");
        var chart = new ChartElement { Id = "c", VegaLite = document.RootElement.Clone() };

        var spec = JsonNode.Parse(ChartSpec.ToVegaLite(chart, 700, 300))!;

        Assert.Equal("point", spec["mark"]!.GetValue<string>());
        Assert.Equal(700, spec["width"]!.GetValue<double>());
        Assert.Equal(300, spec["height"]!.GetValue<double>());
    }
}
