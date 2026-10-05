using System.Numerics;
using System.Text;
using System.Text.Json;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Shapes;

namespace Intrf4ce.Tests.Core;

/// <summary>3D models: shapes made right, files read right, and the agent told when it is wrong.</summary>
public class ModelTests
{
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("cone")]
    [InlineData("capsule")]
    public void EveryFaceOfAClosedShapeFacesOutwards(string shape)
    {
        var (vertices, polygons) = Primitives.Make(shape, new Vector3(2, 3, 1.5f));

        foreach (var face in polygons)
        {
            var corners = face.Select(i => vertices[i]).ToList();
            var middle = corners.Aggregate(Vector3.Add) / corners.Count;
            Assert.True(Vector3.Dot(Normal(corners), middle) > 0, $"{shape}: a face points inwards");
        }
    }

    [Fact]
    public void ATorusFacesAwayFromItsTube()
    {
        var (vertices, polygons) = Primitives.Make("torus", new Vector3(4, 1, 4));

        foreach (var face in polygons)
        {
            var corners = face.Select(i => vertices[i]).ToList();
            var middle = corners.Aggregate(Vector3.Add) / corners.Count;

            // The middle of the tube there: on the ring, at the face's angle round it.
            var ring = Vector3.Normalize(new Vector3(middle.X, 0, middle.Z)) * 1.5f;
            Assert.True(Vector3.Dot(Normal(corners), middle - ring) > 0);
        }
    }

    [Fact]
    public void ABoxIsTwelveWiresAndTwelveTriangles()
    {
        var mesh = Build("""{"parts":[{"shape":"box","size":[2,1,1],"label":"Crate"}]}""");

        Assert.Equal(12, mesh.EdgeCount);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal("crate", mesh.Parts[0].Id);

        // Every wire of a box has a face on either side.
        Assert.DoesNotContain(-1, mesh.EdgeFaces);
    }

    [Fact]
    public void ASpheresWiresAreItsLinesOfLatitudeAndLongitudeNotItsTriangles()
    {
        var mesh = Build("""{"parts":[{"shape":"sphere"}]}""");

        // 18 meridians of 10 segments, and 9 rings of 18: no diagonals across the quads.
        Assert.Equal((18 * 10) + (9 * 18), mesh.EdgeCount);
    }

    [Fact]
    public void PartsAreNamedOnceEachAndPlacedWhereTheySay()
    {
        var mesh = Build("""
            {"parts":[
              {"shape":"sphere","label":"Head","at":[0,2,0]},
              {"shape":"sphere","label":"Head"},
              {"shape":"line","points":[[0,0,0],[1,1,1],[2,0,0]],"id":"path"}]}
            """);

        Assert.Equal(["head", "head-2", "path"], mesh.Parts.Select(p => p.Id));
        Assert.Equal(2, mesh.Parts[0].Centroid.Y, 3);
        Assert.True(mesh.Parts[2].IsLine);
        Assert.Equal(2, mesh.EdgePart.Count(p => p == 2));
    }

    [Fact]
    public void AnObjsGroupsBecomePartsWithTheirOwnCorners()
    {
        var obj = """
            # two quads
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            v 0 0 1
            g left_femur
            f 1/1/1 2/2/2 3/3/3 4/4/4
            g right_femur
            f -4 -3 2 1
            f 1 5 3
            """;

        var groups = ModelFiles.ReadObj(obj.Split('\n'), "bones");

        Assert.Equal(["left_femur", "right_femur"], groups.Select(g => g.Name));
        Assert.Equal(4, groups[0].Vertices.Count);
        Assert.Equal([0, 1, 2, 3], groups[0].Polygons[0]);
        Assert.Equal(2, groups[1].Polygons.Count);

        var mesh = MeshBuilder.Build(MeshBuilder.Parts(new ModelElement { Src = "bones.obj" }, groups));
        Assert.Equal(["left femur", "right femur"], mesh.Parts.Select(p => p.Label));
    }

    [Fact]
    public void AnStlIsWeldedSoACubeHasTwelveCreasesNotEighteenEdges()
    {
        var cube = Primitives.Make("box", Vector3.One);
        var text = new StringBuilder("solid cube\n");
        foreach (var face in cube.Polygons)
        {
            foreach (var triangle in new[] { (face[0], face[1], face[2]), (face[0], face[2], face[3]) })
            {
                text.Append("facet normal 0 0 0\nouter loop\n");
                foreach (var i in new[] { triangle.Item1, triangle.Item2, triangle.Item3 })
                {
                    var v = cube.Vertices[i];
                    text.Append(System.Globalization.CultureInfo.InvariantCulture, $"vertex {v.X} {v.Y} {v.Z}\n");
                }

                text.Append("endloop\nendfacet\n");
            }
        }

        var group = ModelFiles.ReadStl(Encoding.ASCII.GetBytes(text.Append("endsolid cube\n").ToString()), "cube");
        var mesh = MeshBuilder.Build(MeshBuilder.Parts(new ModelElement { Src = "cube.stl" }, [group]));

        Assert.Equal(8, group.Vertices.Count);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(12, mesh.EdgeCount);
    }

    [Fact]
    public void ABigMeshIsThinnedButKeepsItsShape()
    {
        var (vertices, polygons) = Primitives.Make("sphere", new Vector3(10));
        var dense = Subdivide(new FileGroup("ball", vertices, polygons), times: 4);

        var thin = ModelFiles.Simplify(dense, 0.1);

        Assert.True(thin.Polygons.Count < dense.Polygons.Count / 4, $"{thin.Polygons.Count} of {dense.Polygons.Count}");
        Assert.All(thin.Vertices, v => Assert.InRange(v.Length(), 4f, 5.2f));
    }

    [Fact]
    public void TheAgentIsToldExactlyWhatIsWrongWithAModel()
    {
        var slide = JsonSerializer.Deserialize<Slide>("""
            {"id":"s","elements":[{"type":"model3d","id":"knee","style":"shaded","spin":2000,"parts":[
              {"shape":"pyramid"},
              {"shape":"sphere","color":"mauve"},
              {"shape":"mesh","vertices":[[0,0,0],[1,0,0],[0,1,0]],"faces":[[0,1,7]]},
              {"shape":"line","points":[[0,0,0]]}]}]}
            """, DeckJson.Options)!;

        var issues = SpecValidator.Validate(slide).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Path).ToList();

        Assert.Contains("elements[0].style", issues);
        Assert.Contains("elements[0].spin", issues);
        Assert.Contains("elements[0].parts[0].shape", issues);
        Assert.Contains("elements[0].parts[1].color", issues);
        Assert.Contains("elements[0].parts[2].faces[0]", issues);
        Assert.Contains("elements[0].parts[3].points", issues);
    }

    [Fact]
    public void ASizeIsOneNumberWrittenBareOrInAList()
    {
        var mesh = Build("""
            {"parts":[
              {"shape":"sphere","size":0.9},
              {"shape":"box","size":[2],"at":[3,0,0]},
              {"shape":"box","size":[1,"2"],"at":[6,0,0]}]}
            """);

        // A sphere's corners touch its extent up and across, between its meridians in depth.
        Assert.Equal((0.9f, 0.9f), (Extent(mesh, 0).X, Extent(mesh, 0).Y));
        Assert.Equal(new Vector3(2), Extent(mesh, 1));
        Assert.Equal(new Vector3(1, 2, 1), Extent(mesh, 2));

        // Written back, it is a list whichever way it came.
        var written = JsonSerializer.Serialize(new ModelPart { Shape = "sphere", Size = [0.9] }, DeckJson.Options);
        Assert.Contains("\"size\":[0.9]", written, StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ModelPart>("""{"size":"big"}""", DeckJson.Options));
    }

    [Fact]
    public void AModelsPartsAreFocusTargetsAndAFilesAreNotKnownYet()
    {
        var built = new ModelElement { Id = "m", Parts = [new ModelPart { Shape = "box", Label = "Femur" }] };
        var loaded = new ModelElement { Id = "f", Src = "https://example.org/knee.obj" };

        Assert.Equal(["femur"], SpecValidator.PartsOf(built)!);
        Assert.Null(SpecValidator.PartsOf(loaded));
        Assert.Empty(SpecValidator.Validate(new Slide { Id = "s", Elements = [loaded] }));
    }

    private static Mesh Build(string json)
    {
        var model = JsonSerializer.Deserialize<Element>(json.Insert(1, "\"type\":\"model3d\",\"id\":\"m\","), DeckJson.Options) as ModelElement;
        return MeshBuilder.Build(MeshBuilder.Parts(model!, null));
    }

    /// <summary>How far one part reaches along x, y and z, to the nearest thousandth.</summary>
    private static Vector3 Extent(Mesh mesh, int part)
    {
        var corners = mesh.Vertices.Skip(mesh.Parts[part].FirstVertex).Take(mesh.Parts[part].VertexCount).ToList();
        var size = corners.Aggregate(Vector3.Max) - corners.Aggregate(Vector3.Min);
        return new Vector3(MathF.Round(size.X, 3), MathF.Round(size.Y, 3), MathF.Round(size.Z, 3));
    }

    private static Vector3 Normal(List<Vector3> corners)
    {
        var normal = Vector3.Zero;
        for (var k = 0; k < corners.Count; k++)
        {
            var (a, b) = (corners[k], corners[(k + 1) % corners.Count]);
            normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }

        return normal;
    }

    /// <summary>Each triangle into four, pushed out onto the sphere: a dense ball to thin.</summary>
    private static FileGroup Subdivide(FileGroup group, int times)
    {
        var vertices = group.Vertices.ToList();
        var triangles = group.Polygons.SelectMany(p => Enumerable.Range(1, p.Length - 2).Select(k => new[] { p[0], p[k], p[k + 1] })).ToList();
        var radius = vertices.Max(v => v.Length());

        for (var t = 0; t < times; t++)
        {
            var next = new List<int[]>();
            foreach (var tri in triangles)
            {
                int Mid(int a, int b)
                {
                    vertices.Add(Vector3.Normalize((vertices[a] + vertices[b]) / 2) * radius);
                    return vertices.Count - 1;
                }

                var (ab, bc, ca) = (Mid(tri[0], tri[1]), Mid(tri[1], tri[2]), Mid(tri[2], tri[0]));
                next.Add([tri[0], ab, ca]);
                next.Add([ab, tri[1], bc]);
                next.Add([ca, bc, tri[2]]);
                next.Add([ab, bc, ca]);
            }

            triangles = next;
        }

        return new FileGroup(group.Name, vertices, triangles);
    }
}
