using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>add_motion_modifier, set_motion_modifier and remove_motion_modifier, driven directly and through a client: each kind, errors, undo, get_scene.</summary>
public class MotionModifierHandlerTests
{
    private static async Task<Bench> WithLeader()
    {
        var b = new Bench();
        await b.Tools.AddNull("Leader", cancel: Cancel);
        await b.Tools.AddNull("Follower", [0, 1, 0], cancel: Cancel);
        var leader = b.Scene.Find("Leader")!;
        leader[Channel.PositionX].Clear();
        leader[Channel.PositionX].AddKey(new EnvelopeKey(0, 0, Linear: true));
        leader[Channel.PositionX].AddKey(new EnvelopeKey(10, 10, Linear: true));
        return b;
    }

    [Fact]
    public async Task Add_motion_modifier_adds_each_kind_as_one_undo_step()
    {
        using var b = await WithLeader();
        var f = b.Scene.Find("Follower")!;
        int depth = b.Layout.History.Count;
        var r = await Parse(b.Tools.AddMotionModifier("Follower", "lag", leader: "Leader", delay: 3, cancel: Cancel));
        Assert.Equal(depth + 1, b.Layout.History.Count);
        var lag = (JsonObject)r["modifiers"]![0]!;
        Assert.Equal((1, "lag", "Leader", 3.0), ((int)lag["number"]!, (string)lag["kind"]!, (string)lag["leader"]!, (double)lag["delay"]!));
        Assert.NotNull(r["modified_motion"]);
        Assert.True(Vec3.Near(new Vec3(3, 1, 0), f.WorldPosition(6), 1e-9));

        await b.Tools.AddMotionModifier("Follower", "jitter", position_amount: [0.1, 0, 0], frequency: 3, seed: 4, cancel: Cancel);
        await b.Tools.AddMotionModifier("Follower", "oscillate", channel: "Bank", amplitude: 10, period: 24, damping: 0.1, cancel: Cancel);
        var cycle = await Parse(b.Tools.AddMotionModifier("Follower", "cycle", driver: "Leader", driver_channel: "position.x", low: 0, high: 5, at: 1, cancel: Cancel));
        var kinds = ((JsonArray)cycle["modifiers"]!).Select(m => (string)m!["kind"]!).ToList();
        Assert.Equal(["cycle", "lag", "jitter", "oscillate"], kinds);
        Assert.Equal("position.x", (string)cycle["modifiers"]![0]!["driver_channel"]!);
        Assert.Equal("bank", (string)cycle["modifiers"]![3]!["channel"]!);
        Assert.Equal(b.Scene.FirstFrame, ((OscillateModifier)f.Modifiers[3]).Start);
        Assert.Equal(new Vec3(0.1, 0, 0), ((JitterModifier)f.Modifiers[2]).Position);

        // get_scene lists them too.
        var scene = await Parse(b.Tools.GetScene(cancel: Cancel));
        var item = ((JsonArray)scene["items"]!).Single(i => (string)i!["name"]! == "Follower")!;
        Assert.Equal(4, ((JsonArray)item["modifiers"]!).Count);

        await b.Tools.UndoScene(Cancel);
        Assert.Equal(3, f.Modifiers.Count);
    }

    [Fact]
    public async Task An_expression_sets_a_channel_from_a_formula()
    {
        using var b = await WithLeader();
        var f = b.Scene.Find("Follower")!;
        var r = await Parse(b.Tools.AddMotionModifier("Follower", "expression", channel: "heading", formula: "[Leader.world.x] * 10", cancel: Cancel));
        var e = (JsonObject)r["modifiers"]![0]!;
        Assert.Equal(("expression", "heading", "[Leader.world.x] * 10"), ((string)e["kind"]!, (string)e["channel"]!, (string)e["formula"]!));
        var leader = b.Scene.Find("Leader")!;
        Assert.Equal(leader.WorldPosition(4).X * 10, f.MotionRotationAt(4).X, 9);
        Assert.Contains("needs a formula", await Error(b.Tools.AddMotionModifier("Follower", "expression", cancel: Cancel)));
        Assert.Contains("character", await Error(b.Tools.AddMotionModifier("Follower", "expression", formula: "1 +* 2", cancel: Cancel)));
        Assert.Contains("no item of that name", await Error(b.Tools.AddMotionModifier("Follower", "expression", formula: "[Nobody.heading]", cancel: Cancel)));
        Assert.Contains("isn't a setting of an expression", await Error(b.Tools.AddMotionModifier("Follower", "expression", formula: "1", delay: 2, cancel: Cancel)));
    }

