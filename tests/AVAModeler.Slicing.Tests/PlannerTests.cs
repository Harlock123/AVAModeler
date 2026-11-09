using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

public class PlannerTests
{
    private static readonly PrinterProfile Printer = new();

    internal static PrintPlan Plan(Document doc, SliceSettings? settings = null) =>
        Planner.Plan(Slicer.Slice(Placement.Place(doc, Printer), settings ?? new SliceSettings(), Printer), Printer);

    private static IEnumerable<Extrusion> Of(ToolpathLayer layer, PathKind kind) => layer.Paths.Where(p => p.Kind == kind);

    [Fact]
    public void A_cube_gets_walls_solid_bottom_and_top_and_sparse_infill_between()
    {
        var plan = Plan(Shapes.Cube(0.02));
        Assert.Equal(100, plan.Layers.Count);
        foreach (var layer in plan.Layers)
        {
            var outer = Assert.Single(Of(layer, PathKind.OuterWall));
            var inner = Assert.Single(Of(layer, PathKind.InnerWall));
            Assert.True(outer.Closed);
            // The outer wall's middle is half a line in from the edge: a 19.55 mm square; the inner one a line further in.
            Assert.Equal(4 * 19.55, outer.Length, 3);
            Assert.Equal(4 * 18.65, inner.Length, 3);
            // Inner wall before the outer one, then skin or infill.
            Assert.True(layer.Paths.ToList().IndexOf(inner) < layer.Paths.ToList().IndexOf(outer));
        }
        int Count(int i, PathKind k) => Of(plan.Layers[i], k).Count();
        // The first three layers and the last four are solid; in between, sparse.
        foreach (int i in (int[])[0, 1, 2, 96, 97, 98, 99])
        {
            Assert.True(Count(i, PathKind.Skin) > 0, $"layer {i} has skin");
            Assert.Equal(0, Count(i, PathKind.Infill));
        }
        foreach (int i in (int[])[3, 50, 95])
        {
            Assert.Equal(0, Count(i, PathKind.Skin));
            Assert.True(Count(i, PathKind.Infill) > 0, $"layer {i} has infill");
        }
        // A skirt on the first layer only.
        Assert.Single(Of(plan.Layers[0], PathKind.Skirt));
        Assert.All(plan.Layers.Skip(1), l => Assert.Empty(Of(l, PathKind.Skirt)));
        Assert.Empty(plan.Layers.SelectMany(l => Of(l, PathKind.Support)));
    }

    [Fact]
    public void Skin_lines_are_a_line_apart_and_infill_is_spaced_by_its_percentage()
    {
        var plan = Plan(Shapes.Cube(0.02));
        // Solid: the inside (inset by 2 lines + half a line, less the overlap) filled with lines 0.45 mm apart covers
        // about its area divided by the width.
        double side = 20 - 2 * (2 * 0.45 + 0.45 / 2 - 0.15 * 0.45);
        double skin = Of(plan.Layers[0], PathKind.Skin).Sum(e => e.Length);
        Assert.InRange(skin, side * side / 0.45 * 0.95, side * side / 0.45 * 1.05);
        double sparse = Of(plan.Layers[50], PathKind.Infill).Sum(e => e.Length);
        Assert.InRange(sparse, side * side / 2.25 * 0.85, side * side / 2.25 * 1.15);
        // Every skin and infill point stays inside the walls.
        foreach (var p in plan.Layers.SelectMany(l => l.Paths).Where(e => e.Kind is PathKind.Skin or PathKind.Infill).SelectMany(e => e.Points))
            Assert.True(p.X > 100 + 1.0 && p.X < 120 - 1.0 && p.Y > 100 + 1.0 && p.Y < 120 - 1.0, $"{p} inside");
    }

    [Fact]
    public void Lines_turn_each_layer_and_grid_runs_both_ways()
    {
        var plan = Plan(Shapes.Cube(0.02));
        Vec2 Dir(Extrusion e) => (e.Points[^1] - e.Points[0]).Normalized();
        double Slope(int layer, PathKind kind)
        {
            var d = Dir(Of(plan.Layers[layer], kind).OrderByDescending(e => e.Length).First());
            return Math.Round(d.X * d.Y, 3);
        }
        // 45° one layer, 135° the next: the products of the direction's parts are +½ and −½.
        Assert.Equal(0.5, Slope(0, PathKind.Skin));
        Assert.Equal(-0.5, Slope(1, PathKind.Skin));
        var grid = Plan(Shapes.Cube(0.02), new SliceSettings { Infill = InfillPattern.Grid });
        var dirs = Of(grid.Layers[50], PathKind.Infill).Select(e => Math.Sign(Math.Round(Dir(e).X * Dir(e).Y, 3))).Distinct().ToList();
        Assert.Equal(2, dirs.Count);
    }

