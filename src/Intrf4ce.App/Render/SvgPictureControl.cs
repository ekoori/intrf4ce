using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Intrf4ce.App.Render;

/// <summary>
/// Draws an <see cref="SvgPicture"/> scaled to fit its bounds, as vectors.
/// </summary>
/// <remarks>
/// Drawn straight onto Avalonia's Skia canvas rather than rasterised first, so a diagram the
/// camera zooms into stays sharp at any magnification. <see cref="PictureToControl"/> is
/// exposed so part boxes reported by the renderer can be mapped onto the control.
/// </remarks>
public sealed class SvgPictureControl : Control
{
    private SvgPicture? _picture;

    public SvgPicture? Picture
    {
        get => _picture;
        set
        {
            _picture = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Never scale up past this: a two-node flowchart should not fill a wall.</summary>
    public double MaxScale { get; set; } = 2.0;

    /// <summary>Scale and offset from picture coordinates to this control's.</summary>
    public (double Scale, double X, double Y) PictureToControl
    {
        get
        {
            if (_picture is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return (1, 0, 0);
            }

            var scale = Math.Min(MaxScale, Math.Min(Bounds.Width / _picture.Width, Bounds.Height / _picture.Height));
            return (scale, (Bounds.Width - (_picture.Width * scale)) / 2, (Bounds.Height - (_picture.Height * scale)) / 2);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_picture is null)
        {
            return default;
        }

        var scale = Math.Min(
            MaxScale,
            Math.Min(
                double.IsInfinity(availableSize.Width) ? MaxScale : availableSize.Width / _picture.Width,
                double.IsInfinity(availableSize.Height) ? MaxScale : availableSize.Height / _picture.Height));

        return new Size(_picture.Width * scale, _picture.Height * scale);
    }

    public override void Render(DrawingContext context)
    {
        if (_picture is null)
        {
            return;
        }

        var (scale, x, y) = PictureToControl;
        context.Custom(new PictureOperation(new Rect(Bounds.Size), _picture.Picture, (float)scale, (float)x, (float)y));
    }

    private sealed class PictureOperation(Rect bounds, SKPicture picture, float scale, float x, float y) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) =>
            other is PictureOperation o && ReferenceEquals(o.Picture, Picture) && o.Bounds == Bounds;

        private SKPicture Picture { get; } = picture;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var saved = canvas.Save();

            // Raw Skia drawing does not inherit Avalonia's opacity; without this a diagram
            // would stay solid while the slide around it fades.
            if (lease.CurrentOpacity < 0.999)
            {
                using var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * lease.CurrentOpacity)) };
                canvas.SaveLayer(paint);
            }

            canvas.Translate(x, y);
            canvas.Scale(scale);
            canvas.DrawPicture(Picture);
            canvas.RestoreToCount(saved);
        }

        public void Dispose()
        {
        }
    }
}
