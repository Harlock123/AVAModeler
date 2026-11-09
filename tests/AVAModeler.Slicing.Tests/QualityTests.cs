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

public class SupportTests
{
    private static IEnumerable<Extrusion> Of(ToolpathLayer layer, PathKind kind) => layer.Paths.Where(p => p.Kind == kind);

    [Fact]
    public void The_top_of_the_support_is_dense_crossways_interface()
    {
        var plan = PlannerTests.Plan(SafetyTests.Tee(), new SliceSettings { Supports = true });
        var supported = plan.Layers.Where(l => l.Paths.Any(p => p.Kind is PathKind.Support or PathKind.SupportInterface)).ToList();
        // The last two support layers (under the slab, past the layer of air) are interface; those below are not.
        Assert.All(supported[^2..], l => Assert.NotEmpty(Of(l, PathKind.SupportInterface)));
        Assert.All(supported[..^2], l => Assert.Empty(Of(l, PathKind.SupportInterface)));
        Assert.All(supported[..^2], l => Assert.NotEmpty(Of(l, PathKind.Support)));
        // Interface lines run along Y, the rest along X, and the interface is denser.
        Vec2 Dir(Extrusion e) => (e.Points[^1] - e.Points[0]).Normalized();
        Assert.All(Of(supported[^1], PathKind.SupportInterface), e => Assert.True(Math.Abs(Dir(e).Y) > 0.99));
        Assert.All(Of(supported[0], PathKind.Support), e => Assert.True(Math.Abs(Dir(e).X) > 0.99));
        double body = Of(supported[0], PathKind.Support).Sum(e => e.Length), face = Of(supported[^1], PathKind.SupportInterface).Sum(e => e.Length);
        Assert.True(face > body * 3, $"{face} against {body}");
        // None asked for: all plain support. The G-code names it.
        var plain = PlannerTests.Plan(SafetyTests.Tee(), new SliceSettings { Supports = true, SupportInterfaceLayers = 0 });
        Assert.Empty(plain.Layers.SelectMany(l => Of(l, PathKind.SupportInterface)));
        Assert.Contains(";TYPE:SUPPORT-INTERFACE\n", GCode.Write(plan, Presets.Materials[0]).Text);
    }

    [Fact]
    public void Bed_only_support_doesnt_stand_on_the_object()
    {
        // A T whose right arm is over a low block 3 mm tall: support there would stand on the block.
        var doc = SafetyTests.Tee();
        Shapes.Box(doc.Layers[0].Mesh, new Vec3(0.004, 0, -0.002), new Vec3(0.009, 0.003, 0.002));
        doc.Layers[0].Mesh.Touch();
        // A line's run crossing over the block's middle (clear of the gap round it).
        bool OverBlock(Extrusion e) => e.Points.Min(p => p.X) < 110 + 9 - 0.8 && e.Points.Max(p => p.X) > 110 + 4 + 0.8;
        var everywhere = PlannerTests.Plan(doc, new SliceSettings { Supports = true });
        var high = everywhere.Layers.Where(l => l.Layer.CutAt > 4 && l.Layer.CutAt < 9).ToList();
        Assert.Contains(high.SelectMany(l => l.Paths), e => e.Kind == PathKind.Support && OverBlock(e));
        var bedOnly = PlannerTests.Plan(doc, new SliceSettings { Supports = true, SupportOn = SupportPlacement.BedOnly });
        var highOnly = bedOnly.Layers.Where(l => l.Layer.CutAt > 4 && l.Layer.CutAt < 9).ToList();
        Assert.DoesNotContain(highOnly.SelectMany(l => l.Paths), e => e.Kind is PathKind.Support or PathKind.SupportInterface && OverBlock(e));
        // The left arm, over the bed, is still held up.
        Assert.Contains(highOnly.SelectMany(l => l.Paths), e => e.Kind == PathKind.Support && e.Points.All(p => p.X < 110 - 2));
    }
}

public class GyroidTests
{
    private static IEnumerable<Extrusion> Infill(ToolpathLayer l) => l.Paths.Where(p => p.Kind == PathKind.Infill);