    [Fact]
    public void A_tube_has_walls_round_its_hole_too()
    {
        var plan = Plan(Shapes.Tube(0.02, 0.01, 0.01));
        var layer = plan.Layers[20];
        Assert.Equal(2, Of(layer, PathKind.OuterWall).Count());
        Assert.Equal(2, Of(layer, PathKind.InnerWall).Count());
        // The hole's outer wall is half a line out from it: a 10.45 mm square.
        Assert.Contains(Of(layer, PathKind.OuterWall), e => Math.Abs(e.Length - 4 * 10.45) < 1e-3);
    }

    [Fact]
    public void Seams_sit_at_the_back()
    {
        var plan = Plan(Shapes.Cube(0.02));
        foreach (var e in plan.Layers.SelectMany(l => l.Paths).Where(e => e.Closed && e.Kind != PathKind.Skirt))
            Assert.Equal(e.Points.Max(p => p.Y), e.Points[0].Y, 6);
        var loop = new Extrusion(PathKind.OuterWall, [new(0, 0), new(10, 0), new(10, 10), new(0, 10)], true);
        Assert.Equal(new Vec2(10, 0), Planner.Seam(loop, SeamPosition.Nearest, new Vec2(11, -1)).Points[0]);
        Assert.Equal(new Vec2(0, 10), Planner.Seam(loop, SeamPosition.Rear, Vec2.Zero).Points[0]);
    }

    [Fact]
    public void A_brim_replaces_the_skirt_and_touches_the_object()
    {
        var plan = Plan(Shapes.Tube(0.02, 0.01, 0.01), new SliceSettings { BrimLines = 5 });
        var first = plan.Layers[0];
        Assert.Empty(Of(first, PathKind.Skirt));
        var brim = Of(first, PathKind.Brim).ToList();
        // Round the outside only, not in the hole.
        Assert.Equal(5, brim.Count);
        Assert.All(brim, e => Assert.True(e.Points.Min(p => p.X) < 100));
        double innermost = brim.Min(e => 100 - e.Points.Min(p => p.X));
        Assert.Equal(0.225, innermost, 3);
    }

    [Fact]
    public void An_overhang_gets_support_and_a_cube_does_not()
    {
        // A T: a 4 mm post 10 mm tall with a 20 × 4 mm slab on top.
        var doc = Shapes.Doc(m =>
        {
            Shapes.Box(m, new Vec3(-0.002, 0, -0.002), new Vec3(0.002, 0.01, 0.002));
            Shapes.Box(m, new Vec3(-0.01, 0.01, -0.002), new Vec3(0.01, 0.012, 0.002));
        });
        var none = Plan(doc);
        Assert.Empty(none.Layers.SelectMany(l => Of(l, PathKind.Support)));
        var plan = Plan(doc, new SliceSettings { Supports = true });
        var supported = plan.Layers.Where(l => Of(l, PathKind.Support).Any()).ToList();
        Assert.NotEmpty(supported);
        Assert.Equal(0, supported[0].Layer.Index);
        // Support stops a layer below the slab, and keeps its gap from the post.
        Assert.True(supported[^1].Layer.Top < 10 - 0.1);
        foreach (var p in supported.SelectMany(l => Of(l, PathKind.Support)).SelectMany(e => e.Points))
            Assert.True(Math.Abs(p.X - 110) > 2 + 0.7 - 1e-6 || Math.Abs(p.Y - 110) > 2 + 0.7 - 1e-6, $"{p} clear of the post");
        Assert.Empty(Plan(Shapes.Cube(0.02), new SliceSettings { Supports = true }).Layers.SelectMany(l => Of(l, PathKind.Support)));
    }

    [Fact]
    public void Lines_are_ordered_to_keep_travel_short()
    {
        var lines = new[]
        {
            new Extrusion(PathKind.Infill, [new(0, 10), new(10, 10)], false),
            new Extrusion(PathKind.Infill, [new(0, 0), new(10, 0)], false),
            new Extrusion(PathKind.Infill, [new(0, 5), new(10, 5)], false),
        };
        var ordered = Planner.Order(lines, new Vec2(0, 0));
        Assert.Equal([new Vec2(0, 0), new Vec2(10, 0)], ordered[0].Points);
        // The next starts at the near end: a zigzag.
        Assert.Equal([new Vec2(10, 5), new Vec2(0, 5)], ordered[1].Points);
        Assert.Equal([new Vec2(0, 10), new Vec2(10, 10)], ordered[2].Points);
    }

    [Fact]
    public void Zero_infill_leaves_the_middle_hollow_and_bad_settings_are_refused()
    {
        var plan = Plan(Shapes.Cube(0.02), new SliceSettings { InfillPercent = 0 });
        Assert.Empty(plan.Layers.SelectMany(l => Of(l, PathKind.Infill)));
        Assert.Contains("line width", Assert.Throws<ArgumentException>(() => new SliceSettings { LineWidth = 2 }.Checked(Printer)).Message);
        Assert.Throws<ArgumentException>(() => new SliceSettings { Walls = 0 }.Checked(Printer));
        Assert.Throws<ArgumentException>(() => new SliceSettings { TravelSpeed = 0 }.Checked(Printer));
    }
}
