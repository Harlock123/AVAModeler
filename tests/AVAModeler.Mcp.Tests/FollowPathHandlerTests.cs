using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_follow_path and bake_path, driven directly and through a client: following, timing, aligning, the path.position channel, errors, undo.</summary>
public class FollowPathHandlerTests
{
    private static async Task<Bench> WithPoints()
    {
        var b = new Bench();
        foreach (var (name, x, z) in new[] { ("A", 0.0, 0.0), ("B", 4, 0), ("C", 4, 4), ("D", 0, 4) })
            await b.Tools.AddNull(name, [x, 0, z], cancel: Cancel);
        await b.Tools.AddNull("Car", cancel: Cancel);
        return b;
    }

    [Fact]
    public async Task Set_follow_path_makes_an_item_go_round_a_loop_as_one_undo_step()
    {
        using var b = await WithPoints();
        int depth = b.Layout.History.Count;
        var r = await Parse(b.Tools.SetFollowPath("Car", ["A", "B", "C", "D"], closed: true, from_frame: 1, to_frame: 41, cancel: Cancel));
        var fp = (JsonObject)r["follow_path"]!;
        Assert.Equal(["A", "B", "C", "D"], ((JsonArray)fp["points"]!).Select(x => (string)x!));
        Assert.True((bool)fp["closed"]!);
        Assert.True((bool)fp["align_to_path"]!);
        Assert.InRange((double)fp["length"]!, 12, 17);
        Assert.Contains("path.position", ((JsonArray)r["keyed"]!).Select(x => (string)x!));
        Assert.Equal(depth + 1, b.Layout.History.Count);
        var car = b.Scene.Find("Car")!;
        Assert.True(Vec3.Near(new Vec3(0, 0, 0), car.WorldPosition(1), 1e-9));
        Assert.True(Vec3.Near(new Vec3(0, 0, 0), car.WorldPosition(41), 1e-9));
        // A quarter of the way round, near B, heading along +X.
        var forward = car.WorldMatrix(11).TransformVector(Vec3.UnitZ);
        Assert.True(Vec3.Dot(forward, Vec3.UnitX) > 0.7, $"{forward}");
        // path.position is a channel like any other.
        var keys = await Parse(b.Tools.GetKeys("Car", "path.position", Cancel));
        Assert.Equal(2, ((JsonArray)keys["channels"]![0]!["keys"]!).Count);
        await b.Tools.SetKey("Car", "path.position", 21, 0.75, cancel: Cancel);
        Assert.Equal(0.75, car.PathPosition.Evaluate(21), 9);
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        Assert.Null(car.Path);
    }

    [Fact]
    public async Task Set_follow_path_changes_settings_stops_and_refuses_what_cant_work()
    {
        using var b = await WithPoints();
        await b.Tools.SetFollowPath("Car", ["A", "B", "C"], cancel: Cancel);
        var car = b.Scene.Find("Car")!;
        Assert.False(car.Path!.Closed);
        await b.Tools.SetFollowPath("Car", banking: 40, look_ahead: 0.5, align_to_path: false, cancel: Cancel);
        Assert.Equal((40.0, 0.5, false), (car.Path!.Banking, car.Path.LookAhead, car.Path.AlignToPath));
        int depth = b.Layout.History.Count;
        Assert.Contains("its own path", await Error(b.Tools.SetFollowPath("Car", ["A", "Car"], cancel: Cancel)));
        Assert.Contains("twice", await Error(b.Tools.SetFollowPath("Car", ["A", "A"], cancel: Cancel)));
        Assert.Contains("at least 3 points", await Error(b.Tools.SetFollowPath("Car", ["A", "B"], closed: true, cancel: Cancel)));
        Assert.Contains("look-ahead", await Error(b.Tools.SetFollowPath("Car", look_ahead: -1, cancel: Cancel)));
        Assert.Contains("together", await Error(b.Tools.SetFollowPath("Car", from_frame: 1, cancel: Cancel)));
        Assert.Contains("after its first", await Error(b.Tools.SetFollowPath("Car", from_frame: 30, to_frame: 10, cancel: Cancel)));
        Assert.Contains("doesn't follow a path yet", await Error(b.Tools.SetFollowPath("Light", closed: true, cancel: Cancel)));
        Assert.Contains("Give points", await Error(b.Tools.SetFollowPath("Car", cancel: Cancel)));
        Assert.Equal(depth, b.Layout.History.Count);
        await b.Tools.RunTool("Box", null, Cancel);
        await b.Tools.AddObject(name: "Box", cancel: Cancel);
        await b.Tools.AddBone("Box", "Bone", cancel: Cancel);
        Assert.Contains("bones can't follow", await Error(b.Tools.SetFollowPath("Bone", ["A", "B"], cancel: Cancel)));
        var stopped = await Parse(b.Tools.SetFollowPath("Car", [], cancel: Cancel));
        Assert.Contains("no longer follows", (string)stopped["note"]!);
        Assert.Null(car.Path);
    }

    [Fact]
    public async Task Bake_path_keys_the_motion_and_an_agent_reaches_both_through_a_client()
    {
        using var b = await WithPoints();
        await using var s = await InProcess.Start(b.Host);
        await s.Client.Json("set_follow_path", new { item = "Car", points = new[] { "A", "B", "C", "D" }, closed = true, from_frame = 1, to_frame = 60 });
        var car = b.Scene.Find("Car")!;
        var at20 = car.WorldPosition(20);
        var r = await s.Client.Json("bake_path", new { item = "Car", step = 5 });
        Assert.Equal(13, (int)r["keyed_frames"]!);
        Assert.Null(car.Path);
        Assert.True((car.WorldPosition(20) - at20).Length < 0.1);
        Assert.Contains("doesn't follow a path", await s.Client.Fails("bake_path", new { item = "Car" }));
        // The dope sheet's tools see path.position like any other channel.
        await s.Client.Json("set_follow_path", new { item = "Car", points = new[] { "A", "B" }, from_frame = 1, to_frame = 30 });
        var shifted = await s.Client.Json("shift_keys", new { item = "Car", channel = "path.position", offset = 10 });
        Assert.Equal(2, (int)shifted["moved"]!);
        Assert.Equal([11.0, 40], car.PathPosition.Keys.Select(k => k.Frame));
    }
}
