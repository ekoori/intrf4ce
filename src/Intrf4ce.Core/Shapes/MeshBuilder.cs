using System.Numerics;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Core.Shapes;

/// <summary>
/// Turns a 3D model as the agent described it — shapes, or a file's groups with notes on them —
/// into one <see cref="Mesh"/> to draw.
/// </summary>
public static class MeshBuilder
{
    /// <summary>Faces meeting at more than this are a crease, and their shared edge a wire.</summary>
    private const float CreaseDegrees = 25;

    /// <summary>
    /// The ids its parts will have, as focus cues name them: a file's groups first, then the
    /// shapes; for a model from a file, null, because the groups are not known until it is read.
    /// </summary>
    public static IReadOnlyList<string>? Ids(ModelElement model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Src is not null ? null : [.. Parts(model, null).Select(p => p.Id)];
    }

    /// <summary>Each part's own corners and faces, in model space, with its id, label and colour.</summary>
    /// <param name="file">The groups of the model's file, when it has one, read already.</param>
    public static IReadOnlyList<PartGeometry> Parts(ModelElement model, IReadOnlyList<FileGroup>? file)
    {
        ArgumentNullException.ThrowIfNull(model);

        var parts = new List<PartGeometry>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var shapes = model.Parts ?? [];
        var notes = shapes
            .Where(p => p.Group is not null)
            .GroupBy(p => p.Group!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in file ?? [])
        {
            notes.TryGetValue(group.Name, out var note);
            var label = note?.Label ?? group.Name.Replace('_', ' ');
            parts.Add(new PartGeometry(
                Unique(note?.Id ?? Slug.From(label, "part"), used),
                label,
                note?.Detail,
                note?.Color ?? model.Color ?? ModelColors.Cycle[parts.Count % ModelColors.Cycle.Count],
                note?.Fill,
                group.Vertices,
                group.Polygons,
                [],
                EveryEdge: group.Polygons.Any(p => p.Length > 3) && group.Polygons.Count <= 20_000));
        }

        foreach (var shape in shapes.Where(p => p.Group is null))
        {
            var kind = shape.Shape ?? "box";
            var id = Unique(shape.Id ?? Slug.From(shape.Label, kind), used);
            var size = Size(shape.Size);
            var turn = Rotation(shape.Rotation);
            var at = Vector(shape.At) ?? Vector3.Zero;

            List<Vector3> vertices;
            IReadOnlyList<int[]> polygons = [];
            IReadOnlyList<(int, int)> lines = [];
            var every = true;

            switch (kind)
            {
                case "line":
                    vertices = [.. (shape.Points ?? []).Select(p => Vector(p) ?? Vector3.Zero)];
                    lines = [.. Enumerable.Range(0, Math.Max(0, vertices.Count - 1)).Select(i => (i, i + 1))];
                    break;

                case "mesh":
                    vertices = [.. (shape.Vertices ?? []).Select(v => (Vector(v) ?? Vector3.Zero) * size)];
                    polygons = shape.Faces ?? [];
                    every = polygons.Any(f => f.Length > 3) || polygons.Count <= 600;
                    break;

                default:
                    (vertices, var made) = Primitives.Make(kind, size);
                    polygons = made;
                    break;
            }

            parts.Add(new PartGeometry(
                id,
                shape.Label,
                shape.Detail,
                shape.Color ?? model.Color ?? ModelColors.Cycle[parts.Count % ModelColors.Cycle.Count],
                kind == "line" ? false : shape.Fill,
                [.. vertices.Select(v => Vector3.Transform(v, turn) + at)],
                polygons,
                lines,
                every));
        }

        return parts;
    }

