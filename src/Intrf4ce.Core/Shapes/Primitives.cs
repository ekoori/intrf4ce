using System.Numerics;

namespace Intrf4ce.Core.Shapes;

/// <summary>
/// The simple shapes a model is built from, as corners and faces of the size asked for, centred on
/// the origin, every face counter-clockwise seen from outside.
/// </summary>
/// <remarks>
/// Low in detail on purpose: eighteen segments round, ten rings down. Drawn as wires that reads as
/// a model rather than as a mesh, and it keeps thousands of shapes cheap to turn every frame.
/// Spheres, cylinders and cones are elliptical when their x and z sizes differ; a capsule is a
/// cylinder with a hemisphere at each end, and a torus lies flat, its hole along y.
/// </remarks>
public static class Primitives
{
    public static readonly IReadOnlyList<string> Shapes = ["box", "sphere", "cylinder", "cone", "capsule", "torus", "line", "mesh"];

    private const int Segments = 18;
    private const int Rings = 10;
    private const int TorusSegments = 24;
    private const int TubeSegments = 10;

    /// <summary>A shape's corners and faces, <paramref name="size"/> across in x, y and z.</summary>
    public static (List<Vector3> Vertices, List<int[]> Polygons) Make(string shape, Vector3 size) => shape switch
    {
        "box" => Box(size),
        "sphere" => Sphere(size),
        "cylinder" => Cylinder(size),
        "cone" => Cone(size),
        "capsule" => Capsule(size),
        "torus" => Torus(size),
        _ => throw new ArgumentException($"'{shape}' is not a shape that can be made", nameof(shape)),
    };

    private static (List<Vector3>, List<int[]>) Box(Vector3 size)
    {
        var h = size / 2;
        var vertices = new List<Vector3>(8);
        for (var i = 0; i < 8; i++)
        {
            vertices.Add(new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z));
        }

