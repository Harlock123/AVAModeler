using System.Text.Json.Nodes;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>shift_keys and scale_keys: moving and scaling keys in time, as the dope sheet does, with their errors and undo.</summary>
public class KeyTimingHandlerTests
{
    /// <summary>A null keyed on position.y at 1, 10 and 20 and heading at 5 and 25; the camera keyed at 1 and 30.</summary>
    private static async Task<Bench> WithKeys()
    {
        var b = new Bench();
        await b.Tools.AddNull("Ball", cancel: Cancel);
        await b.Tools.SetKey("Ball", "position.y", 1, 0, cancel: Cancel);
        await b.Tools.SetKey("Ball", "position.y", 10, 2, tension: 0.4, cancel: Cancel);
        await b.Tools.SetKey("Ball", "position.y", 20, 0, cancel: Cancel);
        await b.Tools.SetKey("Ball", "heading", 5, 0, cancel: Cancel);
        await b.Tools.SetKey("Ball", "heading", 25, 360, cancel: Cancel);
        await b.Tools.SetTransform("Camera", 1, position: [0, 0, -5], key: true, cancel: Cancel);
        await b.Tools.SetTransform("Camera", 30, position: [2, 0, -5], key: true, cancel: Cancel);
        return b;
    }

    private static double[] Frames(Envelope e) => [.. e.Keys.Select(k => k.Frame)];

    [Fact]
    public async Task Shift_keys_moves_an_items_keys_in_time_as_one_undo_step()
    {
        using var b = await WithKeys();
        var ball = b.Scene.Find("Ball")!;
        int depth = b.Layout.History.Count;
        var r = await Parse(b.Tools.ShiftKeys(10, "Ball", cancel: Cancel));
        Assert.Equal(5, (int)r["moved"]!);
        Assert.Equal(depth + 1, b.Layout.History.Count);
        Assert.Equal([11.0, 20, 30], Frames(ball[Channel.PositionY]));
        Assert.Equal(0.4, ball[Channel.PositionY].Keys[1].Tension);
        Assert.Equal([15.0, 35], Frames(ball[Channel.Heading]));
        Assert.Equal([11.0, 15, 20, 30, 35], ((JsonArray)r["items"]![0]!["frames_with_keys"]!).Select(x => (double)x!));
        // The camera wasn't asked about.
        Assert.Equal([1.0, 30], Frames(b.Scene.CurrentCamera![Channel.PositionX]));
        await b.Tools.UndoScene(Cancel);
        Assert.Equal([1.0, 10, 20], Frames(ball[Channel.PositionY]));

        // One channel, a range of frames, earlier: frame 20 onto 10, which it replaces.
        r = await Parse(b.Tools.ShiftKeys(-10, "Ball", "position.y", from: 15, cancel: Cancel));
        Assert.Equal(1, (int)r["replaced"]!);
        Assert.Equal([1.0, 10], Frames(ball[Channel.PositionY]));
        Assert.Equal(0, ball[Channel.PositionY].Keys[1].Value);
        // Every item's keys.
        await b.Tools.ShiftKeys(2, cancel: Cancel);
        Assert.Equal([3.0, 32], Frames(b.Scene.CurrentCamera![Channel.PositionX]));
    }

    [Fact]
    public async Task Scale_keys_stretches_timing_about_a_pivot()
    {
        using var b = await WithKeys();
        var ball = b.Scene.Find("Ball")!;
        var r = await Parse(b.Tools.ScaleKeys(2, item: "Ball", cancel: Cancel));
        // About the earliest key (frame 1): 10 → 19, 20 → 39; heading 5 → 9, 25 → 49.
        Assert.Equal([1.0, 19, 39], Frames(ball[Channel.PositionY]));
        Assert.Equal([9.0, 49], Frames(ball[Channel.Heading]));
        Assert.Equal(4, (int)r["moved"]!);
        await b.Tools.UndoScene(Cancel);
        await b.Tools.ScaleKeys(0.5, pivot: 20, item: "Ball", channel: "position.y", cancel: Cancel);
        Assert.Equal([11.0, 15, 20], Frames(ball[Channel.PositionY]));
        Assert.Equal([5.0, 25], Frames(ball[Channel.Heading]));
    }

    [Fact]
    public async Task Retiming_mistakes_change_nothing()
    {
        using var b = await WithKeys();
        int depth = b.Layout.History.Count;
        Assert.Contains("whole number of frames", await Error(b.Tools.ShiftKeys(2.5, "Ball", cancel: Cancel)));
        Assert.Contains("factor must be more than 0", await Error(b.Tools.ScaleKeys(0, item: "Ball", cancel: Cancel)));
        Assert.Contains("Give item with channel", await Error(b.Tools.ShiftKeys(1, channel: "heading", cancel: Cancel)));
        Assert.Contains("no channel", await Error(b.Tools.ShiftKeys(1, "Ball", "twist", cancel: Cancel)));
        Assert.Contains("no keys on Ball", await Error(b.Tools.ShiftKeys(1, "Ball", from: 40, cancel: Cancel)));
        Assert.Contains("is after", await Error(b.Tools.ShiftKeys(1, "Ball", from: 20, to: 10, cancel: Cancel)));
        Assert.Contains("no keys on Light", await Error(b.Tools.ShiftKeys(1, "Light", cancel: Cancel)));
        Assert.Equal(depth, b.Layout.History.Count);
    }

    [Fact]
    public async Task An_agent_retimes_keys_through_a_client()
    {
        using var b = await WithKeys();
        await using var s = await InProcess.Start(b.Host);
        var r = await s.Client.Json("shift_keys", new { item = "Ball", offset = 5 });
        Assert.Equal(5, (int)r["moved"]!);
        r = await s.Client.Json("scale_keys", new { item = "Ball", factor = 1.5, pivot = 6 });
        Assert.True((int)r["moved"]! > 0);
        Assert.Contains("factor", await s.Client.Fails("scale_keys", new { factor = -1 }));
    }
}
