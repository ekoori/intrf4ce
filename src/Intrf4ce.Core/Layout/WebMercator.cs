namespace Intrf4ce.Core.Layout;

/// <summary>A place on the earth.</summary>
public readonly record struct LatLon(double Latitude, double Longitude);

/// <summary>What a map is showing: where its middle is, and how close in.</summary>
/// <param name="Zoom">The slippy-map zoom: 0 is the whole world, 19 is a doorway.</param>
public readonly record struct MapFrame(LatLon Centre, double Zoom);

/// <summary>
/// The projection every tiled map uses, so a place lands on the same pixel as its tile.
/// </summary>
/// <remarks>
/// Web Mercator, as OpenStreetMap and every other slippy map draws it: the world is one square
/// tile at zoom 0 and four times as many at each zoom after that, with latitude stretched so
/// that north is always up and a small area keeps its shape. Latitudes past about 85 degrees
/// project to infinity, so they are clamped; nothing is presented from the poles anyway.
/// <para>
/// Here rather than in the view because it is arithmetic with right answers, which is worth
/// testing on its own, and the view that draws the tiles must agree with it exactly or every
/// marker sits beside the thing it names.
/// </para>
/// </remarks>
public static class WebMercator
{
    public const int TileSize = 256;

    /// <summary>Past this the projection runs off to infinity.</summary>
    public const double MaxLatitude = 85.05112878;

    public const int MinZoom = 0;

    /// <summary>OpenStreetMap's own tiles stop here.</summary>
    public const int MaxZoom = 19;

    /// <summary>World pixels across at <paramref name="zoom"/>.</summary>
    public static double WorldSize(double zoom) => TileSize * Math.Pow(2, zoom);

    /// <summary>Where a place is in world pixels at <paramref name="zoom"/>, from the top-left.</summary>
    public static (double X, double Y) ToWorld(LatLon place, double zoom)
    {
        var size = WorldSize(zoom);
        var latitude = Math.Clamp(place.Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        var x = (Wrap(place.Longitude) + 180) / 360 * size;
        var y = (1 - (Math.Log(Math.Tan(latitude) + (1 / Math.Cos(latitude))) / Math.PI)) / 2 * size;
        return (x, y);
    }

    /// <summary>The place at a point in world pixels: the inverse of <see cref="ToWorld"/>.</summary>
    public static LatLon ToPlace(double x, double y, double zoom)
    {
        var size = WorldSize(zoom);
        var longitude = (x / size * 360) - 180;
        var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - (2 * y / size)))) * 180 / Math.PI;
        return new LatLon(latitude, longitude);
    }

    /// <summary>Longitudes wrap; 190 east is 170 west, and a map must not draw it off the edge.</summary>
    public static double Wrap(double longitude)
    {
        var wrapped = (longitude + 180) % 360;
        return (wrapped < 0 ? wrapped + 360 : wrapped) - 180;
    }

    /// <summary>
    /// The frame that shows every one of <paramref name="places"/> inside a rectangle of
    /// <paramref name="width"/> by <paramref name="height"/> pixels, with room to spare.
    /// </summary>
    /// <param name="padding">A share of the rectangle left empty around the edge, 0 to 0.4.</param>
    /// <param name="single">The zoom for one place on its own, where there is nothing to fit.</param>
    public static MapFrame Fit(
        IReadOnlyList<LatLon> places, double width, double height, double padding = 0.12, double single = 11)
    {
        ArgumentNullException.ThrowIfNull(places);

        if (places.Count == 0)
        {
            return new MapFrame(new LatLon(20, 0), 1);
        }

        var centre = Centre(places);
        if (places.Count == 1 || width <= 0 || height <= 0)
        {
            return new MapFrame(centre, Math.Clamp(single, MinZoom, MaxZoom));
        }

        // Measured at zoom 0 and scaled: doubling the zoom doubles the pixels, so the zoom that
        // fits is however many doublings the span will take.
        var room = Math.Clamp(1 - (2 * Math.Clamp(padding, 0, 0.4)), 0.2, 1);
        var (left, top, right, bottom) = Bounds(places);

        var span = Math.Max(1e-9, right - left);
        var tall = Math.Max(1e-9, bottom - top);
        var zoom = Math.Min(
            Math.Log2(width * room / span),
            Math.Log2(height * room / tall));

        return new MapFrame(centre, Math.Clamp(Math.Floor(zoom * 2) / 2, MinZoom, MaxZoom));
    }

    /// <summary>The middle of a set of places, in projected space so that it looks middling.</summary>
    public static LatLon Centre(IReadOnlyList<LatLon> places)
    {
        ArgumentNullException.ThrowIfNull(places);

        if (places.Count == 0)
        {
            return new LatLon(0, 0);
        }

        var (left, top, right, bottom) = Bounds(places);
        var middle = ToPlace((left + right) / 2, (top + bottom) / 2, 0);

        // The box may run past the date line to hold a set that straddles it.
        return middle with { Longitude = Wrap(middle.Longitude) };
    }

    /// <summary>
    /// The box holding every place, in world pixels at zoom 0.
    /// </summary>
    /// <remarks>
    /// Longitude wraps, so the smaller of the two ways round is taken: Tokyo and Los Angeles
    /// belong together across the date line, not stretched the long way over Europe.
    /// </remarks>
    private static (double Left, double Top, double Right, double Bottom) Bounds(IReadOnlyList<LatLon> places)
    {
        var ys = places.Select(p => ToWorld(p, 0).Y).ToList();
        var xs = places.Select(p => ToWorld(p, 0).X).OrderBy(x => x).ToList();

        // The widest gap between neighbours, going round, is the part of the world to leave out.
        var size = WorldSize(0);
        var left = xs[0];
        var right = xs[^1];
        var widest = size - (right - left);

        for (var i = 1; i < xs.Count; i++)
        {
            var gap = xs[i] - xs[i - 1];
            if (gap > widest)
            {
                widest = gap;
                left = xs[i];
                right = xs[i - 1] + size;
            }
        }

        return (left, ys.Min(), right, ys.Max());
    }
}
