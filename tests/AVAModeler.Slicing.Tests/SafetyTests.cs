using System.Globalization;
using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

/// <summary>
/// What any G-code from the slicer must do, whatever the shape and settings: stay on the bed and under the build
/// height, extrude nothing before the nozzle and bed are hot, rise layer by layer, and put back every retraction.
/// </summary>
public class SafetyTests
{
    public static Document Ball(double radius = 0.025, int sides = 48, int bands = 24) => Shapes.Doc(m =>
    {
        m.Points.Add(new Vec3(0, 0, 0));
        for (int j = 1; j < bands; j++)
            for (int i = 0; i < sides; i++)
            {
                double t = Math.PI * j / bands, a = 2 * Math.PI * i / sides;
                m.Points.Add(new Vec3(radius * Math.Sin(t) * Math.Cos(a), radius - radius * Math.Cos(t), radius * Math.Sin(t) * Math.Sin(a)));
            }
        m.Points.Add(new Vec3(0, 2 * radius, 0));
        int P(int j, int i) => 1 + (j - 1) * sides + i % sides;
        for (int i = 0; i < sides; i++) m.Polygons.Add(new Polygon([0, P(1, i), P(1, i + 1)]));
        for (int j = 1; j < bands - 1; j++)
            for (int i = 0; i < sides; i++) m.Polygons.Add(new Polygon([P(j, i), P(j + 1, i), P(j + 1, i + 1), P(j, i + 1)]));
        int top = m.Points.Count - 1;
        for (int i = 0; i < sides; i++) m.Polygons.Add(new Polygon([top, P(bands - 1, i + 1), P(bands - 1, i)]));
        m.Touch();
        foreach (var p in m.Polygons)
            if (Vec3.Dot(m.Normal(p), m.Centroid(p) - new Vec3(0, radius, 0)) < 0) p.Points.Reverse();
        m.Touch();
    });

    /// <summary>A T: a post with a slab across its top, to need support.</summary>
    public static Document Tee() => Shapes.Doc(m =>
    {
        Shapes.Box(m, new Vec3(-0.002, 0, -0.002), new Vec3(0.002, 0.01, 0.002));
        Shapes.Box(m, new Vec3(-0.01, 0.01, -0.002), new Vec3(0.01, 0.012, 0.002));
    });

    public static TheoryData<string, string> Cases() => new()
    {
        { "cube", "default" }, { "cube", "brim+grid+hop" }, { "tube", "nearest+0infill" }, { "ball", "default" },
        { "ball", "fine" }, { "tee", "supports" }, { "tee", "supports+brim" }, { "two", "as-modelled" }, { "cube", "small-bed" },
    };

    private static (Document Doc, PrintSetup Setup) Case(string shape, string settings)
    {
        var doc = shape switch
        {
            "cube" => Shapes.Cube(0.02),
            "tube" => Shapes.Tube(0.03, 0.012, 0.015),
            "ball" => Ball(),
            "tee" => Tee(),
            "two" => TwoApart(),
            _ => throw new ArgumentException(shape),
        };
        var s = new SliceSettings();
        var setup = new PrintSetup();
        foreach (var part in settings.Split('+'))
            switch (part)
            {
                case "default": break;
                case "brim": s = s with { BrimLines = 6 }; break;
                case "grid": s = s with { Infill = InfillPattern.Grid, InfillPercent = 35 }; break;
                case "hop": s = s with { ZHop = 0.5 }; break;
                case "nearest": s = s with { Seam = SeamPosition.Nearest }; break;
                case "0infill": s = s with { InfillPercent = 0 }; break;
                case "fine": s = s with { LayerHeight = 0.1, FirstLayerHeight = 0.2, Walls = 3 }; break;
                case "supports": s = s with { Supports = true, SupportAngle = 40 }; break;
                case "as-modelled": setup = setup with { Placement = new PlacementOptions { Position = BedPosition.AsModelled } }; break;
                case "small-bed": setup = setup with { Printer = Presets.Printers[3], Material = Presets.Materials[1] }; break;
                default: throw new ArgumentException(part);
            }
        return (doc, setup with { Settings = s });
    }

