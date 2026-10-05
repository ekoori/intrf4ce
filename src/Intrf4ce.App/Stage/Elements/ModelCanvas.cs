using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Shapes;
using SkiaSharp;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>How a model is turned and drawn this frame.</summary>
/// <param name="Orientation">From the model's own axes to the viewer's: x right, y up, z towards the viewer.</param>
/// <param name="Explode">0 to 1: how far the parts are moved apart from the middle.</param>
/// <param name="Highlighted">The part being talked about, drawn in the accent with the rest dimmed; null for none.</param>
internal readonly record struct ModelPose(Quaternion Orientation, float Explode, string? Highlighted);

/// <summary>
/// Draws a <see cref="Mesh"/> as a camera in front of it sees it: faces shaded by a light, wires
/// fading with distance, and labels in gutters beside it, as an image's callouts are.
/// </summary>
/// <remarks>
/// <para>
/// The arithmetic of turning it is done here, every frame, on the CPU: a few thousand corners
/// cost well under a millisecond. The drawing goes to Skia, on the GPU, in as few calls as it can —
/// every face in one triangle mesh, the wires in a handful of batches by colour and depth — so the
/// camera's zoom is the GPU's to apply and a model stays sharp however close it is looked at.
/// </para>
/// <para>
/// Faces are drawn far to near (the painter's way), which is right for anything that does not
/// pass through itself; both sides are lit, so a model from a file with holes in it still reads.
/// </para>
/// </remarks>
internal sealed class ModelCanvas : Control
{
    /// <summary>How far back the camera sits, in model radii; farther is flatter.</summary>
    private const float Distance = 3.6f;

