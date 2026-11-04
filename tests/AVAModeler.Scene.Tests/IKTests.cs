using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Inverse kinematics: a two-bone arm reaching for a goal null, with limits, locks, anchors and baking.</summary>
public class IKTests
{
    /// <summary>
    /// An arm along +X from the origin: Upper (1 m) and Lower (1 m) chained, both pointing along +X at rest, and a goal
    /// null; Lower reaches for it with full-time IK.
    /// </summary>
    private static (Scene Scene, ObjectItem Arm, Bone Upper, Bone Lower, NullItem Goal) Arm(Vec3 goalAt)
    {
        var mesh = new Mesh();
        // Points along the arm, so it bends visibly.
        foreach (var x in new[] { 0.5, 1.5, 1.9 }) mesh.AddPolygon([mesh.AddPoint(new Vec3(x, 0.05, 0))]);
        var scene = new Scene { FirstFrame = 1, LastFrame = 30 };
        var arm = scene.AddObject("Arm", Doc(mesh));
        var upper = scene.AddBone(arm, "Upper", Vec3.Zero, new Vec3(90, 0, 0), 1);
        var lower = scene.AddBone(arm, "Lower", new Vec3(0, 0, 1), Vec3.Zero, 1);
        lower.Parent = upper;
        var goal = scene.AddNull("Goal");
        goal.SetPosition(0, goalAt);
        lower.IKGoal = goal;
        scene.Notify();
        return (scene, arm, upper, lower, goal);
    }

    private static Vec3 Tip(Bone b, double frame) => b.WorldMatrix(frame).TransformPoint(new Vec3(0, 0, b.RestLength));
    private static Vec3 Root(Bone b, double frame) => b.WorldMatrix(frame).TransformPoint(Vec3.Zero);

    [Fact]
    public void The_chain_reaches_a_goal_within_reach()
    {
        var (_, _, upper, lower, goal) = Arm(new Vec3(1.2, 0.9, 0.3));
        Assert.Same(lower, upper.IKEnd);
        Assert.Same(lower, lower.IKEnd);
        Assert.Equal([upper, lower], InverseKinematics.Chain(lower));
        Assert.Equal(2, InverseKinematics.Reach(lower), 12);
        AssertNear(goal.WorldPosition(1), Tip(lower, 1), 1e-5);
        // The root stays where it hangs; the keys aren't touched.
        AssertNear(Vec3.Zero, Root(upper, 1), 1e-12);
        AssertNear(new Vec3(90, 0, 0), upper.RotationAt(1), 1e-12);
        Assert.NotEqual(upper.RotationAt(1), upper.MotionRotationAt(1));
    }

    [Fact]
    public void The_chain_follows_a_moving_goal_frame_by_frame()
    {
        var (_, _, _, lower, goal) = Arm(new Vec3(1.5, 0, 0));
        goal[Channel.PositionX].SetConstant(0);
        goal.SetPosition(1, new Vec3(1.5, 0.5, 0));
        goal.SetPosition(30, new Vec3(0.4, 1.4, -0.6));
        foreach (var f in new[] { 1.0, 7.5, 15, 22, 30 })
            AssertNear(goal.WorldPosition(f), Tip(lower, f), 1e-5);
    }

    [Fact]
    public void A_goal_out_of_reach_leaves_the_chain_stretched_straight_toward_it()
    {
        var (_, _, upper, lower, _) = Arm(new Vec3(3, 4, 0));
        var dir = new Vec3(3, 4, 0).Normalized();
        AssertNear(dir * 2, Tip(lower, 1), 1e-3);
        AssertNear(dir * 1, Root(lower, 1), 1e-3);
        // Fully stretched: Lower carries straight on from Upper.
        var straight = Vec3.Dot((Tip(lower, 1) - Root(lower, 1)).Normalized(), (Root(lower, 1) - Root(upper, 1)).Normalized());
        Assert.True(straight > 0.99999, $"The arm bends: {straight}.");
    }

