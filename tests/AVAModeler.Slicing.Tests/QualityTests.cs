using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

/// <summary>Gap fill, bridges, slowing down to cool, and travel kept inside the object.</summary>
public class QualityTests
{
    private static readonly PrinterProfile Printer = new();

    private static IEnumerable<Extrusion> Of(ToolpathLayer layer, PathKind kind) => layer.Paths.Where(p => p.Kind == kind);

    /// <summary>A wall standing on the bed: <paramref name="thick"/> m thick, 20 mm long, 5 mm tall.</summary>
    private static Document Fin(double thick) => Shapes.Doc(m => Shapes.Box(m, new Vec3(-0.01, 0, -thick / 2), new Vec3(0.01, 0.005, thick / 2)));

    [Fact]
    public void A_fin_too_thin_for_a_wall_is_printed_as_gap_fill_along_it()
    {
        var plan = PlannerTests.Plan(Fin(0.0004));
        foreach (var layer in plan.Layers.Skip(1))
        {
            Assert.Empty(Of(layer, PathKind.OuterWall));
            var fill = Of(layer, PathKind.GapFill).ToList();
            Assert.InRange(fill.Sum(e => e.Length), 18, 20.01);
            // Along the fin (X), as wide as it is.
            Assert.All(fill, e => Assert.True(Math.Abs((e.Points[^1] - e.Points[0]).Normalized().X) > 0.99));
            Assert.All(fill, e => Assert.Equal(0.4, e.Width, 2));
        }
        // Turned off, the fin isn't printed at all.
        var none = PlannerTests.Plan(Fin(0.0004), new SliceSettings { GapFill = false });
        Assert.All(none.Layers.Skip(1), l => Assert.Empty(l.Paths));
    }

    [Fact]
    public void The_strip_between_walls_with_no_room_for_another_loop_gets_gap_fill()
    {
        // 1.3 mm thick: the outer wall's loop takes 0.45 mm each side; an inner loop won't fit, and 0.4 mm is left.
        var plan = PlannerTests.Plan(Fin(0.0013));
        var layer = plan.Layers[10];
        Assert.Single(Of(layer, PathKind.OuterWall));
        Assert.Empty(Of(layer, PathKind.InnerWall));
        var fill = Of(layer, PathKind.GapFill).ToList();
        Assert.InRange(fill.Sum(e => e.Length), 17, 20);
        Assert.All(fill, e => Assert.InRange(e.Width, 0.3, 0.45));
        // A cube's walls and infill meet: no gaps to fill.
        var cube = PlannerTests.Plan(Shapes.Cube(0.02));
        Assert.InRange(cube.Layers.Sum(l => Of(l, PathKind.GapFill).Sum(e => e.Length)), 0, 1);
    }

    [Fact]
    public void Gap_fill_uses_filament_for_its_own_width()
    {
        var plan = PlannerTests.Plan(Fin(0.0004));
        var (_, stats) = GCode.Write(plan, Presets.Materials[0]);
        double expected = plan.Layers.Sum(l => l.Paths.Sum(p => p.Length * GCode.FilamentPerMm(p.Width > 0 ? p.Width : 0.45, l.Layer.Height, 1.75)));
        Assert.Equal(expected, stats.FilamentMm, 2);
    }

    /// <summary>A bridge: two 4 mm posts 6 mm apart, 10 mm tall, with a 2 mm slab across their tops.</summary>
    private static Document Arch() => Shapes.Doc(m =>
    {
        Shapes.Box(m, new Vec3(-0.007, 0, -0.002), new Vec3(-0.003, 0.01, 0.002));
        Shapes.Box(m, new Vec3(0.003, 0, -0.002), new Vec3(0.007, 0.01, 0.002));
        Shapes.Box(m, new Vec3(-0.007, 0.01, -0.002), new Vec3(0.007, 0.012, 0.002));
    });

    [Fact]
    public void A_span_over_air_is_bridged_straight_across_with_the_fan_full()
    {
        var plan = PlannerTests.Plan(Arch());
        var first = plan.Layers.First(l => l.Layer.CutAt > 10);
        var bridges = Of(first, PathKind.Bridge).ToList();
        Assert.NotEmpty(bridges);
        // Across the gap (along X), landing on both posts.
        Assert.All(bridges, e => Assert.True(Math.Abs((e.Points[^1] - e.Points[0]).Normalized().X) > 0.99, "along X"));
        Assert.All(bridges, e => Assert.True(Math.Min(e.Points[0].X, e.Points[^1].X) < 110 - 3 && Math.Max(e.Points[0].X, e.Points[^1].X) > 110 + 3));
        // Only the slab's first layer is a bridge; nothing in a cube.
        Assert.Empty(plan.Layers.Where(l => l.Layer.CutAt > 10.5).SelectMany(l => Of(l, PathKind.Bridge)));
        Assert.Empty(PlannerTests.Plan(Shapes.Cube(0.02)).Layers.SelectMany(l => Of(l, PathKind.Bridge)));

        // PETG's fan runs at half: full for the bridge, then back to half; the bridge at its own speed.
        var (text, _) = GCode.Write(plan, Presets.Materials[1] with { MinLayerSeconds = 0 });
        int at = text.IndexOf(";TYPE:SKIN\n", text.IndexOf($";LAYER:{first.Layer.Index}\n"));
        var before = text[..at];
        Assert.EndsWith("M106 S255\n", before);
        var after = text[at..];
        Assert.Contains("F1500", after[..after.IndexOf("M106 S128")]);
    }

