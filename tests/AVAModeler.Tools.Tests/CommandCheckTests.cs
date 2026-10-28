using AVAModeler.Core;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>The command API's checks and guarantees that scripts and AI agents lean on.</summary>
public class CommandCheckTests
{
    [Fact]
    public void Parameters_set_by_name_are_checked_against_their_type_and_limits()
    {
        var box = new BoxTool();
        box.Parameter("Size")!.Assign(new[] { 2.0, 3, 4 });
        Assert.Equal(new Vec3(2, 3, 4), box.Size);
        box.Parameter("SegmentsX")!.Assign(3.0);
        Assert.Equal(3, box.SegmentsX);
        box.Parameter("Surface")!.Assign("Red");
        Assert.Equal("Red", box.Surface);

        string Fails(Tool t, string name, object? value) => Assert.Throws<ArgumentException>(() => t.Parameter(name)!.Assign(value)).Message;
        Assert.Contains("Box's SegmentsX must be a whole number, not 2.5", Fails(box, "SegmentsX", 2.5));
        Assert.Contains("must be from 1 to 1000, not 0", Fails(box, "SegmentsX", 0.0));
        Assert.Contains("must be at least 0", Fails(box, "Size", new[] { 1.0, -1, 1 }));
        Assert.Contains("vector of three numbers [x, y, z], not 2", Fails(box, "Size", new[] { 1.0, 1 }));
        Assert.Contains("must be text", Fails(box, "Surface", 5.0));
        Assert.Contains("needs a value", Fails(box, "Surface", null));
        Assert.Contains("finite", Fails(new MoveTool(), "Offset", new[] { double.NaN, 0, 0 }));
        var ball = new BallTool();
        ball.Parameter("Kind")!.Assign("TESSELLATED");
        Assert.Equal(BallKind.Tessellated, ball.Style);
        ball.Parameter("Axis")!.Assign(Axis.X);
        Assert.Equal(Axis.X, ball.Axis);
        Assert.Contains("one of Globe, Tessellated, not \"1\"", Fails(ball, "Kind", "1"));
        Assert.Contains("one of Globe, Tessellated", Fails(ball, "Kind", 1.0));
        Assert.Contains("true or false", Fails(new DiscTool(), "Caps", "yes"));
        // Nothing that failed was set.
        Assert.Equal(new Vec3(2, 3, 4), box.Size);
        Assert.Equal(3, box.SegmentsX);
        Assert.Equal("from 1 to 1000", box.Parameter("SegmentsX")!.Limits());
        Assert.Equal("", new MoveTool().Parameter("Offset")!.Limits());
    }

    [Fact]
    public void Every_tool_says_what_it_does_and_whether_it_runs_by_name()
    {
        foreach (var group in ToolRegistry.Default.ByTab())
            foreach (var tool in group)
            {
                Assert.False(string.IsNullOrWhiteSpace(tool.Summary), $"{tool.Name} has no summary");
                Assert.EndsWith(".", tool.Summary);
                Assert.Equal(tool.Name is "Points" or "Drag", !tool.Scriptable);
            }
    }

    [Fact]
    public void A_tool_that_fails_is_undone_before_the_error_goes_on()
    {
        var cmd = new Commands(new Document());
        cmd.Run("Box");
        var before = cmd.Document.Current.Mesh.Clone();
        // Boolean needs something in a background layer.
        Assert.Throws<InvalidOperationException>(() => cmd.Run("Boolean"));
        Assert.Equal(before.Points, cmd.Document.Current.Mesh.Points);
        Assert.Equal(1, cmd.Document.Undo.Count);
        Assert.Equal("Box", cmd.Document.Undo.UndoLabel);
        // A bad value changes nothing either.
        Assert.Throws<ArgumentException>(() => cmd.Run("Box", new Dictionary<string, object?> { ["SegmentsY"] = -1.0 }));
        Assert.Equal(1, cmd.Document.Undo.Count);
    }

