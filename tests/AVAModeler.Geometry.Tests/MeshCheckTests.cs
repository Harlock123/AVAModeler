using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Geometry.Tests.TestShapes;

namespace AVAModeler.Geometry.Tests;

/// <summary>The mesh check on shapes whose answers are known.</summary>
public class MeshCheckTests
{
    private static MeshReport Check(params Mesh[] layers)
    {
        var doc = new Document();
        for (int i = 0; i < layers.Length; i++) doc.Layers[i].Mesh = layers[i];
        doc.SetLayers(Enumerable.Range(0, layers.Length));
        return MeshCheck.Document(doc);
    }

    [Fact]
    public void A_closed_box_is_printable_with_its_volume_area_and_centre()
    {
        var box = Box(new Vec3(1, 2, 3), new Vec3(3, 5, 7)); // 2 × 3 × 4
        var r = Check(box);
        Assert.True(r.Watertight);
        Assert.True(r.Printable);
        Assert.Equal("Closed and printable (1 shell)", r.Verdict);
        Assert.Equal(1, r.Shells);
        Assert.Equal(0, r.Holes);
        Assert.Equal(0, r.BoundaryEdges + r.NonManifoldEdges + r.InconsistentEdges + r.FlippedPolygons + r.DegeneratePolygons);
        // LightWave's clockwise winding, seen from outside, gives a positive volume.
        Assert.Equal(24, r.Volume!.Value, 9);
        Assert.Equal(2 * (2 * 3 + 2 * 4 + 3 * 4), r.Area, 9);
        AssertClose(new Vec3(2, 3.5, 5), r.CenterOfMass!.Value);
        AssertClose(new Vec3(2, 3, 4), r.Bounds.Size);
        Assert.Equal((0, 0, 0, 6, 0), r.Types);
        Assert.Equal(8, r.Points);
        Assert.Contains("Volume 24 m³", r.Lines());
    }

    [Fact]
    public void A_box_missing_a_face_has_one_hole_of_four_edges()
    {
        var box = Box();
        box.RemovePolygons([5]);
        var r = Check(box);
        Assert.False(r.Watertight);
        Assert.False(r.Printable);
        Assert.Equal(4, r.BoundaryEdges);
        Assert.Equal(1, r.Holes);
        Assert.Null(r.Volume);
        Assert.Null(r.CenterOfMass);
        Assert.Equal("Open: 1 hole along 4 edges", r.Verdict);
        var layer = Assert.Single(r.Layers);
        Assert.All(layer.BoundaryEdges, e => Assert.Contains(e, box.EdgeMap().Keys));
    }

    [Fact]
    public void Two_holes_are_two_loops()
    {
        var box = Box();
        box.RemovePolygons([4, 5]); // the -Z and +Z faces: a tube open at both ends
        var r = Check(box);
        Assert.Equal(8, r.BoundaryEdges);
        Assert.Equal(2, r.Holes);
        Assert.Equal("Open: 2 holes along 8 edges", r.Verdict);
    }

    [Fact]
    public void Two_boxes_are_two_shells_and_their_volumes_add()
    {
        var m = Box(2);
        m.Append(Box(new Vec3(5, 0, 0), new Vec3(6, 1, 1)));
        var r = Check(m);
        Assert.Equal(2, r.Shells);
        Assert.True(r.Printable);
        Assert.Equal(9, r.Volume!.Value, 9);
        // The centre of mass leans toward the big one: (8 · 0 + 1 · 5.5) / 9 along X.
        Assert.Equal(5.5 / 9, r.CenterOfMass!.Value.X, 9);
        Assert.Equal("Closed and printable (2 shells)", r.Verdict);
    }

    [Fact]
    public void Layers_are_checked_each_on_its_own_and_added_up()
    {
        var r = Check(Box(2), Box(new Vec3(5, 0, 0), new Vec3(6, 1, 1)));
        Assert.Equal([0, 1], r.Layers.Select(l => l.Layer));
        Assert.Equal(2, r.Shells);
        Assert.Equal(9, r.Volume!.Value, 9);
        Assert.Equal(16, r.Points);
    }

