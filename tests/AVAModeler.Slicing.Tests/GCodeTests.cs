using System.Globalization;
using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

/// <summary>Plays G-code back as a printer would: where the nozzle goes, what it extrudes, what's heated when.</summary>
internal sealed class Playback
{
    public double X = double.NaN, Y = double.NaN, Z;
    public bool Absolute = true, RelativeE;
    public double E, TotalE, MaxRetract;
    public int? Nozzle, Bed, Fan;
    public bool Homed;
    public readonly List<(double X, double Y, double Z, double E, int Layer)> Extrusions = [];
    public readonly List<string> Order = [];
    public readonly List<int> Layers = [];
    public readonly List<string> Types = [];
    public int NozzleWhenFirstExtruded = -1, BedWhenFirstExtruded = -1;
    public bool HomedBeforeFirstExtrusion;

    public static Playback Run(string gcode)
    {
        var p = new Playback();
        foreach (var raw in gcode.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(";LAYER:")) p.Layers.Add(int.Parse(line[7..], CultureInfo.InvariantCulture));
            if (line.StartsWith(";TYPE:")) p.Types.Add(line[6..]);
            int c = line.IndexOf(';');
            if (c >= 0) line = line[..c].Trim();
            if (line.Length == 0) continue;
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double? Arg(char k) => words.Skip(1).Where(w => w[0] == k).Select(w => (double?)double.Parse(w[1..], CultureInfo.InvariantCulture)).FirstOrDefault();
            switch (words[0])
            {
                case "G21": case "M107": case "M84": case "G92": case "M18": case "M132": case "G80": case "G29": break;
                // Sailfish homes X and Y to their maximum (G162) and Z to its minimum (G161).
                case "G161": case "G162": p.Homed = true; break;
                case "G90": p.Absolute = true; break;
                case "G91": p.Absolute = false; break;
                case "M83": p.RelativeE = true; break;
                case "M82": p.RelativeE = false; break;
                case "G28": p.Homed = true; p.X = p.Y = 0; p.Z = 0; break;
                case "M104" when line.Contains(" T"): case "M140" when line.Contains(" T"):
                    if (words[0] == "M104") p.Nozzle = (int)Arg('S')!.Value; else p.Bed = (int)Arg('S')!.Value;
                    break;
                case "M104": case "M109": p.Nozzle = (int)Arg('S')!.Value; p.Order.Add(words[0]); break;
                case "M140": case "M190": p.Bed = (int)Arg('S')!.Value; p.Order.Add(words[0]); break;
                case "M106": p.Fan = (int)Arg('S')!.Value; break;
                case "G0": case "G1":
                {
                    double? x = Arg('X'), y = Arg('Y'), z = Arg('Z'), e = Arg('E');
                    if (p.Absolute)
                    {
                        if (x != null) p.X = x.Value;
                        if (y != null) p.Y = y.Value;
                        if (z != null) p.Z = z.Value;
                    }
                    else
                    {
                        p.X += x ?? 0; p.Y += y ?? 0; p.Z += z ?? 0;
                    }
                    if (e is { } de)
                    {
                        Assert.True(p.RelativeE, "extrusion is relative");
                        p.E += de;
                        if (de < 0) p.MaxRetract = Math.Max(p.MaxRetract, -p.E);
                        if (de > 0 && (x != null || y != null))
                        {
                            if (p.Extrusions.Count == 0)
                            {
                                p.NozzleWhenFirstExtruded = p.Nozzle ?? -1;
                                p.BedWhenFirstExtruded = p.Bed ?? -1;
                                p.HomedBeforeFirstExtrusion = p.Homed;
                            }
                            p.Extrusions.Add((p.X, p.Y, p.Z, de, p.Layers.Count - 1));
                            p.TotalE += de;
                        }
                    }
                    break;
                }
                default: throw new InvalidOperationException($"Unexpected G-code: {line}");
            }
        }
        return p;
    }
}

public class GCodeTests
{
    private static readonly PrinterProfile Printer = new();
    private static readonly MaterialProfile Pla = Presets.Materials[0];

