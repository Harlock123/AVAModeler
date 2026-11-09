using System.Buffers.Binary;
using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class StlTests
{
    private static Document TwoBoxes()
    {
        var doc = new Document();
        Models.Box(doc.Layers[0].Mesh, new Vec3(-1, 0, -1), new Vec3(1, 2, 1));
        Models.Box(doc.Layers[1].Mesh, new Vec3(3, 0, 0), new Vec3(4, 1, 3));
        return doc;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Round_trips_binary_and_ascii(bool ascii)
    {
        using var dir = new TempDir();
        var doc = TwoBoxes();
        string path = dir.File("boxes.stl");
        ObjectFiles.Save(doc, path, new SaveOptions { AsciiStl = ascii });
        var bytes = File.ReadAllBytes(path);
        if (ascii)
        {
            string text = Encoding.ASCII.GetString(bytes);
            Assert.StartsWith("solid boxes\n", text);
            Assert.EndsWith("endsolid boxes\n", text);
            Assert.Equal(24, text.Split("facet normal").Length - 1);
        }
        else
        {
            Assert.Equal(84 + 24 * 50, bytes.Length);
            Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80)));
        }

        var back = ObjectFiles.Load(path);
        var mesh = back.Layers[0].Mesh;
        Assert.Equal(24, mesh.Polygons.Count);
        Assert.All(mesh.Polygons, p => Assert.Equal(3, p.Count));
        // Duplicate points are merged: the two boxes' sixteen corners.
        Assert.Equal(16, mesh.Points.Count);
        foreach (var p in doc.Layers.SelectMany(l => l.Mesh.Points)) Assert.Contains(mesh.Points, q => Vec3.Near(p, q, 1e-6));
        // Faces still face out of their box.
        foreach (var poly in mesh.Polygons)
        {
            var c = mesh.Centroid(poly);
            var centre = c.X < 2 ? new Vec3(0, 1, 0) : new Vec3(3.5, 0.5, 1.5);
            Assert.True(Vec3.Dot(mesh.Normal(poly), c - centre) > 0);
        }
    }

    [Fact]
    public void Z_is_up_and_facets_wind_anticlockwise_round_their_normals()
    {
        var doc = new Document();
        // A LightWave triangle facing up (+Y), clockwise seen from above.
        doc.Layers[0].Mesh.AddFace(0, new Vec3(0, 2, 0), new Vec3(0, 2, 1), new Vec3(1, 2, 0));
        Assert.Equal(new Vec3(0, 1, 0), doc.Layers[0].Mesh.Normal(0));
        var tri = Assert.Single(Stl.Triangles(doc));
        Assert.Equal(new Vec3(0, 0, 1), tri.Normal);        // up is STL's +Z
        // In millimetres, as slicers read STL: 2 m up is 2000.
        Assert.All([tri.A, tri.B, tri.C], v => Assert.Equal(2000, v.Z));
        Assert.Contains(tri.A, (Vec3[])[new(0, 0, 2000), new(0, 1000, 2000), new(1000, 0, 2000)]);
        // Right-hand rule in STL's right-handed space: the winding agrees with the facet normal.
        Assert.Equal(tri.Normal, Vec3.Cross(tri.B - tri.A, tri.C - tri.A).Normalized());

        // A face toward the Front view's viewer (-Z in LightWave) faces -Y in STL: the front of the bed.
        var front = new Document();
        front.Layers[0].Mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        Assert.Equal(new Vec3(0, -1, 0), Stl.Triangles(front)[0].Normal);
    }

    [Fact]
    public void A_binary_file_whose_header_starts_with_solid_is_still_binary()
    {
        using var dir = new TempDir();
        var doc = new Document();
        doc.Layers[0].Mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        using (var s = new MemoryStream())
        {
            Stl.WriteBinary(doc, s);
            var bytes = s.ToArray();
            Encoding.ASCII.GetBytes("solid but binary").CopyTo(bytes, 0);
            File.WriteAllBytes(dir.File("tricky.stl"), bytes);
        }
        var back = ObjectFiles.Load(dir.File("tricky.stl"));
        Assert.Single(back.Layers[0].Mesh.Polygons);
        Assert.Equal(new Vec3(0, 0, -1), back.Layers[0].Mesh.Normal(0));
    }

    [Fact]
    public void Ascii_files_from_elsewhere_are_read()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("hand.stl"), """
            solid hand
              facet normal 0 0 1
                outer loop
                  vertex 0 0 0
                  vertex 1 0 0
                  vertex 1 1 0
                endloop
              endfacet
              facet normal 0 0 1
                outer loop
                  vertex 0 0 0
                  vertex 1 1 0
                  vertex 0 1 0
                endloop
              endfacet
            endsolid hand
            """);
        var mesh = ObjectFiles.Load(dir.File("hand.stl")).Layers[0].Mesh;
        Assert.Equal(4, mesh.Points.Count);
        // Millimetres in the file, metres in the Modeler: the 1 mm square is 0.001 m across.
        Assert.Contains(new Vec3(0.001, 0, 0.001), mesh.Points);
        Assert.Equal(2, mesh.Polygons.Count);
        // Facing STL's +Z (up) is LightWave's +Y.
        Assert.All(mesh.Polygons, p => Assert.Equal(new Vec3(0, 1, 0), mesh.Normal(p)));
        File.WriteAllText(dir.File("bad.stl"), "solid x\n facet normal 0 0 1\n outer loop\n vertex 0 zero 0\n");
        Assert.Throws<FormatException>(() => ObjectFiles.Load(dir.File("bad.stl")));
    }
}