    [Fact]
    public void A_flipped_face_shows_as_inconsistent_winding()
    {
        var box = Box();
        box.Flip(2);
        var r = Check(box);
        Assert.True(r.Watertight);
        Assert.False(r.Printable);
        Assert.Equal(4, r.InconsistentEdges);
        Assert.Equal([2], Assert.Single(r.Layers).FlippedPolygons);
        Assert.Null(r.Volume);
        Assert.StartsWith("Inconsistent winding: 4 edges", r.Verdict);
    }

    [Fact]
    public void A_box_facing_inward_is_inside_out_with_a_negative_volume()
    {
        var box = Box();
        for (int i = 0; i < 6; i++) box.Flip(i);
        var r = Check(box);
        Assert.True(r.Watertight);
        Assert.Equal(0, r.InconsistentEdges);
        Assert.Equal(1, r.InwardShells);
        Assert.Equal(-8, r.Volume!.Value, 9);
        Assert.Equal(6, r.FlippedPolygons);
        Assert.False(r.Printable);
        Assert.Equal("Inside out: 1 closed shell facing inward", r.Verdict);
    }

    [Fact]
    public void An_inward_box_inside_another_is_a_cavity_not_a_mistake()
    {
        var m = Box(4);
        var inner = Box(2);
        for (int i = 0; i < 6; i++) inner.Flip(i);
        m.Append(inner);
        var r = Check(m);
        Assert.True(r.Printable);
        Assert.Equal(1, r.Cavities);
        Assert.Equal(0, r.InwardShells);
        Assert.Equal(0, r.FlippedPolygons);
        Assert.Equal(64 - 8, r.Volume!.Value, 9);
        Assert.Contains(r.Notes(), n => n.StartsWith("1 cavity"));

        // Turn the outer box inside out too: it's the one at fault, and Align would turn only it.
        for (int i = 0; i < 6; i++) m.Flip(i);
        r = Check(m);
        Assert.Equal(1, r.Cavities);
        Assert.Equal(1, r.InwardShells);
        Assert.Equal([0, 1, 2, 3, 4, 5], Assert.Single(r.Layers).FlippedPolygons);
    }

    [Fact]
    public void Duplicate_and_unused_points_are_found()
    {
        var box = Box();
        box.AddPoint(box.Points[0] + new Vec3(1e-8, 0, 0)); // 8: a copy of 0, used by nothing
        box.AddPoint(new Vec3(5, 5, 5));                    // 9: alone
        var r = Check(box);
        var layer = Assert.Single(r.Layers);
        Assert.Equal(1, r.DuplicatePoints);
        Assert.Equal([0, 8], layer.DuplicatePoints);
        Assert.Equal([8, 9], layer.UnusedPoints);
        Assert.True(r.Printable); // still a closed box
        Assert.Contains(r.Notes(), n => n == "1 duplicate point (Merge Points welds them)");
        Assert.Contains(r.Notes(), n => n == "2 unused points");
    }

    [Fact]
    public void Degenerate_polygons_have_no_area_or_repeat_a_point()
    {
        var m = Box();
        int a = m.AddPoint(new Vec3(5, 0, 0)), b = m.AddPoint(new Vec3(6, 0, 0)), c = m.AddPoint(new Vec3(7, 0, 0));
        m.AddPolygon([a, b, c]);           // 6: all on one line
        m.AddPolygon([0, 1, 1]);           // 7: a point twice
        var r = Check(m);
        Assert.Equal([6, 7], Assert.Single(r.Layers).DegeneratePolygons);
        Assert.False(r.Printable);
        Assert.Contains("2 degenerate polygons", r.Verdict);
    }

    [Fact]
    public void Three_faces_on_one_edge_are_non_manifold()
    {
        // Three fins hanging off the edge from (0,0,0) to (0,0,1).
        var m = new Mesh();
        m.Points.AddRange([new Vec3(0, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 1), new Vec3(1, 0, 0), new Vec3(0, 1, 1), new Vec3(0, 1, 0), new Vec3(-1, 0, 1), new Vec3(-1, 0, 0)]);
        m.Polygons.Add(new Polygon([0, 1, 2, 3]));
        m.Polygons.Add(new Polygon([1, 0, 5, 4]));
        m.Polygons.Add(new Polygon([0, 1, 6, 7]));
        m.Touch();
        var r = Check(m);
        Assert.Equal([Edge.Of(0, 1)], Assert.Single(r.Layers).NonManifoldEdges);
        Assert.False(r.Watertight);
        Assert.Contains("not manifold: 1 edge shared by 3 or more polygons", r.Verdict);
    }