    [Fact]
    public void Limits_bound_what_IK_turns_a_bone_to()
    {
        var (scene, _, _, lower, goal) = Arm(new Vec3(0.6, 1.1, 0));
        // Free, the elbow bends a long way to reach a goal so close in.
        double free = Math.Abs(lower.MotionRotationAt(1).Y) + Math.Abs(lower.MotionRotationAt(1).X);
        Assert.True(free > 40, $"The elbow only bent {free}°.");
        lower.IKLimits = new IKLimits(new IKLimit(-20, 20), new IKLimit(-20, 20), null);
        scene.Notify();
        var r = lower.MotionRotationAt(1);
        Assert.InRange(r.X, -20 - 1e-9, 20 + 1e-9);
        Assert.InRange(r.Y, -20 - 1e-9, 20 + 1e-9);
        // It can't bend enough to get there, but gets as close as it can.
        Assert.True((Tip(lower, 1) - goal.WorldPosition(1)).Length > 0.05);
        Assert.Throws<ArgumentException>(() => new IKLimits(new IKLimit(10, -10)).Checked());
    }

    [Fact]
    public void A_locked_channel_keeps_its_keyed_value()
    {
        var (scene, _, upper, lower, goal) = Arm(new Vec3(1.0, 1.0, 0.8));
        upper.IKLocked = IKChannels.Heading;
        scene.Notify();
        Assert.Equal(90, upper.MotionRotationAt(1).X, 9);
        // Still within reach without turning Upper's heading? Then IK found another way; either way it didn't turn it.
        Assert.True((Tip(lower, 1) - goal.WorldPosition(1)).Length < 0.5);
    }

    [Fact]
    public void An_anchor_stops_the_chain_below_it()
    {
        var (scene, _, upper, lower, _) = Arm(new Vec3(1.2, 0.9, 0));
        upper.IKAnchor = true;
        scene.Notify();
        Assert.Equal([lower], InverseKinematics.Chain(lower));
        Assert.Null(upper.IKEnd);
        Assert.Equal(upper.RotationAt(1), upper.MotionRotationAt(1));
        // Lower alone points at the goal from the end of Upper.
        var aim = (Tip(lower, 1) - Root(lower, 1)).Normalized();
        AssertNear((new Vec3(1.2, 0.9, 0) - Root(lower, 1)).Normalized(), aim, 1e-6);
    }

    [Fact]
    public void Solving_is_the_same_every_time_and_in_a_copy()
    {
        var (scene, _, upper, lower, _) = Arm(new Vec3(0.9, 1.2, -0.4));
        var first = InverseKinematics.Solve(lower, 1);
        var again = InverseKinematics.Solve(lower, 1);
        Assert.Equal(first[upper], again[upper]);
        Assert.Equal(first[lower], again[lower]);
        var copy = scene.Copy();
        var copyLower = copy.Bones.Single(b => b.Name == "Lower");
        Assert.Same(copy.Nulls.Single(), copyLower.IKGoal);
        Assert.Equal(lower.MotionRotationAt(1), copyLower.MotionRotationAt(1));
        // From several threads at once.
        var results = new Vec3[16];
        Parallel.For(0, results.Length, i => results[i] = scene.Copy().Bones.Single(b => b.Name == "Upper").MotionRotationAt(1));
        Assert.All(results, r => Assert.Equal(upper.MotionRotationAt(1), r));
    }

    [Fact]
    public void The_solved_pose_bends_the_object()
    {
        var (scene, arm, _, lower, _) = Arm(new Vec3(1.0, 1.0, 0));
        var tipPoint = arm.DeformedMesh(1).Points[2];
        Assert.True(tipPoint.Y > 0.5, $"The arm's end didn't rise: {tipPoint}.");
        lower.IKGoal = null;
        scene.Notify();
        AssertNear(new Vec3(1.9, 0.05, 0), arm.DeformedMesh(1).Points[2], 1e-9);
    }

