using AVAModeler.Core;

namespace AVAModeler.Tools.Tests;

/// <summary>Drawing curves (Pen, Make Curve), reshaping them (Smooth Curve) and skinning across them (Loft).</summary>
public class CurveDrawingTests
{
    private static List<Polygon> Lines(Mesh m) => [.. m.Polygons.Where(p => p.Count == 2)];

    [Fact]
    public void Pen_draws_a_smooth_curve_through_the_clicks()
    {
        Vec3[] clicks = [new(0, 0, 0), new(1, 1, 0), new(2, 0, 0), new(3, 1, 0)];
        var (d, c) = Make.Empty();
        var pen = new PenTool { PerSpan = 4 };
        pen.Begin(c);
        foreach (var p in clicks) pen.Drag(Make.FrontDrag(Vec3.Zero, p));
        pen.Commit();
        var m = d.Current.Mesh;
        // Three spans of four lines, through every click exactly, the lines selected.
        Assert.Equal(12, Lines(m).Count);
        Assert.All(clicks, p => Assert.Contains(p, m.Points));
        Assert.Equal(12, d.Selection.PolygonsIn(0).Count);
        // Smooth: the points between leave the straight lines (the first runs along y = x).
        Assert.Contains(m.Points, p => p.X is > 0 and < 1 && Math.Abs(p.Y - p.X) > 0.01);
        // Closed and straight: four lines, a loop.
        var (d2, c2) = Make.Empty();
        var loop = new PenTool { Closed = true, Smooth = false };
        loop.Begin(c2);
        foreach (var p in clicks) loop.Drag(Make.FrontDrag(Vec3.Zero, p));
        loop.Commit();
        var lines = Lines(d2.Current.Mesh);
        Assert.Equal(4, lines.Count);
        Assert.Equal([3, 0], lines[^1].Points);
        Assert.False(new PenTool().Scriptable);
    }

    [Fact]
    public void Make_curve_runs_through_the_selected_points_in_order()
    {
        var (d, c) = Make.Empty();
        var m = d.Current.Mesh;
        int a = m.AddPoint(new Vec3(0, 0, 0)), b = m.AddPoint(new Vec3(1, 0, 0)), e = m.AddPoint(new Vec3(1, 1, 0));
        d.Selection.SelectPoint(0, e);
        d.Selection.SelectPoint(0, a);
        d.Selection.SelectPoint(0, b);
        new MakeCurveTool { Smooth = false }.Run(c);
        Assert.Equal([[e, a], [a, b]], Lines(m).Select(p => p.Points.ToArray()));
        Assert.Equal(SelectionMode.Polygons, d.Selection.Mode);
        // Smooth and closed: through the three, eight pieces a span, round to the first.
        d.Selection.Clear(allModes: true);
        d.Selection.Mode = SelectionMode.Points;
        foreach (int p in new[] { a, b, e }) d.Selection.SelectPoint(0, p);
        new MakeCurveTool { Closed = true }.Run(c);
        Assert.Equal(2 + 24, Lines(m).Count);
        Assert.Equal(3 + 21, m.Points.Count);
        // One point isn't a curve.
        d.Selection.Clear(allModes: true);
        d.Selection.Mode = SelectionMode.Points;
        d.Selection.SelectPoint(0, a);
        Assert.Contains("two or more points", Assert.Throws<InvalidOperationException>(() => new MakeCurveTool().Run(c)).Message);
        Assert.Equal(26, Lines(m).Count);
    }

    [Fact]
    public void Smooth_curve_rounds_a_zigzag_or_lays_it_out_evenly()
    {
        var (d, c) = Make.Empty();
        var m = d.Current.Mesh;
        var ids = new[] { new Vec3(0, 0, 0), new Vec3(1, 1, 0), new Vec3(2, 0, 0), new Vec3(3, 1, 0) }.Select(m.AddPoint).ToList();
        for (int i = 0; i < 3; i++) m.AddPolygon([ids[i], ids[i + 1]]);
        int lone = m.AddPoint(new Vec3(9, 9, 9));
        new SmoothCurveTool { PerSpan = 4 }.Run(c);
        Assert.Equal(12, Lines(m).Count);
        Assert.Equal(4 + 1 + 9, m.Points.Count);
        // Laid out in ten even pieces: the old middle points gone, the ends and the lone point kept.
        new SmoothCurveTool { Pieces = 10 }.Run(c);
        var lines = Lines(m);
        Assert.Equal(10, lines.Count);
        Assert.Contains(new Vec3(9, 9, 9), m.Points);
        Assert.Contains(new Vec3(0, 0, 0), m.Points);
        Assert.Contains(new Vec3(3, 1, 0), m.Points);
        Assert.DoesNotContain(new Vec3(1, 1, 0), m.Points);
        var lengths = lines.Select(l => Vec3.Distance(m.Points[l.Points[0]], m.Points[l.Points[1]])).ToList();
        // Even along the curve: the straight lines between cut the bends a little shorter.
        Assert.True(lengths.Max() - lengths.Min() < 0.1 * lengths.Average(), string.Join(", ", lengths));
        _ = lone;
        var (d2, c2) = Make.Empty();
        Assert.Contains("three or more points", Assert.Throws<InvalidOperationException>(() => new SmoothCurveTool().Run(c2)).Message);
    }

