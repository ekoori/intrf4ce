using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Tests.Core;

public class LayoutTests
{
    private static Element Visual(string id) => new ImageElement { Id = id, Src = "x.png" };

    private static Element Text(string id) => new MarkdownElement { Id = id, Text = "x" };

    [Theory]
    [InlineData(1, 0, "full")]
    [InlineData(1, 1, "wide")]
    [InlineData(0, 2, "split")]
    [InlineData(2, 0, "split")]
    [InlineData(1, 2, "wide")]
    [InlineData(0, 3, "stack")]
    [InlineData(2, 2, "grid")]
    public void TheLayoutIsChosenFromTheElements(int visuals, int texts, string expected)
    {
        var elements = Enumerable.Range(0, visuals).Select(i => Visual("v" + i))
            .Concat(Enumerable.Range(0, texts).Select(i => Text("t" + i)))
            .ToList();

        Assert.Equal(expected, SlideLayout.Resolve(new Slide { Elements = elements }));
    }

    [Fact]
    public void TheVisualGetsTheWideColumnEvenWhenListedSecond()
    {
        var slide = new Slide { Elements = [Text("notes"), Visual("knee")] };

        var boxes = SlideLayout.Arrange(slide);

        Assert.True(boxes["knee"].Width > boxes["notes"].Width);
        Assert.True(boxes["knee"].X < boxes["notes"].X);
    }

    [Property(MaxTest = 200)]
    public Property ArrangedElementsStayInsideTheContentAndNeverOverlap(PositiveInt count, bool[] visual, bool titled, int layoutPick)
    {
        var n = 1 + (count.Get % 7);
        var elements = Enumerable.Range(0, n)
            .Select(i => i < visual.Length && visual[i] ? Visual("e" + i) : Text("e" + i))
            .ToList();
        var layouts = new string?[] { null, "full", "split", "wide", "stack", "grid" };
        var slide = new Slide
        {
            Title = titled ? "A title" : null,
            Layout = layouts[Math.Abs(layoutPick % layouts.Length)],
            Elements = elements,
        };

        var boxes = SlideLayout.Arrange(slide).Values.ToList();
        var content = SlideLayout.ContentArea(titled);

        var inside = boxes.All(b => content.Contains(b));
        var disjoint = boxes.SelectMany((a, i) => boxes.Skip(i + 1).Select(b => (a, b))).All(p => !p.a.Intersects(p.b));
        var all = boxes.Count == n;

        return (inside && disjoint && all).ToProperty();
    }

    [Fact]
    public void FreeLayoutUsesSlideFractions()
    {
        var slide = new Slide
        {
            Layout = "free",
            Elements = [new MarkdownElement { Id = "a", Text = "x", Rect = [0.5, 0.5, 0.25, 0.25] }],
        };

        Assert.Equal(new Box(800, 450, 400, 225), SlideLayout.Arrange(slide)["a"]);
    }

    [Property(MaxTest = 300)]
    public Property CalloutLabelsNeverOverlapAndLeadersNeverCross(NonEmptyArray<(NormalFloat X, NormalFloat Y)> anchors)
    {
        var image = new Box(300, 40, 600, 700);
        var bounds = new Box(0, 0, 1200, 780);
        var inputs = anchors.Get
            .Take(12)
            .Select((a, i) => new CalloutInput(
                "c" + i,
                new Pt(image.X + (Frac(a.X.Get) * image.Width), image.Y + (Frac(a.Y.Get) * image.Height)),
                null,
                (80 + (i % 3 * 20), 28),
                null))
            .ToList();

        var result = CalloutLayout.Arrange(image, bounds, inputs);
        var placed = result.Placements;

        var labelsDisjoint = placed
            .SelectMany((a, i) => placed.Skip(i + 1).Select(b => (a, b)))
            .All(p => !p.a.Label.Intersects(p.b.Label));

        var noCrossings = placed
            .GroupBy(p => p.Side)
            .All(side => side
                .SelectMany((a, i) => side.Skip(i + 1).Select(b => (a, b)))
                .All(p => !Segments.Cross(p.a.Leader[1], p.a.Leader[2], p.b.Leader[1], p.b.Leader[2])));

        var inBounds = result.Overflowed || placed.All(p => bounds.Contains(p.Label));

        return (placed.Count == inputs.Count && labelsDisjoint && noCrossings && inBounds)
            .Label($"disjoint={labelsDisjoint} uncrossed={noCrossings} inBounds={inBounds}");
    }

    [Fact]
    public void LabelsGoToTheSideNearestTheirAnchorUnlessToldOtherwise()
    {
        var image = new Box(300, 0, 600, 600);
        var inputs = new[]
        {
            new CalloutInput("left", new Pt(350, 100), null, (60, 24), null),
            new CalloutInput("right", new Pt(850, 100), null, (60, 24), null),
            new CalloutInput("forced", new Pt(850, 300), null, (60, 24), "left"),
        };

        var placed = CalloutLayout.Arrange(image, new Box(0, 0, 1200, 600), inputs).Placements.ToDictionary(p => p.Id);

        Assert.Equal("left", placed["left"].Side);
        Assert.Equal("right", placed["right"].Side);
        Assert.Equal("left", placed["forced"].Side);
        Assert.True(placed["left"].Label.Right <= image.X);
    }

    [Fact]
    public void AnAreaCalloutsLeaderStopsAtTheAreasEdge()
    {
        var area = new Box(500, 200, 100, 100);
        var input = new CalloutInput("a", area.Center, area, (60, 24), "left");

        var placement = CalloutLayout.Arrange(new Box(300, 0, 600, 600), new Box(0, 0, 1200, 600), [input]).Placements[0];

        var end = placement.Leader[^1];
        Assert.True(Math.Abs(end.X - area.X) < 1e-6 || Math.Abs(end.Y - area.Y) < 1e-6 || Math.Abs(end.Y - area.Bottom) < 1e-6);
    }

    private static double Frac(double value)
    {
        var f = Math.Abs(value) % 1;
        return double.IsFinite(f) ? f : 0.5;
    }
}
