using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_symmetry and the Knife through run_tool.</summary>
public class SymmetryHandlerTests
{
    private static Dictionary<string, System.Text.Json.JsonElement> P(object o) => Bench.Args(o);

    [Fact]
    public async Task Symmetry_mirrors_a_move_and_the_knife_cuts_both_sides()
    {
        using var b = new Bench();
        await Parse(b.Tools.RunTool("Box", P(new { Size = new[] { 2.0, 2, 2 }, SegmentsX = 2 }), Cancel));
        var on = await Parse(b.Tools.SetSymmetry("x", Cancel));
        Assert.Equal("x", (string?)on["symmetry"]);
        Assert.Equal(0, (int)on["points_without_partner"]!);
        Assert.Equal("x", (string?)(await Parse(b.Tools.GetDocument(cancel: Cancel)))["symmetry"]);
        var m = b.Doc.Current.Mesh;
        int right = Enumerable.Range(0, m.Points.Count).First(i => Vec3.Near(m.Points[i], new Vec3(1, 1, 1), 1e-9));
        await Parse(b.Tools.SelectPoints([right], cancel: Cancel));
        await Parse(b.Tools.RunTool("Move", P(new { Offset = new[] { 0.5, 0, 0 } }), Cancel));
        Assert.Contains(b.Doc.Current.Mesh.Points, p => Vec3.Near(p, new Vec3(-1.5, 1, 1), 1e-9));
        await Parse(b.Tools.SelectNone(Cancel));
        await Parse(b.Tools.RunTool("Knife", P(new { Start = new[] { 0.5, -3, 0 }, End = new[] { 0.5, 3, 0 }, Axis = "Z" }), Cancel));
        Assert.Contains(b.Doc.Current.Mesh.Points, p => Math.Abs(p.X + 0.5) < 1e-9);
        Assert.True((bool)(await Parse(b.Tools.CheckMesh(cancel: Cancel)))["printable"]!);
        Assert.Equal("off", (string?)(await Parse(b.Tools.SetSymmetry("off", Cancel)))["symmetry"]);
        Assert.Contains("x, y, z or off", await Error(b.Tools.SetSymmetry("sideways", Cancel)));
        Assert.Contains("set_symmetry", ModelerServer.Instructions);
    }
}
