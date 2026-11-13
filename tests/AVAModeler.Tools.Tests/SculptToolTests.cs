using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>The sculpt brushes and the colour tools.</summary>
public class SculptToolTests
{
    private static Dictionary<string, object?> Args(params (string, object?)[] a) => a.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    /// <summary>A flat grid of quads, 2 m across in X and Z, facing up (+Y), 20 squares a side.</summary>
    private static (Document Doc, Commands Cmd) Plane()
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Box", Args(("Size", new[] { 2.0, 0, 2 }), ("SegmentsX", 20), ("SegmentsZ", 20)));
        var m = d.Current.Mesh;
        // A box of no height is two grids on top of each other: keep the top one.
        m.RemovePolygons(Enumerable.Range(0, m.Polygons.Count).Where(i => m.Normal(i).Y < 0.5).ToList());
        m.RemoveUnusedPoints();
        m.MergePoints(1e-9);
        return (d, cmd);
    }

    private static int Find(Mesh m, Vec3 v) => Enumerable.Range(0, m.Points.Count).First(i => Vec3.Near(m.Points[i], v, 1e-6));

    [Fact]
    public void Draw_raises_the_middle_most_and_invert_digs()
    {
        var (d, cmd) = Plane();
        var m = d.Current.Mesh;
        Assert.True(m.Normal(0).Y > 0.9);
        int middle = Find(m, Vec3.Zero), near = Find(m, new Vec3(0.4, 0, 0));
        cmd.Run("Draw Brush", Args(("Center", new[] { 0.0, 0, 0 }), ("Radius", 0.5), ("Strength", 1.0)));
        m = d.Current.Mesh;
        Assert.Equal(0.05, m.Points[middle].Y, 9);
        Assert.True(m.Points[near].Y > 0 && m.Points[near].Y < 0.05);
        Assert.Equal(0, m.Points.Where(p => Math.Abs(p.X) > 0.55).Max(p => Math.Abs(p.Y)), 12);
        cmd.Run("Draw Brush", Args(("Center", new[] { 0.0, 0.05, 0 }), ("Radius", 0.5), ("Strength", 1.0), ("Invert", true)));
        Assert.True(d.Current.Mesh.Points[middle].Y < 0.01);
    }

    [Fact]
    public void Flatten_smooth_pinch_and_inflate_do_what_they_say()
    {
        var (d, cmd) = Plane();
        var m = d.Current.Mesh;
        int middle = Find(m, Vec3.Zero), side = Find(m, new Vec3(0.2, 0, 0));
        var round = Enumerable.Range(0, m.Points.Count).Where(i => m.Points[i].X * m.Points[i].X + m.Points[i].Z * m.Points[i].Z < 0.04).ToList();
        cmd.Run("Draw Brush", Args(("Center", new[] { 0.0, 0, 0 }), ("Radius", 0.3), ("Strength", 1.0)));
        double peak = m.Points[middle].Y;
        cmd.Run("Smooth Brush", Args(("Center", new[] { 0.0, 0, 0 }), ("Radius", 0.3), ("Strength", 1.0)));
        Assert.True(m.Points[middle].Y < peak);
        cmd.Run("Flatten Brush", Args(("Center", new[] { 0.0, 0, 0 }), ("Radius", 0.6), ("Strength", 1.0)));
        Assert.True(round.Max(i => m.Points[i].Y) - round.Min(i => m.Points[i].Y) < peak / 4);
        double before = m.Points[side].X;
        cmd.Run("Pinch Brush", Args(("Center", new[] { 0.0, 0, 0 }), ("Radius", 0.5), ("Strength", 1.0)));
        Assert.True(m.Points[side].X < before);

        var ball = new Document();
        var bc = new Commands(ball);
        bc.Run("Ball", Args(("Radius", new[] { 1.0, 1, 1 })));
        double volume = MeshMeasure.Volume(ball.Current.Mesh);
        bc.Run("Inflate Brush", Args(("Center", new[] { 0.0, 1, 0 }), ("Radius", 0.8), ("Strength", 1.0)));
        Assert.True(MeshMeasure.Volume(ball.Current.Mesh) > volume);
        Assert.True(MeshCheck.Document(ball).Printable);
    }

    [Fact]
    public void Grab_moves_what_is_under_the_brush_and_symmetry_mirrors_it()
    {
        var (d, cmd) = Plane();
        var m = d.Current.Mesh;
        d.Symmetry = Axis.X;
        cmd.Run("Grab Brush", Args(("Center", new[] { 0.5, 0, 0 }), ("Radius", 0.3), ("Offset", new[] { 0.0, 0.4, 0 })));
        Assert.Equal(0.4, m.Points[Find(m, new Vec3(0.5, 0.4, 0))].Y, 9);
        Assert.Equal(0.4, m.Points[Find(m, new Vec3(-0.5, 0.4, 0))].Y, 9);
        Assert.Equal(0, m.Points[Find(m, new Vec3(0, 0, 0))].Y, 12);
        d.UndoLast();
        Assert.Equal(0, d.Current.Mesh.Points.Max(p => Math.Abs(p.Y)), 12);
    }

    [Fact]
    public void A_stroke_is_one_step_and_lands_where_the_surface_is_now()
    {
        var (d, _) = Plane();
        int mid = Find(d.Current.Mesh, Vec3.Zero);
        var tool = new DrawBrushTool { Radius = 0.4, Strength = 1 };
        tool.Begin(new ToolContext(d));
        // Two drags straight down onto the middle: the second lands on the raised surface, not the flat one.
        var down = new Ray(new Vec3(0, 5, 0), new Vec3(0, -1, 0));
        tool.Drag(new DragInfo { Ray = down, First = true });
        tool.Drag(new DragInfo { Ray = new Ray(new Vec3(0.2, 5, 0), new Vec3(0, -1, 0)) });
        tool.Commit();
        var m = d.Current.Mesh;
        var middle = m.Points[mid];
        // The first dab raised the middle by 0.04; the second, landing on it, adds a share of the same.
        Assert.True(middle.Y > 0.04, $"{middle}");
        Assert.Equal(2, d.Undo.Count); // the plane, then the one stroke
        // Pen pressure scales the dab.
        var (light, _) = Plane();
        var soft = new DrawBrushTool { Radius = 0.4, Strength = 1 };
        soft.Begin(new ToolContext(light));
        soft.Drag(new DragInfo { Ray = down, First = true, Pressure = 0.5 });
        soft.Commit();
        Assert.Equal(0.02, light.Current.Mesh.Points.Max(p => p.Y), 9);
    }

    [Fact]
    public void Colours_are_painted_and_set()
    {
        var (d, cmd) = Plane();
        var m = d.Current.Mesh;
        cmd.Run("Set Color", Args(("Map", "Paint"), ("Color", new[] { 255.0, 255, 255 })));
        cmd.Run("Paint Colors", Args(("Map", "Paint"), ("Color", new[] { 255.0, 0, 0 }), ("Center", new[] { 0.0, 0, 0 }), ("Radius", 0.3), ("Strength", 1.0)));
        var map = m.FindPointMap("Paint")!;
        Assert.Equal(PointMapKind.Rgb, map.Kind);
        Assert.Equal(new Vec3(1, 0, 0), map.Color(Find(m, Vec3.Zero)));
        Assert.Equal(Vec3.One, map.Color(Find(m, new Vec3(1, 0, 1))));
        Assert.Equal("Paint", d.ShownPointMap);
        Assert.Throws<ArgumentException>(() => cmd.Run("Set Color", Args(("Color", new[] { 300.0, 0, 0 }))));
    }
}

public class MeasureToolTests
{
    [Fact]
    public void The_ruler_snaps_to_points_and_changes_nothing()
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 2, 2 } });
        d.Modified = false;
        var tool = new MeasureTool();
        tool.Begin(new ToolContext(d));
        // A drag in the Front view from near one corner to near another: both ends snap to the corners (with their depth).
        var drag = new DragInfo { Start = new Vec3(-0.98, -1.01, 0), Current = new Vec3(0.99, 1.02, 0), Right = Vec3.UnitX, Up = Vec3.UnitY, Forward = Vec3.UnitZ, UnitsPerPixel = 0.01, First = true };
        tool.Drag(drag);
        tool.Commit();
        Assert.Equal(new Vec3(-1, -1, -1), tool.From);
        Assert.Equal(new Vec3(1, 1, -1), tool.To);
        Assert.Equal(Math.Sqrt(8), d.Measuring!.Length, 9);
        Assert.False(d.Modified);
        Assert.Equal(8, d.Current.Mesh.Points.Count);
    }
}