    [Fact]
    public void Quick_layers_are_slowed_to_cool_but_not_below_the_slowest_speed()
    {
        // A 5 mm cube's layers take a second or two at full speed.
        var plan = PlannerTests.Plan(Shapes.Cube(0.005));
        var pla = Presets.Materials[0];
        var (text, stats) = GCode.Write(plan, pla);
        Assert.Equal(plan.Layers.Count, stats.SlowedLayers);
        Assert.Contains(";Slowed to ", text);
        var feeds = text[text.IndexOf(";LAYER:0")..text.IndexOf(";TYPE:END")].Split('\n').Where(l => l.StartsWith("G1 X") && l.Contains(" F")).Select(l => double.Parse(l[(l.LastIndexOf('F') + 1)..], System.Globalization.CultureInfo.InvariantCulture) / 60).ToList();
        Assert.All(feeds, f => Assert.InRange(f, pla.MinSpeed - 1e-9, 50));
        // Slower than the same print without it.
        var (_, fast) = GCode.Write(plan, pla with { MinLayerSeconds = 0 });
        Assert.True(stats.Seconds > fast.Seconds * 1.5);
        Assert.Throws<ArgumentException>(() => (pla with { MinSpeed = 0 }).Checked());
    }

    [Fact]
    public void Travel_inside_an_island_goes_round_a_hole_without_retracting()
    {
        var island = new Island(
            new Loop([new(0, 0), new(30, 0), new(30, 30), new(0, 30)]),
            [new Loop([new(10, 10), new(10, 20), new(20, 20), new(20, 10)])]);
        var route = Combing.Route([island], new Vec2(5, 15), new Vec2(25, 15), 0.7)!;
        Assert.NotNull(route);
        Assert.Equal(new Vec2(25, 15), route[^1]);
        // Never through the hole: every leg's middle is outside it.
        var legs = new List<Vec2> { new(5, 15) };
        legs.AddRange(route);
        for (int i = 1; i < legs.Count; i++)
        {
            var mid = (legs[i] + legs[i - 1]) / 2;
            Assert.False(mid.X > 10 + 1e-9 && mid.X < 20 - 1e-9 && mid.Y > 10 + 1e-9 && mid.Y < 20 - 1e-9, $"{mid} in the hole");
        }
        // A straight run that misses the hole goes straight.
        Assert.Equal([new Vec2(25, 5)], Combing.Route([island], new Vec2(5, 5), new Vec2(25, 5), 0.7));
        // Into the hole, or to another island, it can't: travel straight and retract.
        Assert.Null(Combing.Route([island], new Vec2(5, 5), new Vec2(15, 15), 0.7));
        Assert.Null(Combing.Route([island], new Vec2(5, 5), new Vec2(50, 5), 0.7));
        // A point just outside (on the outer wall) steps in to the edge first.
        var fromWall = Combing.Route([island], new Vec2(-0.3, 15), new Vec2(5, 15), 0.7)!;
        Assert.Equal([new Vec2(0, 15), new Vec2(5, 15)], fromWall);
    }

    [Fact]
    public void Combing_cuts_the_retractions_in_a_print()
    {
        var doc = Shapes.Tube(0.03, 0.012, 0.01);
        var setup = new PrintSetup();
        var combed = PrintJob.Run(doc, setup);
        var straight = PrintJob.Run(doc, setup with { Settings = setup.Settings with { Combing = false } });
        Assert.True(combed.Stats.CombedTravels > 100);
        Assert.Equal(0, straight.Stats.CombedTravels);
        Assert.True(combed.Stats.Retractions < straight.Stats.Retractions / 2, $"{combed.Stats.Retractions} against {straight.Stats.Retractions}");
        // And every travel stays on the bed, the safety checks still holding.
        var run = Playback.Run(combed.GCode);
        Assert.Equal(-2, run.E - run.TotalE, 6);
    }
}
