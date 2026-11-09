using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

/// <summary>The printers on the market as presets: each checks out and prints a cube safely on its own bed.</summary>
public class PresetTests
{
    [Fact]
    public void There_are_dozens_of_printers_each_checked_and_named_once()
    {
        Assert.True(PrinterPresets.All.Count >= 40, $"{PrinterPresets.All.Count} printers");
        Assert.Equal(Presets.Printers.Count, Presets.Printers.Select(p => p.Name).Distinct().Count());
        foreach (var p in Presets.Printers) p.Checked();
        // Makers people ask for are there.
        foreach (string name in (string[])["Flashforge Creator Pro", "Prusa i3 MK3S+", "Prusa MK4S", "Prusa MINI+", "Creality Ender 3 / 3 Pro / 3 V2", "Bambu Lab X1 Carbon", "Voron 2.4 (350)", "Ultimaker S5"])
            Assert.Contains(Presets.Printers, p => p.Name == name);
        // The deltas are round, with the origin in the middle; Bowden printers pull back further than direct drives.
        Assert.All(Presets.Printers.Where(p => p.RoundBed), p => Assert.True(p.OriginAtCentre && p.BedWidth == p.BedDepth));
        Assert.Equal(5, Presets.Printers.Single(p => p.Name.StartsWith("Creality Ender 3 /")).RetractLength);
        Assert.Equal(0.8, Presets.Printers.Single(p => p.Name == "Prusa MK4S").RetractLength);
        Assert.All(Presets.Printers.Where(p => p.Name.StartsWith("Ultimaker")), p => Assert.Equal(2.85, p.FilamentDiameter));
    }

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var p in Presets.Printers) data.Add(p.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_printer_prints_a_cube_on_its_own_bed(string name)
    {
        var printer = Presets.Printers.Single(p => p.Name == name);
        var job = PrintJob.Run(Shapes.Cube(0.02), new PrintSetup { Printer = printer });
        Assert.Empty(job.Warnings);
        Assert.DoesNotContain("{", job.GCode);
        var run = Playback.Run(job.GCode);
        Assert.True(run.HomedBeforeFirstExtrusion);
        Assert.Equal(210, run.NozzleWhenFirstExtruded);
        // In the firmware's own coordinates: from the front-left corner, or round the middle.
        double x0 = printer.OriginAtCentre ? -printer.BedWidth / 2 : 0, y0 = printer.OriginAtCentre ? -printer.BedDepth / 2 : 0;
        foreach (var e in run.Extrusions.Where(e => e.Layer >= 0))
        {
            Assert.InRange(e.X, x0, x0 + printer.BedWidth);
            Assert.InRange(e.Y, y0, y0 + printer.BedDepth);
            // The cube sits in the middle of the bed: within its 20 mm and the skirt of the centre.
            Assert.InRange(e.X - x0, printer.BedWidth / 2 - 14, printer.BedWidth / 2 + 14);
            Assert.InRange(e.Y - y0, printer.BedDepth / 2 - 14, printer.BedDepth / 2 + 14);
        }
        // The printer's retraction, not the material's, when it has one.
        if (printer.RetractLength is { } r) Assert.Contains($"G1 E-{GCode.F(r, 5)} F", job.GCode);
    }

    [Fact]
    public void The_creator_pro_prints_round_its_middle_and_says_to_convert_for_sailfish()
    {
        var fcp = Presets.Printers.Single(p => p.Name == "Flashforge Creator Pro");
        Assert.Equal((227.0, 148.0, 150.0), (fcp.BedWidth, fcp.BedDepth, fcp.MaxHeight));
        Assert.Contains("GPX", fcp.Notes);
        var job = PrintJob.Run(Shapes.Cube(0.02), new PrintSetup { Printer = fcp });
        var run = Playback.Run(job.GCode);
        var printed = run.Extrusions.Where(e => e.Layer >= 0).ToList();
        Assert.InRange(printed.Average(e => e.X), -1, 1);
        Assert.InRange(printed.Average(e => e.Y), -1, 1);
        Assert.Contains(";MINX:-10\n", job.GCode);
        // Its start G-code homes the Sailfish way and primes along the front of its own bed.
        Assert.Contains("G162 X Y F2000", job.GCode);
        Assert.Contains("G1 X-113.5 Y-74 Z5 F3000", job.GCode);
    }

    [Fact]
    public void Round_beds_take_what_fits_inside_the_circle()
    {
        var kossel = Presets.Printers.Single(p => p.Name == "Anycubic Kossel Linear Plus");
        // A 150 mm cube's corners are 106 mm from its middle: inside the 115 mm radius. A 170 mm one's aren't.
        Assert.Empty(Placement.Place(Shapes.Cube(0.15), kossel).Problems);
        var big = Placement.Place(Shapes.Cube(0.17), kossel);
        Assert.Contains(big.Problems, p => p.Contains("past its 115 mm edge"));
        Assert.Throws<ArgumentException>(() => (kossel with { BedDepth = 200 }).Checked());
    }

    [Fact]
    public void A_profile_with_the_new_settings_round_trips_through_json()
    {
        var mine = Presets.Printers.Single(p => p.Name == "FLSUN Q5") with { Name = "My delta", RetractLength = 4.5 };
        var setup = new PrintSetup().SavePrinter(mine);
        var back = PrintSetup.FromJson(setup.ToJson());
        Assert.Equal(mine, back.Printer);
        Assert.True(back.Printer.RoundBed && back.Printer.OriginAtCentre);
        Assert.Equal(4.5, back.Printer.RetractLength);
        // An old setup without them keeps the material's retraction and a corner origin.
        var old = PrintSetup.FromJson("""{"Printer":{"Name":"Old one","BedWidth":200,"BedDepth":200}}""");
        Assert.Null(old.Printer.RetractLength);
        Assert.False(old.Printer.OriginAtCentre);
    }
}
