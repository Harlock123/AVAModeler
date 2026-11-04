using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_ik and bake_ik, driven directly and through a client: goals, limits, locks, anchors, baking, errors, undo.</summary>
public class IKHandlerTests
{
    /// <summary>A bench with a 2 m bar along +X in the scene as "Arm", two chained bones along it and a null "Goal".</summary>
    private static async Task<Bench> WithArm()
    {
        var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new Dictionary<string, object> { ["Center"] = new[] { 1.0, 0, 0 }, ["Size"] = new[] { 2.0, 0.2, 0.2 }, ["Segments X"] = 8 }), Cancel);
        await b.Tools.AddObject(layer: 1, name: "Arm", cancel: Cancel);
        await b.Tools.AddBone("Arm", "Upper", [0, 0, 0], [90, 0, 0], 1, cancel: Cancel);
        await b.Tools.AddBone("Arm", "Lower", [0, 0, 1], null, 1, "Upper", cancel: Cancel);
        await b.Tools.AddNull("Goal", [1.1, 1.0, 0.2], cancel: Cancel);
        return b;
    }

    private static Bone Bone(Bench b, string name) => (Bone)b.Scene.Find(name)!;
    private static Vec3 Tip(Bone bone, double f) => bone.WorldMatrix(f).TransformPoint(new Vec3(0, 0, bone.RestLength));

    [Fact]
    public async Task Set_ik_makes_the_chain_reach_its_goal_as_one_undo_step()
    {
        using var b = await WithArm();
        int depth = b.Layout.History.Count;
        var r = await Parse(b.Tools.SetIK("Lower", goal: "Goal", cancel: Cancel));
        var ik = (JsonObject)r["ik"]!;
        Assert.Equal("Goal", (string)ik["goal"]!);
        Assert.True((bool)ik["full_time"]!);
        Assert.Equal(["Upper", "Lower"], ((JsonArray)ik["chain"]!).Select(x => (string)x!));
        Assert.Equal(2, (double)ik["reach"]!, 6);
        Assert.True((double)ik["tip_to_goal"]! < 1e-4);
        Assert.Equal(depth + 1, b.Layout.History.Count);
        Assert.Equal("Agent: set a bone's IK", b.Layout.History.UndoLabel);
        var lower = Bone(b, "Lower");
        Assert.True((Tip(lower, 1) - b.Scene.Find("Goal")!.WorldPosition(1)).Length < 1e-4);
        // get_scene shows the upper bone turned by the lower one's IK.
        var scene = await Parse(b.Tools.GetScene(cancel: Cancel));
        var upper = ((JsonArray)scene["items"]!).OfType<JsonObject>().Single(i => (string)i["name"]! == "Upper");
        Assert.Equal("Lower", (string)upper["ik"]!["turned_by_ik_of"]!);
        Assert.NotNull(upper["ik"]!["ik_rotation"]);
        await b.Tools.UndoScene(Cancel);
        Assert.Null(lower.IKGoal);
        Assert.Null(Bone(b, "Upper").IKEnd);
    }

    [Fact]
    public async Task Set_ik_sets_limits_locks_and_anchors_and_refuses_what_cant_work()
    {
        using var b = await WithArm();
        await b.Tools.SetIK("Lower", goal: "Goal", cancel: Cancel);
        var r = await Parse(b.Tools.SetIK("Lower", heading_limits: [-120, 0], pitch_limits: [-10, 10], cancel: Cancel));
        Assert.Equal(-120, (double)r["ik"]!["limits"]!["heading"]![0]!);
        var lower = Bone(b, "Lower");
        Assert.Equal(new IKLimit(-10, 10), lower.IKLimits.Pitch);
        Assert.InRange(lower.MotionRotationAt(1).Y, -10 - 1e-9, 10 + 1e-9);
        await b.Tools.SetIK("Lower", pitch_limits: [], cancel: Cancel);
        Assert.Null(lower.IKLimits.Pitch);
        await b.Tools.SetIK("Upper", @lock: ["bank", "pitch"], cancel: Cancel);
        Assert.Equal(IKChannels.Bank | IKChannels.Pitch, Bone(b, "Upper").IKLocked);
        await b.Tools.SetIK("Upper", anchor: true, cancel: Cancel);
        Assert.Null(Bone(b, "Upper").IKEnd);
        var off = await Parse(b.Tools.SetIK("Lower", full_time: false, cancel: Cancel));
        Assert.Contains("bake_ik", (string)off["note"]!);
        Assert.Null(lower.IKEnd);

        int depth = b.Layout.History.Count;
        Assert.Contains("its own goal", await Error(b.Tools.SetIK("Lower", goal: "Lower", cancel: Cancel)));
        Assert.Contains("another of Arm's bones", await Error(b.Tools.SetIK("Lower", goal: "Upper", cancel: Cancel)));
        Assert.Contains("not a bone", await Error(b.Tools.SetIK("Goal", goal: "Arm", cancel: Cancel)));
        Assert.Contains("least", await Error(b.Tools.SetIK("Lower", bank_limits: [30, -30], cancel: Cancel)));
        Assert.Contains("[min, max]", await Error(b.Tools.SetIK("Lower", bank_limits: [30], cancel: Cancel)));
        Assert.Contains("lock takes heading, pitch and bank", await Error(b.Tools.SetIK("Lower", @lock: ["twist"], cancel: Cancel)));
        Assert.Contains("Give goal", await Error(b.Tools.SetIK("Lower", cancel: Cancel)));
        // Failed calls leave nothing behind.
        Assert.Equal(depth, b.Layout.History.Count);
        Assert.Equal(new IKLimit(-120, 0), lower.IKLimits.Heading);
        await b.Tools.SetIK("Lower", goal: "", cancel: Cancel);
        Assert.Null(lower.IKGoal);
    }

    [Fact]
    public async Task Bake_ik_keys_the_chain_and_turns_full_time_IK_off()
    {
        using var b = await WithArm();
        await b.Tools.SetIK("Lower", goal: "Goal", cancel: Cancel);
        await b.Tools.SetTransform("Goal", 1, position: [1.1, 1.0, 0.2], key: true, cancel: Cancel);
        await b.Tools.SetTransform("Goal", 20, position: [0.5, 0.2, -1.2], key: true, cancel: Cancel);
        var goal = b.Scene.Find("Goal")!;
        var lower = Bone(b, "Lower");
        int depth = b.Layout.History.Count;
        var r = await Parse(b.Tools.BakeIK("Lower", 1, 20, 1, cancel: Cancel));
        Assert.Equal(20, (int)r["keyed_frames"]!);
        Assert.False((bool)r["full_time"]!);
        Assert.Equal(depth + 1, b.Layout.History.Count);
        Assert.False(lower.FullTimeIK);
        Assert.Equal(20, Bone(b, "Upper")[Channel.Heading].Keys.Count);
        foreach (var f in new[] { 1.0, 7, 13, 20 }) Assert.True((Tip(lower, f) - goal.WorldPosition(f)).Length < 1e-3);
        await b.Tools.UndoScene(Cancel);
        Assert.True(lower.FullTimeIK);
        Assert.Single(Bone(b, "Upper")[Channel.Heading].Keys);
        Assert.Contains("no IK goal", await Error(b.Tools.BakeIK("Upper", cancel: Cancel)));
        Assert.Contains("outside the scene's frames", await Error(b.Tools.BakeIK("Lower", 0, 10, cancel: Cancel)));
        Assert.Contains("step must be", await Error(b.Tools.BakeIK("Lower", step: 0, cancel: Cancel)));
    }

    [Fact]
    public async Task An_agent_sets_up_and_bakes_IK_through_a_client()
    {
        using var b = await WithArm();
        await using var s = await InProcess.Start(b.Host);
        var r = await s.Client.Json("set_ik", new { bone = "Lower", goal = "Goal", heading_limits = new[] { -150.0, 0 } });
        Assert.True((double)r["ik"]!["tip_to_goal"]! < 1e-3);
        var baked = await s.Client.Json("bake_ik", new { bone = "Lower", step = 10 });
        Assert.Equal(7, (int)baked["keyed_frames"]!);
        Assert.Contains("isn't a bone", await s.Client.Fails("bake_ik", new { bone = "Goal" }));
    }
}