    [Fact]
    public void A_cube_prints_inside_its_footprint_layer_by_layer_after_heating_and_homing()
    {
        var plan = PlannerTests.Plan(Shapes.Cube(0.02));
        var (text, stats) = GCode.Write(plan, Pla);
        var run = Playback.Run(text);
        Assert.Equal(Enumerable.Range(0, 100), run.Layers);
        Assert.Equal(100, stats.Layers);
        // Heated and homed before anything is printed, bed first.
        Assert.Equal(["M140", "M104", "M190", "M109"], run.Order.Take(4));
        Assert.Equal(210, run.NozzleWhenFirstExtruded);
        Assert.Equal(60, run.BedWhenFirstExtruded);
        Assert.True(run.HomedBeforeFirstExtrusion);
        // After the first layer, the nozzle drops to its usual temperature; the fan comes on from layer 2.
        Assert.Contains("M104 S205", text);
        Assert.Contains("M106 S255", text);
        // The print's own lines (after the start G-code's prime line) stay on the cube and its skirt, never off the bed.
        var printed = run.Extrusions.Where(e => e.Layer >= 0).ToList();
        Assert.All(printed, e => Assert.True(e.X > 100 - 4 && e.X < 120 + 4 && e.Y > 100 - 4 && e.Y < 120 + 4, $"({e.X}, {e.Y}) on the cube"));
        Assert.Equal(20, run.Extrusions.Max(e => e.Z), 6);
        // Everything is switched off at the end.
        Assert.Equal(0, run.Nozzle);
        Assert.Equal(0, run.Bed);
        Assert.Equal(0, run.Fan);
        Assert.Contains("WALL-OUTER", run.Types);
        Assert.Contains("FILL", run.Types);
        Assert.Contains("SKIRT", run.Types);
    }

    [Fact]
    public void The_filament_used_matches_the_volume_of_the_lines()
    {
        var plan = PlannerTests.Plan(Shapes.Cube(0.02));
        var (text, stats) = GCode.Write(plan, Pla);
        double perMm = GCode.FilamentPerMm(0.45, 0.2, 1.75);
        // 0.45 × 0.2 less the corners: 0.25 × 0.2 + π × 0.01 = 0.0814 mm² over the filament's 2.405 mm².
        Assert.Equal(0.0814159 / 2.4052819, perMm, 6);
        double lines = plan.Layers.Sum(l => l.Paths.Sum(p => p.Length) * GCode.FilamentPerMm(0.45, l.Layer.Height, 1.75));
        Assert.Equal(lines, stats.FilamentMm, 1);
        var run = Playback.Run(text);
        // What's extruded is the lines plus the start G-code's 15 mm prime line; retractions are put back.
        Assert.Equal(stats.FilamentMm + 15, run.TotalE, 1);
        // Every retraction is put back before the next line; the end G-code's own 2 mm pull is all that's left.
        Assert.Equal(-2, run.E - run.TotalE, 6);
        Assert.True(run.MaxRetract <= Pla.RetractLength + 1e-9);
        // The cube's walls and infill: a 20 mm cube at 20% weighs a few grams of PLA.
        Assert.InRange(stats.Grams, 2, 8);
        Assert.Equal(stats.FilamentMm * 2.4052819 / 1000 * 1.24, stats.Grams, 3);
        Assert.Contains($";Filament used: ", text);
        Assert.StartsWith(";FLAVOR:Marlin\n;TIME:", text);
        // Plain ASCII throughout, the printer's name included.
        Assert.All(text, c => Assert.True(c < 128, $"'{c}' is ASCII"));
        Assert.Contains("for Generic 220 x 220 and PLA", text);
        Assert.Contains(";LAYER_COUNT:100\n", text);
        Assert.Contains(";MAXZ:20\n", text);
    }

