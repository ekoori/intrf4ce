namespace Intrf4ce.Core.Layout;

/// <summary>What the view knows about one callout before layout.</summary>
/// <param name="Anchor">Where the leader ends, in element coordinates.</param>
/// <param name="Area">For box callouts: the outlined area. The leader stops at its edge.</param>
/// <param name="LabelSize">The measured label, width and height.</param>
/// <param name="Side">left, right or null for automatic.</param>
public sealed record CalloutInput(string Id, Pt Anchor, Box? Area, (double Width, double Height) LabelSize, string? Side);

/// <summary>Where one callout ended up.</summary>
/// <param name="Label">The label's box.</param>
/// <param name="Leader">The leader polyline, from the label to the anchor.</param>
public sealed record CalloutPlacement(string Id, string Side, Box Label, IReadOnlyList<Pt> Leader);

public sealed record CalloutLayoutResult(IReadOnlyList<CalloutPlacement> Placements, bool Overflowed);

/// <summary>
/// Places callout labels in gutters beside an image, the way an anatomy atlas does.
/// </summary>
/// <remarks>
/// Boundary labelling rather than labels dropped next to their anchors: labels beside the
/// picture never cover what they point at, line up so they read as a list, and stay legible
/// when the picture is busy. The algorithm per side:
/// <list type="number">
/// <item>sort by anchor height and stack the labels as close to their anchors as the gap allows;</item>
/// <item>assign labels to those slots in order;</item>
/// <item>while two leaders cross, swap their slots.</item>
/// </list>
/// Step 3 terminates: every leader runs from the same column to its anchor, and uncrossing two
/// such segments strictly shortens their total length, so no assignment can recur.
/// </remarks>
public static class CalloutLayout
{
    /// <summary>Space between the label column and the image.</summary>
    public const double Margin = 28;

    /// <summary>Minimum space between stacked labels.</summary>
    public const double LabelGap = 10;

    /// <param name="image">Where the image is drawn, in element coordinates.</param>
    /// <param name="bounds">The whole element; labels must stay inside it.</param>
    public static CalloutLayoutResult Arrange(Box image, Box bounds, IReadOnlyList<CalloutInput> callouts)
    {
        ArgumentNullException.ThrowIfNull(callouts);

        var left = new List<CalloutInput>();
        var right = new List<CalloutInput>();

        foreach (var callout in callouts)
        {
            var side = callout.Side switch
            {
                "left" => "left",
                "right" => "right",
                _ => callout.Anchor.X < image.Center.X ? "left" : "right",
            };

            (side == "left" ? left : right).Add(callout);
        }

        Balance(left, right, image);

        var placements = new List<CalloutPlacement>();
        var overflow = false;
        overflow |= PlaceSide(left, isLeft: true, image, bounds, placements);
        overflow |= PlaceSide(right, isLeft: false, image, bounds, placements);

        // Stable output order: the order the agent listed them.
        var order = callouts.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        placements.Sort((a, b) => order.GetValueOrDefault(a.Id).CompareTo(order.GetValueOrDefault(b.Id)));

        return new CalloutLayoutResult(placements, overflow);
    }

    /// <summary>How much room each gutter needs for these labels.</summary>
    public static (double Left, double Right) GutterWidths(Box bounds, IReadOnlyList<CalloutInput> callouts)
    {
        ArgumentNullException.ThrowIfNull(callouts);

        var center = bounds.Center.X;
        double left = 0, right = 0;

        foreach (var callout in callouts)
        {
            var need = callout.LabelSize.Width + Margin + 8;
            var goesLeft = callout.Side == "left" || (callout.Side != "right" && callout.Anchor.X < center);
            if (goesLeft)
            {
                left = Math.Max(left, need);
            }
            else
            {
                right = Math.Max(right, need);
            }
        }

        return (left, right);
    }

    /// <summary>Moves automatic callouts to the emptier side when one side is badly overloaded.</summary>
    private static void Balance(List<CalloutInput> left, List<CalloutInput> right, Box image)
    {
        while (Math.Abs(left.Count - right.Count) > 2)
        {
            var (from, to) = left.Count > right.Count ? (left, right) : (right, left);

            // The automatic callout nearest the middle is the least committed to its side.
            var candidate = from
                .Where(c => c.Side is null or "auto")
                .OrderBy(c => Math.Abs(c.Anchor.X - image.Center.X))
                .FirstOrDefault();

            if (candidate is null)
            {
                return;
            }

            from.Remove(candidate);
            to.Add(candidate);
        }
    }

