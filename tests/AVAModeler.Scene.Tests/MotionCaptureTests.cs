using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>BVH files read, and retargeted onto an object's bones.</summary>
public class MotionCaptureTests
{
    /// <summary>
    /// Hips 100 cm up, a spine, and a left arm out along +X ending 30 cm on. Frame 1 is the rest pose; frame 2 lifts the
    /// arm straight up (90° about Z) and steps the hips 10 cm along +X and 20 along +Z.
    /// </summary>
    private const string Capture = """
        HIERARCHY
        ROOT mixamorig:Hips
        {
          OFFSET 0 100 0
          CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation
          JOINT mixamorig:Spine
          {
            OFFSET 0 20 0
            CHANNELS 3 Zrotation Xrotation Yrotation
            JOINT mixamorig:LeftArm
            {
              OFFSET 5 10 0
              CHANNELS 3 Zrotation Xrotation Yrotation
              End Site
              {
                OFFSET 30 0 0
              }
            }
          }
        }
        MOTION
        Frames: 2
        Frame Time: 0.0333333
        0 100 0 0 0 0   0 0 0   0 0 0
        10 100 20 0 0 0   0 0 0   90 0 0
        """;

    /// <summary>A rig at rest in the capture's pose: hips and spine up +Y, the arm out along +X, at a hundredth the size.</summary>
    private static (Scene, ObjectItem, Bone Hips, Bone Spine, Bone Arm) Rig()
    {
        var scene = new Scene();
        var o = scene.AddObject("Figure", Doc(Square(Vec3.Zero, 1, Vec3.UnitZ)));
        var hips = scene.AddBone(o, "Hips", new Vec3(0, 1, 0), new Vec3(0, -90, 0), 0.2);
        // Children are placed in their parent's axes: up the parent is its +Z.
        var spine = scene.AddBone(o, "Spine", new Vec3(0, 0, 0.2), Vec3.Zero, 0.1);
        spine.Parent = hips;
        var arm = scene.AddBone(o, "LeftArm", new Vec3(0, 0.05, 0.1), new Vec3(90, 0, 0), 0.3);
        arm.Parent = spine;
        return (scene, o, hips, spine, arm);
    }

    [Fact]
    public void A_bvh_file_is_read_joint_by_joint_with_its_frames()
    {
        var bvh = BvhFile.Parse(Capture);
        Assert.Equal(["mixamorig:Hips", "mixamorig:Spine", "mixamorig:LeftArm"], bvh.Joints.Select(j => j.Name));
        Assert.Equal(6, bvh.Root.Channels.Count);
        Assert.Equal(new Vec3(30, 0, 0), bvh.Joints[2].EndSite);
        Assert.Equal(2, bvh.Frames.Count);
        Assert.Equal(1 / 30.0, bvh.FrameTime, 5);
        Assert.Equal(new Vec3(10, 100, 20), bvh.Position(bvh.Root, 1));
        Assert.Throws<InvalidDataException>(() => BvhFile.Parse("HIERARCHY ROOT A { OFFSET 0 0 0 CHANNELS 1 Wobble }"));
        Assert.Throws<InvalidDataException>(() => BvhFile.Parse("HIERARCHY ROOT A { OFFSET 0 0 0 CHANNELS 0 } MOTION Frames: 1 Frame Time: 0"));
    }

    [Fact]
    public void Retargeting_lifts_the_arm_and_steps_the_hips_at_the_rigs_scale()
    {
        var (scene, o, hips, _, arm) = Rig();
        var r = MotionCapture.Apply(scene, o, BvhFile.Parse(Capture));
        Assert.Equal(3, r.Matched.Count);
        Assert.Empty(r.Unmatched);
        Assert.Equal(1, r.FirstFrame);
        Assert.Equal(2, r.LastFrame, 3);
        Assert.InRange(r.Scale, 0.009, 0.011);

        // At rest on frame 1 the arm still points along +X, as it rests; on frame 2 it points straight up.
        AssertNear(Vec3.UnitX, arm.RestInObject().TransformVector(Vec3.UnitZ).Normalized(), 1e-9);
        AssertNear(Vec3.UnitX, arm.PoseInObject(1).TransformVector(Vec3.UnitZ).Normalized(), 1e-6);
        AssertNear(Vec3.UnitY, arm.PoseInObject(2).TransformVector(Vec3.UnitZ).Normalized(), 1e-6);
        // The hips step 10 cm along X, and along Z the other way round (LightWave's Z runs the other way).
        var step = hips.PositionAt(2) - hips.PositionAt(1);
        AssertNear(new Vec3(0.1, 0, -0.2) * (r.Scale / 0.01), step, 1e-6);
    }

    [Fact]
    public void Names_match_by_a_map_or_not_at_all_and_keys_follow_the_scenes_rate()
    {
        var (scene, o, _, _, _) = Rig();
        var renamed = Capture.Replace("mixamorig:LeftArm", "Bicep_L");
        var r = MotionCapture.Apply(scene, o, BvhFile.Parse(renamed));
        Assert.Equal(["LeftArm"], r.Unmatched);
        r = MotionCapture.Apply(scene, o, BvhFile.Parse(renamed), new RetargetOptions { Map = new Dictionary<string, string> { ["LeftArm"] = "Bicep_L" } });
        Assert.Empty(r.Unmatched);
        Assert.Throws<ArgumentException>(() => MotionCapture.Apply(scene, o, BvhFile.Parse(Capture), new RetargetOptions { Map = new Dictionary<string, string> { ["LeftArm"] = "Nope" } }));

        // Half a second of capture at 60 frames a second, onto a 30 fps scene from frame 10: keys 10 to 25.
        var frames = string.Join("\n", Enumerable.Range(0, 31).Select(i => $"0 100 0 0 0 0 0 0 0 {i * 3} 0 0"));
        var halfSecond = Capture[..Capture.IndexOf("MOTION")] + $"MOTION\nFrames: 31\nFrame Time: {1 / 60.0}\n" + frames;
        var (s2, o2, _, _, arm2) = Rig();
        MotionCapture.Apply(s2, o2, BvhFile.Parse(halfSecond), new RetargetOptions { StartFrame = 10 });
        Assert.Equal(16, arm2[Channel.Heading].Keys.Count);
        Assert.Equal(10, arm2[Channel.Heading].Keys[0].Frame);
        Assert.Equal(25, arm2[Channel.Heading].Keys[^1].Frame, 6);

        var bare = new Scene();
        var nobody = bare.AddObject("Rock", Doc(Square(Vec3.Zero, 1, Vec3.UnitZ)));
        Assert.Contains("no bones", Assert.Throws<InvalidOperationException>(() => MotionCapture.Apply(bare, nobody, BvhFile.Parse(Capture))).Message);
    }
}
