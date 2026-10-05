using System.Numerics;

namespace Intrf4ce.Core.Shapes;

/// <summary>One part of a mesh: what it is called, its colour, and which of the mesh's corners are its own.</summary>
/// <param name="Centroid">The middle of its corners, in model space: where its label points, and what focusing it turns to face.</param>
/// <param name="FirstVertex">Its corners are <see cref="Mesh.Vertices"/> from here, <paramref name="VertexCount"/> of them: no corner is shared between parts, so a part can move on its own.</param>
/// <param name="IsLine">Only wires, never faces: a nerve, a path, an axis.</param>
public sealed record MeshPart(
    string Id,
    string? Label,
    string? Detail,
    string Color,
    bool? Fill,
    Vector3 Centroid,
    int FirstVertex,
    int VertexCount,
    bool IsLine);

/// <summary>
/// A 3D model ready to draw: its corners, the triangles its faces were cut into, and the edges to
/// draw as wires, each belonging to a part.
/// </summary>
/// <remarks>
/// Wires are the outlines of the faces as they were described — a box's twelve edges, a sphere's
/// lines of latitude and longitude — not the diagonals of the triangles a quad is cut into; and,
/// for a mesh that came as nothing but triangles, only its creases and borders, because every
/// edge of a scanned bone is a grey fog. Each edge knows the faces either side of it, so a solid
/// model can draw only the wires facing the viewer.
/// </remarks>
public sealed class Mesh
{
    public required Vector3[] Vertices { get; init; }

    /// <summary>Three corner indexes a triangle.</summary>
    public required int[] Triangles { get; init; }

    /// <summary>The part each triangle belongs to.</summary>
    public required int[] TrianglePart { get; init; }

    /// <summary>Two corner indexes an edge.</summary>
    public required int[] Edges { get; init; }

    /// <summary>The part each edge belongs to.</summary>
    public required int[] EdgePart { get; init; }

    /// <summary>Two triangle indexes an edge: a triangle of each face either side, or -1 where there is none.</summary>
    public required int[] EdgeFaces { get; init; }

    public required IReadOnlyList<MeshPart> Parts { get; init; }

    /// <summary>The middle of the whole model, and how far its farthest corner is from it.</summary>
    public required Vector3 Center { get; init; }

    public required float Radius { get; init; }

    public int TriangleCount => Triangles.Length / 3;

    public int EdgeCount => Edges.Length / 2;
}

/// <summary>A part's own corners and faces, before they are put together into a <see cref="Mesh"/>.</summary>
/// <param name="Polygons">Faces as corner indexes, counter-clockwise seen from outside.</param>
/// <param name="Lines">Wires that are not the edges of any face: a line's segments.</param>
/// <param name="EveryEdge">Draw every edge of every face as a wire; otherwise only creases and borders.</param>
public sealed record PartGeometry(
    string Id,
    string? Label,
    string? Detail,
    string Color,
    bool? Fill,
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<int[]> Polygons,
    IReadOnlyList<(int A, int B)> Lines,
    bool EveryEdge);
