using Avalonia;

namespace Intrf4ce.App.Stage;

/// <summary>A view of the world: the point at the centre of the screen and how much world fits across it.</summary>
public readonly record struct CameraView(Point Center, double Width)
{
    /// <summary>The world-to-screen transform for a viewport of <paramref name="viewport"/>.</summary>
    public Matrix ToMatrix(Size viewport)
    {
        var scale = viewport.Width / Width;
        return Matrix.CreateTranslation(-Center.X, -Center.Y)
            * Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(viewport.Width / 2, viewport.Height / 2);
    }

    /// <summary>
    /// The view that shows <paramref name="rect"/> whole inside the part of the viewport not
    /// covered by <paramref name="insets"/> (the header and chat bar), with a margin.
    /// </summary>
    public static CameraView Fit(Rect rect, Size viewport, Thickness insets = default, double margin = 0.04)
    {
        var usable = new Size(
            Math.Max(1, viewport.Width - insets.Left - insets.Right),
            Math.Max(1, viewport.Height - insets.Top - insets.Bottom));

        // World units per pixel needed to fit the rectangle into the usable area.
        var perPixel = Math.Max(rect.Width / usable.Width, rect.Height / usable.Height) * (1 + (2 * margin));
        var width = perPixel * viewport.Width;

        // The usable area's centre is off the screen's centre by half the inset difference.
        var shift = new Vector((insets.Left - insets.Right) / 2, (insets.Top - insets.Bottom) / 2) * perPixel;
        return new CameraView(rect.Center - shift, width);
    }
}

/// <summary>
/// The smooth zoom-and-pan path of van Wijk and Nuij (2003).
/// </summary>
/// <remarks>
/// Between two slides the camera does not slide sideways at a fixed zoom, which reads as the
/// world scrolling past; it pulls back, travels, and settles in, the way the zoomable
/// interfaces of the 90s (Pad++) moved. The path minimises perceived motion, so long jumps
/// pull back further and short ones barely at all. The same algorithm as d3's
/// <c>interpolateZoom</c>.
/// </remarks>
public sealed class CameraPath
{
    private const double Rho = 1.42;
    private const double Epsilon = 1e-6;

    private readonly CameraView _from;
    private readonly CameraView _to;
    private readonly double _r0;
    private readonly double _distance;
    private readonly bool _zoomOnly;

    public CameraPath(CameraView from, CameraView to)
    {
        _from = from;
        _to = to;

        var delta = to.Center - from.Center;
        _distance = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
        double w0 = from.Width, w1 = to.Width;

        if (_distance < Epsilon)
        {
            _zoomOnly = true;
            Length = Math.Log(w1 / w0) / Rho;
            return;
        }

        const double Rho2 = Rho * Rho;
        const double Rho4 = Rho2 * Rho2;
        var d2 = _distance * _distance;
        var b0 = ((w1 * w1) - (w0 * w0) + (Rho4 * d2)) / (2 * w0 * Rho2 * _distance);
        var b1 = ((w1 * w1) - (w0 * w0) - (Rho4 * d2)) / (2 * w1 * Rho2 * _distance);
        _r0 = Math.Log(Math.Sqrt((b0 * b0) + 1) - b0);
        var r1 = Math.Log(Math.Sqrt((b1 * b1) + 1) - b1);
        Length = (r1 - _r0) / Rho;
    }

    /// <summary>The path's length; d3 turns it into milliseconds as <c>|S| · 1000 · ρ / √2</c>.</summary>
    public double Length { get; }

    /// <summary>How long the flight takes at <paramref name="speed"/> (1 = d3's default pace).</summary>
    public TimeSpan Duration(double speed, TimeSpan min, TimeSpan max)
    {
        var ms = Math.Abs(Length) * 1000 * Rho / Math.Sqrt(2) * 0.55 / Math.Max(0.05, speed);
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, min.TotalMilliseconds, max.TotalMilliseconds));
    }

    /// <summary>The view a fraction <paramref name="t"/> (0..1) of the way along.</summary>
    public CameraView At(double t)
    {
        if (t <= 0)
        {
            return _from;
        }

        if (t >= 1)
        {
            return _to;
        }

        var s = t * Length;
        var w0 = _from.Width;

        if (_zoomOnly)
        {
            return new CameraView(_from.Center, w0 * Math.Exp(Rho * s));
        }

        var coshR0 = Math.Cosh(_r0);
        var fraction = w0 / (Rho * Rho * _distance) * ((coshR0 * Math.Tanh((Rho * s) + _r0)) - Math.Sinh(_r0));
        var center = _from.Center + ((_to.Center - _from.Center) * fraction);
        return new CameraView(center, w0 * coshR0 / Math.Cosh((Rho * s) + _r0));
    }
}
