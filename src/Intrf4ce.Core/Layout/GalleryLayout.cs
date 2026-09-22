namespace Intrf4ce.Core.Layout;

/// <summary>Where one photo goes: its box before rotation, and the rotation in degrees.</summary>
public readonly record struct PhotoPlacement(Box Box, double Rotation)
{
    /// <summary>The axis-aligned box the rotated photo covers, for hit testing and framing.</summary>
    public Box Covered
    {
        get
        {
            var radians = Math.Abs(Rotation) * Math.PI / 180;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            var width = (Box.Width * cos) + (Box.Height * sin);
            var height = (Box.Width * sin) + (Box.Height * cos);
            var center = Box.Center;
            return new Box(center.X - (width / 2), center.Y - (height / 2), width, height);
        }
    }
}

/// <summary>
/// Three ways to lay out a set of photos in a box.
/// </summary>
/// <remarks>
/// All take the photos' aspect ratios (width / height) in display order and return one
/// placement each. Scatter is seeded, so the same gallery falls the same way every time it
/// is shown; nothing reshuffles when the slide is revisited or the window resized.
/// </remarks>
public static class GalleryLayout
{
    /// <summary>
    /// An organic pile: a loose grid, each print nudged and turned a little, overlapping at the
    /// edges like prints spread on a table.
    /// </summary>
    public static IReadOnlyList<PhotoPlacement> Scatter(Box area, IReadOnlyList<double> aspects, int seed)
    {
        ArgumentNullException.ThrowIfNull(aspects);

        var n = aspects.Count;
        if (n == 0 || area.IsEmpty)
        {
            return [];
        }

        var random = new Random(seed);
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * area.Width / area.Height * 0.85)));
        var rows = (int)Math.Ceiling(n / (double)columns);
        var cellWidth = area.Width / columns;
        var cellHeight = area.Height / rows;

        // One photo alone gets most of the space; many share it and overlap a little.
        var grow = n == 1 ? 0.82 : n <= 4 ? 1.05 : 1.18;

        var placements = new List<PhotoPlacement>(n);
        for (var i = 0; i < n; i++)
        {
            var row = i / columns;
            var column = i % columns;

            // Centre a short last row rather than leaving it hanging on the left.
            var inRow = row == rows - 1 ? n - (row * columns) : columns;
            var shift = (columns - inRow) * cellWidth / 2;

            var cell = new Box(area.X + shift + (column * cellWidth), area.Y + (row * cellHeight), cellWidth, cellHeight);
            var size = new Box(0, 0, cell.Width * grow, cell.Height * grow).Fit(Math.Max(0.2, aspects[i]));

            var rotation = (random.NextDouble() - 0.5) * 2 * (n == 1 ? 2.5 : 8);
            var jitterX = (random.NextDouble() - 0.5) * cell.Width * 0.22;
            var jitterY = (random.NextDouble() - 0.5) * cell.Height * 0.22;
            var center = new Pt(cell.Center.X + jitterX, cell.Center.Y + jitterY);

            var placement = new PhotoPlacement(new Box(center.X - (size.Width / 2), center.Y - (size.Height / 2), size.Width, size.Height), rotation);
            placements.Add(Inside(placement, area));
        }

        return placements;
    }

    /// <summary>
    /// Justified rows: every photo at its own aspect, rows scaled so each fills the width.
    /// </summary>
    /// <remarks>
    /// Each row ends where its height comes closest to a target that would fill the area, so
    /// rows come out about the same height. A last row too short to fill the width keeps the
    /// height of the rows above it rather than growing into a banner.
    /// </remarks>
    public static IReadOnlyList<PhotoPlacement> Justified(Box area, IReadOnlyList<double> aspects, double gap = 12)
    {
        ArgumentNullException.ThrowIfNull(aspects);

        var n = aspects.Count;
        if (n == 0 || area.IsEmpty)
        {
            return [];
        }

        var clamped = aspects.Select(a => Math.Clamp(a, 0.25, 4)).ToArray();

        // The row height that would fill the area if every row came out full.
        var target = Math.Sqrt(area.Width * area.Height / clamped.Sum()) * 0.92;
        var rows = new List<(int Start, int Count, double Height)>();
        var start = 0;

        while (start < n)
        {
            // Add photos while the row, stretched to the full width, is still taller than the
            // target; then keep whichever of the last two counts lands closer to it.
            var sum = 0.0;
            var count = 0;
            var height = double.PositiveInfinity;
            while (start + count < n)
            {
                var next = (area.Width - (gap * count)) / (sum + clamped[start + count]);
                if (count > 0 && height > target && next < target && target - next > height - target)
                {
                    break;
                }

                sum += clamped[start + count];
                count++;
                height = next;
                if (height <= target)
                {
                    break;
                }
            }

            var last = start + count == n;
            if (last && height > target)
            {
                // Too few photos left to fill a row: as tall as the rows above, or the target.
                height = rows.Count > 0 ? Math.Min(height, rows.Average(r => r.Height)) : target;
            }

            rows.Add((start, count, height));
            start += count;
        }

        // Too tall for the area: shrink everything, then centre.
        var total = rows.Sum(r => r.Height) + (gap * (rows.Count - 1));
        var scale = total > area.Height ? area.Height / total : 1;
        var offsetY = area.Y + ((area.Height - (total * scale)) / 2);

        var placements = new PhotoPlacement[n];
        var y = offsetY;
        foreach (var (rowStart, count, rowHeight) in rows)
        {
            var h = rowHeight * scale;
            var width = Enumerable.Range(rowStart, count).Sum(i => clamped[i] * h) + (gap * scale * (count - 1));
            var x = area.X + ((area.Width - width) / 2);

            for (var i = rowStart; i < rowStart + count; i++)
            {
                var w = clamped[i] * h;
                placements[i] = new PhotoPlacement(new Box(x, y, w, h), 0);
                x += w + (gap * scale);
            }

            y += h + (gap * scale);
        }

        return placements;
    }

    /// <summary>
    /// Along a time axis through the middle, alternating above and below it, in the order given
    /// (the caller sorts by date).
    /// </summary>
    /// <returns>The placements and the axis's y.</returns>
    public static (IReadOnlyList<PhotoPlacement> Placements, double AxisY) Timeline(Box area, IReadOnlyList<double> aspects)
    {
        ArgumentNullException.ThrowIfNull(aspects);

        var n = aspects.Count;
        var axis = area.Y + (area.Height / 2);
        if (n == 0 || area.IsEmpty)
        {
            return ([], axis);
        }

        // A photo may be 1.9 spacings wide (its neighbours on the same side are two away), so the
        // first and last centres sit 0.95 of a spacing in from the edges.
        var spacing = area.Width / (n + 0.9);
        var band = (area.Height / 2) - 26;
        var placements = new List<PhotoPlacement>(n);

        for (var i = 0; i < n; i++)
        {
            var above = i % 2 == 0;
            var center = area.X + ((i + 0.95) * spacing);

            // Neighbours on the same side are two spacings apart, so a photo may be that wide.
            var height = Math.Min(band * 0.92, (spacing * 1.9) / Math.Max(0.3, aspects[i]));
            var width = height * aspects[i];
            var y = above ? axis - 22 - height : axis + 22;

            placements.Add(Inside(new PhotoPlacement(new Box(center - (width / 2), y, width, height), 0), area));
        }

        return (placements, axis);
    }

    /// <summary>Shrinks a placement if the rotated photo is bigger than <paramref name="area"/>, then moves it inside.</summary>
    private static PhotoPlacement Inside(PhotoPlacement placement, Box area)
    {
        var covered = placement.Covered;
        var shrink = Math.Min(1, Math.Min(area.Width / covered.Width, area.Height / covered.Height));
        if (shrink < 1)
        {
            var center = placement.Box.Center;
            var width = placement.Box.Width * shrink;
            var height = placement.Box.Height * shrink;
            placement = placement with { Box = new Box(center.X - (width / 2), center.Y - (height / 2), width, height) };
            covered = placement.Covered;
        }

        var dx = covered.X < area.X ? area.X - covered.X : covered.Right > area.Right ? area.Right - covered.Right : 0;
        var dy = covered.Y < area.Y ? area.Y - covered.Y : covered.Bottom > area.Bottom ? area.Bottom - covered.Bottom : 0;
        var box = placement.Box;
        return placement with { Box = new Box(box.X + dx, box.Y + dy, box.Width, box.Height) };
    }
}
