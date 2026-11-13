using AVAModeler.Core;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>sculpt: brushes along strokes, painting maps, and colours in surfaces.</summary>
public class SculptHandlerTests
{
    [Fact]
    public async Task A_stroke_raises_a_ridge_in_one_undo_step()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 2.0, 2, 2 }, SegmentsX = 16, SegmentsY = 16, SegmentsZ = 16 }), Cancel);
        int depth = b.Doc.Undo.Count;
        var r = await Parse(b.Tools.Sculpt("draw", [[-0.6, 1.3, 0], [0.6, 1.3, 0]], radius: 0.3, strength: 1, passes: 2, cancel: Cancel));
        Assert.Equal(depth + 1, b.Doc.Undo.Count);
        Assert.True((int)r["dabs_per_pass"]! >= 16);
        var m = b.Doc.Current.Mesh;
        Assert.True(m.Points.Max(p => p.Y) > 1.03);
        Assert.True((bool)(await Parse(b.Tools.CheckMesh(cancel: Cancel)))["printable"]!);
        await Parse(b.Tools.Undo(Cancel));
        Assert.Equal(1, b.Doc.Current.Mesh.Points.Max(p => p.Y), 9);

        Assert.Contains("no brush \"chisel\"", await Error(b.Tools.Sculpt("chisel", [[0, 1, 0]], cancel: Cancel)));
        Assert.Contains("at least one point", await Error(b.Tools.Sculpt("draw", [], cancel: Cancel)));
    }

    [Fact]
    public async Task Grab_pulls_and_paint_colors_shows_up_in_the_surface()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Ball", Bench.Args(new { Radius = new[] { 1.0, 1, 1 } }), Cancel);
        await Parse(b.Tools.Sculpt("grab", [[0, 1, 0], [0, 1.5, 0]], radius: 0.5, cancel: Cancel));
        Assert.True(b.Doc.Current.Mesh.Points.Max(p => p.Y) > 1.45);

        var paint = await Parse(b.Tools.Sculpt("paint_colors", [[1, 0, 0]], radius: 0.6, strength: 1, map: "Paint", color: [0, 0, 255], cancel: Cancel));
        Assert.Equal("Paint", (string?)paint["shown_map"]);
        var surf = await Parse(b.Tools.SetSurface("Default", vertex_color_map: "Paint", vertex_color_intensity: 0.5, assign: false, cancel: Cancel));
        Assert.Equal("Paint", (string?)surf["surface"]!["vertex_color_map"]);
        Assert.Contains("Paint", (await Parse(b.Tools.GetDocument(cancel: Cancel)))["color_maps"]!.AsArray().Select(x => (string?)x));

        var w = await Parse(b.Tools.Sculpt("paint_weights", [[0, -1, 0]], radius: 0.5, strength: 1, map: "Foot", mode: "set", value: 0.8, cancel: Cancel));
        Assert.Equal("Foot", (string?)w["shown_map"]);
        Assert.True(b.Doc.Current.Mesh.FindPointMap("Foot")!.Values.Count > 0);
    }
}