    [Fact]
    public void Points_and_polygons_add_in_one_undo_step_each()
    {
        var cmd = new Commands(new Document());
        var pts = cmd.AddPoints([new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0)]);
        Assert.Equal([0, 1, 2, 3], pts);
        var polys = cmd.AddPolygons([[0, 1, 2, 3], [0, 2], [1]], "Red");
        Assert.Equal([0, 1, 2], polys);
        Assert.Equal(2, cmd.Document.Undo.Count);
        Assert.All(cmd.Document.Current.Mesh.Polygons, p => Assert.Equal("Red", cmd.Document.Surfaces[p.Surface].Name));
        Assert.Contains("Polygon 1 uses point 7", Assert.Throws<ArgumentException>(() => cmd.AddPolygons([[0, 1], [0, 7]])).Message);
        Assert.Contains("same point twice", Assert.Throws<ArgumentException>(() => cmd.AddPolygon([0, 0, 1])).Message);
        Assert.Throws<ArgumentException>(() => cmd.AddPoints([new(0, double.PositiveInfinity, 0)]));
        Assert.Equal(3, cmd.Document.Current.Mesh.Polygons.Count);
        Assert.Equal(2, cmd.Document.Undo.Count);
        cmd.Undo();
        Assert.Empty(cmd.Document.Current.Mesh.Polygons);
        Assert.Equal(4, cmd.PointCount);
    }

    [Fact]
    public void Selecting_by_index_checks_the_indices_first()
    {
        var cmd = new Commands(new Document());
        cmd.Run("Box");
        cmd.SelectPoints([1, 2]);
        Assert.Contains("There's no point 8: the current layer's points are numbered 0 to 7", Assert.Throws<ArgumentException>(() => cmd.SelectPoints([3, 8])).Message);
        Assert.Equal([1, 2], cmd.Document.Selection.OrderedPoints(0));
        Assert.Throws<ArgumentException>(() => cmd.SelectPolygons([6]));
        cmd.SelectPolygons([5]);
        Assert.Equal([5], cmd.Document.Selection.PolygonsIn(0));
    }

    [Fact]
    public void A_group_of_commands_is_one_undo_step_or_nothing()
    {
        var cmd = new Commands(new Document());
        cmd.Group("Two", () => { cmd.Run("Box"); cmd.Run("Move", new Dictionary<string, object?> { ["Offset"] = new[] { 1.0, 0, 0 } }); return 0; });
        Assert.Equal(1, cmd.Document.Undo.Count);
        Assert.Throws<ArgumentException>(() => cmd.Group("Bad", () => { cmd.Run("Ball"); return cmd.Run("Nope"); }));
        Assert.Equal(8, cmd.PointCount);
        Assert.Equal(1, cmd.Document.Undo.Count);
    }

    [Fact]
    public void A_replace_guard_can_refuse_new_and_load()
    {
        var cmd = new Commands(new Document());
        cmd.Run("Box");
        cmd.ReplaceGuard = () => cmd.Document.Modified ? "Unsaved." : null;
        Assert.Equal("Unsaved.", Assert.Throws<InvalidOperationException>(cmd.New).Message);
        Assert.Equal("Unsaved.", Assert.Throws<InvalidOperationException>(() => cmd.Load("whatever.lwo")).Message);
        Assert.Equal(8, cmd.PointCount);
        cmd.ReplaceGuard = null;
        cmd.New();
        Assert.Equal(0, cmd.PointCount);
    }

    [Fact]
    public void A_macro_that_runs_too_long_is_stopped()
    {
        var cmd = new Commands(new Document());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<MacroException>(() => Macros.Run("local n = 0 while true do n = n + 1 end", cmd, TimeSpan.FromMilliseconds(200)));
        Assert.Contains("stopped after running for 0.2 seconds", e.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        // A macro within its time runs as usual, printing and calling tools.
        Macros.Run("tool('Box'); print(pointcount())", cmd, TimeSpan.FromSeconds(10));
        Assert.Equal(["8"], cmd.Output);
        Assert.Contains("no tool called", Assert.Throws<MacroException>(() => Macros.Run("tool('Nope')", cmd, TimeSpan.FromSeconds(10))).Message);
    }
}
