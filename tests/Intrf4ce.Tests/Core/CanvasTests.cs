using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// A canvas is a slide as big as it needs to be. Ordinary slides must not notice it exists,
/// and nothing may overlap it.
/// </summary>
public class CanvasTests
{
    [Fact]
    public void OrdinarySlidesLandExactlyWhereTheyAlwaysHave()
    {
        var layout = WorldLayout.Standard(12);

        for (var i = 0; i < 12; i++)
        {
            var column = i % 5;
            var row = i / 5;
            Assert.Equal(new Box(column * (1600 + 240), row * (900 + 200), 1600, 900), layout.Rect(i));
        }
    }

    [Property(MaxTest = 200)]
    public Property NoSlideOverlapsAnother(NonEmptyArray<PositiveInt> seeds)
    {
        var sizes = seeds.Get.Take(14)
            .Select(s => ((double)(800 + (s.Get % 9) * 1000), (double)(450 + (s.Get % 7) * 700)))
            .ToList();
        var layout = new WorldLayout(sizes);

        var rects = Enumerable.Range(0, sizes.Count).Select(layout.Rect).ToList();
        var apart = rects.SelectMany((a, i) => rects.Skip(i + 1).Select(b => (a, b)))
            .All(pair => pair.a.X + pair.a.Width <= pair.b.X || pair.b.X + pair.b.Width <= pair.a.X
                || pair.a.Y + pair.a.Height <= pair.b.Y || pair.b.Y + pair.b.Height <= pair.a.Y);
        var sized = rects.Select((r, i) => r.Width == sizes[i].Item1 && r.Height == sizes[i].Item2).All(x => x);

        return (apart && sized).ToProperty().Label($"apart={apart} sized={sized}");
    }

    [Fact]
    public void ACanvasPushesTheSlidesAfterItAlong()
    {
        var layout = new WorldLayout([(1600, 900), (4800, 2700), (1600, 900)]);

        Assert.Equal(1600 + 240, layout.Rect(1).X);
        Assert.Equal(1600 + 240 + 4800 + 240, layout.Rect(2).X);
        Assert.Equal(1, layout.IndexAt(3000, 2000));
        Assert.Equal(new Box(0, 0, layout.Rect(2).X + 1600, 2700), layout.Bounds(3));
    }

    [Fact]
    public void ElementsOnACanvasArePlacedAsFractionsOfTheCanvas()
    {
        var slide = new Slide
        {
            Id = "c",
            Layout = "free",
            Size = [4800, 2700],
            Elements = [new MarkdownElement { Id = "m", Text = "x", Rect = [0.5, 0.5, 0.25, 0.25] }],
        };

        Assert.Equal(new Box(2400, 1350, 1200, 675), SlideLayout.Arrange(slide)["m"]);
        Assert.True(SlideLayout.IsCanvas(slide));
        Assert.False(SlideLayout.IsCanvas(slide with { Size = null }));
    }

    [Fact]
    public void ASizeThatIsNotOneIsRefusedAndOneTooBigIsDrawnAtTheLimit()
    {
        var bad = SpecValidator.Validate(new Slide { Id = "s", Size = [4800] });
        Assert.Contains(bad, i => i.Severity == IssueSeverity.Error && i.Path == "size");

        var huge = new Slide { Id = "s", Size = [50000, 900] };
        Assert.Contains(SpecValidator.Validate(huge), i => i.Severity == IssueSeverity.Warning && i.Path == "size");
        Assert.Equal((9600, 900), SlideLayout.SizeOf(huge));
    }

    [Fact]
    public void ACanvasLeftToTheAutomaticLayoutsIsToldToPlaceThingsByHand()
    {
        var canvas = new Slide
        {
            Id = "s",
            Size = [4800, 2700],
            Elements = [new MarkdownElement { Id = "a", Text = "a" }, new MarkdownElement { Id = "b", Text = "b" }],
        };

        Assert.Contains(SpecValidator.Validate(canvas), i => i.Path == "layout" && i.Message.Contains("free", StringComparison.Ordinal));
    }
}