    /// <summary>Adds a curve of lines through points, closed if asked.</summary>
    private static void Curve(Mesh m, bool closed, params Vec3[] points)
    {
        var ids = points.Select(m.AddPoint).ToArray();
        for (int i = 0; i + 1 < ids.Length || closed && i < ids.Length; i++) m.AddPolygon([ids[i], ids[(i + 1) % ids.Length]]);
    }

    private static Vec3[] Circle(double radius, double y, int n = 12) =>
        [.. Enumerable.Range(0, n).Select(i => new Vec3(radius * Math.Cos(2 * Math.PI * i / n), y, radius * Math.Sin(2 * Math.PI * i / n)))];

    [Fact]
    public void Loft_skins_open_curves_into_a_sheet_whichever_way_they_run()
    {
        var (d, c) = Make.Empty();
        var m = d.Current.Mesh;
        Curve(m, false, new(0, 0, 0), new(2, 0, 0));
        // The second runs the other way: the sheet isn't twisted.
        Curve(m, false, new(2, 0, 1), new(0, 0, 1));
        new LoftTool { Across = 4, Along = 2 }.Run(c);
        var faces = m.Polygons.Where(p => p.Count == 4).ToList();
        Assert.Equal(8, faces.Count);
        Assert.Equal(2, faces.Sum(f => m.Area(f)), 9);
        // All facing the same way.
        var n0 = m.Normal(faces[0]);
        Assert.All(faces, f => Assert.Equal(1, Vec3.Dot(m.Normal(f), n0), 9));
        Assert.Equal(8, d.Selection.PolygonsIn(0).Count);
    }

    [Fact]
    public void Loft_skins_closed_cross_sections_into_a_tube_in_the_order_they_stack()
    {
        var (d, c) = Make.Empty();
        var m = d.Current.Mesh;
        // A vase: the middle section drawn last, and started at another point; smooth through all three.
        Curve(m, true, Circle(1, 0));
        Curve(m, true, Circle(0.5, 2));
        Curve(m, true, [.. Circle(1.2, 1).Skip(5), .. Circle(1.2, 1).Take(5)]);
        new LoftTool { Across = 24, Along = 4, KeepCurves = false }.Run(c);
        Assert.Empty(Lines(m));
        var faces = m.Polygons.Where(p => p.Count == 4).ToList();
        Assert.Equal(24 * 8, faces.Count);
        // A tube: every edge inside it is met once each way (no twist, one winding), only the two rims open.
        var edges = faces.SelectMany(f => f.Points.Select((p, i) => (p, f.Points[(i + 1) % 4]))).ToHashSet();
        Assert.Equal(24 * 2, edges.Count(e => !edges.Contains((e.Item2, e.Item1))));
        // Through the middle section, and bulging smoothly round it (past it between the curves, not straight).
        Assert.Contains(m.Points, p => Math.Abs(p.Y - 1) < 1e-9 && Math.Abs(new Vec3(p.X, 0, p.Z).Length - 1.2) < 0.02);
        Assert.Contains(m.Points, p => p.Y is > 0.4 and < 0.6 && new Vec3(p.X, 0, p.Z).Length > 1.15);
        // Mixed open and closed, or one curve alone, is refused.
        var (d2, c2) = Make.Empty();
        Curve(d2.Current.Mesh, true, Circle(1, 0));
        Assert.Contains("two or more separate curves", Assert.Throws<InvalidOperationException>(() => new LoftTool().Run(c2)).Message);
        Curve(d2.Current.Mesh, false, new(0, 1, 0), new(1, 1, 0));
        Assert.Contains("all open or all closed", Assert.Throws<InvalidOperationException>(() => new LoftTool().Run(c2)).Message);
    }
}
