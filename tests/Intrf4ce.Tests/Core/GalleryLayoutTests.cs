using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Intrf4ce.Core.Layout;

namespace Intrf4ce.Tests.Core;

public class GalleryLayoutTests
{
    private static readonly Box Area = new(0, 0, 1400, 700);

    private static double[] Aspects(PositiveInt[] seeds) =>
        [.. seeds.Take(60).Select(s => 0.5 + ((s.Get % 20) / 10.0))];

    [Property(MaxTest = 300)]
    public Property ScatteredPhotosStayInsideTheArea(NonEmptyArray<PositiveInt> seeds, int seed)
    {
        var aspects = Aspects(seeds.Get);

        var placements = GalleryLayout.Scatter(Area, aspects, seed);

        return (placements.Count == aspects.Length && placements.All(p => Area.Inflate(0.5).Contains(p.Covered))).ToProperty();
    }

    [Fact]
    public void ScatterIsTheSameEveryTimeForTheSameSeed()
    {
        double[] aspects = [1.5, 0.75, 1.33, 1];

        Assert.Equal(GalleryLayout.Scatter(Area, aspects, 42), GalleryLayout.Scatter(Area, aspects, 42));
        Assert.NotEqual(GalleryLayout.Scatter(Area, aspects, 42), GalleryLayout.Scatter(Area, aspects, 43));
    }

    [Property(MaxTest = 300)]
    public Property JustifiedRowsKeepAspectsDoNotOverlapAndFit(NonEmptyArray<PositiveInt> seeds)
    {
        var aspects = Aspects(seeds.Get);

        var placements = GalleryLayout.Justified(Area, aspects);

        var keepsAspect = placements.Select((p, i) => Math.Abs((p.Box.Width / p.Box.Height) - Math.Clamp(aspects[i], 0.25, 4)) < 1e-6).All(x => x);
        var inside = placements.All(p => Area.Inflate(0.5).Contains(p.Box));
        var disjoint = placements.SelectMany((a, i) => placements.Skip(i + 1).Select(b => (a, b))).All(pair => !pair.a.Box.Intersects(pair.b.Box));

        return (keepsAspect && inside && disjoint).Label($"aspect={keepsAspect} inside={inside} disjoint={disjoint}");
    }

    [Property(MaxTest = 300)]
    public Property AShortLastRowIsNoTallerThanTheRowsAbove(NonEmptyArray<PositiveInt> seeds)
    {
        var placements = GalleryLayout.Justified(Area, Aspects(seeds.Get));

        var rows = placements.GroupBy(p => Math.Round(p.Box.Y, 6)).OrderBy(g => g.Key).Select(g => g.Select(p => p.Box).ToList()).ToList();
        static double Span(List<Box> row) => row.Max(b => b.Right) - row.Min(b => b.X);
        var isShort = rows.Count > 1 && Span(rows[^1]) < Span(rows[0]) - 1;

        return (!isShort || rows[^1][0].Height <= rows.Take(rows.Count - 1).Max(r => r[0].Height) + 1e-6)
            .Label(string.Join(", ", rows.Select(r => r[0].Height.ToString("0.#"))));
    }

    [Property(MaxTest = 300)]
    public Property TimelinePhotosFitAndDoNotOverlapTheirSide(NonEmptyArray<PositiveInt> seeds)
    {
        var aspects = Aspects(seeds.Get);

        var (placements, axis) = GalleryLayout.Timeline(Area, aspects);

        var inside = placements.All(p => Area.Inflate(0.5).Contains(p.Box));
        var sides = placements.Select((p, i) => (p, i)).GroupBy(x => x.i % 2).Select(g => g.Select(x => x.p.Box).ToList());
        var apart = sides.All(side => side.Zip(side.Skip(1)).All(pair => pair.First.Right <= pair.Second.X + 1e-6));

        return (inside && apart).Label($"inside={inside} apart={apart}");
    }

    [Fact]
    public void TimelineAlternatesAboveAndBelowTheAxis()
    {
        var (placements, axis) = GalleryLayout.Timeline(Area, [1.5, 1.5, 1.5, 1.5]);

        Assert.True(placements[0].Box.Bottom <= axis);
        Assert.True(placements[1].Box.Y >= axis);
        Assert.True(placements[2].Box.Bottom <= axis);
        Assert.True(placements.Select(p => p.Box.Center.X).SequenceEqual(placements.Select(p => p.Box.Center.X).Order()));
    }
}