    [Fact]
    public void Gyroid_infill_is_as_dense_as_its_percentage_and_shifts_with_height()
    {
        var plan = PlannerTests.Plan(Shapes.Cube(0.02), new SliceSettings { Infill = InfillPattern.Gyroid });
        double sp = Planner.Spacing(0.45, 0.2), apart = sp * 100 / 20;
        double side = 20 - 2 * (0.45 / 2 + 2 * sp - 0.15 * 0.45);
        foreach (int i in (int[])[20, 33, 50, 71])
        {
            var lines = Infill(plan.Layers[i]).ToList();
            double length = lines.Sum(e => e.Length);
            // About the length lines that far apart would have, a little more for the waves.
            Assert.InRange(length, side * side / apart * 0.8, side * side / apart * 1.5);
            // Curved: most pieces aren't straight lines.
            Assert.True(lines.Count(e => e.Points.Count > 4) > lines.Count / 2, $"layer {i}: {lines.Count} pieces");
            Assert.All(lines.SelectMany(e => e.Points), p => Assert.True(p.X > 100.9 && p.X < 119.1 && p.Y > 100.9 && p.Y < 119.1, $"{p}"));
        }
        // The pattern changes with height: two layers apart are different.
        static string Shape(ToolpathLayer l) => string.Join(";", l.Paths.Where(p => p.Kind == PathKind.Infill).SelectMany(e => e.Points).Take(20).Select(p => $"{p.X:0.0},{p.Y:0.0}"));
        Assert.NotEqual(Shape(plan.Layers[30]), Shape(plan.Layers[40]));
        // And it's the same every time.
        var again = PlannerTests.Plan(Shapes.Cube(0.02), new SliceSettings { Infill = InfillPattern.Gyroid });
        Assert.Equal(Shape(plan.Layers[30]), Shape(again.Layers[30]));
    }

    [Fact]
    public void A_gyroid_print_is_safe_to_send()
    {
        var job = PrintJob.Run(Shapes.Tube(0.03, 0.012, 0.01), new PrintSetup { Settings = new SliceSettings { Infill = InfillPattern.Gyroid, InfillPercent = 15 } });
        var run = Playback.Run(job.GCode);
        Assert.Equal(-2, run.E - run.TotalE, 6);
        Assert.All(run.Extrusions.Where(e => e.Layer >= 0), e => Assert.InRange(e.E, 0, 5));
        Assert.True(job.Plan.Lengths[PathKind.Infill] > 100);
    }
}

public class IroningTests
{
    private static IEnumerable<Extrusion> Ironed(ToolpathLayer l) => l.Paths.Where(p => p.Kind == PathKind.Ironing);

    [Fact]
    public void Only_the_top_surface_is_ironed_with_close_crossways_lines_and_a_trickle_of_plastic()
    {
        var plan = PlannerTests.Plan(Shapes.Cube(0.02), new SliceSettings { Ironing = true });
        Assert.All(plan.Layers.SkipLast(1), l => Assert.Empty(Ironed(l)));
        var top = plan.Layers[^1];
        var lines = Ironed(top).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, e => Assert.Equal(0.1, e.Flow, 9));
        // Lines 0.1 mm apart over the ~17.5 mm square inside the walls: about 3000 mm of them.
        Assert.InRange(lines.Sum(e => e.Length), 2500, 3300);
        // Ironing comes after the top layer's own skin, crossways to it.
        int lastSkin = top.Paths.ToList().FindLastIndex(p => p.Kind == PathKind.Skin), firstIron = top.Paths.ToList().FindIndex(p => p.Kind == PathKind.Ironing);
        Assert.True(lastSkin < firstIron);
        Vec2 Dir(Extrusion e) => (e.Points[^1] - e.Points[0]).Normalized();
        var skinDir = Dir(top.Paths.Where(p => p.Kind == PathKind.Skin).MaxBy(p => p.Length)!);
        Assert.InRange(Math.Abs(Vec2.Dot(skinDir, Dir(lines.MaxBy(p => p.Length)!))), 0, 0.01);

        // A step's top is ironed where it is, not just the very top: a 10 mm block beside a 20 mm one.
        var doc = Shapes.Cube(0.02);
        Shapes.Box(doc.Layers[0].Mesh, new Vec3(0.01, 0, -0.01), new Vec3(0.03, 0.01, 0.01));
        doc.Layers[0].Mesh.Touch();
        var step = PlannerTests.Plan(doc, new SliceSettings { Ironing = true });
        // The block's top layer: the last cut through it.
        var stepTop = step.Layers.Last(l => l.Layer.CutAt < 10);
        Assert.Contains(Ironed(stepTop), e => e.Points.All(p => p.X > 120));
        Assert.Empty(PlannerTests.Plan(Shapes.Cube(0.02)).Layers.SelectMany(Ironed));
    }

    [Fact]
    public void Ironing_uses_a_tenth_of_the_plastic_slowly()
    {
        var job = PrintJob.Run(Shapes.Cube(0.02), new PrintSetup { Settings = new SliceSettings { Ironing = true } });
        Assert.Contains(";TYPE:IRONING", job.GCode);
        var ironing = job.GCode[job.GCode.IndexOf(";TYPE:IRONING")..job.GCode.IndexOf(";TYPE:END")];
        Assert.Contains("F900", ironing);
        double perMm = GCode.FilamentPerMm(0.45, job.Plan.Layers[^1].Layer.Height, 1.75) * 0.1;
        var first = ironing.Split('\n').First(l => l.StartsWith("G1 X") && l.Contains(" E"));
        double e = double.Parse(first[(first.IndexOf(" E") + 2)..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(e < 1.0 * perMm * 30, $"{e}");
        Assert.Equal("Fine 0.12", PrintProfile.Presets.Single(p => p.Settings.Ironing).Name);
    }
}
