using System.Globalization;
using SkiaSharp;

namespace Intrf4ce.Media.Images;

/// <summary>
/// Draws the measuring grid the agent reads callout coordinates from.
/// </summary>
/// <remarks>
/// A model asked "where is the ACL in this picture" answers in vague words; the same model
/// shown lines every tenth of the width and height, labelled with their value, reads off
/// numbers to about a hundredth. The grid is drawn over the image rather than beside it so the
/// line nearest any feature is always in view, with a dark halo so it shows on light and dark
/// images alike. Labels repeat on all four edges for the same reason.
/// </remarks>
public static class CoordinateGrid
{
    public static SKBitmap Draw(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var bitmap = source.Copy();
        using var canvas = new SKCanvas(bitmap);

        float w = bitmap.Width;
        float h = bitmap.Height;
        var unit = Math.Max(1f, Math.Min(w, h) / 900f);

        using var halo = new SKPaint { Color = new SKColor(0, 0, 0, 150), StrokeWidth = 3 * unit, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var line = new SKPaint { Color = new SKColor(255, 255, 255, 170), StrokeWidth = 1.2f * unit, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var half = new SKPaint { Color = new SKColor(255, 196, 64, 220), StrokeWidth = 1.6f * unit, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var minor = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 70),
            StrokeWidth = 1 * unit,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash([4 * unit, 6 * unit], 0),
        };

        // Minor lines at the twentieths, for finer reading between the labelled ones.
        for (var i = 1; i < 20; i += 2)
        {
            var f = i / 20f;
            canvas.DrawLine(f * w, 0, f * w, h, minor);
            canvas.DrawLine(0, f * h, w, f * h, minor);
        }

        for (var i = 1; i < 10; i++)
        {
            var f = i / 10f;
            var paint = i == 5 ? half : line;
            canvas.DrawLine(f * w, 0, f * w, h, halo);
            canvas.DrawLine(0, f * h, w, f * h, halo);
            canvas.DrawLine(f * w, 0, f * w, h, paint);
            canvas.DrawLine(0, f * h, w, f * h, paint);
        }

        using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 13 * unit);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var back = new SKPaint { Color = new SKColor(0, 0, 0, 190), IsAntialias = true };

        for (var i = 1; i < 10; i++)
        {
            var f = i / 10f;
            var label = f.ToString("0.0", CultureInfo.InvariantCulture);

            Label(canvas, label, f * w, 10 * unit, font, text, back, unit);
            Label(canvas, label, f * w, h - (10 * unit), font, text, back, unit);
            Label(canvas, label, 16 * unit, f * h, font, text, back, unit);
            Label(canvas, label, w - (16 * unit), f * h, font, text, back, unit);
        }

        return bitmap;
    }

    private static void Label(SKCanvas canvas, string label, float cx, float cy, SKFont font, SKPaint text, SKPaint back, float unit)
    {
        var width = font.MeasureText(label);
        var box = new SKRect(cx - (width / 2) - (3 * unit), cy - (8 * unit), cx + (width / 2) + (3 * unit), cy + (8 * unit));
        canvas.DrawRoundRect(box, 3 * unit, 3 * unit, back);
        canvas.DrawText(label, cx - (width / 2), cy + (4.5f * unit), SKTextAlign.Left, font, text);
    }
}
