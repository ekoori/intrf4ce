using System.Globalization;

namespace Intrf4ce.Media.Images;

/// <summary>One square of a map: which one, and how far in.</summary>
public readonly record struct Tile(int Zoom, int X, int Y)
{
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Zoom}/{X}/{Y}");
}

/// <summary>
/// Map tiles from OpenStreetMap, kept on this machine once fetched.
/// </summary>
/// <remarks>
/// OpenStreetMap gives its tiles away on conditions, and intrf4ce keeps them: it identifies
/// itself, it never asks for a tile it already has, it asks for only a few at a time, and it
/// stops at the zoom the servers offer. A presentation about a journey is a few dozen tiles,
/// not a bulk download, and the cache means a session reopened next week asks for none at all.
/// <para>
/// The other condition is credit, which is not this class's to give: the map draws
/// "© OpenStreetMap contributors" in its corner, and that notice is not optional.
/// </para>
/// </remarks>
public sealed class TileSource(HttpClient http, string cacheFolder)
{
    /// <summary>What every map made from these tiles must show.</summary>
    public const string Attribution = "© OpenStreetMap contributors";

    /// <summary>Their servers ask for no more than a couple of connections at a time.</summary>
    private static readonly SemaphoreSlim Fetching = new(2);

    private readonly string _cache = Path.Combine(cacheFolder, "tiles");

    /// <summary>Where the tiles come from; <c>{z}</c>, <c>{x}</c> and <c>{y}</c> are filled in.</summary>
    public string Template { get; init; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>The PNG for a tile, from disk if it has been fetched before.</summary>
    public async Task<byte[]> GetAsync(Tile tile, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_cache, tile.Zoom.ToString(CultureInfo.InvariantCulture), tile.X.ToString(CultureInfo.InvariantCulture),
            tile.Y.ToString(CultureInfo.InvariantCulture) + ".png");

        if (File.Exists(path))
        {
            try
            {
                return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Half-written by an earlier run, or locked: fetch it again.
            }
        }

        await Fetching.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes;
        try
        {
            var url = Template
                .Replace("{z}", tile.Zoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", tile.X.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{y}", tile.Y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"the map tile server answered {(int)response.StatusCode} for {tile.Key}"
                    + (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "; it is asking for a slower pace" : ""));
            }

            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Fetching.Release();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            // A cache that cannot be written is a slower map, not a broken one.
        }

        return bytes;
    }

    /// <summary>
    /// The tiles covering a rectangle of world pixels, and where each goes.
    /// </summary>
    /// <param name="left">The world-pixel coordinate of the rectangle's left edge, at this zoom.</param>
    /// <returns>Each tile with the offset, in pixels from the rectangle's top-left, to draw it at.</returns>
    public static IReadOnlyList<(Tile Tile, double X, double Y)> Covering(
        int zoom, double left, double top, double width, double height, int tileSize = 256)
    {
        var count = 1 << zoom;
        var tiles = new List<(Tile, double, double)>();

        var firstX = (int)Math.Floor(left / tileSize);
        var firstY = (int)Math.Floor(top / tileSize);
        var lastX = (int)Math.Floor((left + width) / tileSize);
        var lastY = (int)Math.Floor((top + height) / tileSize);

        for (var y = firstY; y <= lastY; y++)
        {
            // Above the north pole or below the south there is no tile to ask for.
            if (y < 0 || y >= count)
            {
                continue;
            }

            for (var x = firstX; x <= lastX; x++)
            {
                // Longitude wraps, so the world repeats sideways for ever.
                var wrapped = ((x % count) + count) % count;
                tiles.Add((new Tile(zoom, wrapped, y), (x * tileSize) - left, (y * tileSize) - top));
            }
        }

        return tiles;
    }
}