    [Fact]
    public void A_ball_holds_close_to_four_thirds_pi_r_cubed()
    {
        var center = new Vec3(1, -2, 0.5);
        var ball = Sphere(center, 0.5, sides: 64, bands: 32);
        var r = Check(ball);
        Assert.True(r.Printable);
        double exact = 4.0 / 3 * Math.PI * 0.125;
        // An inscribed polyhedron falls a little short of the sphere.
        Assert.InRange(r.Volume!.Value, exact * 0.98, exact);
        Assert.InRange(r.Area, 4 * Math.PI * 0.25 * 0.98, 4 * Math.PI * 0.25);
        AssertClose(center, r.CenterOfMass!.Value, 1e-9);
    }

    [Fact]
    public void A_bent_quad_is_non_planar()
    {
        var box = Box();
        box.Points[7] += new Vec3(0.2, 0.2, 0.2);
        box.Touch();
        var r = Check(box);
        // The three faces that meet at the moved corner bend; a little bend is within the tolerance.
        Assert.Equal(3, r.NonPlanarPolygons);
        Assert.True(r.Printable);
        var tiny = Box();
        tiny.Points[7] += new Vec3(1e-5, 1e-5, 1e-5);
        tiny.Touch();
        Assert.Equal(0, Check(tiny).NonPlanarPolygons);
    }

    [Fact]
    public void Dots_and_lines_are_counted_but_dont_open_a_solid()
    {
        var box = Box();
        box.AddPolygon([0, 7]);
        box.AddPolygon([3]);
        var r = Check(box);
        Assert.Equal(2, r.PointsAndLines);
        Assert.Equal([6, 7], Assert.Single(r.Layers).PointsAndLines);
        Assert.True(r.Printable);
        Assert.Equal(6, r.Faces);
    }

    [Fact]
    public void An_empty_foreground_says_so()
    {
        Assert.Equal("Empty: nothing in the foreground", Check(new Mesh()).Verdict);
        var dots = new Mesh();
        dots.AddPoint(Vec3.Zero);
        dots.AddPolygon([0]);
        Assert.Equal("No faces: only points and lines", Check(dots).Verdict);
        Assert.False(Check(dots).Watertight);
    }

    [Fact]
    public void Remove_degenerate_drops_faces_without_area_and_keeps_the_rest()
    {
        var m = Box();
        int a = m.AddPoint(new Vec3(5, 0, 0)), b = m.AddPoint(new Vec3(6, 0, 0)), c = m.AddPoint(new Vec3(7, 0, 0));
        m.AddPolygon([a, b, c]);
        m.AddPolygon([0, 1, 1]);
        m.AddPolygon([0, 1]);
        m.Polygons[0].Points.Insert(1, m.Polygons[0].Points[0]); // a repeat straight after itself: tidied, kept
        m.Touch();
        Assert.Equal(2, PolygonOps.RemoveDegenerate(m));
        Assert.Equal(7, m.Polygons.Count);
        Assert.Equal(4, m.Polygons[0].Count);
        Assert.Equal(2, m.Polygons[^1].Count);
        Assert.True(Check(m).Printable);
    }

    [Fact]
    public void A_small_part_reads_in_millimetres_and_cubic_centimetres()
    {
        var lines = Check(Box(Vec3.Zero, new Vec3(0.02, 0.03, 0.04))).Lines();
        Assert.Contains("Size 20 × 30 × 40 mm (0.02 × 0.03 × 0.04 m)", lines);
        Assert.Contains("Volume 0.000024 m³ (24 cm³)", lines);
        Assert.Contains("Size 2 × 2 × 2 m", Check(Box()).Lines());
    }

    [Fact]
    public void Numbers_read_plainly()
    {
        Assert.Equal("0.000123457", MeshReport.Num(0.000123456789));
        Assert.Equal("24", MeshReport.Num(24));
        Assert.Equal("1234.57", MeshReport.Num(1234.5678));
        Assert.Equal("0", MeshReport.Num(0));
    }
}