    [Fact]
    public void Without_full_time_IK_the_chain_plays_its_keys_until_baked()
    {
        var (scene, _, upper, lower, goal) = Arm(new Vec3(1.2, 0.9, 0));
        lower.FullTimeIK = false;
        scene.Notify();
        Assert.Null(upper.IKEnd);
        Assert.Equal(upper.RotationAt(10), upper.MotionRotationAt(10));
        lower.FullTimeIK = true;
        goal.SetPosition(1, new Vec3(1.2, 0.9, 0));
        goal.SetPosition(20, new Vec3(0.3, 1.5, 0.5));
        var keyed = InverseKinematics.Bake(lower, 1, 20, 5, (e, f, v) => e.AddKey(f, v));
        Assert.Equal([1.0, 6, 11, 16, 20], keyed);
        Assert.False(lower.FullTimeIK);
        Assert.Null(lower.IKEnd);
        // The keys hold the solved pose at the baked frames.
        foreach (var f in keyed)
        {
            AssertNear(goal.WorldPosition(f), Tip(lower, f), 2e-3);
            Assert.Equal(upper.RotationAt(f), upper.MotionRotationAt(f));
        }
        Assert.Throws<InvalidOperationException>(() => InverseKinematics.Bake(upper, 1, 20, 1, (_, _, _) => { }));
        Assert.Throws<ArgumentException>(() => InverseKinematics.Bake(lower, 20, 1, 1, (_, _, _) => { }));
    }

    [Fact]
    public void A_goal_must_not_be_the_chain_or_ride_on_it()
    {
        var (scene, arm, upper, lower, goal) = Arm(new Vec3(1, 1, 0));
        Assert.Null(InverseKinematics.GoalProblem(lower, goal));
        Assert.Contains("its own goal", InverseKinematics.GoalProblem(lower, lower));
        Assert.Contains("another of Arm's bones", InverseKinematics.GoalProblem(lower, upper));
        var rider = scene.AddNull("Rider");
        rider.Parent = lower;
        Assert.Contains("moves with Lower", InverseKinematics.GoalProblem(lower, rider));
        // The object itself is fine: it doesn't move with its bones.
        Assert.Null(InverseKinematics.GoalProblem(lower, arm));
        // A goal that's gone is forgotten.
        scene.Remove(goal);
        Assert.Null(lower.IKGoal);
        Assert.Null(upper.IKEnd);
    }

    [Fact]
    public void Snapshots_put_IK_settings_back()
    {
        var (scene, _, upper, lower, goal) = Arm(new Vec3(1, 1, 0));
        var snap = scene.Snapshot();
        lower.IKGoal = null;
        lower.FullTimeIK = false;
        upper.IKAnchor = true;
        upper.IKLocked = IKChannels.Pitch | IKChannels.Bank;
        upper.IKLimits = new IKLimits(Bank: new IKLimit(-5, 5));
        scene.Notify();
        snap.Restore();
        Assert.Same(goal, lower.IKGoal);
        Assert.True(lower.FullTimeIK);
        Assert.False(upper.IKAnchor);
        Assert.Equal(IKChannels.None, upper.IKLocked);
        Assert.False(upper.IKLimits.Any);
        Assert.Same(lower, upper.IKEnd);
    }

