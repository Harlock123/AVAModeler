using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>SubPatch objects in Layout: subdivided at the display level in the views and the render level when rendered.</summary>
public class SubPatchSceneTests
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
    public void Views_get_the_display_level_and_renders_the_render_level()
    {
        var scene = new Scene();
        var o = scene.AddObject("Blob", Cube(true));
        Assert.True(o.HasSubPatches);
        Assert.Equal(6 * 16, scene.Evaluate(1).Objects[0].Mesh.Polygons.Count);
        Assert.Equal(6 * 64, scene.Evaluate(1, render: true).Objects[0].Mesh.Polygons.Count);
        o.SubPatchLevel = 0;
        Assert.Equal(6, scene.Evaluate(1).Objects[0].Mesh.Polygons.Count);
        // A plain object is as it always was.
        var plain = new Scene();
        plain.AddObject("Box", Cube(false));
        Assert.False(plain.Objects.First().HasSubPatches);
        Assert.Equal(6, plain.Evaluate(1, render: true).Objects[0].Mesh.Polygons.Count);
    }

    [Fact]
    public void Only_a_merged_objects_SubPatch_layers_are_subdivided()
    {
        var doc = Cube(true);
        var second = Cube(false).Layers[0].Mesh;
        doc.Layers.Add(new Layer { Mesh = second });
        var scene = new Scene();
        scene.AddObject("Two", doc);
        Assert.Equal(6 * 16 + 6, scene.Evaluate(1).Objects[0].Mesh.Polygons.Count);
    }

    [Fact]
    public void Levels_round_trip_in_avascene_lws_snapshots_and_copies()
    {
        var scene = new Scene();
        var o = scene.AddObject("Blob", Cube(true));
        o.SourcePath = "/objects/blob.lwo";
        o.SubPatchLevel = 1;
        o.SubPatchRenderLevel = 4;

        var (json, _) = SceneJson.Read(SceneJson.Write(scene, embedMeshes: true));
        var back = json.Objects.First();
        Assert.Equal((1, 4), (back.SubPatchLevel, back.SubPatchRenderLevel));
        Assert.True(back.HasSubPatches);
        Assert.DoesNotContain("SubPatch", SceneJson.Write(new Scene()));

        var text = LwsWriter.Write(scene);
        Assert.Contains("SubPatchLevel 1 4", text);
        var (lws, _) = LwsReader.Read(text, _ => Cube(true));
        Assert.Equal((1, 4), (lws.Objects.First().SubPatchLevel, lws.Objects.First().SubPatchRenderLevel));
        var plain = new Scene();
        plain.AddObject("Box", Cube(false)).SourcePath = "/objects/box.lwo";
        Assert.DoesNotContain("SubPatchLevel", LwsWriter.Write(plain));

        var snap = scene.Snapshot();
        o.SubPatchLevel = 3;
        snap.Restore();
        Assert.Equal(1, o.SubPatchLevel);
        Assert.Equal(4, scene.Copy().Objects.First().SubPatchRenderLevel);
    }
}
