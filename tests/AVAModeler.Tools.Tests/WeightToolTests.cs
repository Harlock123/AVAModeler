using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>The Maps tab's weight tools: painting, setting, smoothing, normalising, mirroring and auto weights.</summary>
public class WeightToolTests
{
    private static Dictionary<string, object?> Args(params (string, object?)[] a) => a.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    private static (Document Doc, Commands Cmd) Grid()
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Box", Args(("Size", new[] { 2.0, 2, 2 }), ("SegmentsX", 4), ("SegmentsY", 4), ("SegmentsZ", 4)));
        return (d, cmd);
    }

    private static int Find(Mesh m, Vec3 v) => Enumerable.Range(0, m.Points.Count).First(i => Vec3.Near(m.Points[i], v, 1e-6));

    [Fact]
    public void A_dab_paints_by_falloff_and_symmetry_paints_the_opposite_map()
    {
        var (d, cmd) = Grid();
        var m = d.Current.Mesh;
        cmd.Run("Paint Weights", Args(("Map", "Left Arm"), ("Center", new[] { 1.0, 0, 0 }), ("Radius", 0.6), ("Strength", 1.0), ("Falloff", "Linear")));
        var left = m.FindPointMap("Left Arm")!;
        Assert.Equal(1, left.Weight(Find(m, new Vec3(1, 0, 0))), 6);
        Assert.Equal(1 - 0.5 / 0.6, left.Weight(Find(m, new Vec3(1, 0.5, 0))), 6);
        Assert.Equal(0, left.Weight(Find(m, new Vec3(1, 1, 1))));
        Assert.Equal("Left Arm", d.ShownPointMap);
        Assert.Equal("Left Arm", d.CurrentWeightMap);

        // With symmetry and a "Right Arm" map, the mirrored dab paints it.
        cmd.SetWeights("Right Arm", [0], [0]);
        d.Symmetry = Axis.X;
        cmd.Run("Paint Weights", Args(("Map", "Left Arm"), ("Center", new[] { 1.0, -1, 0 }), ("Radius", 0.3), ("Strength", 1.0)));
        Assert.Equal(1, m.FindPointMap("Right Arm")!.Weight(Find(m, new Vec3(-1, -1, 0))), 6);
        Assert.Equal(0, left.Weight(Find(m, new Vec3(-1, -1, 0))));
        d.UndoLast();
        Assert.Equal(0, d.Current.Mesh.FindPointMap("Right Arm")!.Weight(Find(d.Current.Mesh, new Vec3(-1, -1, 0))));
    }

    [Fact]
    public void Set_smooth_normalize_and_mirror()
    {
        var (d, cmd) = Grid();
        var m = d.Current.Mesh;
        int p = Find(m, new Vec3(1, 1, 1)), q = Find(m, new Vec3(-1, 1, 1));
        cmd.SelectMode(SelectionMode.Points);
        cmd.SelectPoints([p]);
        cmd.Run("Set Weight", Args(("Map", "A"), ("Value", 0.6)));
        cmd.Run("Set Weight", Args(("Map", "B"), ("Value", 0.6)));
        Assert.Equal(0.6, m.FindPointMap("A")!.Weight(p), 6);
        cmd.Run("Normalize Weights", Args(("Maps", "A, B")));
        Assert.Equal(0.5, m.FindPointMap("A")!.Weight(p), 6);
        Assert.Throws<ArgumentException>(() => cmd.Run("Normalize Weights", Args(("Maps", "C"))));
        m = d.Current.Mesh; // a tool that fails puts the layer back as a fresh copy
        cmd.SelectNone();
        cmd.Run("Mirror Weights", Args(("Maps", "A")));
        Assert.Equal(0.5, m.FindPointMap("A")!.Weight(q), 6);
        cmd.Run("Smooth Weights", Args(("Map", "A"), ("Strength", 1.0), ("Iterations", 1)));
        Assert.True(m.FindPointMap("A")!.Weight(p) < 0.5);
    }

    [Fact]
    public void Weights_from_skelegons_share_each_point_among_the_bones()
    {
        var (d, cmd) = Grid();
        var m = d.Current.Mesh;
        int upper = d.SurfaceIndex("Skelegon_Upper"), lower = d.SurfaceIndex("Skelegon_Lower");
        int a = m.AddPoint(new Vec3(-1, 0, 0)), b = m.AddPoint(new Vec3(0, 0, 0)), c = m.AddPoint(new Vec3(1, 0, 0));
        m.AddPolygon([a, b], upper);
        m.AddPolygon([b, c], lower);
        cmd.Run("Weights from Skelegons");
        var u = m.FindPointMap("Upper")!;
        var l = m.FindPointMap("Lower")!;
        int left = Find(m, new Vec3(-1, 1, 1)), right = Find(m, new Vec3(1, 1, 1));
        Assert.True(u.Weight(left) > 0.6 && u.Weight(left) > l.Weight(left));
        Assert.True(l.Weight(right) > 0.6 && l.Weight(right) > u.Weight(right));
        for (int i = 0; i < 8; i++) Assert.Equal(1, u.Weight(i) + l.Weight(i), 6);
        Assert.Equal("Upper", d.ShownPointMap);
        var empty = new Commands(new Document());
        empty.Run("Box");
        Assert.Throws<InvalidOperationException>(() => empty.Run("Weights from Skelegons"));
    }

    [Fact]
    public void A_stroke_lays_dabs_a_quarter_radius_apart()
    {
        var (d, _) = Grid();
        var tool = new PaintWeightsTool { Map = "S", Radius = 0.45, Strength = 0.1, Falloff = BrushFalloff.Constant };
        tool.Begin(new ToolContext(d));
        tool.SetStroke(0, [new Vec3(-1, 1, 0), new Vec3(1, 1, 0)]);
        tool.Update();
        tool.Commit();
        // Dabs a quarter radius (0.1125 m) apart from x = -1: eight of them are within 0.45 of the middle point.
        var m = d.Current.Mesh;
        double w = m.FindPointMap("S")!.Weight(Find(m, new Vec3(0, 1, 0)));
        Assert.Equal(0.8, w, 5);
        Assert.Equal(21, Brushes.Space([new Vec3(-1, 1, 0), new Vec3(1, 1, 0)], 0.1).Count);
    }

    [Fact]
    public void Copies_made_by_mirror_and_extrude_keep_their_originals_weights()
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Box", Args(("Size", new[] { 1.0, 1, 1 }), ("Center", new[] { 1.0, 0, 0 })));
        var m = d.Current.Mesh;
        int corner = Find(m, new Vec3(1.5, 0.5, 0.5));
        cmd.SetWeights("Arm", [corner], [0.75]);
        cmd.Run("Mirror", Args(("Axis", "X")));
        m = d.Current.Mesh;
        Assert.Equal(0.75, m.FindPointMap("Arm")!.Weight(Find(m, new Vec3(-1.5, 0.5, 0.5))), 6);
        cmd.SelectMode(SelectionMode.Polygons);
        int top = Enumerable.Range(0, m.Polygons.Count).First(i => m.Normal(i).Y > 0.9 && m.Centroid(m.Polygons[i]).X > 0);
        cmd.SelectPolygons([top]);
        cmd.Run("Extrude", Args(("Offset", new[] { 0.0, 1, 0 })));
        m = d.Current.Mesh;
        Assert.Equal(0.75, m.FindPointMap("Arm")!.Weight(Find(m, new Vec3(1.5, 1.5, 0.5))), 6);
    }

    [Fact]
    public void Lua_sets_and_reads_weights()
    {
        var (d, cmd) = Grid();
        Macros.Run("setweight('Hip', {1, 2}, 0.25) print(getweight('Hip', 2)) print(getweight('Hip', 3))", cmd);
        Assert.Equal(["0.25", "0"], cmd.Output);
        Assert.Throws<MacroException>(() => Macros.Run("setweight('Hip', {1}, 2)", cmd));
    }
}