    [Fact]
    public void Avascene_files_keep_every_IK_setting()
    {
        var (scene, _, upper, lower, _) = Arm(new Vec3(1, 1, 0));
        lower.FullTimeIK = false;
        upper.IKLocked = IKChannels.Bank;
        upper.IKLimits = new IKLimits(new IKLimit(-45, 120), null, new IKLimit(0, 0));
        lower.IKAnchor = true;
        var read = SceneJson.Read(SceneJson.Write(scene, embedMeshes: true));
        Assert.Empty(read.Warnings);
        var u = read.Scene.Bones.Single(b => b.Name == "Upper");
        var l = read.Scene.Bones.Single(b => b.Name == "Lower");
        Assert.Same(read.Scene.Nulls.Single(), l.IKGoal);
        Assert.False(l.FullTimeIK);
        Assert.True(l.IKAnchor);
        Assert.Equal(IKChannels.Bank, u.IKLocked);
        Assert.Equal(upper.IKLimits, u.IKLimits);
        Assert.Null(u.IKGoal);
        Assert.True(u.FullTimeIK);
        // A scene without IK writes none of it.
        var plain = SceneJson.Write(new Scene());
        Assert.DoesNotContain("ik", plain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lws_files_keep_the_goal_full_time_IK_and_limits()
    {
        var (scene, arm, upper, lower, _) = Arm(new Vec3(1, 1, 0));
        arm.SourcePath = "/objects/arm.lwo";
        upper.IKLimits = new IKLimits(Pitch: new IKLimit(-30, 60));
        var warnings = new List<string>();
        var text = LwsWriter.Write(scene, warnings);
        Assert.Empty(warnings);
        Assert.Contains("GoalItem 10000001", text);
        Assert.Contains("FullTimeIK 1", text);
        Assert.Contains("PLimits -30 60", text);
        var doc = arm.Document!;
        var (read, readWarnings) = LwsReader.Read(text, _ => doc);
        Assert.Empty(readWarnings);
        var l = read.Bones.Single(b => b.Name == "Lower");
        Assert.Equal("Goal", l.IKGoal?.Name);
        Assert.True(l.FullTimeIK);
        Assert.Equal(new IKLimit(-30, 60), read.Bones.Single(b => b.Name == "Upper").IKLimits.Pitch);
        AssertNear(Tip(lower, 1), Tip(l, 1), 1e-9);
        // What LightWave has no place for is kept only in the .avascene, and the writer says so.
        upper.IKAnchor = true;
        LwsWriter.Write(scene, warnings);
        Assert.Contains(warnings, w => w.Contains("Upper's IK anchor"));
    }

    [Fact]
    public void LightWave_5_scenes_name_the_goal_by_object_number()
    {
        // A version 1 scene: the arm (object 1), its two bones, and a goal null (object 2) on the second bone, with
        // its heading limited and no FullTimeIK line (off, as in LightWave).
        string text = """
            LWSC
            1

            FirstFrame 1
            LastFrame 30
            LoadObject arm.lwo
            ObjectMotion (unnamed)
              9
              1
              0 0 0 0 0 0 1 1 1
              0 0 0 0 0
            AddBone
            BoneName Upper
            BoneRestPosition 0 0 0
            BoneRestDirection 90 0 0
            BoneRestLength 1
            BoneMotion (unnamed)
              9
              1
              0 0 0 90 0 0 1 1 1
              0 0 0 0 0
            AddBone
            BoneName Lower
            BoneRestPosition 0 0 1
            BoneRestDirection 0 0 0
            BoneRestLength 1
            BoneMotion (unnamed)
              9
              1
              0 0 1 0 0 0 1 1 1
              0 0 0 0 0
            ParentObject 1
            GoalObject 2
            HLimits -10 80
            PLimits 0 0
            AddNullObject Goal
            ObjectMotion (unnamed)
              9
              1
              1 1 0 0 0 0 1 1 1
              0 0 0 0 0
            """;
        var doc = new Document();
        var (scene, warnings) = LwsReader.Read(text, _ => doc);
        Assert.Empty(warnings);
        var lower = scene.Bones.Single(b => b.Name == "Lower");
        Assert.Equal("Goal", lower.IKGoal?.Name);
        Assert.False(lower.FullTimeIK);
        Assert.Equal(new IKLimit(-10, 80), lower.IKLimits.Heading);
        Assert.Null(lower.IKLimits.Pitch);
        Assert.Same(scene.Bones.Single(b => b.Name == "Upper"), lower.Parent);
    }
}
