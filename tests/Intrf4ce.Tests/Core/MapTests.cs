using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// The arithmetic a map stands on: a place must land on the pixel its tile draws it at, or
/// every marker sits beside the thing it names.
/// </summary>
public class MapTests
{
    private static readonly LatLon London = new(51.5074, -0.1278);
    private static readonly LatLon Cairo = new(30.0444, 31.2357);
    private static readonly LatLon Tokyo = new(35.6762, 139.6503);
    private static readonly LatLon LosAngeles = new(34.0522, -118.2437);

    [Fact]
    public void TheMiddleOfTheWorldIsTheMiddleOfTheFirstTile()
    {
        var (x, y) = WebMercator.ToWorld(new LatLon(0, 0), 0);

        Assert.Equal(128, x, 6);
        Assert.Equal(128, y, 6);
    }

    /// <summary>The tile numbers every slippy map agrees on, checked against a known one.</summary>
    [Fact]
    public void LondonIsInTheTileEveryMapPutsItIn()
    {
        var (x, y) = WebMercator.ToWorld(London, 12);

        Assert.Equal(2046, (int)(x / WebMercator.TileSize));
        Assert.Equal(1362, (int)(y / WebMercator.TileSize));
    }

    [Property(MaxTest = 300)]
    public Property APlaceProjectsBackToItself(PositiveInt latitude, PositiveInt longitude, PositiveInt zoom)
    {
        // Anywhere a map can draw: past 85 degrees the projection runs off to infinity.
        var place = new LatLon((latitude.Get % 16900 / 100.0) - 84.5, (longitude.Get % 35900 / 100.0) - 179.5);
        var close = zoom.Get % (WebMercator.MaxZoom + 1);

        var (x, y) = WebMercator.ToWorld(place, close);
        var back = WebMercator.ToPlace(x, y, close);

        return (Math.Abs(back.Latitude - place.Latitude) < 1e-6 && Math.Abs(back.Longitude - place.Longitude) < 1e-6)
            .ToProperty()
            .Label($"{place} at zoom {close} came back as {back}");
    }

    /// <summary>Whatever the map is asked to show, all of it has to be on screen.</summary>
    [Theory]
    [InlineData(900, 600)]
    [InlineData(400, 900)]
    [InlineData(1600, 200)]
    public void EveryPlaceFitsInsideTheFrameThatWasChosenForThem(double width, double height)
    {
        LatLon[] places = [London, Cairo, new(-33.8688, 151.2093)];

        var frame = WebMercator.Fit(places, width, height);
        var centre = WebMercator.ToWorld(frame.Centre, frame.Zoom);

        foreach (var place in places)
        {
            var (x, y) = WebMercator.ToWorld(place, frame.Zoom);
            var size = WebMercator.WorldSize(frame.Zoom);
            var dx = x - centre.X;
            dx -= Math.Round(dx / size) * size;

            Assert.InRange(dx, -width / 2, width / 2);
            Assert.InRange(y - centre.Y, -height / 2, height / 2);
        }
    }

    /// <summary>
    /// Tokyo and Los Angeles are neighbours across the Pacific, not opposite ends of a map of
    /// the whole world with Europe in the middle.
    /// </summary>
    [Fact]
    public void PlacesEitherSideOfTheDateLineAreFramedTheShortWayRound()
    {
        var frame = WebMercator.Fit([Tokyo, LosAngeles], 900, 600);

        Assert.True(Math.Abs(frame.Centre.Longitude) > 150, $"centred on {frame.Centre.Longitude:0.#}, the long way round");
        Assert.True(frame.Zoom >= 2, $"zoomed to {frame.Zoom}, as though they were half a world apart");
    }

    [Fact]
    public void OnePlaceOnItsOwnIsShownCloseUp()
    {
        var frame = WebMercator.Fit([Cairo], 900, 600);

        Assert.Equal(Cairo.Latitude, frame.Centre.Latitude, 3);
        Assert.Equal(11, frame.Zoom);
    }

    [Fact]
    public void PlacesAreNamedByTheirLabelsUnlessTheAgentNamedThem()
    {
        IReadOnlyList<Place> places =
        [
            new() { Label = "Cairo" },
            new() { Label = "Cairo" },
            new() { Id = "mouth", Label = "The mouth of the Nile" },
            new() { Label = "" },
        ];

        Assert.Equal(["cairo", "cairo-2", "mouth", "place4"], ElementParts.Places(places));
    }

    [Fact]
    public void AMapWithNowhereToShowIsRejectedAndSaysWhatIsMissing()
    {
        var issues = SpecValidator.Validate(new Slide
        {
            Id = "s",
            Elements = [new MapElement { Id = "m" }],
        });

        Assert.Contains(issues, i => i.Message.Contains("needs places", StringComparison.Ordinal));
    }

    [Fact]
    public void APlaceTheWrongWayRoundIsRejectedWithTheSuspicion()
    {
        var issues = SpecValidator.Validate(new Slide
        {
            Id = "s",
            Elements =
            [
                new MapElement
                {
                    Id = "m",
                    Places = [new Place { Label = "Cairo", At = [31.2357, 30.0444] }, new Place { Label = "Nowhere", At = [200, 5] }],
                },
            ],
        });

        Assert.DoesNotContain(issues, i => i.Path.Contains("places[0]", StringComparison.Ordinal));

        var wrong = Assert.Single(issues, i => i.Path.Contains("places[1]", StringComparison.Ordinal));
        Assert.Contains("the wrong way round?", wrong.Message, StringComparison.Ordinal);
    }

    /// <summary>A cue that names a place must be checked before the map is ever drawn.</summary>
    [Fact]
    public void ACueAtAPlaceTheMapDoesNotHaveIsReported()
    {
        var slide = new Slide
        {
            Id = "s",
            Elements = [new MapElement { Id = "journey", Places = [new Place { Label = "Cairo", At = [30.0444, 31.2357] }] }],
            Narration = "We start in [[focus journey.cairo]] Cairo and go to [[focus journey.luxor]] Luxor.",
        };

        var issues = SpecValidator.Validate(slide);

        var problem = Assert.Single(issues);
        Assert.Contains("journey.luxor", problem.Message, StringComparison.Ordinal);
        Assert.Contains("cairo", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>Tiles cover the whole view, and a longitude past the edge wraps rather than 404s.</summary>
    [Fact]
    public void TheTilesAskedForCoverTheViewAndWrapAroundTheWorld()
    {
        var covering = Intrf4ce.Media.Images.TileSource.Covering(2, -300, 100, 800, 500);

        Assert.All(covering, t => Assert.InRange(t.Tile.X, 0, 3));
        Assert.All(covering, t => Assert.InRange(t.Tile.Y, 0, 3));

        // Every pixel of the view is inside some tile.
        Assert.True(covering.Min(t => t.X) <= 0);
        Assert.True(covering.Max(t => t.X) + 256 >= 800);
        Assert.True(covering.Min(t => t.Y) <= 0);
        Assert.True(covering.Max(t => t.Y) + 256 >= 500);
    }
}
