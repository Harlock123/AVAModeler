using System.Buffers.Binary;
using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

/// <summary>Two extruders: the part on either, supports in a second material on the other.</summary>
public class DualExtruderTests
{
    private static readonly PrinterProfile Fcp = Presets.Printers.Single(p => p.Name == "Flashforge Creator Pro");

    private static PrintJob Tee(SliceSettings settings, MaterialProfile? support = null) => PrintJob.Run(SafetyTests.Tee(),
        new PrintSetup { Printer = Fcp, Settings = settings, SupportMaterial = support ?? Presets.Materials[0] });

    [Fact]
    public void Supports_in_a_second_material_go_on_the_other_extruder()
    {
        var petg = Presets.Materials.Single(m => m.Name == "PETG");
        var job = Tee(new SliceSettings { Supports = true, SupportExtruder = 1 }, petg);
        string g = job.GCode;
        // Both heated (each its own temperature) and waited for before the print; the part's extruder chosen.
        Assert.Contains("M104 T0 S210", g);
        Assert.Contains("M104 T1 S240", g);
        Assert.Contains("M109 T0 S210", g);
        Assert.Contains("M109 T1 S240", g);
        Assert.Contains("T0 ; the part's extruder", g);
        // Every support line is printed on T1, everything else on T0.
        string? tool = null, type = null;
        foreach (var line in g[g.IndexOf(";LAYER:0")..g.IndexOf(";TYPE:END")].Split('\n'))
        {
            if (line is "T0" or "T1") tool = line;
            if (line.StartsWith(";TYPE:")) type = line[6..];
            // (The prime tower is printed by both: each primes on it.)
            if (line.StartsWith("G1 X") && line.Contains(" E") && type != "PRIME-TOWER")
                Assert.Equal(type is "SUPPORT" or "SUPPORT-INTERFACE" ? "T1" : "T0", tool ?? "T0");
        }
        // The filament split between them, weighed by each one's density; the second turned off at the end.
        Assert.True(job.Stats.SupportFilamentMm > 0 && job.Stats.SupportFilamentMm < job.Stats.FilamentMm);
        double area = Math.PI * 1.75 * 1.75 / 4 / 1000;
        Assert.Equal((job.Stats.FilamentMm - job.Stats.SupportFilamentMm) * area * 1.24 + job.Stats.SupportFilamentMm * area * 1.27, job.Stats.Grams, 6);
        Assert.Contains("M104 T1 S0", g[g.IndexOf(";TYPE:END")..] + g);
        // Pulled back before each change, so the idle nozzle doesn't drip.
        var lines = g.Split('\n');
        int start = Array.IndexOf(lines, ";LAYER:0");
        for (int i = start; i < lines.Length; i++)
            if (lines[i] is "T0" or "T1")
                Assert.StartsWith("G1 E-", lines[start..i].Last(l => l.Contains(" E")));
    }

    [Fact]
    public void The_part_can_print_on_the_second_extruder_and_one_extruder_stays_untagged()
    {
        var left = PrintJob.Run(Shapes.Cube(0.01), new PrintSetup { Printer = Fcp, Settings = new SliceSettings { ModelExtruder = 1 } });
        Assert.Contains("M109 T1 S210", left.GCode);
        Assert.Contains("T1 ; the part's extruder", left.GCode);
        Assert.DoesNotContain("M104 T0", left.GCode);
        // One extruder, as before: no T at all.
        var plain = PrintJob.Run(Shapes.Cube(0.01), new PrintSetup());
        Assert.DoesNotContain("T0", plain.GCode);
        Assert.Contains("M109 S210 ; wait for the nozzle\n", plain.GCode);
        // Asking for a second extruder on a printer with one is refused.
        Assert.Contains("one extruder", Assert.Throws<ArgumentException>(() => new SliceSettings { SupportExtruder = 1 }.Checked(new PrinterProfile())).Message);
        Assert.Throws<ArgumentException>(() => new SliceSettings { ModelExtruder = 2 }.Checked(Fcp));
    }

    [Fact]
    public void In_x3g_the_second_extruder_is_its_own_axis_after_a_change()
    {
        var job = Tee(new SliceSettings { Supports = true, SupportExtruder = 1 });
        var x = X3g.Convert(job.GCode, SailfishMachine.CreatorPro);
        // Walk the commands: after 134 with tool 1, moves push filament on B and leave A still; after tool 0, the reverse.
        // Past the build's start: 153, four reserved bytes and the name up to its null.
        int i = Array.IndexOf(x, (byte)0, 5) + 1, tool = 0, changes = 0;
        long aMoved = 0, bMoved = 0, wrong = 0;
        while (i < x.Length)
        {
            int c = x[i++];
            switch (c)
            {
                case 134: tool = x[i++]; changes++; break;
                case 155:
                {
                    long a = BinaryPrimitives.ReadInt32LittleEndian(x.AsSpan(i + 12)), b = BinaryPrimitives.ReadInt32LittleEndian(x.AsSpan(i + 16));
                    if (tool == 0) { aMoved += Math.Abs(a); if (b != 0) wrong++; }
                    else { bMoved += Math.Abs(b); if (a != 0) wrong++; }
                    i += 20 + 4 + 1 + 4 + 2;
                    break;
                }
                case 139: i += 24; break;
                case 140: i += 20; break;
                case 131: case 132: i += 7; break;
                case 144: case 137: i += 1; break;
                case 135: case 141: i += 5; break;
                case 136: { i += 2; int len = x[i++]; i += len; break; }
                case 154: i += 1; break;
                default: throw new InvalidOperationException($"command {c} at {i - 1}");
            }
        }
        Assert.True(changes > 4);
        Assert.Equal(0, wrong);
        Assert.True(aMoved > 0 && bMoved > 0);
    }
}

