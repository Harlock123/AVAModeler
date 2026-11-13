using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Geometry.Tests;

/// <summary>Reduce Polygons, Remesh, welding Booleans and measuring the selection.</summary>
public class ProcessingTests
{
    private static MeshReport Check(Mesh m)
    {
        var doc = new Document();
        doc.Layers[0].Mesh = m;
        return MeshCheck.Document(doc);
    }

    [Theory]
    [InlineData(1e-5)]
    [InlineData(3e-6)]
    [InlineData(1e-6)]
    [InlineData(1e-7)]
    [InlineData(1e-8)]
    public void Near_coplanar_faces_leave_no_slivers_or_cracks(double gap)
    {
        foreach (var op in new[] { BooleanOperation.Union, BooleanOperation.Subtract, BooleanOperation.Intersect })
        {
            var a = TestShapes.Box(new Vec3(-1, -1, -1), new Vec3(1, 1, 1));
            var b = TestShapes.Box(new Vec3(0.3, -0.5 + gap * 0.7, -0.4), new Vec3(1 + gap, 1 + gap, 0.6 - gap * 1.3));
            var r = Booleans.Apply(op, a, b);
            var report = Check(r);
            Assert.True(report.Printable, $"{op} at {gap}: {string.Join("; ", report.Issues())}");
            double expected = op switch { BooleanOperation.Union => 8, BooleanOperation.Subtract => 6.95, _ => 1.05 };
            Assert.Equal(expected, MeshMeasure.Volume(r), 3);
        }
        // Without welding the old trouble is still there to be had.
        var x = TestShapes.Box(new Vec3(-1, -1, -1), new Vec3(1, 1, 1));
        var y = TestShapes.Box(new Vec3(0.3, -0.5, -0.4), new Vec3(1 + 1e-7, 1 + 1e-7, 0.6));
        Assert.False(Check(Booleans.Apply(BooleanOperation.Union, x, y, new BooleanOptions { Weld = false })).Printable);
    }

    [Fact]
    public void Reduce_keeps_a_closed_shape_closed_and_near_its_volume()
    {
        var ball = TestShapes.Sphere(Vec3.Zero, 1, 48, 24);
        double volume = MeshMeasure.Volume(ball);
        int tris = ball.Polygons.Sum(p => p.Count - 2);
        var r = Decimate.Reduce(ball, Enumerable.Range(0, ball.Polygons.Count).ToList(), tris / 4);
        Assert.Equal(tris, r.TrianglesBefore);
        Assert.InRange(r.TrianglesAfter, tris / 4 - 2, tris / 4 + 2);
        Assert.True(Check(ball).Printable, string.Join("; ", Check(ball).Issues()));
        Assert.Equal(volume, MeshMeasure.Volume(ball), volume * 0.03);
        Assert.All(ball.Polygons, p => Assert.Equal(3, p.Count));
    }

    [Fact]
    public void Reduce_leaves_borders_seams_and_other_faces_where_they_are()
    {
        // A flat grid: only its inside points may go, and a flat grid reduces to almost nothing inside.
        var grid = new Mesh();
        int n = 10;
        for (int i = 0; i <= n; i++) for (int j = 0; j <= n; j++) grid.AddPoint(new Vec3(i, 0, j));
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) grid.AddPolygon([i * (n + 1) + j, i * (n + 1) + j + 1, (i + 1) * (n + 1) + j + 1, (i + 1) * (n + 1) + j]);
        var border = Enumerable.Range(0, grid.Points.Count).Where(k => grid.Points[k].X is 0 or 10 || grid.Points[k].Z is 0 or 10).Select(k => grid.Points[k]).ToHashSet();
        var w = grid.AddPointMap("W", PointMapKind.Weight);
        for (int k = 0; k < grid.Points.Count; k++) w.SetWeight(k, 0.5);
        Decimate.Reduce(grid, Enumerable.Range(0, grid.Polygons.Count).ToList(), 1);
        Assert.All(border, p => Assert.Contains(p, grid.Points));
        Assert.True(grid.Points.Count < 50);
        Assert.All(grid.Polygons, p => Assert.True(grid.Normal(p).Y > 0.99, "no face turned over"));
        Assert.All(Enumerable.Range(0, grid.Points.Count), k => Assert.Equal(0.5, grid.FindPointMap("W")!.Weight(k), 6));
        // The whole area is still covered.
        Assert.Equal(100, grid.Polygons.Sum(grid.Area), 6);
    }

    [Fact]
    public void Remesh_makes_an_even_closed_skin_of_quads()
    {
        var box = TestShapes.Box(2);
        var r = Remesh.Apply(box, Enumerable.Range(0, box.Polygons.Count).ToList(), 0.2, 2);
        Assert.Equal(6, r.FacesBefore);
        var report = Check(box);
        Assert.True(report.Printable, string.Join("; ", report.Issues()));
        Assert.All(box.Polygons, p => Assert.Equal(4, p.Count));
        Assert.True(box.Polygons.Count > 300);
        Assert.Equal(8, MeshMeasure.Volume(box), 0.6);
        var b = box.Bounds();
        Assert.Equal(2, b.Size.X, 0.15);

        var ball = TestShapes.Sphere(Vec3.Zero, 1, 24, 12);
        double v = MeshMeasure.Volume(ball);
        Remesh.Apply(ball, Enumerable.Range(0, ball.Polygons.Count).ToList(), 0.1, 3);
        Assert.True(Check(ball).Printable);
        Assert.Equal(v, MeshMeasure.Volume(ball), v * 0.05);

        var open = TestShapes.Box(2);
        open.RemovePolygons([0]);
        Assert.Throws<InvalidOperationException>(() => Remesh.Apply(open, Enumerable.Range(0, open.Polygons.Count).ToList(), 0.2));
        Assert.Throws<ArgumentException>(() => Remesh.Apply(TestShapes.Box(2), [0, 1, 2, 3, 4, 5], 0.001));
    }

    [Fact]
    public void The_selection_is_measured()
    {
        var doc = new Document();
        var m = doc.Layers[0].Mesh;
        m.Append(TestShapes.Box(2));
        var sel = doc.Selection;
        sel.Mode = SelectionMode.Points;
        int a = Enumerable.Range(0, 8).First(i => m.Points[i] == new Vec3(-1, -1, -1));
        int b = Enumerable.Range(0, 8).First(i => m.Points[i] == new Vec3(1, -1, -1));
        int c = Enumerable.Range(0, 8).First(i => m.Points[i] == new Vec3(1, 1, -1));
        sel.SelectPoint(0, a);
        sel.SelectPoint(0, b);
        var two = SelectionMeasure.Of(doc);
        Assert.Equal(2, two.Distance);
        Assert.Contains("Distance: 2 m (X 2 m, Y 0 m, Z 0 m)", two.Lines());
        sel.SelectPoint(0, c);
        Assert.Equal(90, SelectionMeasure.Of(doc).Angle!.Value, 9);
        sel.Clear(allModes: true);
        sel.Mode = SelectionMode.Polygons;
        sel.PolygonsIn(0).Add(0);
        var one = SelectionMeasure.Of(doc);
        Assert.Equal(4, one.Area, 9);
        Assert.Equal(8, one.Perimeter);
        Assert.Equal("1.5 cm", SelectionMeasure.Length(0.015));
        Assert.Equal("2.5 mm", SelectionMeasure.Length(0.0025));
    }
}
