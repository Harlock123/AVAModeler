using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>IK in Layout's session: baking and keying as undo steps, and the goal's line in the views.</summary>
public class IKLayoutTests
{
    private static (LayoutSession Session, Bone Upper, Bone Lower, NullItem Goal) Arm()
    {
        var s = new LayoutSession();
        var doc = new Document();
        doc.Layers[0].Mesh.AddPolygon([doc.Layers[0].Mesh.AddPoint(new Vec3(1.5, 0, 0))]);
        var arm = s.Scene.AddObject("Arm", doc);
        var upper = s.Scene.AddBone(arm, "Upper", Vec3.Zero, new Vec3(90, 0, 0), 1);
        var lower = s.Scene.AddBone(arm, "Lower", new Vec3(0, 0, 1), Vec3.Zero, 1);
        lower.Parent = upper;
        var goal = s.Scene.AddNull("Goal");
        goal.SetPosition(1, new Vec3(1.2, 0.8, 0));
        goal.SetPosition(30, new Vec3(0.2, 1.4, 0.6));
        s.Replace(s.Scene);
        s.Edit("Set IK", () => { lower.IKGoal = goal; s.Scene.Notify(); });
        return (s, upper, lower, goal);
    }

    private static Vec3 Tip(Bone b, double f) => b.WorldMatrix(f).TransformPoint(new Vec3(0, 0, b.RestLength));

    [Fact]
    public void Baking_IK_is_one_undo_step_that_keys_the_chain()
    {
        var (s, upper, lower, goal) = Arm();
        int depth = s.History.Count;
        var frames = s.BakeIK(lower, 1, 30, 5);
        Assert.Equal([1.0, 6, 11, 16, 21, 26, 30], frames);
        Assert.Equal(depth + 1, s.History.Count);
        Assert.Equal("Bake IK for Lower", s.History.UndoLabel);
        Assert.False(lower.FullTimeIK);
        // The first key replaces the never-keyed constant, so the channel has just the baked keys.
        Assert.Equal(frames, upper[Channel.Heading].Keys.Select(k => k.Frame));
        foreach (var f in frames) Assert.True((Tip(lower, f) - goal.WorldPosition(f)).Length < 1e-3);
        s.Undo();
        Assert.True(lower.FullTimeIK);
        Assert.Single(upper[Channel.Heading].Keys);
        Assert.Same(lower, upper.IKEnd);
    }

    [Fact]
    public void Keying_IK_keys_the_pose_at_the_frame_without_full_time_IK()
    {
        var (s, upper, lower, goal) = Arm();
        s.Edit("Full-time IK", () => { lower.FullTimeIK = false; s.Scene.Notify(); });
        s.SetFrame(30);
        Assert.True((Tip(lower, 30) - goal.WorldPosition(30)).Length > 0.1);
        s.KeyIK(lower);
        Assert.Equal("Key IK for Lower", s.History.UndoLabel);
        Assert.True((Tip(lower, 30) - goal.WorldPosition(30)).Length < 1e-4);
        Assert.False(lower.FullTimeIK);
        s.Undo();
        Assert.True((Tip(lower, 30) - goal.WorldPosition(30)).Length > 0.1);
        s.Edit("No goal", () => lower.IKGoal = null);
        Assert.Throws<InvalidOperationException>(() => s.KeyIK(lower));
    }

    [Fact]
    public void The_view_draws_a_line_from_a_bones_tip_to_its_goal()
    {
        var (s, _, lower, goal) = Arm();
        var frame = s.Scene.Evaluate(1);
        var drawn = SceneDrawing.Build(frame, s.Scene.Items, shaded: false);
        var link = Assert.Single(drawn.Overlay, l => l.Color == SceneDrawing.IKLink);
        Assert.True(Vec3.Near(goal.WorldPosition(1), link.B, 1e-9));
        Assert.True(Vec3.Near(Tip(lower, 1), link.A, 1e-9));
    }
}