        return (vertices, [[4, 5, 7, 6], [0, 2, 3, 1], [1, 3, 7, 5], [0, 4, 6, 2], [2, 6, 7, 3], [0, 1, 5, 4]]);
    }

    private static (List<Vector3>, List<int[]>) Sphere(Vector3 size)
    {
        var r = size / 2;
        var rings = new List<Vector3[]>();
        for (var k = 1; k < Rings; k++)
        {
            var theta = MathF.PI * k / Rings;
            rings.Add(Ring(r.X * MathF.Sin(theta), r.Z * MathF.Sin(theta), r.Y * MathF.Cos(theta)));
        }

        return Banded(new Vector3(0, r.Y, 0), rings, new Vector3(0, -r.Y, 0));
    }

    private static (List<Vector3>, List<int[]>) Capsule(Vector3 size)
    {
        var r = size / 2;
        var cap = MathF.Min(MathF.Min(r.X, r.Z), r.Y);
        var body = MathF.Max(0, r.Y - cap);
        var rings = new List<Vector3[]>();

        // The equator twice — once for each hemisphere — so the body between them is a cylinder.
        for (var k = 1; k <= Rings / 2; k++)
        {
            var theta = MathF.PI * k / Rings;
            rings.Add(Ring(r.X * MathF.Sin(theta), r.Z * MathF.Sin(theta), (cap * MathF.Cos(theta)) + body));
        }

        for (var k = Rings / 2; k < Rings; k++)
        {
            var theta = MathF.PI * k / Rings;
            rings.Add(Ring(r.X * MathF.Sin(theta), r.Z * MathF.Sin(theta), (cap * MathF.Cos(theta)) - body));
        }

        return Banded(new Vector3(0, cap + body, 0), rings, new Vector3(0, -cap - body, 0));
    }

    private static (List<Vector3>, List<int[]>) Cylinder(Vector3 size)
    {
        var r = size / 2;
        var top = Ring(r.X, r.Z, r.Y);
        var bottom = Ring(r.X, r.Z, -r.Y);
        var vertices = new List<Vector3>(top);
        vertices.AddRange(bottom);

        var polygons = new List<int[]>();
        for (var j = 0; j < Segments; j++)
        {
            var next = (j + 1) % Segments;
            polygons.Add([j, next, Segments + next, Segments + j]);
        }

        // The caps go round opposite ways, each counter-clockwise seen from its own side.
        polygons.Add([.. Enumerable.Range(0, Segments).Reverse()]);
        polygons.Add([.. Enumerable.Range(Segments, Segments)]);
        return (vertices, polygons);
    }

    private static (List<Vector3>, List<int[]>) Cone(Vector3 size)
    {
        var r = size / 2;
        var vertices = new List<Vector3> { new(0, r.Y, 0) };
        vertices.AddRange(Ring(r.X, r.Z, -r.Y));

        var polygons = new List<int[]>();
        for (var j = 0; j < Segments; j++)
        {
            polygons.Add([0, 1 + ((j + 1) % Segments), 1 + j]);
        }

        polygons.Add([.. Enumerable.Range(1, Segments)]);
        return (vertices, polygons);
    }

    private static (List<Vector3>, List<int[]>) Torus(Vector3 size)
    {
        var tube = size.Y / 2;
        var across = MathF.Max(tube * 1.2f, (size.X / 2) - tube);
        var deep = MathF.Max(tube * 1.2f, (size.Z / 2) - tube);
        var vertices = new List<Vector3>(TorusSegments * TubeSegments);

        for (var i = 0; i < TorusSegments; i++)
        {
            var phi = 2 * MathF.PI * i / TorusSegments;
            var (c, s) = (MathF.Cos(phi), MathF.Sin(phi));
            for (var j = 0; j < TubeSegments; j++)
            {
                var psi = 2 * MathF.PI * j / TubeSegments;
                var outward = tube * MathF.Cos(psi);
                vertices.Add(new Vector3(c * (across + outward), tube * MathF.Sin(psi), s * (deep + outward)));
            }
        }

        var polygons = new List<int[]>();
        for (var i = 0; i < TorusSegments; i++)
        {
            for (var j = 0; j < TubeSegments; j++)
            {
                int At(int a, int b) => (a % TorusSegments * TubeSegments) + (b % TubeSegments);
                polygons.Add([At(i, j), At(i, j + 1), At(i + 1, j + 1), At(i + 1, j)]);
            }
        }

        return (vertices, polygons);
    }

    /// <summary>A ring of <see cref="Segments"/> corners at height <paramref name="y"/>.</summary>
    private static Vector3[] Ring(float rx, float rz, float y)
    {
        var ring = new Vector3[Segments];
        for (var j = 0; j < Segments; j++)
        {
            var phi = 2 * MathF.PI * j / Segments;
            ring[j] = new Vector3(rx * MathF.Cos(phi), y, rz * MathF.Sin(phi));
        }

        return ring;
    }

    /// <summary>A pole, rings from top to bottom, and a pole: a sphere's or a capsule's corners and faces.</summary>
    private static (List<Vector3>, List<int[]>) Banded(Vector3 top, List<Vector3[]> rings, Vector3 bottom)
    {
        var vertices = new List<Vector3> { top };
        foreach (var ring in rings)
        {
            vertices.AddRange(ring);
        }

        var last = vertices.Count;
        vertices.Add(bottom);

        int At(int ring, int j) => 1 + (ring * Segments) + (j % Segments);

        var polygons = new List<int[]>();
        for (var j = 0; j < Segments; j++)
        {
            polygons.Add([0, At(0, j + 1), At(0, j)]);
        }

        for (var k = 0; k < rings.Count - 1; k++)
        {
            for (var j = 0; j < Segments; j++)
            {
                polygons.Add([At(k, j), At(k, j + 1), At(k + 1, j + 1), At(k + 1, j)]);
            }
        }

        for (var j = 0; j < Segments; j++)
        {
            polygons.Add([At(rings.Count - 1, j), At(rings.Count - 1, j + 1), last]);
        }

        return (vertices, polygons);
    }
}
