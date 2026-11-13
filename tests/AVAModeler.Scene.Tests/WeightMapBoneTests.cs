using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Bones that follow weight maps, skelegons that find theirs, and the files that keep them.</summary>
public class WeightMapBoneTests
{
    /// <summary>Two points side by side, a bone along Z between them, and a map weighting only the first.</summary>
    private static (Scene Scene, ObjectItem Object, Bone Bone) Rig()
    {
        var mesh = new Mesh();
        mesh.AddPolygon([mesh.AddPoint(new Vec3(0.1, 0, 0.5))]);
        mesh.AddPolygon([mesh.AddPoint(new Vec3(-0.1, 0, 0.5))]);
        mesh.AddPointMap("Arm", PointMapKind.Weight).SetWeight(0, 1);
        var scene = new Scene();
        var o = scene.AddObject("Points", Doc(mesh));
        var bone = scene.AddBone(o, "Arm");
        bone.SetRotation(10, new Vec3(90, 0, 0));
        return (scene, o, bone);
    }

    [Fact]
    public void A_bone_with_a_weight_map_moves_only_the_points_in_it()
    {
        var (_, o, bone) = Rig();
        // By distance both points follow the one bone.
        var bent = o.DeformedMesh(10).Points;
        Assert.True(bent[0].X > 0.4 && bent[1].X > 0.4);
        bone.WeightMap = "Arm";
        o.Scene!.Notify();
        bent = o.DeformedMesh(10).Points;
        AssertNear(new Vec3(0.5, 0, -0.1), bent[0], 1e-9);
        AssertNear(new Vec3(-0.1, 0, 0.5), bent[1], 1e-9);
        // Map times falloff: the same here (one bone), but a missing map leaves distance weighting.
        bone.WeightMapOnly = false;
        o.Scene.Notify();
        AssertNear(new Vec3(0.5, 0, -0.1), o.DeformedMesh(10).Points[0], 1e-9);
        bone.WeightMap = "Nope";
        o.Scene.Notify();
        Assert.True(o.DeformedMesh(10).Points[1].X > 0.4);
    }

    [Fact]
    public void Skelegons_find_the_weight_maps_of_their_names()
    {
        var doc = new Document();
        int arm = doc.SurfaceIndex("Skelegon_Arm");
        var m = doc.Layers[0].Mesh;
        int a = m.AddPoint(Vec3.Zero), b = m.AddPoint(new Vec3(0, 0, 1));
        m.AddPolygon([a, b], arm);
        m.AddPointMap("Arm", PointMapKind.Weight).SetWeight(a, 1);
        var scene = new Scene();
        var o = scene.AddObject("Figure", doc);
        var bones = Skelegons.CreateBones(scene, o);
        Assert.Equal("Arm", bones.Single().WeightMap);
    }

    [Fact]
    public void Lws_and_avascene_keep_a_bones_weight_map()
    {
        var (scene, o, bone) = Rig();
        bone.WeightMap = "Arm";
        bone.WeightMapOnly = false;
        o.SourcePath = "/objects/points.lwo";
        var warnings = new List<string>();
        string text = LwsWriter.Write(scene, warnings);
        Assert.Contains("BoneWeightMapName Arm", text);
        Assert.Contains("BoneWeightMapOnly 0", text);
        var (read, _) = LwsReader.Read(text, _ => o.Document!);
        Assert.Equal("Arm", read.Bones.Single().WeightMap);
        Assert.False(read.Bones.Single().WeightMapOnly);

        var json = SceneJson.Read(SceneJson.Write(scene, embedMeshes: true));
        var jb = json.Scene.Bones.Single();
        Assert.Equal("Arm", jb.WeightMap);
        Assert.False(jb.WeightMapOnly);
        // The embedded mesh keeps the map itself.
        Assert.Equal(1, json.Scene.Objects.Single().BaseMesh().FindPointMap("Arm")!.Weight(0));
        // Undo (a snapshot) puts the setting back; a copy keeps it.
        var snap = scene.Snapshot();
        bone.WeightMap = null;
        snap.Restore();
        Assert.Equal("Arm", bone.WeightMap);
        Assert.Equal("Arm", scene.Copy().Bones.Single().WeightMap);
    }
}
