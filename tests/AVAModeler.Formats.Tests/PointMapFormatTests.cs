using AVAModeler.Core;

namespace AVAModeler.Formats.Tests;

/// <summary>Weight and colour maps in LWOB files (as LWO2-style VMAPs) and LWO2's own.</summary>
public class PointMapFormatTests
{
    [Fact]
    public void Lwob_keeps_weight_and_colour_maps_and_writes_them_the_same_again()
    {
        var doc = new Document();
        var m = doc.Layers[0].Mesh;
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0));
        m.AddPointMap("Arm", PointMapKind.Weight).SetWeight(1, 0.75);
        m.AddPointMap("Tint", PointMapKind.Rgba).SetColor(2, new Vec3(0.5, 0.25, 1), 0.5);
        var bytes = Lwob.Write(doc);
        var read = Lwob.Read(bytes);
        var rm = read.Layers[0].Mesh;
        Assert.Equal(0.75, rm.FindPointMap("Arm", PointMapKind.Weight)!.Weight(1), 6);
        var tint = rm.FindPointMap("Tint")!;
        Assert.Equal(PointMapKind.Rgba, tint.Kind);
        Assert.Equal([0.5f, 0.25f, 1f, 0.5f], tint.Values[2]);
        Assert.Equal(bytes, Lwob.Write(read));
        // An object without them is as it was: no extra chunk.
        var plain = new Document();
        plain.Layers[0].Mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0));
        Assert.DoesNotContain("VMAP", System.Text.Encoding.ASCII.GetString(Lwob.Write(plain)));
    }

    [Fact]
    public void Glb_and_obj_saves_ignore_point_maps_without_failing()
    {
        var doc = new Document();
        var m = doc.Layers[0].Mesh;
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0));
        m.AddPointMap("Arm", PointMapKind.Weight).SetWeight(0, 1);
        Assert.NotEmpty(Gltf.Write(doc, GltfPacking.Binary).Main);
    }
}

public class VertexColorFormatTests
{
    private static Document Painted()
    {
        var doc = new Document();
        var m = doc.Layers[0].Mesh;
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0));
        var map = m.AddPointMap("Paint", PointMapKind.Rgb);
        map.SetColor(0, new Vec3(1, 0, 0));
        map.SetColor(1, new Vec3(0, 1, 0));
        map.SetColor(2, new Vec3(0, 0, 1));
        doc.Surfaces[0].VertexColorMap = "Paint";
        doc.Surfaces[0].VertexColorIntensity = 0.75;
        return doc;
    }

    [Fact]
    public void Lwob_keeps_the_surfaces_vertex_colour_map()
    {
        var read = Lwob.Read(Lwob.Write(Painted()));
        Assert.Equal("Paint", read.Surfaces[0].VertexColorMap);
        Assert.Equal(0.75, read.Surfaces[0].VertexColorIntensity, 6);
        Assert.Equal(Lwob.Write(Painted()), Lwob.Write(read));
    }

    [Fact]
    public void Gltf_writes_and_reads_COLOR_0()
    {
        var doc = Painted();
        doc.Surfaces[0].VertexColorIntensity = 1;
        var (glb, _, _) = Gltf.Write(doc, GltfPacking.Binary);
        var read = Gltf.Read(glb, null, "painted.glb");
        var m = read.Layers.First(l => !l.Mesh.IsEmpty).Mesh;
        var map = m.FindPointMap(Gltf.ColorMapName)!;
        Assert.Equal(3, map.Values.Count);
        Assert.Contains(m.Points.Select((p, i) => (p, i)), x => Vec3.Near(x.p, new Vec3(0, 1, 0), 1e-6) && map.Color(x.i) is { } c && Vec3.Near(c, new Vec3(0, 1, 0), 1e-6));
        Assert.Equal(Gltf.ColorMapName, read.Surfaces.First(s => m.Polygons.Any(p => read.Surfaces[p.Surface] == s)).VertexColorMap);
        // Without a vertex colour map, no COLOR_0.
        var plain = Painted();
        plain.Surfaces[0].VertexColorMap = null;
        Assert.DoesNotContain("COLOR_0", System.Text.Encoding.UTF8.GetString(Gltf.Write(plain, GltfPacking.Embedded).Main));
    }

    [Fact]
    public void Obj_vertex_colours_go_both_ways()
    {
        var w = new StringWriter();
        Obj.Write(Painted(), w);
        Assert.Contains("v 0 1 0 0 1 0", w.ToString().Replace("-0", "0"));
        var read = Obj.Read(new StringReader(w.ToString()));
        var map = read.Layers[0].Mesh.FindPointMap(Obj.ColorMapName)!;
        Assert.Equal(new Vec3(0, 0, 1), map.Color(2));
        Assert.Equal(Obj.ColorMapName, read.Surfaces[0].VertexColorMap);
        // A plain OBJ has none.
        var plain = Obj.Read(new StringReader("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"));
        Assert.Empty(plain.Layers[0].Mesh.PointMaps);
    }
}
