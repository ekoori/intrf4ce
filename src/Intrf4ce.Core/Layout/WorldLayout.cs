namespace Intrf4ce.Core.Layout;

/// <summary>
/// Where each slide sits in the world the camera looks at.
/// </summary>
/// <remarks>
/// Slides go in reading order, five to a row, which is what makes overview a view of the
/// whole deck growing. Slides of the standard size land exactly where they always have. A
/// canvas takes the room it needs: the columns of its row are as wide as its widest member and
/// the row as tall as its tallest, so a big canvas never overlaps the slides beside it.
/// </remarks>
public sealed class WorldLayout
{
    public const int Columns = 5;
    public const double GapX = 240;
    public const double GapY = 200;

    private readonly List<Box> _rects;

    public WorldLayout(IReadOnlyList<(double Width, double Height)> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        _rects = new List<Box>(sizes.Count);
        var y = 0.0;
        for (var row = 0; row * Columns < sizes.Count; row++)
        {
            var members = sizes.Skip(row * Columns).Take(Columns).ToList();
            var height = members.Max(s => s.Height);

            var x = 0.0;
            for (var column = 0; column < members.Count; column++)
            {
                // Columns keep their standard pitch unless a slide in this row is wider.
                var pitch = Math.Max(SlideLayout.Width, members[column].Width);
                _rects.Add(new Box(x, y, members[column].Width, members[column].Height));
                x += pitch + GapX;
            }

            y += Math.Max(SlideLayout.Height, height) + GapY;
        }
    }

    /// <summary>A deck of standard slides.</summary>
    public static WorldLayout Standard(int count) =>
        new([.. Enumerable.Repeat((SlideLayout.Width, SlideLayout.Height), Math.Max(0, count))]);

    public int Count => _rects.Count;

    /// <summary>Where slide <paramref name="index"/> is; past the end, where one would go.</summary>
    public Box Rect(int index)
    {
        if (index >= 0 && index < _rects.Count)
        {
            return _rects[index];
        }

        // Somewhere sensible for a slide not laid out yet: the standard grid.
        var column = Math.Max(0, index) % Columns;
        var row = Math.Max(0, index) / Columns;
        return new Box(column * (SlideLayout.Width + GapX), row * (SlideLayout.Height + GapY), SlideLayout.Width, SlideLayout.Height);
    }

    /// <summary>The box holding the first <paramref name="count"/> slides.</summary>
    public Box Bounds(int count)
    {
        if (count <= 0 || _rects.Count == 0)
        {
            return Rect(0);
        }

        var taken = _rects.Take(Math.Min(count, _rects.Count)).ToList();
        var right = taken.Max(r => r.X + r.Width);
        var bottom = taken.Max(r => r.Y + r.Height);
        return new Box(0, 0, right, bottom);
    }

    /// <summary>The slide under a point in the world, or -1.</summary>
    public int IndexAt(double x, double y) =>
        _rects.FindIndex(r => x >= r.X && x <= r.X + r.Width && y >= r.Y && y <= r.Y + r.Height);
}
