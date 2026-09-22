using SkiaSharp;

namespace Intrf4ce.Media.Images;

/// <summary>
/// Decodes images the right way up and no bigger than needed.
/// </summary>
/// <remarks>
/// Phones store most photos sideways and record the rotation in EXIF; a decoder that ignores
/// it shows a gallery of portraits lying on their side. Decoding goes through
/// <see cref="SKCodec"/> so JPEGs can be scaled down while decoding, which for a 48-megapixel
/// photo shown as a thumbnail is the difference between 200 MB and 2 MB.
/// </remarks>
public static class ImageDecoder
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".ico", ".wbmp", ".heic", ".heif", ".avif", ".svg",
    };

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>Decodes <paramref name="path"/> upright, with its longest side at most <paramref name="maxSide"/>.</summary>
    /// <returns>Null when the format cannot be decoded here (HEIC on most platforms).</returns>
    public static SKBitmap? Decode(string path, int maxSide)
    {
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return RasteriseSvg(File.ReadAllText(path), maxSide);
        }

        using var stream = File.OpenRead(path);
        return Decode(stream, maxSide);
    }

    /// <summary>Draws an SVG at a size whose longest side is <paramref name="maxSide"/>.</summary>
    public static SKBitmap? RasteriseSvg(string svg, int maxSide)
    {
        using var document = new Svg.Skia.SKSvg();
        var picture = document.FromSvg(svg);
        if (picture is null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
        {
            return null;
        }

        var bounds = picture.CullRect;
        var scale = maxSide / Math.Max(bounds.Width, bounds.Height);
        var bitmap = new SKBitmap(new SKImageInfo(
            Math.Max(1, (int)(bounds.Width * scale)),
            Math.Max(1, (int)(bounds.Height * scale)),
            SKColorType.Bgra8888,
            SKAlphaType.Premul));

        using var canvas = new SKCanvas(bitmap);

        // Diagrams are usually drawn for white paper; on a dark stage they need it back.
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        return bitmap;
    }

    public static SKBitmap? Decode(Stream stream, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var managed = new SKManagedStream(stream);
        using var codec = SKCodec.Create(managed);
        if (codec is null)
        {
            return null;
        }

        var origin = codec.EncodedOrigin;
        var info = codec.Info;

        // Let the codec do most of the shrinking (JPEG decodes at 1/2, 1/4, 1/8 for free).
        var longest = Math.Max(info.Width, info.Height);
        var scale = longest > maxSide ? maxSide / (float)longest : 1f;
        var scaled = codec.GetScaledDimensions(scale);
        var target = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

        var bitmap = new SKBitmap(target);
        var result = codec.GetPixels(target, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }

        // Codecs only scale in steps; finish the job with a proper filter.
        if (Math.Max(bitmap.Width, bitmap.Height) > maxSide)
        {
            var ratio = maxSide / (float)Math.Max(bitmap.Width, bitmap.Height);
            var resized = bitmap.Resize(
                new SKImageInfo(Math.Max(1, (int)(bitmap.Width * ratio)), Math.Max(1, (int)(bitmap.Height * ratio)), SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            bitmap.Dispose();
            bitmap = resized;
        }

        return Upright(bitmap, origin);
    }

    /// <summary>The image's displayed size (after EXIF rotation) without decoding pixels.</summary>
    public static (int Width, int Height)? Probe(string path)
    {
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            using var svg = RasteriseSvg(File.ReadAllText(path), 64);
            return svg is null ? null : (svg.Width, svg.Height);
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var managed = new SKManagedStream(stream);
            using var codec = SKCodec.Create(managed);
            if (codec is null)
            {
                return null;
            }

            var sideways = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            return sideways ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Applies an EXIF origin, returning a new bitmap (and disposing the old) when it moves pixels.</summary>
    internal static SKBitmap Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return bitmap;
        }

        float w = bitmap.Width;
        float h = bitmap.Height;
        var sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        // x' = scaleX·x + skewX·y + transX ; y' = skewY·x + scaleY·y + transY
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var upright = new SKBitmap(new SKImageInfo(
            sideways ? bitmap.Height : bitmap.Width,
            sideways ? bitmap.Width : bitmap.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));

        using (var canvas = new SKCanvas(upright))
        {
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        bitmap.Dispose();
        return upright;
    }

    public static byte[] EncodePng(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
