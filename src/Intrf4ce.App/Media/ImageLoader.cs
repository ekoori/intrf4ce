using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Intrf4ce.App.Render;
using Intrf4ce.Core.Decks;
using Intrf4ce.Media.Images;
using SkiaSharp;

namespace Intrf4ce.App.Media;

/// <summary>An image ready to draw: a bitmap, or for SVG sources a vector picture.</summary>
public sealed record LoadedImage(Bitmap? Bitmap, SvgPicture? Vector, double Width, double Height, string Path);

/// <summary>
/// Resolves an image source against the open deck and decodes it for the stage.
/// </summary>
/// <remarks>
/// Downloads are recorded in the deck through <see cref="DeckService.AddAsset"/>, so the
/// next open finds the file locally. Decoded images are cached by file and size: the same
/// photo on the stage and in a snapshot, or the same diagram on two slides, is decoded once.
/// SVG stays vector, because the camera zooms into images and a rasterised diagram goes soft.
/// </remarks>
public sealed class ImageLoader(AssetFetcher fetcher, DeckService decks)
{
    private readonly ConcurrentDictionary<(string Path, int Side), Task<LoadedImage>> _cache = new();

    public async Task<LoadedImage> LoadAsync(string source, int maxSide, CancellationToken cancellationToken)
    {
        var deck = decks.Current ?? throw new InvalidOperationException("no deck is open");
        var folder = decks.Store.FolderOf(deck.Id);
        deck.Assets.TryGetValue(source, out var recorded);

        var (path, relative) = await fetcher.ResolveAsync(source, recorded, folder, cancellationToken).ConfigureAwait(false);
        if (relative is not null && relative != recorded)
        {
            decks.AddAsset(source, relative);
        }

        var load = _cache.GetOrAdd((path, maxSide), key => Task.Run(() => Decode(key.Path, key.Side)));
        try
        {
            return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A failed decode is not cached; the file may be fixed and retried.
            _cache.TryRemove((path, maxSide), out _);
            throw;
        }
    }

    private static LoadedImage Decode(string path, int maxSide)
    {
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var picture = SvgPicture.Load(File.ReadAllText(path), 0, 0)
                ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not an SVG that can be drawn");
            return new LoadedImage(null, picture, picture.Width, picture.Height, path);
        }

        using var bitmap = ImageDecoder.Decode(path, maxSide)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not an image this platform can decode");

        return new LoadedImage(ToAvalonia(bitmap), null, bitmap.Width, bitmap.Height, path);
    }

    public static Bitmap ToAvalonia(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        return new Bitmap(
            PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            bitmap.GetPixels(),
            new PixelSize(bitmap.Width, bitmap.Height),
            new Vector(96, 96),
            bitmap.RowBytes);
    }
}
