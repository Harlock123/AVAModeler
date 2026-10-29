using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Putting a scene back as it was, copying it for another thread, aiming items, and Batch.</summary>
public class SnapshotTests
{
    private static Document Cube()
    {
        var doc = new Document();
        doc.Current.Mesh.Append(Sphere(Vec3.Zero, 1, 8, 4));
        return doc;
    }

    [Fact]
    public void A_snapshot_puts_back_the_items_their_channels_and_settings()
    {
        var scene = Scene.CreateDefault();
        var obj = scene.AddObject("Ball", Cube());
        var bone = scene.AddBone(obj, "Bone", new Vec3(0, 0, 0), new Vec3(0, -90, 0), 2);
        var light = scene.Lights.Single();
        var cam = scene.Cameras.Single();
        obj.SetPosition(1, new Vec3(0, 0, 0));
        obj.SetPosition(30, new Vec3(1, 2, 3));
        var keysBefore = obj[Channel.PositionX].Keys.ToList();
        var snap = scene.Snapshot();

        // Change everything a call might.
        var extra = scene.AddNull("Extra");
        light.Parent = extra;
        scene.Remove(obj);
        light.Color = new Vec3(1, 0, 0);
        light.Intensity.SetConstant(3);
        light.Name = "Renamed";
        cam.Zoom.AddKey(10, 6);
        cam.Width = 100;
        scene.FirstFrame = 5;
        scene.AmbientIntensity = 0.9;
        scene.CurrentCamera = scene.AddCamera("Other");
        Assert.DoesNotContain(obj, scene.Items);

        int raised = 0;
        scene.Changed += () => raised++;
        snap.Restore();
        Assert.Equal(1, raised);
        Assert.Equal(["Camera", "Light", "Ball", "Bone"], scene.Items.Select(i => i.Name));
        Assert.Same(obj, scene.Items[2]);
        Assert.Same(scene, obj.Scene);
        Assert.Same(scene, bone.Scene);
        Assert.Equal([bone], obj.Bones);
        Assert.Null(extra.Scene);
        Assert.Null(light.Parent);
        Assert.Equal(Vec3.One, light.Color);
        Assert.Equal(1, light.Intensity.Evaluate(1));
        Assert.Equal("Light", light.Name);
        Assert.True(cam.Zoom.IsConstant);
        Assert.Equal(640, cam.Width);
        Assert.Same(cam, scene.CurrentCamera);
        Assert.Equal(1, scene.FirstFrame);
        Assert.Equal(0.25, scene.AmbientIntensity);
        Assert.Equal(keysBefore, obj[Channel.PositionX].Keys);
    }

    [Fact]
    public void A_snapshot_puts_back_morphs_and_displacements_with_their_settings()
    {
        var scene = Scene.CreateDefault();
        var doc = Cube();
        var obj = scene.AddObject("Ball", doc);
        var target = doc.Current.Mesh.Clone();
        var morph = obj.AddMorph(new MorphTarget("Squash", target));
        morph.Weight.AddKey(1, 0);
        morph.Weight.AddKey(30, 1);
        var wave = obj.AddDisplacement(new WaveDisplacement { Shape = WaveShape.Ripple, Center = new Vec3(1, 0, 0), Wavelength = 2, Speed = 0.1, Phase = 45 });
        wave.Amplitude.SetConstant(0.3);
        var before = SceneJson.Write(scene, embedMeshes: true);
        var snap = scene.Snapshot();

        // Every setting a morph and a displacement have, and the lists themselves.
        morph.Name = "Stretch";
        morph.Target = new Mesh();
        morph.Weight.SetConstant(0.5);
        (wave.Shape, wave.Direction, wave.Along, wave.Center) = (WaveShape.Wave, Axis.X, Axis.Z, Vec3.Zero);
        (wave.Wavelength, wave.Speed, wave.Phase) = (9, 2, 0);
        wave.Amplitude.AddKey(10, 4);
        obj.AddMorph(new MorphTarget("Extra", target));
        obj.RemoveDisplacement(wave);
        obj.AddDisplacement(new WaveDisplacement());
        scene.Notify();

        snap.Restore();
        Assert.Equal(before, SceneJson.Write(scene, embedMeshes: true));
        Assert.Same(morph, obj.Morphs.Single());
        Assert.Same(target, morph.Target);
        Assert.Equal("Squash", morph.Name);
        Assert.Equal([0.0, 1.0, 30.0], morph.Weight.Keys.Select(k => k.Frame));
        Assert.Same(wave, obj.Displacements.Single());
        Assert.Equal((WaveShape.Ripple, Axis.Y, Axis.X, new Vec3(1, 0, 0), 2.0, 0.1, 45.0),
            (wave.Shape, wave.Direction, wave.Along, wave.Center, wave.Wavelength, wave.Speed, wave.Phase));
        Assert.True(wave.Amplitude.IsConstant);
        Assert.Equal(0.3, wave.Amplitude.Evaluate(10));
    }

    [Fact]
    public void Batch_raises_changed_once_and_only_if_something_changed()
    {
        var scene = Scene.CreateDefault();
        int raised = 0;
        scene.Changed += () => raised++;
        scene.Batch(() => { });
        Assert.Equal(0, raised);
        int v = scene.Version;
        scene.Batch(() =>
        {
            scene.AddNull("A");
            scene.AddNull("B");
            scene.Items[0].SetPosition(0, Vec3.One);
        });
        Assert.Equal(1, raised);
        Assert.True(scene.Version > v + 2);
    }

