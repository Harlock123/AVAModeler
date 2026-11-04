using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

/// <summary>Edge loops and rings, Loop Cut, Bridge, Fill Hole, Inset and Chamfer, each checked by the mesh check.</summary>
public class EdgeToolTests
{
    private static Edge EdgeAt(Mesh m, Vec3 a, Vec3 b)
    {
        int Find(Vec3 v) => Enumerable.Range(0, m.Points.Count).First(i => Vec3.Near(m.Points[i], v, 1e-9));
        return Edge.Of(Find(a), Find(b));
    }

    /// <summary>Every corner's UVs equal its point's X and Y (as <see cref="MapXY"/> laid them, linear in position).</summary>
    private static void AssertUVsFollowPositions(Mesh m, double tol = 1e-9)
    {
        foreach (var poly in m.Polygons)
            foreach (int p in poly.Points)
            {
                Assert.True(m.TryGetUV("UV", poly, p, out var uv), $"point {p} has no UVs");
                Assert.Equal(m.Points[p].X, uv.X, tol);
                Assert.Equal(m.Points[p].Y, uv.Y, tol);
            }
    }

    // ───────────────────────────── loops and rings ─────────────────────────────

    [Fact]
    public void A_loop_runs_straight_across_a_grid_and_a_rim_edge_loops_round_the_rim()
    {
        var grid = Grid(4, 4); // points 1 apart, from -2 to 2
        var loop = EdgeLoops.Loop(grid, EdgeAt(grid, new Vec3(-1, 0, 0), new Vec3(0, 0, 0)));
        Assert.Equal(4, loop.Count);
        Assert.All(loop, e => Assert.Equal(0, grid.Points[e.A].Z, 9));
        Assert.All(loop, e => Assert.Equal(0, grid.Points[e.B].Z, 9));
        var rim = EdgeLoops.Loop(grid, EdgeAt(grid, new Vec3(-2, 0, -2), new Vec3(-1, 0, -2)));
        Assert.Equal(16, rim.Count);
    }

    [Fact]
    public void A_triangle_stops_a_loop()
    {
        var grid = Grid(4, 4);
        // Cut the quad at x 1..2, z 0..1 into triangles: the loop along z = 0 stops at x = 1.
        int quad = Enumerable.Range(0, grid.Polygons.Count).First(i => Vec3.Near(grid.Centroid(grid.Polygons[i]), new Vec3(1.5, 0, 0.5), 1e-9));
        Triangulator.Triple(grid, [quad]);
        var loop = EdgeLoops.Loop(grid, EdgeAt(grid, new Vec3(-1, 0, 0), new Vec3(0, 0, 0)));
        Assert.Equal(3, loop.Count);
        Assert.DoesNotContain(loop, e => grid.Points[e.A].X > 1.5 || grid.Points[e.B].X > 1.5);
    }

    [Fact]
    public void A_ring_wraps_round_a_cylinder_and_a_loop_wraps_round_its_middle()
    {
        var cyl = Cylinder(sides: 8, segments: 2);
        var upright = EdgeAt(cyl, new Vec3(1, 0, 0), new Vec3(1, 1, 0));
        var ring = EdgeLoops.Ring(cyl, upright);
        Assert.Equal(8, ring.Count);
        Assert.True(EdgeLoops.Strip(cyl, upright).Closed);
        // Upright edges loop up the side through the middle ring but stop at the caps (an n-gon there).
        Assert.Equal(2, EdgeLoops.Loop(cyl, upright).Count);
        var middle = EdgeLoops.Loop(cyl, EdgeAt(cyl, new Vec3(1, 1, 0), cyl.Points[9]));
        Assert.Equal(8, middle.Count);
        Assert.All(middle, e => Assert.Equal(1, cyl.Points[e.A].Y, 9));
        // Round a box, a ring crosses four faces.
        var box = Box();
        Assert.Equal(4, EdgeLoops.Ring(box, Edge.Of(0, 1)).Count);
    }

    // ───────────────────────────── loop cut ─────────────────────────────

