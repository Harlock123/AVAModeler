using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>The edge selection tools (select_edges, select_loop, select_ring) and the edge and face tools through run_tool.</summary>
public class EdgeHandlerTests
{
    private static Dictionary<string, System.Text.Json.JsonElement> P(object o) => Bench.Args(o);

    [Fact]
    public async Task Select_edges_then_its_ring_and_loop_cut_across_it()
    {
        using var b = new Bench();
        await Parse(b.Tools.RunTool("Disc", P(new { Sides = 12, Segments = 2, Height = 2.0 }), Cancel));
        var m = b.Doc.Current.Mesh;
        int low = Enumerable.Range(0, m.Points.Count).First(i => Vec3.Near(m.Points[i], new Vec3(0.5, -1, 0), 1e-6));
        int mid = m.Neighbours(low).First(n => Vec3.Near(m.Points[n], new Vec3(0.5, 0, 0), 1e-6));
        var sel = await Parse(b.Tools.SelectEdges([[low, mid]], cancel: Cancel));
        Assert.Equal("edges", (string?)sel["mode"]);
        Assert.Equal(1, (int)sel["selected"]!);
        var ring = await Parse(b.Tools.SelectRing(Cancel));
        Assert.Equal(12, (int)ring["selected"]!);
        await Parse(b.Tools.SelectEdges([[low, mid]], cancel: Cancel));
        var loop = await Parse(b.Tools.SelectLoop(Cancel));
        Assert.Equal(2, (int)loop["selected"]!); // up the side, stopping at the caps

        // Loop Cut across the upright edge: a new loop round the cylinder, one undo step.
        await Parse(b.Tools.SelectEdges([[low, mid]], cancel: Cancel));
        int depth = b.Doc.Undo.Count;
        var cut = await Parse(b.Tools.RunTool("Loop Cut", P(new { Position = 0.25 }), Cancel));
        Assert.Equal(12 * 2 + 2, (int)cut["polygons"]!["before"]!);
        Assert.Equal(12 * 3 + 2, (int)cut["polygons"]!["after"]!);
        Assert.Equal(depth + 1, b.Doc.Undo.Count);
        var check = await Parse(b.Tools.CheckMesh(cancel: Cancel));
        Assert.True((bool)check["printable"]!);
        await Parse(b.Tools.Undo(Cancel));
        Assert.Equal(12 * 2 + 2, b.Doc.Current.Mesh.Polygons.Count);
    }

    [Fact]
    public async Task Edge_selection_mistakes_are_explained()
    {
        using var b = new Bench();
        await Parse(b.Tools.RunTool("Box", null, Cancel));
        Assert.Contains("aren't joined by an edge", await Error(b.Tools.SelectEdges([[0, 6]], cancel: Cancel)));
        Assert.Contains("numbered 0 to 7", await Error(b.Tools.SelectEdges([[0, 60]], cancel: Cancel)));
        Assert.Contains("two point indices", await Error(b.Tools.SelectEdges([[0, 1, 2]], cancel: Cancel)));
        Assert.Contains("select_edges", await Error(b.Tools.SelectLoop(Cancel)));
        Assert.Contains("select_edges", await Error(b.Tools.SelectRing(Cancel)));
        // Loop Cut with no edge: an error, nothing changed, no step.
        int depth = b.Doc.Undo.Count;
        Assert.Contains("needs an edge", await Error(b.Tools.RunTool("Loop Cut", null, Cancel)));
        Assert.Equal(depth, b.Doc.Undo.Count);
        Assert.Equal(6, b.Doc.Current.Mesh.Polygons.Count);
        // Agents are told about the edge tools.
        Assert.Contains("select_loop", ModelerServer.Instructions);
        Assert.Contains("\"Chamfer\"", ModelerServer.Instructions);
    }

    [Fact]
    public async Task An_agent_rounds_a_box_bridges_two_and_fills_a_hole_through_a_client()
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        var c = s.Client;
        // Two boxes, bridged face to face into one solid.
        await c.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 0.5, 0.5, 0.5 } } });
        await c.Json("run_tool", new { name = "Box", @params = new { Center = new[] { 2.5, 0.5, 0.5 } } });
        await c.Json("set_selection_mode", new { mode = "polygons" });
        await c.Json("select_box", new { min = new[] { 0.9, -1, -1 }, max = new[] { 2.1, 2, 2 } });
        await c.Json("run_tool", new { name = "Bridge", @params = new { Segments = 2 } });
        var check = await c.Json("check_mesh");
        Assert.True((bool)check["printable"]!);
        Assert.Equal(3, (double)check["volume_m3"]!, 9);
        // Round every edge and check again.
        await c.Json("run_tool", new { name = "Chamfer", @params = new { Width = 0.1, Segments = 2 } });
        check = await c.Json("check_mesh");
        Assert.True((bool)check["printable"]!, check.ToJsonString());
        // Knock a face out, see the hole, and fill it.
        await c.Json("select_polygons", new { indices = new[] { 0 } });
        await c.Json("run_tool", new { name = "Remove" });
        Assert.False((bool)(await c.Json("check_mesh"))["printable"]!);
        await c.Json("run_tool", new { name = "Fill Hole" });
        Assert.True((bool)(await c.Json("check_mesh"))["printable"]!);
        // Edges by their points, through the client.
        var edge = b.Doc.Current.Mesh.EdgeMap().Keys.First(e => b.Doc.Current.Mesh.EdgeMap()[e].Count == 2);
        var sel = await c.Json("select_edges", new { edges = new[] { new[] { edge.A, edge.B } } });
        Assert.Equal(1, (int)sel["selected"]!);
        var mismatch = await c.Fails("run_tool", new { name = "Bridge" });
        Assert.Contains("Bridge", mismatch);
    }
}