    private static Document TwoApart()
    {
        var doc = Shapes.Doc(m =>
        {
            Shapes.Box(m, new Vec3(-0.04, 0, -0.01), new Vec3(-0.02, 0.015, 0.01));
            Shapes.Box(m, new Vec3(0.03, 0, 0.02), new Vec3(0.05, 0.005, 0.04));
        });
        return doc;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_gcode_is_safe_to_send_to_a_printer(string shape, string settings)
    {
        var (doc, setup) = Case(shape, settings);
        var job = PrintJob.Run(doc, setup);
        Assert.Empty(job.Warnings);
        var printer = setup.Printer;
        var run = Playback.Run(job.GCode);

        // Hot and homed before anything is printed.
        Assert.True(run.HomedBeforeFirstExtrusion);
        Assert.Equal(setup.Material.FirstLayerNozzleTemp, run.NozzleWhenFirstExtruded);
        Assert.Equal(setup.Material.BedTemp, run.BedWhenFirstExtruded);

        // Every printed line inside the bed, under the build height, and on the object's footprint (skirt and brim aside).
        var b = job.Model.Bounds;
        double margin = setup.Settings.SkirtDistance + (setup.Settings.SkirtLoops + setup.Settings.BrimLines + 1) * setup.Settings.LineWidth;
        foreach (var e in run.Extrusions.Where(e => e.Layer >= 0))
        {
            Assert.InRange(e.X, 0, printer.BedWidth);
            Assert.InRange(e.Y, 0, printer.BedDepth);
            Assert.InRange(e.Z, 0, Math.Min(printer.MaxHeight, b.Max.Z + 1e-9));
            Assert.InRange(e.X, b.Min.X - margin, b.Max.X + margin);
            Assert.InRange(e.Y, b.Min.Y - margin, b.Max.Y + margin);
        }
        // The top layer prints at the object's top.
        Assert.Equal(b.Max.Z, run.Extrusions.Max(e => e.Z), 6);

        // Layers in order, each printed higher than the last (hops aside), never below the one before.
        Assert.Equal(Enumerable.Range(0, job.Plan.Layers.Count), run.Layers);
        double lastZ = 0;
        foreach (var layer in job.Plan.Layers)
        {
            Assert.True(layer.Layer.Top > lastZ);
            lastZ = layer.Layer.Top;
        }
        var byLayer = run.Extrusions.Where(e => e.Layer >= 0).GroupBy(e => e.Layer).ToList();
        for (int i = 1; i < byLayer.Count; i++) Assert.True(byLayer[i].Min(e => e.Z) > byLayer[i - 1].Max(e => e.Z));

        // Each line uses filament in proportion to its length, never more than a few millimetres of it per move.
        Assert.All(run.Extrusions.Where(e => e.Layer >= 0), e => Assert.InRange(e.E, 0, 5));
        // Every retraction is put back; only the end G-code's own pull is left.
        Assert.Equal(-2, run.E - run.TotalE, 6);
        Assert.True(run.MaxRetract <= setup.Material.RetractLength + 1e-9);
        // Everything off at the end.
        Assert.Equal(0, run.Nozzle);
        Assert.Equal(0, run.Bed);
    }

    [Fact]
    public void A_ball_is_cut_true_to_its_circles_and_slices_quickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var job = PrintJob.Run(Ball(0.025, 96, 48), new PrintSetup());
        watch.Stop();
        // A 9000-triangle ball, 50 mm across: well under a few seconds even on a slow machine.
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 20);
        Assert.Equal(9024, job.Model.Triangles.Count);
        Assert.Equal(0, job.Sliced.OpenChains);
        foreach (var layer in job.Sliced.Layers.Where(l => l.CutAt > 1 && l.CutAt < 49))
        {
            // The cut through a polygonal ball is a little inside the true circle: within 1% of its area here.
            double z = layer.CutAt - 25, r2 = 25 * 25 - z * z;
            double area = Assert.Single(layer.Islands).Area;
            Assert.InRange(area / (Math.PI * r2), 0.97, 1.0);
        }
    }

    [Fact]
    public void The_layers_of_a_cube_add_up_to_its_volume_in_filament_when_solid()
    {
        // 100% infill: every layer solid, so the plastic laid is close to the cube's own volume.
        var setup = new PrintSetup { Settings = new SliceSettings { InfillPercent = 100, SkirtLoops = 0 } };
        var job = PrintJob.Run(Shapes.Cube(0.02), setup);
        double plastic = job.Stats.FilamentMm * Math.PI * 1.75 * 1.75 / 4;
        Assert.InRange(plastic / 8000, 0.93, 1.07);
    }
}

/// <summary>
/// A whole G-code file kept beside the tests, so any change to what the slicer writes shows up as a difference to
/// look at. Set AVAMODELER_REGOLD=1 to write the file afresh after a change that's meant.
/// </summary>
public class ReferenceTests
{
    private static string Folder([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Reference");

    [Fact]
    public void A_10_mm_tube_slices_to_the_reference_gcode()
    {
        var setup = new PrintSetup { Settings = new SliceSettings { LayerHeight = 0.3, FirstLayerHeight = 0.3 } };
        string text = PrintJob.Run(Shapes.Tube(0.01, 0.004, 0.003), setup).GCode;
        string file = Path.Combine(Folder(), "tube-10mm.gcode");
        if (Environment.GetEnvironmentVariable("AVAMODELER_REGOLD") == "1") File.WriteAllText(file, text);
        Assert.True(File.Exists(file), $"No reference at {file}: run with AVAMODELER_REGOLD=1 to write one.");
        var a = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
        var b = text.Split('\n');
        // Line by line, word by word; a number may differ by a unit in its last place (another machine's maths
        // library can round a coordinate the other way), nothing else may.
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string ea = i < a.Length ? a[i] : "(end)", eb = i < b.Length ? b[i] : "(end)";
            if (ea == eb || SameWithin(ea, eb)) continue;
            Assert.Fail($"The G-code differs from the reference at line {i + 1}: expected \"{ea}\", got \"{eb}\". If the change is meant, run with AVAMODELER_REGOLD=1.");
        }
    }

    private static bool SameWithin(string a, string b)
    {
        var wa = a.Split(' ');
        var wb = b.Split(' ');
        if (wa.Length != wb.Length) return false;
        for (int k = 0; k < wa.Length; k++)
        {
            if (wa[k] == wb[k]) continue;
            if (wa[k].Length < 2 || wa[k][0] != wb[k][0]) return false;
            if (!double.TryParse(wa[k][1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(wb[k][1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return false;
            int places = Math.Max(Places(wa[k]), Places(wb[k]));
            if (Math.Abs(x - y) > 1.01 * Math.Pow(10, -places)) return false;
        }
        return true;
    }

    private static int Places(string word) => word.Contains('.') ? word.Length - word.IndexOf('.') - 1 : 0;
}