public class PrimeTowerTests
{
    private static readonly PrinterProfile Fcp = Presets.Printers.Single(p => p.Name == "Flashforge Creator Pro");

    private static PrintJob Dual(Document doc, SliceSettings? extra = null) => PrintJob.Run(doc,
        new PrintSetup { Printer = Fcp, Settings = (extra ?? new SliceSettings()) with { Supports = true, SupportExtruder = 1 }, SupportMaterial = Presets.Materials[1] });

    [Fact]
    public void Each_extruder_primes_on_the_tower_after_a_change_up_to_the_last_change()
    {
        var job = Dual(SafetyTests.Tee());
        var plan = job.Plan;
        var (min, max) = plan.PrimeTower!.Value;
        // 15 mm square, 5 mm to the right of the part, on the bed.
        Assert.Equal(15, max.X - min.X, 6);
        Assert.Equal(plan.Sliced.Model.Bounds.Max.X + 5, min.X, 6);
        Assert.True(max.X <= Fcp.BedWidth - 2);
        int lastSupport = plan.Layers.ToList().FindLastIndex(l => l.Paths.Any(p => p.Kind == PathKind.Support || p.Kind == PathKind.SupportInterface));
        for (int i = 0; i < plan.Layers.Count; i++)
        {
            var paths = plan.Layers[i].Paths.ToList();
            var tower = paths.Where(p => p.Kind == PathKind.PrimeTower).ToList();
            if (i > lastSupport) { Assert.Empty(tower); continue; }
            Assert.NotEmpty(tower);
            Assert.All(tower.SelectMany(p => p.Points), q => Assert.True(q.X >= min.X - 1e-6 && q.X <= max.X + 1e-6 && q.Y >= min.Y - 1e-6 && q.Y <= max.Y + 1e-6));
            if (!paths.Any(p => p.Kind == PathKind.Support || p.Kind == PathKind.SupportInterface)) continue;
            // The support extruder primes before its supports; the part's after them, before the part.
            int firstSupport = paths.FindIndex(p => p.Kind is PathKind.Support or PathKind.SupportInterface);
            int lastSupportPath = paths.FindLastIndex(p => p.Kind is PathKind.Support or PathKind.SupportInterface);
            int firstPart = paths.FindIndex(p => p.Kind is PathKind.OuterWall or PathKind.InnerWall);
            Assert.Contains(paths.Take(firstSupport), p => p.Kind == PathKind.PrimeTower && p.Tool == 1);
            Assert.Contains(paths.Skip(lastSupportPath).Take(firstPart - lastSupportPath), p => p.Kind == PathKind.PrimeTower && p.Tool == 0);
        }
        // In the G-code: after every change of extruder, the first thing printed is the tower.
        var lines = job.GCode[job.GCode.IndexOf(";LAYER:0")..job.GCode.IndexOf(";TYPE:END")].Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i] is "T0" or "T1")
                Assert.Equal(";TYPE:PRIME-TOWER", lines.Skip(i + 1).First(l => l.StartsWith(";TYPE:")));
        // The skirt goes round the tower too.
        var skirt = plan.Layers[0].Paths.Where(p => p.Kind == PathKind.Skirt).SelectMany(p => p.Points).ToList();
        Assert.True(skirt.Max(p => p.X) > max.X);
        Assert.Empty(job.Warnings);
    }

    [Fact]
    public void No_tower_with_one_extruder_or_when_turned_off_and_a_note_when_theres_no_room()
    {
        Assert.Null(PrintJob.Run(SafetyTests.Tee(), new PrintSetup { Printer = Fcp, Settings = new SliceSettings { Supports = true } }).Plan.PrimeTower);
        var off = Dual(SafetyTests.Tee(), new SliceSettings { PrimeTower = false });
        Assert.Null(off.Plan.PrimeTower);
        Assert.DoesNotContain(off.Plan.Layers.SelectMany(l => l.Paths), p => p.Kind == PathKind.PrimeTower);
        // A part nearly as wide as the bed leaves no room either side, in front or behind: said, and printed without.
        var crowded = Dual(WideWithOverhang());
        Assert.Contains(crowded.Warnings, w => w.Contains("no room on the bed for the 15 mm prime tower"));
        Assert.Null(crowded.Plan.PrimeTower);
    }

    /// <summary>A T across nearly the whole bed: a 20 mm post with a slab 220 × 140 mm on top.</summary>
    private static Document WideWithOverhang() => Shapes.Doc(m =>
    {
        Shapes.Box(m, new Vec3(-0.01, 0, -0.01), new Vec3(0.01, 0.01, 0.01));
        Shapes.Box(m, new Vec3(-0.11, 0.01, -0.07), new Vec3(0.11, 0.012, 0.07));
    });
}
