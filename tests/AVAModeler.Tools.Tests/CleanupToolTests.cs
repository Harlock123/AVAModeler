using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>Shrinkwrap, Spin Edge, Dissolve Edges and Weld.</summary>
public class CleanupToolTests
{
    /// <summary>A ball of radius 1 in layer 2 (the background) and a box of side 3 in layer 1 (the foreground).</summary>
    private static (Document, ToolContext) BoxOverBall()
    {
        var (d, c) = Make.Empty();
        d.SetLayers([1]);
        new BallTool { Radius = Vec3.One, Sides = 48, Segments = 24 }.Run(c);
        d.SetLayers([0], [1]);
        new BoxTool { Size = new Vec3(3, 3, 3), SegmentsX = 3, SegmentsY = 3, SegmentsZ = 3 }.Run(c);
        return (d, c);
    }

    [Fact]
    public void Shrinkwrap_puts_points_on_the_background_its_offset_out_and_part_way_with_less_strength()
    {
        var (d, c) = BoxOverBall();
        var before = d.Layers[0].Mesh.Points.ToArray();
        var tool = new ShrinkwrapTool();
        tool.Run(c);
        Assert.Equal(before.Length, tool.Moved);
        Assert.All(d.Layers[0].Mesh.Points, p => Assert.InRange(p.Length, 0.99, 1.0 + 1e-9));
        Assert.True(MeshCheck.Mesh(d.Layers[0].Mesh).Watertight);
        d.UndoLast();
        Assert.Equal(before, d.Layers[0].Mesh.Points);

        new ShrinkwrapTool { Offset = 0.1 }.Run(c);
        Assert.All(d.Layers[0].Mesh.Points, p => Assert.InRange(p.Length, 1.09, 1.1 + 1e-6));
        d.UndoLast();
        new ShrinkwrapTool { Mode = ShrinkwrapMode.AlongNormal, Strength = 0.5 }.Run(c);
        // Halfway from the box's corner (2.6 out) to the ball.
        var corner = d.Layers[0].Mesh.Points.MaxBy(p => p.Length);
        Assert.InRange(corner.Length, 1.7, 1.85);
        d.UndoLast();
        new ShrinkwrapTool { MaxDistance = 1 }.Run(c);
        Assert.Contains(d.Layers[0].Mesh.Points, p => p.Length > 2);

        d.SetLayers([0]);
        Assert.Throws<InvalidOperationException>(() => new ShrinkwrapTool().Run(c));
    }

    [Fact]
    public void Shrinkwrap_keeps_symmetric_partners_mirrored()
    {
        var (d, c) = BoxOverBall();
        d.Symmetry = Axis.X;
        new ShrinkwrapTool { Mode = ShrinkwrapMode.AlongNormal }.Run(c);
        var pts = d.Layers[0].Mesh.Points;
        foreach (var p in pts) Assert.Contains(pts, q => Vec3.Near(q, p with { X = -p.X }, 1e-9));
    }

    /// <summary>Two faces side by side in the XZ plane facing up: a 2 × 1 strip of quads, or a square cut into two triangles.</summary>
    private static (Document, ToolContext) Pair(bool triangles)
    {
        var (d, c) = Make.Empty();
        var m = d.Current.Mesh;
        if (triangles)
        {
            int a = m.AddPoint(new Vec3(0, 0, 0)), b = m.AddPoint(new Vec3(0, 0, 1)), cc = m.AddPoint(new Vec3(1, 0, 1)), dd = m.AddPoint(new Vec3(1, 0, 0));
            m.AddPolygon([a, b, cc]);
            m.AddPolygon([a, cc, dd]);
        }
        else
        {
            for (int i = 0; i < 3; i++) { m.AddPoint(new Vec3(i, 0, 0)); m.AddPoint(new Vec3(i, 0, 1)); }
            m.AddPolygon([0, 1, 3, 2]);
            m.AddPolygon([2, 3, 5, 4]);
        }
        foreach (var p in m.Polygons) if (m.Normal(p).Y < 0) p.Points.Reverse();
        m.Touch();
        return (d, c);
    }

    private static void SelectEdge(Document d, int a, int b)
    {
        d.Selection.Mode = SelectionMode.Edges;
        d.Selection.EdgesIn(0).Clear();
        d.Selection.EdgesIn(0).Add(Edge.Of(a, b));
    }