    [Fact]
    public void Loop_cut_round_a_box_keeps_it_closed_with_its_volume()
    {
        var box = Box(); // 2 × 2 × 2, points 0..7
        MapXY(box);
        var cut = EdgeLoops.LoopCut(box, Edge.Of(0, 1), 0.25);
        Assert.Equal(4, cut.Count);
        Assert.Equal(12, box.Points.Count);
        Assert.Equal(10, box.Polygons.Count);
        AssertSolid(box);
        Assert.Equal(8, MeshCheck.Mesh(box).Volume!.Value, 9);
        // Edge 0-1 runs along X from -1 to 1: the cut is a quarter of the way along, at x = -0.5.
        Assert.All(Enumerable.Range(8, 4), i => Assert.Equal(-0.5, box.Points[i].X, 9));
        AssertUVsFollowPositions(box);

        var three = Box();
        EdgeLoops.LoopCut(three, Edge.Of(0, 1), 0.5, 3);
        Assert.Equal(8 + 12, three.Points.Count);
        Assert.Equal(6 + 12, three.Polygons.Count);
        AssertSolid(three);
    }

    [Fact]
    public void Loop_cut_round_a_cylinder_and_across_one_into_its_caps()
    {
        var cyl = Cylinder(sides: 8);
        EdgeLoops.LoopCut(cyl, EdgeAt(cyl, new Vec3(1, 0, 0), new Vec3(1, 2, 0)));
        Assert.Equal(24, cyl.Points.Count);
        Assert.Equal(8 * 2 + 2, cyl.Polygons.Count);
        AssertSolid(cyl);

        // Across: the ring of a rim edge is one quad between the two caps, which take the new points.
        var across = Cylinder(sides: 8);
        MapXY(across);
        var cut = EdgeLoops.LoopCut(across, EdgeAt(across, new Vec3(1, 0, 0), across.Points[1]));
        Assert.Single(cut);
        Assert.Equal(18, across.Points.Count);
        Assert.Equal(11, across.Polygons.Count);
        Assert.Equal(2, across.Polygons.Count(p => p.Count == 9));
        AssertSolid(across);
        AssertUVsFollowPositions(across);
    }

    // ───────────────────────────── fill hole ─────────────────────────────

    [Fact]
    public void Fill_hole_closes_a_box_missing_a_face_every_way()
    {
        foreach (var mode in Enum.GetValues<FillMode>())
        {
            var box = Box();
            box.RemovePolygons([5]);
            var loops = Holes.BoundaryLoops(box);
            Assert.Single(loops);
            Assert.Equal(4, loops[0].Count);
            var made = Holes.Fill(box, loops, mode);
            Assert.Equal(mode == FillMode.Triangles ? 2 : 1, made.Count);
            AssertSolid(box);
            Assert.Equal(8, MeshCheck.Mesh(box).Volume!.Value, 9);
        }
        // An octagonal hole as a ladder of three quads.
        var cyl = Cylinder(sides: 8);
        cyl.RemovePolygons([cyl.Polygons.Count - 1]);
        var quads = Holes.Fill(cyl, Holes.BoundaryLoops(cyl), FillMode.Quads);
        Assert.Equal(3, quads.Count);
        Assert.All(quads, q => Assert.Equal(4, cyl.Polygons[q].Count));
        AssertSolid(cyl);
    }

    // ───────────────────────────── bridge ─────────────────────────────

    /// <summary>Two unit boxes one apart along X, each open on the side facing the other.</summary>
    private static Mesh TwoOpenBoxes(int rotateSecond = 0)
    {
        var a = Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        a.RemovePolygons([1]); // +X
        var b = Box(new Vec3(2, 0, 0), new Vec3(3, 1, 1));
        b.RemovePolygons([0]); // -X
        foreach (var p in b.Polygons)
            for (int k = 0; k < rotateSecond; k++)
            {
                p.Points.Add(p.Points[0]);
                p.Points.RemoveAt(0);
            }
        a.Append(b);
        return a;
    }

