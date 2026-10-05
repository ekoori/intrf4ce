using System.Globalization;
using System.Numerics;
using System.Text;

namespace Intrf4ce.Core.Shapes;

/// <summary>A group of a model file: its name, its corners and its faces.</summary>
public sealed record FileGroup(string Name, IReadOnlyList<Vector3> Vertices, IReadOnlyList<int[]> Polygons);

/// <summary>
/// Reads the two formats an agent is likely to find a 3D model in: Wavefront .obj, whose groups
/// and objects become parts, and .stl, binary or text, which is one part.
/// </summary>
/// <remarks>
/// Only geometry is read — corners and faces — and materials, textures and normals are passed
/// over: a model on the stage is drawn in the stage's own colours and light. An .stl repeats every
/// corner for every triangle; corners in the same place are made one, so its creases can be found.
/// </remarks>
public static class ModelFiles
{
    /// <summary>The extensions it reads.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".obj", ".stl"];

    /// <summary>More triangles than this in a file are thinned: a model is turned every frame, and this one is for looking at.</summary>
    public const int MaxTriangles = 40_000;

    /// <param name="name">What to call a part the file does not name: the source's own file name, not the session's copy of it.</param>
    /// <exception cref="InvalidDataException">It is not a model this can read.</exception>
    public static IReadOnlyList<FileGroup> Read(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        name ??= Path.GetFileNameWithoutExtension(path);

        var groups = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".obj" => ReadObj(File.ReadAllLines(path), name),
            ".stl" => [ReadStl(File.ReadAllBytes(path), name)],
            var other => throw new InvalidDataException($"a model is an .obj or .stl file, not {other}"),
        };

