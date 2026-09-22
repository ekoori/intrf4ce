namespace Intrf4ce.Core.Layout;

/// <summary>A point in whatever space the caller is working in.</summary>
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);

    public double DistanceTo(Pt other) => Math.Sqrt(((X - other.X) * (X - other.X)) + ((Y - other.Y) * (Y - other.Y)));
}

/// <summary>An axis-aligned rectangle. Core's own, so layout never needs Avalonia.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public Pt Center => new(X + (Width / 2), Y + (Height / 2));

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public Box Inflate(double by) => new(X - by, Y - by, Width + (2 * by), Height + (2 * by));

    public Box Deflate(double left, double top, double right, double bottom) =>
        new(X + left, Y + top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));

    public bool Intersects(Box other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public bool Contains(Pt p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public bool Contains(Box other, double tolerance = 1e-6) =>
        other.X >= X - tolerance && other.Y >= Y - tolerance
        && other.Right <= Right + tolerance && other.Bottom <= Bottom + tolerance;

    /// <summary>A sub-box given as fractions of this one: <c>[x, y, w, h]</c> in 0..1.</summary>
    public Box Fraction(double fx, double fy, double fw, double fh) =>
        new(X + (fx * Width), Y + (fy * Height), fw * Width, fh * Height);

    /// <summary>The largest box of <paramref name="aspect"/> (width / height) that fits, centred.</summary>
    public Box Fit(double aspect)
    {
        if (aspect <= 0 || IsEmpty)
        {
            return this;
        }

        var width = Width;
        var height = width / aspect;
        if (height > Height)
        {
            height = Height;
            width = height * aspect;
        }

        return new Box(X + ((Width - width) / 2), Y + ((Height - height) / 2), width, height);
    }

    /// <summary>Scale and offset that map this box onto <paramref name="target"/>, keeping aspect ("contain").</summary>
    public (double Scale, double OffsetX, double OffsetY) ContainIn(Box target)
    {
        var scale = Math.Min(target.Width / Math.Max(1e-9, Width), target.Height / Math.Max(1e-9, Height));
        var offsetX = target.X + ((target.Width - (Width * scale)) / 2) - (X * scale);
        var offsetY = target.Y + ((target.Height - (Height * scale)) / 2) - (Y * scale);
        return (scale, offsetX, offsetY);
    }
}

public static class Segments
{
    /// <summary>Whether two segments properly cross. Touching at an endpoint does not count.</summary>
    public static bool Cross(Pt a1, Pt a2, Pt b1, Pt b2)
    {
        var d1 = Orientation(b1, b2, a1);
        var d2 = Orientation(b1, b2, a2);
        var d3 = Orientation(a1, a2, b1);
        var d4 = Orientation(a1, a2, b2);

        const double Epsilon = 1e-9;
        return ((d1 > Epsilon && d2 < -Epsilon) || (d1 < -Epsilon && d2 > Epsilon))
            && ((d3 > Epsilon && d4 < -Epsilon) || (d3 < -Epsilon && d4 > Epsilon));
    }

    private static double Orientation(Pt p, Pt q, Pt r) =>
        ((q.X - p.X) * (r.Y - p.Y)) - ((q.Y - p.Y) * (r.X - p.X));
}