    [Fact]
    public void Bridging_two_holes_joins_two_open_boxes_into_one_closed_solid()
    {
        for (int rotate = 0; rotate < 4; rotate++)
        {
            var m = TwoOpenBoxes(rotate);
            var made = Holes.BridgeHoles(m);
            Assert.Equal(4, made.Count);
            AssertSolid(m);
            Assert.Equal(3, MeshCheck.Mesh(m).Volume!.Value, 9);
            Assert.Single(MeshCheck.Mesh(m).Shells);
            // No twist: every rung runs straight along X.
            foreach (int f in made)
                foreach (var (x, y) in m.Polygons[f].Edges())
                {
                    var d = m.Points[y] - m.Points[x];
                    Assert.True(Math.Abs(d.Length - 1) < 1e-9, $"edge {d} of the bridge isn't 1 long");
                }
        }
        var segmented = TwoOpenBoxes();
        Holes.BridgeHoles(segmented, 3);
        Assert.Equal(16 + 8, segmented.Points.Count);
        AssertSolid(segmented);
        Assert.Equal(3, MeshCheck.Mesh(segmented).Volume!.Value, 9);
    }

    [Fact]
    public void Bridging_two_faces_removes_them_and_joins_their_outlines()
    {
        var m = Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        m.Append(Box(new Vec3(2, 0, 0), new Vec3(3, 1, 1)));
        var made = Holes.BridgeFaces(m, [1, 6], 2);
        Assert.Equal(8, made.Count);
        Assert.Equal(10 + 8, m.Polygons.Count);
        Assert.Equal(16 + 4, m.Points.Count);
        AssertSolid(m);
        Assert.Equal(3, MeshCheck.Mesh(m).Volume!.Value, 9);
    }

