using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Copying items into a scene, from the same scene (cloning) or from another (merging), with their links.</summary>
public class CopyItemsTests
{
    private static Document Ball()
    {
        var doc = new Document();
        doc.Current.Mesh.Append(Sphere(Vec3.Zero, 1, 8, 4));
        return doc;
    }

    [Fact]
    public void A_copy_in_the_same_scene_keeps_motion_settings_parent_and_bones()
    {
        var scene = Scene.CreateDefault();
        var rig = scene.AddNull("Rig");
        var ball = scene.AddObject("Ball", Ball());
        ball.Parent = rig;
        ball.CastShadows = false;
        ball.SetPosition(1, new Vec3(0, 0, 0));
        ball.SetPosition(30, new Vec3(2, 0, 0));
        var bone = scene.AddBone(ball, "Bone", Vec3.Zero, new Vec3(0, -90, 0), 1.5);
        int version = scene.Version;
        int changes = 0;
        scene.Changed += () => changes++;

        var map = scene.CopyItems([ball]);

        Assert.Equal(1, changes);
        Assert.True(scene.Version > version);
        var copy = Assert.IsType<ObjectItem>(map[ball]);
        Assert.NotSame(ball, copy);
        Assert.Same(ball.Document, copy.Document);
        Assert.Same(rig, copy.Parent);
        Assert.False(copy.CastShadows);
        AssertNear(ball.PositionAt(15.5), copy.PositionAt(15.5));
        var boneCopy = Assert.IsType<Bone>(map[bone]);
        Assert.Same(copy, boneCopy.Owner);
        Assert.Contains(boneCopy, copy.Bones);
        Assert.Single(ball.Bones);
        Assert.Equal(1.5, boneCopy.RestLength);
        // The copy's keys are its own.
        copy.SetPosition(30, new Vec3(5, 0, 0));
        AssertNear(new Vec3(2, 0, 0), ball.PositionAt(30));
    }

    [Fact]
    public void Items_from_another_scene_come_with_their_links_among_themselves()
    {
        var other = new Scene();
        var pivot = other.AddNull("Pivot");
        var lamp = other.AddLight("Lamp", LightType.Spot);
        lamp.Parent = pivot;
        lamp.ConeAngle = 12;
        lamp.Intensity.AddKey(1, 0);
        lamp.Intensity.AddKey(20, 2);
        var stray = other.AddNull("Stray");
        var cam = other.AddCamera("Cam");
        cam.Parent = stray;

        var scene = Scene.CreateDefault();
        var map = scene.CopyItems([pivot, lamp, cam]);

        var lampCopy = Assert.IsType<LightItem>(map[lamp]);
        Assert.Same(map[pivot], lampCopy.Parent);
        Assert.Same(scene, lampCopy.Scene);
        Assert.Equal(12, lampCopy.ConeAngle);
        AssertNear(lamp.Intensity.Evaluate(10.5), lampCopy.Intensity.Evaluate(10.5));
        Assert.Equal(lamp.Intensity.Keys, lampCopy.Intensity.Keys);
        // The camera's parent wasn't brought, so it has none here; the other scene is untouched.
        Assert.Null(map[cam].Parent);
        Assert.Same(stray, cam.Parent);
        Assert.Equal(4, other.Items.Count);
        Assert.Equal(5, scene.Items.Count);
    }

    [Fact]
    public void A_bone_without_its_object_needs_the_object_here()
    {
        var other = new Scene();
        var ball = other.AddObject("Ball", Ball());
        var bone = other.AddBone(ball, "Bone");
        var scene = new Scene();
        Assert.Throws<ArgumentException>(() => scene.CopyItems([bone]));
        Assert.Empty(scene.Items);
        // In its own scene it goes on the same object.
        var copy = (Bone)other.CopyItems([bone])[bone];
        Assert.Same(ball, copy.Owner);
        Assert.Equal(2, ball.Bones.Count);
    }

    [Fact]
    public void A_path_and_an_ik_goal_point_at_the_copies_or_stay_on_the_originals()
    {
        var scene = new Scene();
        var a = scene.AddNull("A");
        var b = scene.AddNull("B");
        b.SetPosition(0, new Vec3(4, 0, 0));
        var rider = scene.AddNull("Rider");
        rider.Path = new FollowPath { Points = [a, b] };
        var arm = scene.AddObject("Arm", Ball());
        var bone = scene.AddBone(arm, "Upper");
        var goal = scene.AddNull("Goal");
        bone.IKGoal = goal;

        var map = scene.CopyItems([rider, arm, a]);

        // The path's first point was copied too, its second wasn't (it stays the original).
        Assert.Equal([map[a], b], map[rider].Path!.Points);
        Assert.Same(goal, ((Bone)map[bone]).IKGoal);
    }

    [Fact]
    public void Copy_still_makes_a_scene_that_shares_nothing_that_changes()
    {
        var scene = Scene.CreateDefault();
        var ball = scene.AddObject("Ball", Ball());
        var bone = scene.AddBone(ball, "Bone");
        var goal = scene.AddNull("Goal");
        bone.IKGoal = goal;
        ball.SetPosition(10, new Vec3(1, 2, 3));
        var copy = scene.Copy();
        Assert.Equal(scene.Items.Select(i => i.Name), copy.Items.Select(i => i.Name));
        var ballCopy = (ObjectItem)copy.Find("Ball")!;
        Assert.Same(copy.Find("Goal"), ((Bone)copy.Find("Bone")!).IKGoal);
        Assert.Same(ballCopy, ((Bone)copy.Find("Bone")!).Owner);
        Assert.Equal(scene.CurrentCamera!.Name, copy.CurrentCamera!.Name);
        Assert.NotSame(scene.CurrentCamera, copy.CurrentCamera);
    }
}