    [Fact]
    public void Spin_Edge_swaps_two_triangles_diagonal_and_turns_two_quads_edge()
    {
        var (d, c) = Pair(triangles: true);
        SelectEdge(d, 0, 2);
        new SpinEdgeTool().Run(c);
        var m = d.Current.Mesh;
        Assert.True(m.EdgeMap().ContainsKey(Edge.Of(1, 3)));
        Assert.False(m.EdgeMap().ContainsKey(Edge.Of(0, 2)));
        Assert.All(m.Polygons, p => { Assert.Equal(3, p.Count); Assert.True(m.Normal(p).Y > 0.99); });
        Assert.Equal(1, m.Polygons.Sum(p => m.Area(p)), 9);
        d.UndoLast();
        Assert.True(d.Current.Mesh.EdgeMap().ContainsKey(Edge.Of(0, 2)));

        var (q, qc) = Pair(triangles: false);
        SelectEdge(q, 2, 3);
        new SpinEdgeTool().Run(qc);
        var qm = q.Current.Mesh;
        Assert.False(qm.EdgeMap().ContainsKey(Edge.Of(2, 3)));
        Assert.All(qm.Polygons, p => { Assert.Equal(4, p.Count); Assert.True(qm.Normal(p).Y > 0.99); });
        Assert.Equal(2, qm.Polygons.Sum(p => qm.Area(p)), 9);
        q.Selection.Mode = SelectionMode.Edges;
        Assert.Throws<InvalidOperationException>(() => new SpinEdgeTool().Run(qc));
    }

    [Fact]
    public void Dissolve_Edges_joins_two_quads_and_drops_the_points_left_in_line()
    {
        var (d, c) = Pair(triangles: false);
        SelectEdge(d, 2, 3);
        new DissolveEdgesTool().Run(c);
        var m = d.Current.Mesh;
        var face = Assert.Single(m.Polygons);
        Assert.Equal(4, face.Count);
        Assert.Equal(4, m.Points.Count);
        Assert.True(m.Normal(face).Y > 0.99);
        d.UndoLast();
        SelectEdge(d, 2, 3);
        new DissolveEdgesTool { CleanUp = false }.Run(c);
        Assert.Equal(6, Assert.Single(d.Current.Mesh.Polygons).Count);

        // On a closed box, the joined face keeps the box closed.
        var (b, bc) = Make.WithCube();
        var e = b.Current.Mesh.EdgeMap().Keys.First();
        SelectEdge(b, e.A, e.B);
        new DissolveEdgesTool().Run(bc);
        Assert.Equal(5, b.Current.Mesh.Polygons.Count);
        Assert.True(MeshCheck.Mesh(b.Current.Mesh).Watertight);
    }

    [Fact]
    public void Weld_joins_halves_down_the_middle_onto_the_mirror_plane()
    {
        var (d, c) = Make.Empty();
        var m = d.Current.Mesh;
        // Two halves of a strip, each with its own points a hair either side of x = 0.
        int a = m.AddPoint(new Vec3(-1, 0, 0)), b = m.AddPoint(new Vec3(-0.0003, 0, 0)), cc = m.AddPoint(new Vec3(-0.0003, 0, 1)), dd = m.AddPoint(new Vec3(-1, 0, 1));
        int e = m.AddPoint(new Vec3(0.0002, 0, 0)), f = m.AddPoint(new Vec3(1, 0, 0)), g = m.AddPoint(new Vec3(1, 0, 1)), h = m.AddPoint(new Vec3(0.0002, 0, 1));
        m.AddPolygon([a, dd, cc, b]);
        m.AddPolygon([e, h, g, f]);
        d.Symmetry = Axis.X;
        var tool = new WeldTool { Distance = 0.001 };
        tool.Run(c);
        Assert.Equal(6, d.Current.Mesh.Points.Count);
        Assert.Equal(2, tool.Result.Merged);
        Assert.Equal(4, tool.Result.OnPlane);
        Assert.Equal(2, d.Current.Mesh.Points.Count(p => p.X == 0));
        Assert.True(MeshCheck.Mesh(d.Current.Mesh).NonManifoldEdges.Count == 0);
    }
}