    [Fact]
    public void Bridging_edge_loops_needs_matching_counts()
    {
        var m = TwoOpenBoxes();
        var rims = Holes.BoundaryLoops(m);
        var edges = rims.SelectMany(l => l.Select((p, i) => Edge.Of(p, l[(i + 1) % l.Count]))).ToList();
        Holes.BridgeEdges(m, edges);
        AssertSolid(m);

        var box = Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1));
        box.RemovePolygons([3]); // +Y: a square hole
        var tube = Cylinder(sides: 8, radius: 0.5, height: 1);
        tube.Transform(Mat4.Translation(new Vec3(0.5, 2, 0.5)));
        tube.RemovePolygons([8]); // the bottom cap: an octagonal hole
        box.Append(tube);
        var all = Holes.BoundaryLoops(box).SelectMany(l => l.Select((p, i) => Edge.Of(p, l[(i + 1) % l.Count]))).ToList();
        var e = Assert.Throws<ArgumentException>(() => Holes.BridgeEdges(box, all));
        Assert.Contains("same number of points", e.Message);
        Assert.Contains("4", e.Message);
        Assert.Contains("8", e.Message);
        Assert.Throws<ArgumentException>(() => Holes.BridgeHoles(Box()));
    }

    // ───────────────────────────── inset ─────────────────────────────

    [Fact]
    public void Inset_one_face_of_a_box_stays_closed_and_a_depth_adds_a_frustum()
    {
        var flat = Box();
        MapXY(flat);
        Insets.Apply(flat, [4], 0.5, 0); // the -Z face, in the XY plane: UVs follow X and Y exactly
        Assert.Equal(12, flat.Points.Count);
        Assert.Equal(10, flat.Polygons.Count);
        AssertSolid(flat);
        Assert.Equal(8, MeshCheck.Mesh(flat).Volume!.Value, 9);
        AssertUVsFollowPositions(flat);

        var raised = Box();
        Insets.Apply(raised, [3], 0.5, 0.5); // the top, 2 × 2, inset to 1 × 1 and raised 0.5
        AssertSolid(raised);
        Assert.Equal(8 + 0.5 / 3 * (4 + 1 + 2), MeshCheck.Mesh(raised).Volume!.Value, 9);
    }

    [Fact]
    public void Inset_as_a_group_puts_quads_round_the_region_only()
    {
        var group = Grid(2, 2);
        Insets.Apply(group, [0, 1, 2, 3], 0.25, 0, InsetMode.Group);
        Assert.Equal(9 + 8, group.Points.Count);
        Assert.Equal(4 + 8, group.Polygons.Count);
        var c = MeshCheck.Mesh(group);
        Assert.Empty(c.InconsistentEdges);
        Assert.Equal(8, c.BoundaryEdges.Count);
        Assert.Equal(4, c.Area, 9);

        var each = Grid(2, 2);
        Insets.Apply(each, [0, 1, 2, 3], 0.25, 0, InsetMode.Individual);
        Assert.Equal(9 + 16, each.Points.Count);
        Assert.Equal(4 + 16, each.Polygons.Count);
        Assert.Empty(MeshCheck.Mesh(each).InconsistentEdges);

        // A group bevel on two faces of a box: the region moves out as one.
        var box = Box();
        Insets.Apply(box, [3, 5], 0.2, 0.3, InsetMode.Group);
        AssertSolid(box);
        Assert.Equal(6 + 6, box.Polygons.Count);
    }

    // ───────────────────────────── chamfer ─────────────────────────────

    [Fact]
    public void Chamfering_one_box_edge_cuts_off_a_prism()
    {
        var box = Box(); // 2 × 2 × 2
        MapXY(box);
        Assert.Equal(1, Chamfer.Apply(box, [Edge.Of(2, 6)], 0.2)); // the edge at x = -1, y = 1, along Z
        Assert.Equal(10, box.Points.Count);
        Assert.Equal(7, box.Polygons.Count);
        AssertSolid(box);
        Assert.Equal(8 - 0.2 * 0.2 / 2 * 2, MeshCheck.Mesh(box).Volume!.Value, 9);
        AssertUVsFollowPositions(box);

        var round = Box();
        Chamfer.Apply(round, [Edge.Of(2, 6)], 0.2, 4);
        Assert.Equal(6 + 2 * (4 + 1), round.Points.Count);
        Assert.Equal(6 + 4, round.Polygons.Count);
        AssertSolid(round);
        double v = MeshCheck.Mesh(round).Volume!.Value;
        // Between a flat chamfer and a true quarter circle of radius 0.2.
        Assert.InRange(v, 8 - 0.04, 8 - (1 - Math.PI / 4) * 0.04 * 2 + 1e-9);
        // The arc's middle point is the radius from the arc's centre.
        var centre = new Vec3(-0.8, 0.8, -1);
        Assert.Contains(Enumerable.Range(0, round.Points.Count), i => Math.Abs(Vec3.Distance(round.Points[i], centre) - 0.2) < 1e-9 && Math.Abs(round.Points[i].X + 0.8 - 0.2 * Math.Cos(Math.PI / 4) * -1) < 1e-9);
    }

    [Fact]
    public void Chamfering_where_three_edges_meet_closes_the_corner()
    {
        var corner = Box();
        // The three edges at point 7 (1, 1, 1).
        Chamfer.Apply(corner, [Edge.Of(3, 7), Edge.Of(5, 7), Edge.Of(6, 7)], 0.2);
        Assert.Equal(1, corner.Polygons.Count(p => p.Count == 3));
        AssertSolid(corner);

        var all = Box();
        MapXY(all);
        Assert.Equal(12, Chamfer.Apply(all, all.EdgeMap().Keys.ToList(), 0.2, 1));
        Assert.Equal(24, all.Points.Count);
        Assert.Equal(6 + 12 + 8, all.Polygons.Count);
        AssertSolid(all);

        var rounded = Box();
        Chamfer.Apply(rounded, rounded.EdgeMap().Keys.ToList(), 0.2, 3);
        AssertSolid(rounded);

        // A loop of four edges round the top: the strips share their ends at the corners.
        var top = Box();
        Chamfer.Apply(top, [Edge.Of(2, 6), Edge.Of(6, 7), Edge.Of(7, 3), Edge.Of(3, 2)], 0.2, 2);
        AssertSolid(top);
        Assert.Equal(6 + 4 * 2, top.Polygons.Count);
    }
}
