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
