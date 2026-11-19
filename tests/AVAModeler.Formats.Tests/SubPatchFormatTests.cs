using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Formats.Tests;

/// <summary>AVA's AVSP chunk keeps SubPatch layers in .lwo; every other format gets the surface frozen.</summary>
public class SubPatchFormatTests
{
    private static Document Cube(bool subPatch)
    {
        var doc = new Document();
        var m = doc.Layers[0].Mesh;
        for (int i = 0; i < 8; i++) m.Points.Add(new Vec3((i & 1) == 0 ? -0.5 : 0.5, (i & 2) == 0 ? -0.5 : 0.5, (i & 4) == 0 ? -0.5 : 0.5));
        foreach (var f in new[] { new[] { 0, 4, 6, 2 }, [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6] }) m.Polygons.Add(new Polygon(f));
        m.SubPatch = subPatch;
        m.Touch();
        return doc;
    }

    [Fact]
    public void A_SubPatch_layer_and_its_creases_round_trip_in_lwo_and_others_write_no_chunk()
    {
        var doc = Cube(true);
        doc.Layers[0].Mesh.AddPointMap(SubPatches.CreaseMap, PointMapKind.Weight).SetWeight(0, 1);
        var back = Lwob.Read(Lwob.Write(doc));
        Assert.True(back.Layers[0].Mesh.SubPatch);
        Assert.Equal(1, back.Layers[0].Mesh.FindPointMap(SubPatches.CreaseMap)!.Weight(0), 6);
        Assert.DoesNotContain("AVSP", System.Text.Encoding.ASCII.GetString(Lwob.Write(Cube(false))));
        Assert.False(Lwob.Read(Lwob.Write(Cube(false))).Layers[0].Mesh.SubPatch);
    }

    [Fact]
    public void Formats_without_SubPatches_get_the_surface_and_the_document_is_left_alone()
    {
        var doc = Cube(true);
        var frozen = ObjectFiles.FrozenSubPatches(doc);
        Assert.NotSame(doc, frozen);
        Assert.Equal(6 * 16, frozen.Layers[0].Mesh.Polygons.Count);
        Assert.False(frozen.Layers[0].Mesh.SubPatch);
        Assert.Equal(6, doc.Layers[0].Mesh.Polygons.Count);
        Assert.True(doc.Layers[0].Mesh.SubPatch);
        var plain = Cube(false);
        Assert.Same(plain, ObjectFiles.FrozenSubPatches(plain));

        string dir = Directory.CreateTempSubdirectory("ava-subpatch-").FullName;
        try
        {
            string stl = Path.Combine(dir, "cube.stl");
            ObjectFiles.Save(doc, stl);
            Assert.Equal(6 * 16 * 2, ObjectFiles.Load(stl).Layers[0].Mesh.Polygons.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