    /// <summary>Where the light comes from: above, a little left, towards the viewer.</summary>
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.35f, 0.6f, 0.72f));

    private readonly Skin _skin;
    private Mesh? _mesh;
    private string _style = "wire";
    private bool _labels = true;
    private IReadOnlyDictionary<string, string> _sides = new Dictionary<string, string>();
    private (Color Colour, double Opacity)[] _colours = [];
    private ModelPose _pose = new(Quaternion.Identity, 0, null);
    private Frame? _frame;

    public ModelCanvas(Skin skin)
    {
        _skin = skin;
        ClipToBounds = false;
    }

    public Mesh? Mesh => _mesh;

    public ModelPose Pose
    {
        get => _pose;
        set
        {
            _pose = value;
            InvalidateVisual();
        }
    }

    public void Show(Mesh mesh, string style, bool labels, Quaternion home)
    {
        _mesh = mesh;
        _style = style;
        _labels = labels;
        _colours = [.. mesh.Parts.Select(p => ModelColors.Of(p.Color) is var (hex, opacity) ? (Color.Parse(hex), opacity) : default)];

        // Each label keeps to the side its part is on in the model's own view, so a turning
        // model's labels do not swap gutters every half turn.
        _sides = mesh.Parts.ToDictionary(
            p => p.Id,
            p => Vector3.Transform(p.Centroid - mesh.Center, home).X < 0 ? "left" : "right",
            StringComparer.Ordinal);
        InvalidateVisual();
    }

    /// <summary>The part drawn nearest the viewer under <paramref name="point"/> in the last frame; null for none.</summary>
    public string? PartAt(Point point)
    {
        if (_frame is not { } frame || _mesh is not { } mesh)
        {
            return null;
        }

        // The nearest face under the point, filled or not, or failing that a wire within a few
        // pixels: a part drawn only as wires is still something to click.
        var (best, nearest) = (-1, float.MaxValue);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = (mesh.Triangles[t * 3], mesh.Triangles[(t * 3) + 1], mesh.Triangles[(t * 3) + 2]);
            var depth = frame.Depth[a] + frame.Depth[b] + frame.Depth[c];
            if (depth < nearest && Inside(point, frame.Screen[a], frame.Screen[b], frame.Screen[c]))
            {
                (best, nearest) = (mesh.TrianglePart[t], depth);
            }
        }

        if (best >= 0)
        {
            return mesh.Parts[best].Id;
        }

        var close = 8.0;
        for (var e = 0; e < mesh.EdgeCount; e++)
        {
            var gap = DistanceToSegment(point, frame.Screen[mesh.Edges[e * 2]], frame.Screen[mesh.Edges[(e * 2) + 1]]);
            if (gap < close)
            {
                (best, close) = (mesh.EdgePart[e], gap);
            }
        }

        return best >= 0 ? mesh.Parts[best].Id : null;
    }

    /// <summary>Where a part would be drawn turned <paramref name="orientation"/>, in this control's coordinates.</summary>
    public Rect? PartBounds(string part, Quaternion orientation)
    {
        if (_mesh is not { } mesh || Bounds.Width <= 0)
        {
            return null;
        }

        var index = Index(part);
        if (index < 0)
        {
            return null;
        }

        var p = mesh.Parts[index];
        var area = ModelArea();
        var (minX, minY, maxX, maxY) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        for (var v = p.FirstVertex; v < p.FirstVertex + p.VertexCount; v++)
        {
            var (screen, _) = Project(View(mesh.Vertices[v], index, orientation, _pose.Explode), area);
            (minX, minY, maxX, maxY) = (Math.Min(minX, screen.X), Math.Min(minY, screen.Y), Math.Max(maxX, screen.X), Math.Max(maxY, screen.Y));
        }

        return minX > maxX ? null : new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>The middle of a part, in model space, from the model's middle: what facing it turns towards.</summary>
    public Vector3? Direction(string part) =>
        _mesh is { } mesh && Index(part) is var index and >= 0 ? mesh.Parts[index].Centroid - mesh.Center : null;

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_mesh is not { } mesh || mesh.Vertices.Length == 0 || Bounds.Width < 20 || Bounds.Height < 20)
        {
            return;
        }

        var frame = Compute(mesh);
        _frame = frame;
        context.Custom(new MeshOperation(new Rect(Bounds.Size), frame.Faces, frame.FaceColors, frame.Wires));
        DrawLabels(context, mesh, frame);
    }

    /// <summary>Where the model itself goes: the element less the gutters its labels need.</summary>
    private Rect ModelArea()
    {
        if (!_labels || _mesh is not { } mesh || !mesh.Parts.Any(p => p.Label is not null))
        {
            return new Rect(Bounds.Size);
        }

        var size = LabelSize;
        var inputs = mesh.Parts.Where(p => p.Label is not null)
            .Select(p => new CalloutInput(p.Id, default, null, (Measure(p.Label!, size).Width + 8, size * 1.4), _sides.GetValueOrDefault(p.Id)))
            .ToList();
        var (left, right) = CalloutLayout.GutterWidths(new Box(0, 0, Bounds.Width, Bounds.Height), inputs);
        return new Rect(left, 0, Math.Max(40, Bounds.Width - left - right), Bounds.Height);
    }

    private double LabelSize => Math.Clamp(Math.Min(Bounds.Width, Bounds.Height) / 26, 16, 26);

    private Frame Compute(Mesh mesh)
    {
        var area = ModelArea();
        var count = mesh.Vertices.Length;
        var screen = new Point[count];
        var view = new Vector3[count];
        var depth = new float[count];
        var (near, far) = (float.MaxValue, float.MinValue);

        for (var p = 0; p < mesh.Parts.Count; p++)
        {
            var part = mesh.Parts[p];
            for (var v = part.FirstVertex; v < part.FirstVertex + part.VertexCount; v++)
            {
                view[v] = View(mesh.Vertices[v], p, _pose.Orientation, _pose.Explode);
                (screen[v], depth[v]) = Project(view[v], area);
                (near, far) = (MathF.Min(near, depth[v]), MathF.Max(far, depth[v]));
            }
        }

        var span = MathF.Max(1e-4f, far - near);
        var highlighted = _pose.Highlighted is { } h ? Index(h) : -1;

        // Faces: which parts are filled, each triangle's shade, then far to near.
        var filled = mesh.Parts.Select(IsFilled).ToArray();
        var triangles = mesh.TriangleCount;
        var facing = new bool[triangles];
        var keys = new List<float>(triangles);
        var drawn = new List<int>(triangles);

        for (var t = 0; t < triangles; t++)
        {
            var (a, b, c) = (mesh.Triangles[t * 3], mesh.Triangles[(t * 3) + 1], mesh.Triangles[(t * 3) + 2]);
            facing[t] = Vector3.Cross(view[b] - view[a], view[c] - view[a]).Z > 0;
            if (filled[mesh.TrianglePart[t]])
            {
                keys.Add(-(depth[a] + depth[b] + depth[c]));
                drawn.Add(t);
            }
        }

        var order = drawn.ToArray();
        var sortKeys = keys.ToArray();
        Array.Sort(sortKeys, order);

        var faces = new SKPoint[order.Length * 3];
        var colors = new SKColor[order.Length * 3];
        for (var k = 0; k < order.Length; k++)
        {
            var t = order[k];
            var (a, b, c) = (mesh.Triangles[t * 3], mesh.Triangles[(t * 3) + 1], mesh.Triangles[(t * 3) + 2]);
            var normal = Vector3.Cross(view[b] - view[a], view[c] - view[a]);
            var lit = normal.LengthSquared() > 0 ? MathF.Abs(Vector3.Dot(Vector3.Normalize(normal), Light)) : 0.5f;
            var colour = FaceColour(mesh.TrianglePart[t], highlighted, 0.36f + (0.64f * lit));

            faces[k * 3] = new SKPoint((float)screen[a].X, (float)screen[a].Y);
            faces[(k * 3) + 1] = new SKPoint((float)screen[b].X, (float)screen[b].Y);
            faces[(k * 3) + 2] = new SKPoint((float)screen[c].X, (float)screen[c].Y);
            colors[k * 3] = colors[(k * 3) + 1] = colors[(k * 3) + 2] = colour;
        }

        // Wires: batched by part, by how near they are, and by whether they face the viewer.
        var batches = new Dictionary<(int Part, int Shade), List<SKPoint>>();
        for (var e = 0; e < mesh.EdgeCount; e++)
        {
            var part = mesh.EdgePart[e];
            var (a, b) = (mesh.Edges[e * 2], mesh.Edges[(e * 2) + 1]);
            var nearness = 1 - ((((depth[a] + depth[b]) / 2) - near) / span);
            var (f1, f2) = (mesh.EdgeFaces[e * 2], mesh.EdgeFaces[(e * 2) + 1]);
            var front = mesh.Parts[part].IsLine || (f1 < 0 && f2 < 0) || (f1 >= 0 && facing[f1]) || (f2 >= 0 && facing[f2]);

            // On a filled part, a wire behind it is hidden by it — mostly: a hint shows through.
            var shade = filled[part]
                ? front ? 7 : (_style == "both" ? 1 : -1)
                : 2 + (int)MathF.Round(nearness * 5);
            if (shade < 0)
            {
                continue;
            }

            if (!batches.TryGetValue((part, shade), out var points))
            {
                batches[(part, shade)] = points = [];
            }

            points.Add(new SKPoint((float)screen[a].X, (float)screen[a].Y));
            points.Add(new SKPoint((float)screen[b].X, (float)screen[b].Y));
        }

        var width = (float)Math.Clamp(Math.Min(area.Width, area.Height) / 320, 1.2, 3.2);
        var wires = batches.Select(pair =>
        {
            var (part, shade) = pair.Key;
            var isHighlighted = part == highlighted;
            var dimmed = highlighted >= 0 && !isHighlighted;
            var alpha = shade / 7f * (filled[part] && _style == "solid" ? 0.45f : 1f) * (dimmed ? 0.3f : 1f);
            var colour = isHighlighted ? _skin.Accent : WireColour(part, filled[part]);
            return new Wires([.. pair.Value], new SKColor(colour.R, colour.G, colour.B, (byte)(255 * Math.Clamp(alpha, 0.06f, 1f))), isHighlighted ? width * 2 : width);
        }).ToArray();

        return new Frame(screen, depth, order, faces, colors, wires, near, span);
    }

    private bool IsFilled(MeshPart part) =>
        !part.IsLine && (part.Fill ?? _style is "solid" or "both");

    private SKColor FaceColour(int part, int highlighted, float light)
    {
        var (own, opacity) = _colours[part];
        var colour = part == highlighted ? _skin.Accent : own;
        var alpha = opacity * (_style == "both" ? 0.62 : _style == "wire" ? 0.5 : 0.96) * (highlighted >= 0 && part != highlighted ? 0.35 : 1);
        return new SKColor((byte)(colour.R * light), (byte)(colour.G * light), (byte)(colour.B * light), (byte)(255 * alpha));
    }

    /// <summary>A wire in its part's colour; on a filled part a lighter one, so the edges show against the faces.</summary>
    private Color WireColour(int part, bool filled)
    {
        var colour = _colours[part].Colour;
        return filled
            ? Color.FromRgb((byte)(colour.R + ((255 - colour.R) * 0.45)), (byte)(colour.G + ((255 - colour.G) * 0.45)), (byte)(colour.B + ((255 - colour.B) * 0.45)))
            : colour;
    }

    /// <summary>A corner in the viewer's space: the model's middle at the origin, its radius one, turned and pulled apart.</summary>
    private Vector3 View(Vector3 vertex, int part, Quaternion orientation, float explode)
    {
        var mesh = _mesh!;
        var offset = explode > 0 ? (mesh.Parts[part].Centroid - mesh.Center) * explode * 0.9f : Vector3.Zero;
        return Vector3.Transform((vertex + offset - mesh.Center) / mesh.Radius, orientation);
    }

    /// <summary>Perspective onto the model's area: the farther, the smaller.</summary>
    private static (Point Screen, float Depth) Project(Vector3 view, Rect area)
    {
        var depth = Distance - view.Z;
        var scale = (Distance - 1.15f) / MathF.Max(0.2f, depth);
        var half = Math.Min(area.Width, area.Height) / 2 * 0.94;
        return (new Point(area.Center.X + (view.X * scale * half), area.Center.Y - (view.Y * scale * half)), depth);
    }

    private void DrawLabels(DrawingContext context, Mesh mesh, Frame frame)
    {
        var labelled = mesh.Parts
            .Select((part, index) => (part, index))
            .Where(p => p.part.Label is not null && (_labels || p.part.Id == _pose.Highlighted))
            .ToList();
        if (labelled.Count == 0)
        {
            return;
        }

        var size = LabelSize;
        var area = ModelArea();
        var anchors = labelled.ToDictionary(
            p => p.part.Id,
            p => Project(View(p.part.Centroid, p.index, _pose.Orientation, _pose.Explode), area),
            StringComparer.Ordinal);

        var inputs = labelled.Select(p =>
        {
            var at = anchors[p.part.Id].Screen;
            return new CalloutInput(p.part.Id, new Pt(at.X, at.Y), null, (Measure(p.part.Label!, size).Width + 8, size * 1.4), _sides.GetValueOrDefault(p.part.Id));
        }).ToList();

        var layout = CalloutLayout.Arrange(new Box(area.X, area.Y, area.Width, area.Height), new Box(0, 0, Bounds.Width, Bounds.Height), inputs);
        var middle = frame.Near + (frame.Span / 2);

        foreach (var placement in layout.Placements)
        {
            var part = labelled.First(p => p.part.Id == placement.Id).part;
            var behind = anchors[part.Id].Depth > middle;
            var isHighlighted = part.Id == _pose.Highlighted;
            var dimmed = _pose.Highlighted is not null && !isHighlighted;
            var opacity = (behind ? 0.5 : 1) * (dimmed ? 0.35 : 1);
            var colour = isHighlighted ? _skin.Accent : _skin.Text;

            var leader = placement.Leader;
            var pen = new Pen(_skin.Brush(isHighlighted ? _skin.Accent : _skin.Muted, 0.85 * opacity), Math.Max(1, size / 14));
            for (var i = 1; i < leader.Count; i++)
            {
                context.DrawLine(pen, new Point(leader[i - 1].X, leader[i - 1].Y), new Point(leader[i].X, leader[i].Y));
            }

            var end = leader[^1];
            context.DrawEllipse(_skin.Brush(colour, opacity), null, new Point(end.X, end.Y), size / 6, size / 6);

            var text = Text(part.Label!, size, colour, opacity, isHighlighted);
            var x = placement.Side == "left" ? placement.Label.Right - text.Width - 4 : placement.Label.X + 4;
            context.DrawText(text, new Point(x, placement.Label.Y + ((placement.Label.Height - text.Height) / 2)));
        }
    }

    private FormattedText Text(string text, double size, Color colour, double opacity, bool bold) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(_skin.UiFont, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
        size,
        _skin.Brush(colour, opacity));

    private Size Measure(string text, double size)
    {
        var formatted = Text(text, size, _skin.Text, 1, bold: true);
        return new Size(formatted.Width, formatted.Height);
    }

    private int Index(string part)
    {
        var parts = _mesh?.Parts ?? [];
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Id == part)
            {
                return i;
            }
        }

        return -1;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = (dx * dx) + (dy * dy);
        var t = length == 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / length, 0, 1);
        var (x, y) = (a.X + (t * dx) - p.X, a.Y + (t * dy) - p.Y);
        return Math.Sqrt((x * x) + (y * y));
    }

    private static bool Inside(Point p, Point a, Point b, Point c)
    {
        static double Side(Point p1, Point p2, Point p3) => ((p1.X - p3.X) * (p2.Y - p3.Y)) - ((p2.X - p3.X) * (p1.Y - p3.Y));
        var (d1, d2, d3) = (Side(p, a, b), Side(p, b, c), Side(p, c, a));
        var negative = d1 < 0 || d2 < 0 || d3 < 0;
        var positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    /// <summary>One frame, worked out: where every corner went, and what to draw.</summary>
    private sealed record Frame(Point[] Screen, float[] Depth, int[] Order, SKPoint[] Faces, SKColor[] FaceColors, Wires[] Wires, float Near, float Span);

    /// <summary>Wires of one colour and width, as pairs of points.</summary>
    private sealed record Wires(SKPoint[] Points, SKColor Colour, float Width);

    /// <summary>The frame, drawn on Skia's canvas: the faces as one mesh, then the wires.</summary>
    private sealed class MeshOperation(Rect bounds, SKPoint[] faces, SKColor[] colors, Wires[] wires) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

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

            // Raw Skia does not inherit Avalonia's opacity; without this a model would stay solid
            // while the slide around it fades.
            if (lease.CurrentOpacity < 0.999)
            {
                using var fade = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * lease.CurrentOpacity)) };
                canvas.SaveLayer(fade);
            }

            if (faces.Length > 0)
            {
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawVertices(SKVertexMode.Triangles, faces, null, colors, paint);
            }

            foreach (var wire in wires)
            {
                using var pen = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = wire.Width,
                    StrokeCap = SKStrokeCap.Round,
                    Color = wire.Colour,
                };
                canvas.DrawPoints(SKPointMode.Lines, wire.Points, pen);
            }

            canvas.RestoreToCount(saved);
        }

        public void Dispose()
        {
        }
    }
}