    [Fact]
    public async Task Bad_modifiers_are_refused_and_change_nothing()
    {
        using var b = await WithLeader();
        int depth = b.Layout.History.Count;
        Assert.Contains("lag, jitter, cycle, oscillate or expression", await Error(b.Tools.AddMotionModifier("Follower", "wobble", cancel: Cancel)));
        Assert.Contains("needs a leader", await Error(b.Tools.AddMotionModifier("Follower", "lag", cancel: Cancel)));
        Assert.Contains("itself", await Error(b.Tools.AddMotionModifier("Follower", "lag", leader: "Follower", cancel: Cancel)));
        Assert.Contains("isn't a setting of a jitter", await Error(b.Tools.AddMotionModifier("Follower", "jitter", delay: 2, cancel: Cancel)));
        Assert.Contains("frequency", await Error(b.Tools.AddMotionModifier("Follower", "jitter", frequency: 0, cancel: Cancel)));
        Assert.Contains("strength", await Error(b.Tools.AddMotionModifier("Follower", "lag", leader: "Leader", strength: 1.5, cancel: Cancel)));
        Assert.Contains("channel is one of", await Error(b.Tools.AddMotionModifier("Follower", "oscillate", channel: "roll", cancel: Cancel)));
        Assert.Contains("differ", await Error(b.Tools.AddMotionModifier("Follower", "cycle", driver: "Leader", low: 2, high: 2, cancel: Cancel)));
        Assert.Contains("at is from 1 to 1", await Error(b.Tools.AddMotionModifier("Follower", "jitter", at: 3, cancel: Cancel)));
        await b.Tools.AddMotionModifier("Leader", "lag", leader: "Follower", cancel: Cancel);
        depth = b.Layout.History.Count;
        Assert.Contains("loop", await Error(b.Tools.AddMotionModifier("Follower", "lag", leader: "Leader", cancel: Cancel)));
        Assert.Equal(depth, b.Layout.History.Count);
        Assert.Empty(b.Scene.Find("Follower")!.Modifiers);
    }

    [Fact]
    public async Task Set_and_remove_motion_modifier_change_one_or_all()
    {
        using var b = await WithLeader();
        var f = b.Scene.Find("Follower")!;
        await b.Tools.AddMotionModifier("Follower", "lag", leader: "Leader", cancel: Cancel);
        await b.Tools.AddMotionModifier("Follower", "oscillate", cancel: Cancel);
        var r = await Parse(b.Tools.SetMotionModifier("Follower", 1, delay: 7.5, rotation: true, cancel: Cancel));
        Assert.Equal(7.5, (double)r["modifiers"]![0]!["delay"]!);
        Assert.Equal(new LagModifier(b.Scene.Find("Leader")!) { Delay = 7.5, Rotation = true }, f.Modifiers[0]);
        Assert.Contains("isn't a setting of a lag", await Error(b.Tools.SetMotionModifier("Follower", 1, amplitude: 2, cancel: Cancel)));
        Assert.Contains("Give a setting", await Error(b.Tools.SetMotionModifier("Follower", 2, cancel: Cancel)));
        Assert.Contains("numbered 1 to 2", await Error(b.Tools.SetMotionModifier("Follower", 3, period: 2, cancel: Cancel)));
        Assert.Contains("not both", await Error(b.Tools.RemoveMotionModifier("Follower", 1, all: true, cancel: Cancel)));
        await b.Tools.RemoveMotionModifier("Follower", 1, cancel: Cancel);
        Assert.IsType<OscillateModifier>(Assert.Single(f.Modifiers));
        await b.Tools.RemoveMotionModifier("Follower", all: true, cancel: Cancel);
        Assert.Empty(f.Modifiers);
        Assert.Contains("no motion modifiers", await Error(b.Tools.RemoveMotionModifier("Follower", all: true, cancel: Cancel)));
        await b.Tools.UndoScene(Cancel);
        Assert.Single(f.Modifiers);
    }

    [Fact]
    public async Task An_agent_reaches_them_through_a_client()
    {
        using var b = await WithLeader();
        await using var s = await InProcess.Start(b.Host);
        var r = await s.Client.Json("add_motion_modifier", new { item = "Follower", kind = "oscillate", channel = "position.y", amplitude = 1, period = 20, phase = 90, start = 0 });
        Assert.Equal("oscillate", (string)r["modifiers"]![0]!["kind"]!);
        Assert.Equal(2, b.Scene.Find("Follower")!.MotionPositionAt(0).Y, 9);
        Assert.Contains("isn't a setting", await s.Client.Fails("add_motion_modifier", new { item = "Follower", kind = "oscillate", seed = 3 }));
        await s.Client.Json("set_motion_modifier", new { item = "Follower", number = 1, amplitude = 2 });
        await s.Client.Json("remove_motion_modifier", new { item = "Follower", number = 1 });
        Assert.Empty(b.Scene.Find("Follower")!.Modifiers);
    }
}
