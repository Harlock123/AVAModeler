using AVAModeler.Core;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>measure, and Reduce Polygons and Remesh through run_tool.</summary>
public class MeasureHandlerTests
{
    [Fact]
    public async Task Measure_places_points_and_the_selection()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 0.04, 0.02, 0.01 } }), Cancel);
        var places = await Parse(b.Tools.Measure(from: [0, 0, 0], to: [3, 4, 0], cancel: Cancel));
        Assert.Equal(5, (double)places["distance"]!);
        var m = b.Doc.Current.Mesh;
        int lo = Enumerable.Range(0, 8).First(i => m.Points[i].X < 0 && m.Points[i].Y < 0 && m.Points[i].Z < 0);
        int hiX = Enumerable.Range(0, 8).First(i => m.Points[i].X > 0 && m.Points[i].Y < 0 && m.Points[i].Z < 0);
        int up = Enumerable.Range(0, 8).First(i => m.Points[i].X > 0 && m.Points[i].Y > 0 && m.Points[i].Z < 0);
        var angle = await Parse(b.Tools.Measure(points: [lo, hiX, up], cancel: Cancel));
        Assert.Equal(90, (double)angle["angle"]!, 6);
        Assert.Equal(0.04, (double)angle["distance"]!, 9);
        var whole = await Parse(b.Tools.Measure(cancel: Cancel));
        Assert.Equal([0.04, 0.02, 0.01], whole["size"]!.AsArray().Select(x => (double)x!));
        Assert.Contains(whole["text"]!.AsArray(), l => ((string?)l)!.Contains("4 cm × 2 cm × 1 cm"));
        Assert.Contains("two points", await Error(b.Tools.Measure(points: [0], cancel: Cancel)));
        Assert.Contains("both from and to", await Error(b.Tools.Measure(from: [0, 0, 0], cancel: Cancel)));
    }

    [Fact]
    public async Task Reduce_and_remesh_run_as_tools()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Ball", Bench.Args(new { Sides = 48, Segments = 24 }), Cancel);
        int before = b.Doc.Current.Mesh.Polygons.Count;
        await Parse(b.Tools.RunTool("Reduce Polygons", Bench.Args(new { Percent = 25 }), Cancel));
        Assert.True(b.Doc.Current.Mesh.Polygons.Count < before);
        Assert.True((bool)(await Parse(b.Tools.CheckMesh(cancel: Cancel)))["printable"]!);
        await Parse(b.Tools.RunTool("Remesh", Bench.Args(new { Voxel = 0.05 }), Cancel));
        Assert.All(b.Doc.Current.Mesh.Polygons, p => Assert.Equal(4, p.Count));
        Assert.True((bool)(await Parse(b.Tools.CheckMesh(cancel: Cancel)))["printable"]!);
        // An open shape is refused, saying why.
        await b.Tools.SelectPolygons([0], cancel: Cancel);
        Assert.Contains("closed solid", await Error(b.Tools.RunTool("Remesh", Bench.Args(new { Voxel = 0.05 }), Cancel)));
    }
}
