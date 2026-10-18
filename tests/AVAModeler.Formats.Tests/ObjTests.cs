using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

public class ObjTests
{
    private static Document Read(string text, Func<string, TextReader?>? mtl = null) => Obj.Read(new StringReader(text), mtl);

    [Fact]
    public void TriangleComesInFacingTheSameWay()
    {
        // Anticlockwise from +Z in OBJ's right-handed space, so it faces the viewer: -Z in LightWave's space.
        var doc = Read("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        var mesh = doc.Layers[0].Mesh;
        Assert.Equal(new Vec3(0, 0, -1), mesh.Normal(0));

        var tilted = Read("v 0 0 1\nv 1 0 2\nv 0 1 3\nf 1 2 3\n").Layers[0].Mesh;
        Assert.Equal(new Vec3(1, 0, -2), tilted.Points[1]);
        Assert.True(tilted.Normal(0).Z < 0);
    }

    [Fact]
    public void ReadsEveryFaceIndexForm()
    {
        var doc = Read("""
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            vt 0 0
            vn 0 0 1
            f 1 2 3
            f 1/1 2/1 3/1
            f 1//1 2//1 4//1
            f 1/1/1 3/1/1 4/1/1
            f -4 -3 -2 -1
            f -4/1/1 -2//1 -1/1
            """);
        var polys = doc.Layers[0].Mesh.Polygons;
        Assert.Equal(4, doc.Layers[0].Mesh.Points.Count);
        Assert.Equal([[2, 1, 0], [2, 1, 0], [3, 1, 0], [3, 2, 0], [3, 2, 1, 0], [3, 2, 0]], polys.Select(p => p.Points));
        Assert.All(polys, p => Assert.Equal(0, p.Surface));
        Assert.Equal(["Default"], doc.Surfaces.Select(s => s.Name));
    }

    [Fact]
    public void LinesAndPointsBecomeSmallPolygons()
    {
        var mesh = Read("v 0 0 0\nv 1 0 0\nv 2 0 0\nv 3 0 0\nl 1 2 3\nl 4/1 1/1\np 2 4\n").Layers[0].Mesh;
        Assert.Equal([[0, 1], [1, 2], [3, 0], [1], [3]], mesh.Polygons.Select(p => p.Points));
    }

    [Fact]
    public void GroupsBecomeLayersInOrder()
    {
        var doc = Read("""
            o Ship
            g Hull
            v 0 0 0
            v 1 0 0
            v 0 1 0
            f 1 2 3
            g Wing
            v 5 0 0
            f 2 3 4
            g Hull
            p 4
            g Loose
            v 9 9 9
            """);
        Assert.Equal(("Hull", 4), (doc.Layers[0].Name, doc.Layers[0].Mesh.Points.Count));
        Assert.Equal(("Wing", 3), (doc.Layers[1].Name, doc.Layers[1].Mesh.Points.Count));
        Assert.Equal(new Vec3(5, 0, 0), doc.Layers[1].Mesh.Points[2]);
        Assert.Equal([[3]], doc.Layers[0].Mesh.Polygons.Skip(1).Select(p => p.Points));
        // A vertex nobody uses stays in the group it was declared in.
        Assert.Equal(("Loose", new Vec3(9, 9, -9)), (doc.Layers[2].Name, doc.Layers[2].Mesh.Points.Single()));
        Assert.Equal([LayerState.Foreground, LayerState.Off, LayerState.Off], doc.Layers.Take(3).Select(l => l.State));

        var many = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"g G{i}\nv {i} 0 0\np -1"));
        var grown = Read(many);
        Assert.Equal(12, grown.Layers.Count);
        Assert.Equal("G11", grown.Layers[11].Name);
    }

    [Fact]
    public void MaterialsComeFromTheMtlFile()
    {
        const string mtl = """
            # materials
            newmtl Red Paint
            Kd 1 0 0
            Ks 0.5 0.25 0
            Ns 128
            d 0.75
            newmtl Glow
            Kd 0 0 1
            Ke 0.5 0.5 0.5
            Tr 0.2
            newmtl Unused
            """;
        string? asked = null;
        var doc = Read("mtllib my mats.mtl\nv 0 0 0\nusemtl Glow\np 1\nusemtl Red Paint\np 1\nusemtl Glow\np 1\n",
            name => { asked ??= name; return name == "my mats.mtl" ? new StringReader(mtl) : null; });
        Assert.Equal("my mats.mtl", asked);
        Assert.Equal(["Glow", "Red Paint"], doc.Surfaces.Select(s => s.Name));
        Assert.Equal([0, 1, 0], doc.Layers[0].Mesh.Polygons.Select(p => p.Surface));
        var red = doc.Surfaces[1];
        Assert.Equal((new Rgb(255, 0, 0), 0.5, 128.0, 0.25), (red.Color, red.Specular, red.Glossiness, red.Transparency));
        var glow = doc.Surfaces[0];
        Assert.Equal((new Rgb(0, 0, 255), 0.5, 0.2), (glow.Color, glow.Luminosity, glow.Transparency));
    }

    [Fact]
    public void ExportThenImportKeepsGeometryAndSurfaces()
    {
        var doc = new Document();
        doc.Surfaces[0].Color = new Rgb(10, 200, 30);
        doc.Surfaces.Add(new Surface("Chrome") { Specular = 0.75, Glossiness = 256, Transparency = 0.25, Luminosity = 0.5 });
        var a = doc.Layers[0];
        a.Name = "Body";
        a.Mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0.1), new Vec3(1, 1, -0.3), new Vec3(1, 0, 0));
        a.Mesh.AddPolygon([0, 2], 1);
        a.Mesh.AddPolygon([3], 1);
        a.Mesh.AddFace(0, new Vec3(0.1, 0.2, 0.3), new Vec3(-4, 5, 6.5), new Vec3(7, -8, 9));
        doc.Layers[4].Mesh.AddFace(1, new Vec3(3, 3, 3), new Vec3(3, 4, 3), new Vec3(4, 3, 3));

        var obj = new StringWriter();
        var mtl = new StringWriter();
        Obj.Write(doc, obj, mtl, "out.mtl");
        Assert.Contains("mtllib out.mtl", obj.ToString());
        Assert.Contains("\nl 1 3\n", obj.ToString());
        Assert.Contains("\np 4\n", obj.ToString());

        var back = Read(obj.ToString(), name => name == "out.mtl" ? new StringReader(mtl.ToString()) : null);
        Assert.Equal("Body", back.Layers[0].Name);
        Assert.Equal(a.Mesh.Points, back.Layers[0].Mesh.Points);
        Assert.Equal(a.Mesh.Polygons.Select(p => p.Points), back.Layers[0].Mesh.Polygons.Select(p => p.Points));
        Assert.Equal(a.Mesh.Normal(0), back.Layers[0].Mesh.Normal(0));
        Assert.Equal("Layer 5", back.Layers[1].Name);
        Assert.Equal(doc.Layers[4].Mesh.Points, back.Layers[1].Mesh.Points);
        Assert.Equal(["Default", "Chrome"], back.Surfaces.Select(s => s.Name));
        Assert.Equal([0, 1, 1, 0], back.Layers[0].Mesh.Polygons.Select(p => p.Surface));
        Assert.Equal(new Rgb(10, 200, 30), back.Surfaces[0].Color);
        var chrome = back.Surfaces[1];
        Assert.Equal((0.75, 256.0, 0.25, 0.5), (chrome.Specular, chrome.Glossiness, chrome.Transparency, chrome.Luminosity));
    }

    [Theory]
    [InlineData("v 0 0\n", "line 1")]
    [InlineData("v 0 0 0\nv 1 0 0\n\n# fine\nf 1 2 3\n", "line 5")]
    [InlineData("v 0 0 0\nf 1 0 1\n", "line 2")]
    [InlineData("v 0 0 0\nf -2 1 1\n", "vertex -2")]
    [InlineData("v 0 0 x\n", "\"x\"")]
    [InlineData("v 0 0 0\nl 1\n", "line 2")]
    public void RejectsBadLines(string text, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => Read(text)).Message);

    [Fact]
    public void ContinuedLinesAndCommentsAreHandled()
    {
        var mesh = Read("v 0 0 0 # origin\nv 1 0 0\nv 0 1 0\nf 1 \\\n 2 3\n").Layers[0].Mesh;
        Assert.Equal([2, 1, 0], mesh.Polygons.Single().Points);
    }
}