    public static Mesh Build(IReadOnlyList<PartGeometry> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var trianglePart = new List<int>();
        var edges = new List<int>();
        var edgePart = new List<int>();
        var edgeFaces = new List<int>();
        var meshParts = new List<MeshPart>();
        var crease = MathF.Cos(CreaseDegrees * MathF.PI / 180);

        for (var p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var first = vertices.Count;
            vertices.AddRange(part.Vertices);

            // Each face cut into a fan of triangles, remembering the first, which is the face's
            // triangle for the wires either side of an edge.
            var faceTriangle = new int[part.Polygons.Count];
            var normals = new Vector3[part.Polygons.Count];
            var sides = new Dictionary<(int, int), List<int>>();

            for (var f = 0; f < part.Polygons.Count; f++)
            {
                var face = part.Polygons[f];
                faceTriangle[f] = triangles.Count / 3;
                normals[f] = Normal(part.Vertices, face);

                for (var k = 1; k < face.Length - 1; k++)
                {
                    triangles.Add(first + face[0]);
                    triangles.Add(first + face[k]);
                    triangles.Add(first + face[k + 1]);
                    trianglePart.Add(p);
                }

                for (var k = 0; k < face.Length; k++)
                {
                    var (a, b) = (face[k], face[(k + 1) % face.Length]);
                    var key = a < b ? (a, b) : (b, a);
                    if (!sides.TryGetValue(key, out var list))
                    {
                        sides[key] = list = [];
                    }

                    list.Add(f);
                }
            }

            foreach (var ((a, b), faces) in sides)
            {
                // Every edge, or only borders and creases: the edges of a smooth triangulated
                // surface are a fog, not a drawing.
                var keep = part.EveryEdge || faces.Count == 1
                    || Vector3.Dot(normals[faces[0]], normals[faces[1]]) < crease;
                if (!keep)
                {
                    continue;
                }

                edges.Add(first + a);
                edges.Add(first + b);
                edgePart.Add(p);
                edgeFaces.Add(faceTriangle[faces[0]]);
                edgeFaces.Add(faces.Count > 1 ? faceTriangle[faces[1]] : -1);
            }

            foreach (var (a, b) in part.Lines)
            {
                edges.Add(first + a);
                edges.Add(first + b);
                edgePart.Add(p);
                edgeFaces.Add(-1);
                edgeFaces.Add(-1);
            }

            var centroid = part.Vertices.Count == 0
                ? Vector3.Zero
                : (part.Vertices.Aggregate(Vector3.Min) + part.Vertices.Aggregate(Vector3.Max)) / 2;

            meshParts.Add(new MeshPart(
                part.Id,
                part.Label,
                part.Detail,
                part.Color,
                part.Fill,
                centroid,
                first,
                part.Vertices.Count,
                IsLine: part.Polygons.Count == 0));
        }

        var center = vertices.Count == 0 ? Vector3.Zero : (vertices.Aggregate(Vector3.Min) + vertices.Aggregate(Vector3.Max)) / 2;
        var radius = vertices.Count == 0 ? 1 : MathF.Max(1e-6f, vertices.Max(v => Vector3.Distance(v, center)));

        return new Mesh
        {
            Vertices = [.. vertices],
            Triangles = [.. triangles],
            TrianglePart = [.. trianglePart],
            Edges = [.. edges],
            EdgePart = [.. edgePart],
            EdgeFaces = [.. edgeFaces],
            Parts = meshParts,
            Center = center,
            Radius = radius,
        };
    }

    /// <summary>Degrees about x, then y, then z, as one turn.</summary>
    public static Quaternion Rotation(double[]? degrees)
    {
        if (degrees is not [var x, var y, var z])
        {
            return Quaternion.Identity;
        }

        static Quaternion About(Vector3 axis, double angle) => Quaternion.CreateFromAxisAngle(axis, (float)(angle * Math.PI / 180));
        return Quaternion.Concatenate(Quaternion.Concatenate(About(Vector3.UnitX, x), About(Vector3.UnitY, y)), About(Vector3.UnitZ, z));
    }

    /// <summary>One number is all three sizes, two are across and up, three are x, y and z.</summary>
    public static Vector3 Size(double[]? size) => size switch
    {
        [var all] => new Vector3((float)all),
        [var across, var up] => new Vector3((float)across, (float)up, (float)across),
        [var x, var y, var z] => new Vector3((float)x, (float)y, (float)z),
        _ => Vector3.One,
    };

    private static Vector3? Vector(double[]? value) =>
        value is [var x, var y, var z] ? new Vector3((float)x, (float)y, (float)z) : null;

    private static string Unique(string id, HashSet<string> used)
    {
        var unique = id;
        for (var n = 2; !used.Add(unique); n++)
        {
            unique = $"{id}-{n}";
        }

        return unique;
    }

    /// <summary>Newell's normal: right for any flat face, near enough for a slightly bent one.</summary>
    private static Vector3 Normal(IReadOnlyList<Vector3> vertices, int[] face)
    {
        var normal = Vector3.Zero;
        for (var k = 0; k < face.Length; k++)
        {
            var (a, b) = (vertices[face[k]], vertices[face[(k + 1) % face.Length]]);
            normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }

        return normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY;
    }
}