    private static bool PlaceSide(
        List<CalloutInput> side, bool isLeft, Box image, Box bounds, List<CalloutPlacement> placements)
    {
        if (side.Count == 0)
        {
            return false;
        }

        var sorted = side.OrderBy(c => Target(c).Y).ToList();
        var heights = sorted.Select(c => c.LabelSize.Height).ToArray();
        var desired = sorted.Select(c => Target(c).Y).ToArray();
        var (centers, overflowed) = Slots(desired, heights, bounds.Y, bounds.Bottom);

        // The column labels align to, and the column of elbows where every leader's diagonal
        // starts. Crossings are judged from the elbows, because that is the segment drawn.
        var column = isLeft ? image.X - Margin : image.Right + Margin;
        var elbowX = isLeft ? column + (Margin * 0.45) : column - (Margin * 0.45);
        var assignment = Enumerable.Range(0, sorted.Count).ToArray();

        Uncross(sorted, assignment, centers, elbowX);

        for (var slot = 0; slot < sorted.Count; slot++)
        {
            var callout = sorted[assignment[slot]];
            var (width, height) = callout.LabelSize;
            var y = centers[slot] - (height / 2);
            var x = isLeft ? column - width : column;

            // Never past the element's edge, even if that means sitting a little over the image.
            x = Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.Right - width));

            var label = new Box(x, y, width, height);
            var port = new Pt(column, centers[slot]);
            var elbow = new Pt(elbowX, centers[slot]);
            var end = EndPoint(callout, elbow);

            placements.Add(new CalloutPlacement(callout.Id, isLeft ? "left" : "right", label, [port, elbow, end]));
        }

        return overflowed;
    }

    /// <summary>
    /// Label centres as near their anchors as a minimum gap allows, inside the vertical range.
    /// </summary>
    internal static (double[] Centers, bool Overflowed) Slots(double[] desired, double[] heights, double top, double bottom)
    {
        var n = desired.Length;
        var centers = new double[n];

        // Top down: push each label below the one above it.
        for (var i = 0; i < n; i++)
        {
            var minimum = i == 0 ? top + (heights[0] / 2) : centers[i - 1] + (heights[i - 1] / 2) + LabelGap + (heights[i] / 2);
            centers[i] = Math.Max(desired[i], minimum);
        }

        // Bottom up: pull back inside the bottom edge.
        for (var i = n - 1; i >= 0; i--)
        {
            var maximum = i == n - 1 ? bottom - (heights[i] / 2) : centers[i + 1] - (heights[i + 1] / 2) - LabelGap - (heights[i] / 2);
            centers[i] = Math.Min(centers[i], maximum);
        }

        var overflowed = n > 0 && centers[0] - (heights[0] / 2) < top - 1e-6;
        if (overflowed)
        {
            // Too many to fit: pack from the top and let them run over; the view shrinks the font.
            for (var i = 0; i < n; i++)
            {
                centers[i] = i == 0 ? top + (heights[0] / 2) : centers[i - 1] + (heights[i - 1] / 2) + LabelGap + (heights[i] / 2);
            }
        }

        return (centers, overflowed);
    }

    private static void Uncross(List<CalloutInput> sorted, int[] assignment, double[] centers, double elbowX)
    {
        var limit = sorted.Count * sorted.Count * 4;
        bool swapped;

        do
        {
            swapped = false;
            for (var a = 0; a < sorted.Count && limit > 0; a++)
            {
                for (var b = a + 1; b < sorted.Count && limit > 0; b++)
                {
                    var pa = new Pt(elbowX, centers[a]);
                    var pb = new Pt(elbowX, centers[b]);
                    var ta = EndPoint(sorted[assignment[a]], pa);
                    var tb = EndPoint(sorted[assignment[b]], pb);

                    if (Segments.Cross(pa, ta, pb, tb))
                    {
                        (assignment[a], assignment[b]) = (assignment[b], assignment[a]);
                        swapped = true;
                        limit--;
                    }
                }
            }
        }
        while (swapped && limit > 0);
    }

    private static Pt Target(CalloutInput callout) => callout.Area?.Center ?? callout.Anchor;

    /// <summary>For an area, where the leader from <paramref name="from"/> meets its edge.</summary>
    private static Pt EndPoint(CalloutInput callout, Pt from)
    {
        if (callout.Area is not { } area)
        {
            return callout.Anchor;
        }

        var center = area.Center;
        var dx = from.X - center.X;
        var dy = from.Y - center.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
        {
            return center;
        }

        var tx = Math.Abs(dx) < 1e-9 ? double.MaxValue : area.Width / 2 / Math.Abs(dx);
        var ty = Math.Abs(dy) < 1e-9 ? double.MaxValue : area.Height / 2 / Math.Abs(dy);
        var t = Math.Min(1, Math.Min(tx, ty));
        return new Pt(center.X + (dx * t), center.Y + (dy * t));
    }
}