    [Fact]
    public void Long_travel_retracts_and_hops_and_short_travel_does_not()
    {
        var doc = Shapes.Cube(0.01);
        Shapes.Box(doc.Layers[0].Mesh, new Vec3(0.03, 0, -0.005), new Vec3(0.04, 0.01, 0.005));
        doc.Layers[0].Mesh.Touch();
        var plan = PlannerTests.Plan(doc, new SliceSettings { ZHop = 0.4 });
        var (text, stats) = GCode.Write(plan, Pla);
        Assert.True(stats.Retractions > 100);
        // The print itself, between the start and end G-code.
        var lines = text[text.IndexOf(";LAYER:0")..text.IndexOf(";TYPE:END")].Split('\n');
        int hops = 0;
        // Every hop is up and back down round a travel move, after a retraction.
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("G1 Z") && !lines[i - 1].StartsWith("G0 "))
            {
                // The filament was last moved back, not forward: it's retracted while the nozzle travels.
                var lastE = lines[..i].Last(l => l.Contains(" E"));
                Assert.True(lastE.StartsWith("G1 E-0.8 F2100"), string.Join(" | ", lines[(i - 3)..(i + 3)]));
                Assert.StartsWith("G0 X", lines[i + 1]);
                Assert.StartsWith("G1 Z", lines[i + 2]);
                hops++;
            }
        Assert.True(hops > 50);
        var noRetract = GCode.Write(plan, Pla with { RetractLength = 0 }).Stats;
        Assert.Equal(0, noRetract.Retractions);
    }

    [Fact]
    public void Placeholders_in_the_start_and_end_gcode_are_filled_in()
    {
        var printer = Printer with { StartGCode = "G28\nM117 Bed {bed_temp} nozzle {nozzle_temp}", EndGCode = "G1 Y{bed_depth}\nM84" };
        var plan = Planner.Plan(Slicer.Slice(Placement.Place(Shapes.Cube(0.01), printer), new SliceSettings(), printer), printer);
        var (text, _) = GCode.Write(plan, Pla);
        Assert.Contains("M117 Bed 60 nozzle 205\n", text);
        Assert.Contains("G1 Y220\nM84\n;End of print\n", text);
        Assert.DoesNotContain("{", text);
    }

    [Fact]
    public void Speeds_follow_the_kind_of_line_and_the_first_layer_is_slow()
    {
        var plan = PlannerTests.Plan(Shapes.Cube(0.02));
        // No slowing down to cool, to see the speeds as set.
        var (text, stats) = GCode.Write(plan, Pla with { MinLayerSeconds = 0 });
        Assert.Equal(0, stats.SlowedLayers);
        var layer0 = text[text.IndexOf(";LAYER:0\n")..text.IndexOf(";LAYER:1\n")];
        Assert.DoesNotContain("F3000", layer0.Replace("G0 Z", ""));
        Assert.Contains("F1200", layer0);
        var layer50 = text[text.IndexOf(";LAYER:50\n")..text.IndexOf(";LAYER:51\n")];
        Assert.Contains("F1800", layer50); // the outer wall at 30 mm/s
        Assert.Contains("F3600", layer50); // infill at 60
        Assert.Contains("F9000", layer50); // travel at 150
        // Time: at least the lines at their speeds; a 20 mm cube takes a quarter of an hour or so.
        Assert.InRange(stats.Seconds / 60, 8, 40);
        Assert.Matches(@"^\d+ min$", stats.Time);
        Assert.Contains("100 layers · ", stats.Summary);
    }

    [Fact]
    public void Numbers_are_short_and_never_negative_zero()
    {
        Assert.Equal("0", GCode.F(-0.00001, 3));
        Assert.Equal("1.5", GCode.F(1.50000, 3));
        Assert.Equal("-0.8", GCode.F(-0.8, 5));
        Assert.Equal("12346", GCode.F(12345.6, 0));
    }

    [Fact]
    public void Saving_writes_the_file_whole()
    {
        string dir = Path.Combine(Path.GetTempPath(), "avamodeler-slicing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "cube.gcode");
            var stats = GCode.Save(PlannerTests.Plan(Shapes.Cube(0.01)), Pla, path);
            Assert.StartsWith(";FLAVOR:Marlin", File.ReadAllText(path));
            Assert.Equal(["cube.gcode"], Directory.GetFiles(dir).Select(Path.GetFileName));
            Assert.True(stats.ExtrusionMoves > 0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class PrintJobTests
{
    [Fact]
    public void A_job_runs_every_stage_and_warns_about_what_it_should()
    {
        var job = PrintJob.Run(Shapes.Cube(0.02), new PrintSetup());
        Assert.Empty(job.Warnings);
        Assert.Equal(100, job.Stats.Layers);
        Assert.StartsWith(";FLAVOR:Marlin", job.GCode);

        var big = PrintJob.Run(Shapes.Cube(0.3), new PrintSetup());
        Assert.Contains(big.Warnings, w => w.Contains("bigger than"));

        var doc = Shapes.Cube(0.02);
        doc.Layers[0].Mesh.Polygons.RemoveAt(0);
        doc.Layers[0].Mesh.Touch();
        var open = PrintJob.Run(doc, new PrintSetup());
        Assert.Contains(open.Warnings, w => w.StartsWith("The mesh check says: Open"));
        Assert.Contains(open.Warnings, w => w.Contains("didn't close"));
        Assert.Throws<ArgumentException>(() => PrintJob.Run(doc, new PrintSetup { Settings = new SliceSettings { LayerHeight = 5 } }));
    }
}
