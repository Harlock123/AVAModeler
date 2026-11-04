using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>Select Loop and Ring, Loop Cut, Bridge, Fill Hole, Inset and Chamfer as tools: selections, undo, UVs and macros.</summary>
public class EdgeToolsTests
{
    private static Dictionary<string, object?> Args(params (string, object?)[] a) => a.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything about a layer's mesh as text, UVs included, to compare before and after.</summary>
    private static string Dump(Mesh m) =>
        string.Join(";", m.Points.Select(p => p.ToString())) + "|" +
        string.Join(";", m.Polygons.Select(p => $"{p}{(p.CornerUVs == null ? "" : string.Join(",", p.CornerUVs.SelectMany(kv => kv.Value.Select(c => $"{kv.Key}:{c.Key}={c.Value}"))))}")) + "|" +
        string.Join(";", m.UVMaps.Select(u => u.Name + ":" + string.Join(",", u.Points.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))));

    private static (Document Doc, Commands Cmd) UVBox(int segX = 1)
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Box", Args(("Size", new[] { 2.0, 2, 2 }), ("SegmentsX", segX)));
        cmd.MakeUVs("Wrap", UVProjection.Cubic, Axis.Y);
        return (d, cmd);
    }

    private static void AssertSolidAndMapped(Document d)
    {
        var r = MeshCheck.Document(d);
        Assert.True(r.Printable, string.Join("; ", r.Issues()));
        var m = d.Current.Mesh;
        Assert.All(m.Polygons, p => Assert.True(m.IsMapped("Wrap", p), $"{p} has corners without UVs"));
    }

    /// <summary>Runs a step, checks it, undoes it and checks the layer is exactly as it was; then redoes it.</summary>
    private static void UndoesExactly(Document d, Action step)
    {
        string before = Dump(d.Current.Mesh);
        step();
        Assert.NotEqual(before, Dump(d.Current.Mesh));
        string after = Dump(d.Current.Mesh);
        d.UndoLast();
        Assert.Equal(before, Dump(d.Current.Mesh));
        d.RedoLast();
        Assert.Equal(after, Dump(d.Current.Mesh));
    }

    [Fact]
    public void Select_loop_and_ring_from_an_edge_or_two_points()
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Disc", Args(("Sides", 12), ("Segments", 3), ("Height", 2.0)));
        var m = d.Current.Mesh;
        int Find(Vec3 v) => Enumerable.Range(0, m.Points.Count).First(i => Vec3.Near(m.Points[i], v, 1e-6));
        // An upright edge on the side: its ring goes round (12), its loop runs up the side (3).
        int low = Find(new Vec3(0.5, -1 / 3.0, 0)), high = Find(new Vec3(0.5, 1 / 3.0, 0));
        cmd.SelectEdges([(low, high)]);
        Assert.Equal(12, cmd.SelectRing());
        Assert.Equal(SelectionMode.Edges, d.Selection.Mode);
        cmd.SelectNone();
        cmd.SelectEdges([(low, high)]);
        Assert.Equal(3, cmd.SelectLoop());
        // Two neighbouring points round the middle: the loop round the cylinder.
        cmd.SelectNone();
        cmd.SelectMode(SelectionMode.Points);
        var ring = m.Neighbours(high).First(n => Math.Abs(m.Points[n].Y - 1 / 3.0) < 1e-6);
        cmd.SelectPoints([high, ring]);
        Assert.Equal(12, cmd.SelectLoop());
        Assert.All(d.Selection.EdgesIn(0), e => Assert.Equal(1 / 3.0, m.Points[e.A].Y, 6));
        Assert.Throws<ArgumentException>(() => cmd.SelectEdges([(0, 999)]));
    }

    [Fact]
    public void Loop_cut_on_a_uv_mapped_box_keeps_it_closed_mapped_and_undoable()
    {
        var (d, cmd) = UVBox();
        cmd.SelectEdges([(0, 1)]);
        UndoesExactly(d, () => cmd.Run("Loop Cut", Args(("Count", 2))));
        AssertSolidAndMapped(d);
        Assert.Equal(8 + 8, d.Current.Mesh.Points.Count);
        Assert.Equal(6 + 8, d.Current.Mesh.Polygons.Count);
        // The new loops are selected, ready for a Chamfer or another cut.
        Assert.Equal(SelectionMode.Edges, d.Selection.Mode);
        Assert.Equal(8, d.Selection.EdgesIn(0).Count);
        Assert.Equal(8, MeshCheck.Document(d).Volume!.Value, 9);

        // With no edge selected, it says what it needs and changes nothing.
        var (d2, cmd2) = UVBox();
        string before = Dump(d2.Current.Mesh);
        int steps = d2.Undo.Count;
        var e = Assert.Throws<InvalidOperationException>(() => cmd2.Run("Loop Cut"));
        Assert.Contains("edge", e.Message);
        Assert.Equal(before, Dump(d2.Current.Mesh));
        Assert.Equal(steps, d2.Undo.Count);
    }

    [Fact]
    public void Inset_group_and_individual_on_a_box_with_a_split_top()
    {
        foreach (var (mode, added) in new[] { ("Group", 6), ("Individual", 8) })
        {
            var (d, cmd) = UVBox(segX: 2);
            Assert.Equal(10, d.Current.Mesh.Polygons.Count);
            cmd.SelectMode(SelectionMode.Polygons);
            cmd.SelectInside(new Box3(new Vec3(-2, 0.99, -2), new Vec3(2, 2, 2)));
            Assert.Equal(2, d.Selection.PolygonsIn(0).Count);
            UndoesExactly(d, () => cmd.Run("Inset", Args(("Mode", mode), ("Thickness", 0.2), ("Depth", 0.25))));
            Assert.Equal(10 + added, d.Current.Mesh.Polygons.Count);
            AssertSolidAndMapped(d);
            // The inset faces stay selected, raised by the depth.
            Assert.Equal(2, d.Selection.PolygonsIn(0).Count);
            Assert.All(d.Selection.PolygonsIn(0), f => Assert.Equal(1.25, d.Current.Mesh.Centroid(d.Current.Mesh.Polygons[f]).Y, 9));
        }
    }

    [Fact]
    public void Inset_corners_take_interpolated_uvs()
    {
        var (d, cmd) = UVBox();
        var m = d.Current.Mesh;
        int top = Enumerable.Range(0, m.Polygons.Count).First(i => m.Normal(i).Y > 0.9);
        var uvs = m.CornerUVsOf("Wrap", m.Polygons[top]).Select(u => u!.Value).ToList();
        cmd.SelectPolygons([top]);
        cmd.Run("Inset", Args(("Thickness", 0.5)));
        var inner = m.CornerUVsOf("Wrap", m.Polygons[top]).Select(u => u!.Value).ToList();
        // Half the side in from each edge of a 2 × 2 face: the inner square's UVs are a quarter of the way in.
        var (min, max) = (new Vec2(uvs.Min(u => u.X), uvs.Min(u => u.Y)), new Vec2(uvs.Max(u => u.X), uvs.Max(u => u.Y)));
        foreach (var uv in inner)
        {
            Assert.Equal(0.25, Math.Min(uv.X - min.X, max.X - uv.X) / (max.X - min.X), 9);
            Assert.Equal(0.25, Math.Min(uv.Y - min.Y, max.Y - uv.Y) / (max.Y - min.Y), 9);
        }
    }

    [Fact]
    public void Chamfer_one_edge_or_every_edge_of_a_mapped_box()
    {
        var (d, cmd) = UVBox();
        cmd.SelectEdges([(0, 1)]);
        UndoesExactly(d, () => cmd.Run("Chamfer", Args(("Width", 0.2))));
        Assert.Equal(10, d.Current.Mesh.Points.Count);
        Assert.Equal(7, d.Current.Mesh.Polygons.Count);
        AssertSolidAndMapped(d);
        Assert.Equal(8 - 0.04, MeshCheck.Document(d).Volume!.Value, 9);

        var (all, allCmd) = UVBox();
        var tool = (ChamferTool)allCmd.Run("Chamfer", Args(("Width", 0.25), ("Segments", 3)));
        Assert.Equal(12, tool.Chamfered);
        AssertSolidAndMapped(all);

        // A lone quad has no edge with a face on each side.
        var lone = new Commands(new Document());
        lone.Run("Box", Args(("Size", new[] { 1.0, 1, 0 })));
        var e = Assert.Throws<InvalidOperationException>(() => lone.Run("Chamfer"));
        Assert.Contains("face on each side", e.Message);
    }

    [Fact]
    public void Fill_hole_closes_the_holes_the_selection_touches_or_all_of_them()
    {
        var (d, cmd) = UVBox();
        cmd.SelectPolygons([0]);
        cmd.Run("Remove");
        var m = d.Current.Mesh;
        Assert.Equal(1, MeshCheck.Document(d).Holes);
        // A face across the box from the hole doesn't touch it: nothing to fill.
        int far = Enumerable.Range(0, m.Polygons.Count).First(i => m.Polygons[i].Points.All(p => !MeshCheck.Document(d).Layers[0].BoundaryEdges.Any(e => e.A == p || e.B == p)));
        cmd.SelectPolygons([far]);
        Assert.Equal(0, ((FillHoleTool)cmd.Run("Fill Hole")).Filled);
        UndoesExactly(d, () => Assert.Equal(1, ((FillHoleTool)cmd.Run("Fill Hole", Args(("Mode", "Triangles")))).Filled));
        Assert.True(MeshCheck.Document(d).Printable);
        Assert.Equal(8, MeshCheck.Document(d).Volume!.Value, 9);
    }

    [Fact]
    public void Bridge_two_boxes_by_faces_or_by_their_holes_and_refuse_a_mismatch()
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Box", Args(("Center", new[] { 0.5, 0.5, 0.5 })));
        cmd.Run("Box", Args(("Center", new[] { 2.5, 0.5, 0.5 })));
        cmd.SelectMode(SelectionMode.Polygons);
        cmd.SelectInside(new Box3(new Vec3(0.9, -1, -1), new Vec3(2.1, 2, 2)));
        Assert.Equal(2, d.Selection.PolygonsIn(0).Count);
        UndoesExactly(d, () => cmd.Run("Bridge", Args(("Segments", 2))));
        var r = MeshCheck.Document(d);
        Assert.True(r.Printable);
        Assert.Equal(1, r.Shells);
        Assert.Equal(3, r.Volume!.Value, 9);

        // A box and an octagonal tube, open toward each other: 4 points against 8.
        var d2 = new Document();
        var cmd2 = new Commands(d2);
        cmd2.Run("Box", Args(("Center", new[] { 0.0, 0, 0 })));
        cmd2.Run("Disc", Args(("Center", new[] { 0.0, 2, 0 }), ("Sides", 8)));
        cmd2.SelectMode(SelectionMode.Polygons);
        cmd2.SelectInside(new Box3(new Vec3(-1, 0.49, -1), new Vec3(1, 1.51, 1)));
        cmd2.Run("Remove");
        Assert.Equal(2, MeshCheck.Document(d2).Holes);
        cmd2.SelectNone();
        var e = Assert.Throws<ArgumentException>(() => cmd2.Run("Bridge"));
        Assert.Contains("same number of points", e.Message);
        Assert.Equal(2, MeshCheck.Document(d2).Holes);
    }

    [Fact]
    public void Macros_select_edges_loops_and_rings_and_run_the_new_tools()
    {
        var cmd = new Commands(new Document());
        Macros.Run("""
            tool("Box", { Size = {2, 2, 2}, SegmentsX = 2 })
            seledges({{1, 2}})
            print(selring())
            deselect()
            seledges({{1, 2}})
            print(selloop())
            tool("Loop Cut", { Position = 0.5 })
            print(polycount())
            selmode("polygons"); selbox(-2, 0.99, -2, 2, 2, 2)
            tool("Inset", { Mode = "Group", Thickness = 0.1, Depth = -0.2 })
            deselect()
            tool("Chamfer", { Width = 0.05 })
            local c = checkmesh()
            print(c.printable)
            """, cmd);
        // The upright edge at a corner: its ring goes round the box's six side quads; its loop stops at the corners.
        Assert.Equal(["6", "1", "16", "true"], cmd.Output);
    }
}