    [Fact]
    public void A_copy_shares_nothing_that_changes()
    {
        var scene = Scene.CreateDefault();
        var doc = Cube();
        var obj = scene.AddObject("Ball", doc);
        var other = scene.AddObject("Ball again", doc, 0);
        var bone = scene.AddBone(obj, "Bone", Vec3.Zero, Vec3.Zero, 1);
        var n = scene.AddNull("Pivot");
        var cam = scene.AddCamera("Second");
        cam.Parent = n;
        cam.Zoom.AddKey(1, 2);
        cam.Zoom.AddKey(20, 4);
        scene.CurrentCamera = cam;
        obj.AddMorph(new MorphTarget("Squash", doc.Current.Mesh.Clone()));
        var copies = new List<Document>();
        var copy = scene.Copy(d => { var c = d.Copy(); copies.Add(c); return c; });

        Assert.Equal(scene.Items.Select(i => (i.Kind, i.Name)), copy.Items.Select(i => (i.Kind, i.Name)));
        Assert.Single(copies);
        var objCopy = (ObjectItem)copy.Items[2];
        Assert.Same(copies[0], objCopy.Document);
        Assert.Same(copies[0], ((ObjectItem)copy.Items[3]).Document);
        Assert.Equal(0, ((ObjectItem)copy.Items[3]).Layer);
        Assert.Equal("Second", copy.CurrentCamera!.Name);
        Assert.Same(copy.Items[5], copy.CurrentCamera.Parent);
        Assert.Equal(objCopy, ((Bone)copy.Items[4]).Owner);
        Assert.Single(objCopy.Bones);
        Assert.Single(objCopy.Morphs);
        Assert.Equal(cam.Zoom.Evaluate(10), copy.CurrentCamera.Zoom.Evaluate(10));
        // Changing the original leaves the copy alone.
        cam.Zoom.SetConstant(9);
        n.SetPosition(0, new Vec3(5, 0, 0));
        bone.RestLength = 7;
        doc.Current.Mesh = new Mesh();
        Assert.NotEqual(9, copy.CurrentCamera.Zoom.Evaluate(10));
        Assert.Equal(Vec3.Zero, copy.Items[5].PositionAt(1));
        Assert.Equal(1, ((Bone)copy.Items[4]).RestLength);
        Assert.NotEmpty(objCopy.BaseMesh().Points);
        // Both render the same frame.
        Assert.Equal(copy.Evaluate(1).Objects.Count, scene.Evaluate(1).Objects.Count);
    }

    [Theory]
    [InlineData(3, 2, -4, 0, 0.5, 0)]
    [InlineData(-1, 5, 2, 1, 0, 1)]
    [InlineData(0, 0, 0, 0, -3, 0.001)]
    public void Rotation_toward_points_the_z_axis_at_the_target(double x, double y, double z, double tx, double ty, double tz)
    {
        var scene = Scene.CreateDefault();
        var cam = scene.Cameras.Single();
        cam.SetPosition(0, new Vec3(x, y, z));
        cam.SetRotation(0, new Vec3(0, 0, 15));
        var target = new Vec3(tx, ty, tz);
        cam.SetRotation(0, cam.RotationToward(target, 1));
        var look = cam.CameraToWorld(1).TransformVector(Vec3.UnitZ);
        AssertNear((target - new Vec3(x, y, z)).Normalized(), look, 1e-9);
        Assert.Equal(15, cam.RotationAt(1).Z, 9);
    }

    [Fact]
    public void Rotation_toward_allows_for_the_parent_and_keeps_heading_near_the_last()
    {
        var scene = Scene.CreateDefault();
        var rig = scene.AddNull("Rig");
        rig.SetPosition(0, new Vec3(1, 0, 1));
        rig.SetRotation(0, new Vec3(70, 10, -20));
        rig.SetScale(0, new Vec3(2, 1, 0.5));
        var cam = scene.Cameras.Single();
        cam.Parent = rig;
        cam.SetPosition(0, new Vec3(0, 1, -3));
        var target = new Vec3(-2, 0.5, 4);
        cam.SetRotation(0, cam.RotationToward(target, 1));
        var eye = cam.CameraToWorld(1).TransformPoint(Vec3.Zero);
        AssertNear((target - eye).Normalized(), cam.CameraToWorld(1).TransformVector(Vec3.UnitZ), 1e-9);

        // Heading 350 now: facing a point a little to the right comes out as about 370, not 10.
        var light = scene.Lights.Single();
        light.SetPosition(0, Vec3.Zero);
        light.SetRotation(0, new Vec3(350, 0, 0));
        var hpb = light.RotationToward(Rotations.Forward(new Vec3(10, 0, 0)), 1);
        Assert.Equal(370, hpb.X, 9);
        Assert.Throws<ArgumentException>(() => light.RotationToward(Vec3.Zero, 1));
    }

    [Fact]
    public void Lws_names_an_object_by_its_documents_file_when_it_has_no_source()
    {
        var scene = new Scene();
        var doc = Cube();
        doc.FilePath = "/models/ball.lwo";
        scene.AddObject("Ball", doc);
        var warnings = new List<string>();
        Assert.Contains("LoadObject /models/ball.lwo", LwsWriter.Write(scene, warnings));
        Assert.Empty(warnings);
    }
}
