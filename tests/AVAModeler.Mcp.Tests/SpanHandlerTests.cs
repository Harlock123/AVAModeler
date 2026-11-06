using System.Text.Json.Nodes;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_key's span, in_slope and out_slope, get_keys showing them, refusals, and undo.</summary>
public class SpanHandlerTests
{
    private static async Task<Bench> WithKeys()
    {
        var b = new Bench();
        await b.Tools.AddNull("Ball", cancel: Cancel);
        await b.Tools.SetKey("Ball", "position.y", 1, 0, cancel: Cancel);
        await b.Tools.SetKey("Ball", "position.y", 10, 2, cancel: Cancel);
        await b.Tools.SetKey("Ball", "position.y", 20, 0, cancel: Cancel);
        return b;
    }

    [Fact]
    public async Task Set_key_sets_spans_and_slopes_and_get_keys_shows_them()
    {
        using var b = await WithKeys();
        var y = b.Scene.Find("Ball")![Channel.PositionY];
        var r = await Parse(b.Tools.SetKey("Ball", "position.y", 10, 2, span: "stepped", cancel: Cancel));
        Assert.Equal("stepped", (string?)r["keys"]![1]!["span"]);
        Assert.Equal(SpanKind.Stepped, y.Keys[1].Kind);
        Assert.Equal(0, y.Evaluate(9));

        // Bezier with no slopes takes the curve's; given slopes win.
        await b.Tools.SetKey("Ball", "position.y", 10, 2, span: "bezier", in_slope: 0.25, out_slope: -0.5, cancel: Cancel);
        Assert.Equal((SpanKind.Bezier, 0.25, -0.5), (y.Keys[1].Kind, y.Keys[1].InSlope, y.Keys[1].OutSlope));
        var keys = await Parse(b.Tools.GetKeys("Ball", "position.y", Cancel));
        var k = keys["channels"]![0]!["keys"]![1]!;
        Assert.Equal("bezier", (string?)k["span"]);
        Assert.Equal(0.25, (double)k["in_slope"]!);
        Assert.Equal(-0.5, (double)k["out_slope"]!);
        // A plain TCB key shows none of them.
        Assert.Null(keys["channels"]![0]!["keys"]![2]!["span"]);
        Assert.Null(keys["channels"]![0]!["keys"]![2]!["in_slope"]);

        // Changing the value keeps the span and slopes; linear still works and is a span too.
        await b.Tools.SetKey("Ball", "position.y", 10, 3, cancel: Cancel);
        Assert.Equal((SpanKind.Bezier, 0.25), (y.Keys[1].Kind, y.Keys[1].InSlope));
        var lin = await Parse(b.Tools.SetKey("Ball", "position.y", 20, 0, linear: true, cancel: Cancel));
        Assert.True((bool)lin["keys"]![2]!["linear"]!);
        Assert.Equal("linear", (string?)lin["keys"]![2]!["span"]);
        Assert.Equal(SpanKind.Linear, y.Keys[2].Kind);
        await b.Tools.SetKey("Ball", "position.y", 20, 0, span: "tcb", cancel: Cancel);
        Assert.Equal(SpanKind.Tcb, y.Keys[2].Kind);
        Assert.False(y.Keys[2].Linear);
    }

    [Fact]
    public async Task Bezier_without_slopes_keeps_the_curve()
    {
        using var b = await WithKeys();
        var y = b.Scene.Find("Ball")![Channel.PositionY];
        var before = Enumerable.Range(2, 36).Select(f => y.Evaluate(f * 0.5)).ToArray();
        await b.Tools.SetKey("Ball", "position.y", 10, 2, span: "bezier", cancel: Cancel);
        var after = Enumerable.Range(2, 36).Select(f => y.Evaluate(f * 0.5)).ToArray();
        for (int i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i], 9);
    }

    [Fact]
    public async Task Bad_spans_and_slopes_are_refused_and_change_nothing_and_a_call_is_one_undo_step()
    {
        using var b = await WithKeys();
        var y = b.Scene.Find("Ball")![Channel.PositionY];
        var keys = y.Keys.ToArray();
        int depth = b.Layout.History.Count;
        Assert.Contains("isn't one of tcb, linear, stepped or bezier", await Error(b.Tools.SetKey("Ball", "position.y", 10, 5, span: "curvy", cancel: Cancel)));
        Assert.Contains("in_slope must be a number", await Error(b.Tools.SetKey("Ball", "position.y", 10, 5, span: "bezier", in_slope: double.NaN, cancel: Cancel)));
        Assert.Contains("out_slope must be a number", await Error(b.Tools.SetKey("Ball", "position.y", 10, 5, out_slope: double.PositiveInfinity, cancel: Cancel)));
        Assert.Contains("contradicts", await Error(b.Tools.SetKey("Ball", "position.y", 10, 5, span: "stepped", linear: true, cancel: Cancel)));
        Assert.Equal(keys, y.Keys);
        Assert.Equal(depth, b.Layout.History.Count);

        await b.Tools.SetKey("Ball", "position.y", 10, 5, span: "bezier", in_slope: 1, cancel: Cancel);
        Assert.Equal(depth + 1, b.Layout.History.Count);
        await b.Tools.UndoScene(Cancel);
        Assert.Equal(keys, y.Keys);
    }
}
