using SkiaSharp;
using Svg.Skia;

namespace Intrf4ce.App.Render;

/// <summary>
/// A rendered SVG held as a Skia picture: vector, so the stage camera can zoom into a
/// diagram without it going soft.
/// </summary>
public sealed class SvgPicture : IDisposable
{
    private readonly SKSvg _svg;

    private SvgPicture(SKSvg svg, SKPicture picture, double width, double height)
    {
        _svg = svg;
        Picture = picture;
        Width = width;
        Height = height;
    }

    public SKPicture Picture { get; }

    /// <summary>The natural size the renderer reported, which is what part boxes are relative to.</summary>
    public double Width { get; }

    public double Height { get; }

    /// <summary>Parses <paramref name="svg"/>; null if Svg.Skia cannot make a picture of it.</summary>
    public static SvgPicture? Load(string svg, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(svg);

        var document = new SKSvg();
        var picture = document.FromSvg(svg);
        if (picture is null)
        {
            document.Dispose();
            return null;
        }

        if (width <= 0 || height <= 0)
        {
            width = picture.CullRect.Width;
            height = picture.CullRect.Height;
        }

        return new SvgPicture(document, picture, width, height);
    }

    /// <summary>Rasterises to PNG at <paramref name="scale"/>, for snapshots and self-tests.</summary>
    public byte[] ToPng(float scale, SKColor background)
    {
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Ceiling(Width * scale)),
            Math.Max(1, (int)Math.Ceiling(Height * scale)));

        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(background);
        canvas.Scale(scale);
        canvas.DrawPicture(Picture);
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose() => _svg.Dispose();
}