        if (groups.Count == 0 || groups.All(g => g.Polygons.Count == 0))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} has no faces in it");
        }

        var triangles = groups.Sum(g => g.Polygons.Sum(p => p.Length - 2));
        return triangles <= MaxTriangles ? groups : [.. groups.Select(g => Simplify(g, (double)MaxTriangles / triangles))];
    }

    public static IReadOnlyList<FileGroup> ReadObj(IEnumerable<string> lines, string name)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var positions = new List<Vector3>();
        var groups = new List<(string Name, List<int[]> Faces)>();
        var current = (Name: name, Faces: new List<int[]>());

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    positions.Add(new Vector3(Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;

                case "f" when parts.Length >= 4:
                    // v, v/vt, v//vn or v/vt/vn; negative indexes count back from the last corner read.
                    current.Faces.Add([.. parts.Skip(1).Select(p =>
                    {
                        var index = int.Parse(p.Split('/')[0], CultureInfo.InvariantCulture);
                        return index < 0 ? positions.Count + index : index - 1;
                    })]);
                    break;

                case "o" or "g":
                    if (current.Faces.Count > 0)
                    {
                        groups.Add(current);
                    }

                    current = (parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : name, []);
                    break;
            }
        }

        if (current.Faces.Count > 0)
        {
            groups.Add(current);
        }

        // Each group keeps only the corners it uses, renumbered, so parts share nothing.
        var result = new List<FileGroup>();
        foreach (var (groupName, faces) in groups)
        {
            var map = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var polygons = new List<int[]>();
            foreach (var face in faces)
            {
                if (face.Any(i => i < 0 || i >= positions.Count))
                {
                    throw new InvalidDataException($"a face in group '{groupName}' points at a corner that is not there");
                }

                polygons.Add([.. face.Select(i =>
                {
                    if (!map.TryGetValue(i, out var local))
                    {
                        local = vertices.Count;
                        map[i] = local;
                        vertices.Add(positions[i]);
                    }

                    return local;
                })]);
            }

            result.Add(new FileGroup(groupName, vertices, polygons));
        }

        // Two groups of one name are one part.
        return [.. result
            .GroupBy(g => g.Name, StringComparer.Ordinal)
            .Select(same => same.Count() == 1 ? same.First() : Merge(same.Key, [.. same]))];
    }

    public static FileGroup ReadStl(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var triangles = IsBinaryStl(bytes) ? BinaryStl(bytes) : TextStl(Encoding.ASCII.GetString(bytes));
        return Weld(name, triangles);
    }

    /// <summary>
    /// Fewer triangles by vertex clustering: corners in the same cell of a grid become one, and
    /// triangles that collapse go. Rough, but it keeps the shape, and it is fast enough to do on
    /// load.
    /// </summary>
    public static FileGroup Simplify(FileGroup group, double keep)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (keep >= 1 || group.Vertices.Count == 0)
        {
            return group;
        }

        var min = group.Vertices.Aggregate(Vector3.Min);
        var max = group.Vertices.Aggregate(Vector3.Max);
        var extent = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), MathF.Max(max.Z - min.Z, 1e-6f));

        // Triangles go roughly with the square of the cells along an edge; start there and coarsen.
        var cells = Math.Clamp(Math.Sqrt(group.Polygons.Count * keep) * 1.6, 8, 1024);
        FileGroup result = group;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            result = Cluster(group, min, extent / (float)cells);
            if (result.Polygons.Count <= group.Polygons.Count * keep * 1.15)
            {
                break;
            }

            cells /= 1.3;
        }

        return result;
    }

    private static FileGroup Cluster(FileGroup group, Vector3 min, float cell)
    {
        var cellOf = new Dictionary<(int, int, int), int>();
        var sums = new List<(Vector3 Sum, int Count)>();
        var map = new int[group.Vertices.Count];

        for (var i = 0; i < group.Vertices.Count; i++)
        {
            var p = (group.Vertices[i] - min) / cell;
            var key = ((int)p.X, (int)p.Y, (int)p.Z);
            if (!cellOf.TryGetValue(key, out var index))
            {
                index = sums.Count;
                cellOf[key] = index;
                sums.Add((Vector3.Zero, 0));
            }

            map[i] = index;
            sums[index] = (sums[index].Sum + group.Vertices[i], sums[index].Count + 1);
        }

        var seen = new HashSet<(int, int, int)>();
        var polygons = new List<int[]>();
        foreach (var polygon in group.Polygons)
        {
            for (var k = 1; k < polygon.Length - 1; k++)
            {
                var (a, b, c) = (map[polygon[0]], map[polygon[k]], map[polygon[k + 1]]);
                if (a == b || b == c || a == c)
                {
                    continue;
                }

                // The same triangle turned round is the same triangle.
                var key = Sorted(a, b, c);
                if (seen.Add(key))
                {
                    polygons.Add([a, b, c]);
                }
            }
        }

        return new FileGroup(group.Name, [.. sums.Select(s => s.Sum / s.Count)], polygons);
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b)
        {
            (a, b) = (b, a);
        }

        if (b > c)
        {
            (b, c) = (c, b);
        }

        if (a > b)
        {
            (a, b) = (b, a);
        }

        return (a, b, c);
    }

    private static FileGroup Merge(string name, List<FileGroup> groups)
    {
        var vertices = new List<Vector3>();
        var polygons = new List<int[]>();
        foreach (var group in groups)
        {
            var offset = vertices.Count;
            vertices.AddRange(group.Vertices);
            polygons.AddRange(group.Polygons.Select(p => p.Select(i => i + offset).ToArray()));
        }

        return new FileGroup(name, vertices, polygons);
    }

    /// <summary>
    /// Binary when the header says as many triangles as there are bytes for. "solid" at the start
    /// is not enough: plenty of binary files begin with it too.
    /// </summary>
    private static bool IsBinaryStl(byte[] bytes) =>
        bytes.Length >= 84 && 84 + ((long)BitConverter.ToUInt32(bytes, 80) * 50) == bytes.Length;

    private static List<Vector3> BinaryStl(byte[] bytes)
    {
        var count = BitConverter.ToUInt32(bytes, 80);
        var corners = new List<Vector3>((int)count * 3);
        for (var t = 0; t < count; t++)
        {
            var at = 84 + (t * 50) + 12;
            for (var k = 0; k < 3; k++)
            {
                corners.Add(new Vector3(
                    BitConverter.ToSingle(bytes, at + (k * 12)),
                    BitConverter.ToSingle(bytes, at + (k * 12) + 4),
                    BitConverter.ToSingle(bytes, at + (k * 12) + 8)));
            }
        }

        return corners;
    }

    private static List<Vector3> TextStl(string text)
    {
        var corners = new List<Vector3>();
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["vertex", var x, var y, var z, ..])
            {
                corners.Add(new Vector3(Number(x), Number(y), Number(z)));
            }
        }

        if (corners.Count == 0 || corners.Count % 3 != 0)
        {
            throw new InvalidDataException("not an .stl this can read: no whole triangles in it");
        }

        return corners;
    }

    /// <summary>Triangles given corner by corner, with corners in one place made one.</summary>
    private static FileGroup Weld(string name, List<Vector3> corners)
    {
        var min = corners.Aggregate(Vector3.Min);
        var max = corners.Aggregate(Vector3.Max);
        var tolerance = MathF.Max(Vector3.Distance(min, max) * 1e-6f, 1e-9f);

        var index = new Dictionary<(long, long, long), int>();
        var vertices = new List<Vector3>();
        var polygons = new List<int[]>();
        var triangle = new int[3];

        for (var i = 0; i < corners.Count; i++)
        {
            var c = corners[i];
            var key = ((long)MathF.Round(c.X / tolerance), (long)MathF.Round(c.Y / tolerance), (long)MathF.Round(c.Z / tolerance));
            if (!index.TryGetValue(key, out var at))
            {
                at = vertices.Count;
                index[key] = at;
                vertices.Add(c);
            }

            triangle[i % 3] = at;
            if (i % 3 == 2 && triangle[0] != triangle[1] && triangle[1] != triangle[2] && triangle[0] != triangle[2])
            {
                polygons.Add([triangle[0], triangle[1], triangle[2]]);
            }
        }

        return new FileGroup(name, vertices, polygons);
    }

    private static float Number(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"'{text}' is not a number");
}
