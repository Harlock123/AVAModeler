using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools.Tests;

/// <summary>Spline Patch, Rail Extrude's smooth rail and Magnet's falloff shapes.</summary>
public class CurveToolTests
{
    /// <summary>A closed loop of lines through the points, in layer 1.</summary>
    private static (Document Doc, ToolContext Ctx, int[] Points) Loop(params Vec3[] points)
    {
        var (d, c) = Make.Empty();
        var m = d.Current.Mesh;
        var ids = points.Select(p => m.AddPoint(p)).ToArray();
        for (int i = 0; i < ids.Length; i++) m.AddPolygon([ids[i], ids[(i + 1) % ids.Length]]);
        return (d, c, ids);
    }

    private static double FaceArea(Mesh m) => m.Polygons.Where(p => p.Count >= 3).Sum(p => m.Area(p));

    [Fact]
    public void A_square_loop_is_patched_with_a_flat_grid_of_quads()
    {
        var (d, c, _) = Loop(new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1));
        new SplinePatchTool { Across = 10, Along = 5 }.Run(c);
        var m = d.Current.Mesh;
        var faces = m.Polygons.Where(p => p.Count >= 3).ToList();
        Assert.Equal(50, faces.Count);
        Assert.All(faces, f => Assert.Equal(4, f.Count));
        Assert.Equal(1, FaceArea(m), 9);
        Assert.All(m.Points, p => Assert.Equal(0, p.Y, 12));
        // The lines are kept; the grid's points are shared, (10 + 1) × (5 + 1) of them with the loop's four corners among them.
        Assert.Equal(4, m.Polygons.Count(p => p.Count == 2));
        Assert.Equal(66, m.Points.Distinct().Count());
        // All facing one way; Flip turns them all.
        var n0 = m.Normal(faces[0]);
        Assert.All(faces, f => Assert.True(Vec3.Dot(m.Normal(f), n0) > 0.999));
        var (d2, c2, _) = Loop(new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1));
        new SplinePatchTool { Flip = true, KeepCurves = false }.Run(c2);
        var m2 = d2.Current.Mesh;
        Assert.DoesNotContain(m2.Polygons, p => p.Count == 2);
        Assert.True(Vec3.Dot(m2.Normal(m2.Polygons[0]), n0) < -0.999);
    }

    [Fact]
    public void A_curved_four_sided_loop_is_met_exactly_along_every_side()
    {
        // A loop round a disc of radius 1 in 32 points: four sides of 8, between the corners chosen as points.
        var pts = Enumerable.Range(0, 32).Select(i => new Vec3(Math.Cos(i * Math.PI / 16), 0, Math.Sin(i * Math.PI / 16))).ToArray();
        var (d, c, ids) = Loop(pts);
        d.Selection.Mode = SelectionMode.Points;
        foreach (int i in (int[])[0, 8, 16, 24]) d.Selection.PointsIn(0).Add(ids[i]);
        new SplinePatchTool { Across = 8, Along = 8, SmoothSides = false }.Run(c);
        var m = d.Current.Mesh;
        // Close to the disc's area (π), and every loop point is on the patch's edge.
        Assert.InRange(FaceArea(m), Math.PI * 0.95, Math.PI);
        Assert.All(pts, p => Assert.Contains(m.Points, q => Vec3.Near(p, q, 1e-9)));
        Assert.All(m.Points, p => Assert.True(p.X * p.X + p.Z * p.Z <= 1 + 1e-9));
    }

    [Fact]
    public void A_three_sided_loop_narrows_to_triangles_at_its_first_corner()
    {
        var (d, c, _) = Loop(new(0, 0, 0), new(2, 0, 0), new(1, 0, 2));
        new SplinePatchTool { Sides = 3, Across = 4, Along = 4 }.Run(c);
        var m = d.Current.Mesh;
        var faces = m.Polygons.Where(p => p.Count >= 3).ToList();
        Assert.Equal(2, FaceArea(m), 9);
        Assert.Contains(faces, f => f.Count == 3);
        Assert.Contains(faces, f => f.Count == 4);
    }

    [Fact]
    public void Spline_patch_says_what_it_needs()
    {
        // An open chain doesn't close; no lines at all; too few points for four sides.
        var (d, c) = Make.Empty();
        var m = d.Current.Mesh;
        int a = m.AddPoint(Vec3.Zero), b = m.AddPoint(new Vec3(1, 0, 0)), e = m.AddPoint(new Vec3(1, 0, 1));
        m.AddPolygon([a, b]);
        m.AddPolygon([b, e]);
        Assert.Contains("close into a loop", Assert.Throws<InvalidOperationException>(() => new SplinePatchTool().Run(c)).Message);
        var (_, c2) = Make.Empty();
        Assert.Contains("closed loop", Assert.Throws<InvalidOperationException>(() => new SplinePatchTool().Run(c2)).Message);
        var (_, c3, _) = Loop(new(0, 0, 0), new(1, 0, 0), new(1, 0, 1));
        Assert.Contains("at least 4 points", Assert.Throws<InvalidOperationException>(() => new SplinePatchTool().Run(c3)).Message);
    }

    [Fact]
    public void A_smooth_rail_rounds_its_corners()
    {
        Document Run(bool smooth)
        {
            var (d, c) = Make.Empty();
            var path = d.Layers[1].Mesh;
            int a = path.AddPoint(Vec3.Zero), b = path.AddPoint(new Vec3(0, 2, 0)), e = path.AddPoint(new Vec3(2, 2, 0));
            path.AddPolygon([a, b]);
            path.AddPolygon([b, e]);
            d.Current.Mesh.AddFace(0, new Vec3(-0.1, 0, -0.1), new Vec3(-0.1, 0, 0.1), new Vec3(0.1, 0, 0.1), new Vec3(0.1, 0, -0.1));
            d.SetLayers([0], [1]);
            new RailExtrudeTool { Segments = 16, SmoothRail = smooth }.Run(c);
            return d;
        }
        // Straight, the first leg stays within the profile's 0.1 either side of X = 0; smoothed, it bends before the
        // corner (still passing through the rail's points), so the leg strays past that.
        static int Strays(Document d) => d.Current.Mesh.Points.Count(p => p.Y < 1.9 && Math.Abs(p.X) > 0.1 + 1e-6);
        Assert.Equal(0, Strays(Run(false)));
        Assert.True(Strays(Run(true)) > 0);
        Assert.Equal(1 + 16 * 4, Run(true).Current.Mesh.Polygons.Count);
    }

    [Fact]
    public void Smoothing_goes_through_the_points_and_ends_where_the_line_does()
    {
        Vec3[] pts = [new(0, 0, 0), new(1, 1, 0), new(2, 0, 0), new(3, 1, 0)];
        var curve = Curves.Smooth(pts, 8);
        Assert.Equal(3 * 8 + 1, curve.Count);
        foreach (var p in pts) Assert.Contains(curve, q => Vec3.Near(p, q, 1e-9));
        Assert.Equal(pts[^1], curve[^1]);
        // Never overshooting far past the points' own band.
        Assert.All(curve, q => Assert.InRange(q.Y, -0.2, 1.2));
    }

    [Theory]
    [InlineData(MagnetFalloff.Smooth)]
    [InlineData(MagnetFalloff.Linear)]
    [InlineData(MagnetFalloff.Sharp)]
    [InlineData(MagnetFalloff.Dome)]
    public void Each_falloff_pulls_fully_at_the_centre_and_not_at_all_at_the_radius(MagnetFalloff falloff)
    {
        Assert.Equal(1, MagnetTool.Weight(falloff, 0), 12);
        Assert.Equal(0, MagnetTool.Weight(falloff, 1), 12);
        double last = 1;
        for (double r = 0.05; r <= 1; r += 0.05)
        {
            double w = MagnetTool.Weight(falloff, r);
            Assert.True(w <= last + 1e-12);
            last = w;
        }
        Assert.Equal(0.5, MagnetTool.Weight(MagnetFalloff.Linear, 0.5), 12);
        // Dome holds on further out than Sharp does.
        Assert.True(MagnetTool.Weight(MagnetFalloff.Dome, 0.5) > MagnetTool.Weight(MagnetFalloff.Sharp, 0.5));
        var (d, c) = Make.Empty();
        new BoxTool { Size = new Vec3(4, 0, 4), SegmentsX = 8, SegmentsZ = 8 }.Run(c);
        new MagnetTool { Center = Vec3.Zero, Radius = Vec3.One, Offset = new Vec3(0, 1, 0), Falloff = falloff }.Run(c);
        Assert.Equal(1, d.Current.Mesh.Points.Max(p => p.Y), 9);
    }
}
