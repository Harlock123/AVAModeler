using AVAModeler.Core;

namespace AVAModeler.Slicing.Tests;

public class ProfileTests
{
    [Fact]
    public void The_presets_check_out_and_have_names_of_their_own()
    {
        foreach (var p in Presets.Printers) p.Checked();
        foreach (var m in Presets.Materials) m.Checked();
        Assert.Equal(Presets.Printers.Count, Presets.Printers.Select(p => p.Name).Distinct().Count());
        Assert.Equal(Presets.Materials.Count, Presets.Materials.Select(m => m.Name).Distinct().Count());
    }

    [Fact]
    public void Bad_settings_are_refused_saying_what_is_wrong()
    {
        var e = Assert.Throws<ArgumentException>(() => new PrinterProfile { NozzleDiameter = 0 }.Checked());
        Assert.Contains("nozzle diameter", e.Message);
        Assert.Contains("0.1 to 2 mm", e.Message);
        Assert.Throws<ArgumentException>(() => new PrinterProfile { BedWidth = double.NaN }.Checked());
        Assert.Throws<ArgumentException>(() => new PrinterProfile { Name = " " }.Checked());
        Assert.Contains("°C", Assert.Throws<ArgumentException>(() => new MaterialProfile { NozzleTemp = 900 }.Checked()).Message);
        Assert.Throws<ArgumentException>(() => new MaterialProfile { FanPercent = 101 }.Checked());
        Assert.Throws<ArgumentException>(() => new PlacementOptions { ScalePercent = 0 }.Checked());
    }

    [Fact]
    public void Start_and_end_gcode_placeholders_are_checked_and_filled()
    {
        var e = Assert.Throws<ArgumentException>(() => new PrinterProfile { StartGCode = "M190 S{bed_tmp}" }.Checked());
        Assert.Contains("{bed_tmp}", e.Message);
        Assert.Contains("{bed_temp}", e.Message);
        string filled = Placeholders.Fill("M190 S{bed_temp}\nG1 Y{bed_depth}\n{unknown}", new Dictionary<string, double> { ["bed_temp"] = 60, ["bed_depth"] = 220.5 });
        Assert.Equal("M190 S60\nG1 Y220.5\n{unknown}", filled);
        Assert.Contains("{bed_depth}", new PrinterProfile().EndGCode);
    }

    [Fact]
    public void A_setup_round_trips_through_json_with_the_persons_own_profiles()
    {
        var setup = new PrintSetup()
            .SavePrinter(new PrinterProfile { Name = "My delta", BedWidth = 180, BedDepth = 180, MaxHeight = 300 })
            .SaveMaterial(new MaterialProfile { Name = "Silk PLA", NozzleTemp = 215 })
            with { Placement = new PlacementOptions { Position = BedPosition.AsModelled, ScalePercent = 50 } };
        string json = setup.ToJson();
        Assert.Contains("\"AsModelled\"", json);
        var back = PrintSetup.FromJson(json);
        Assert.Equal(setup.Printer, back.Printer);
        Assert.Equal(setup.Material, back.Material);
        Assert.Equal(setup.Placement, back.Placement);
        Assert.Equal("My delta", Assert.Single(back.MyPrinters).Name);
        Assert.Equal(Presets.Printers.Count + 1, back.AllPrinters.Count());
        // Missing parts take their defaults; a bad one is refused.
        Assert.Equal(Presets.Printers[0], PrintSetup.FromJson("{}").Printer);
        Assert.Throws<ArgumentException>(() => PrintSetup.FromJson("""{"Printer":{"NozzleDiameter":9}}"""));
        // Saving again replaces by name; a preset's name is refused.
        var again = back.SavePrinter(back.Printer with { BedWidth = 200 });
        Assert.Equal(200, Assert.Single(again.MyPrinters).BedWidth);
        Assert.Throws<ArgumentException>(() => back.SavePrinter(Presets.Printers[1]));
    }
}

public class PlacementTests
{
    private static readonly PrinterProfile Printer = new();

    [Fact]
    public void A_two_centimetre_cube_lands_20_mm_wide_on_the_bed_centre()
    {
        var model = Placement.Place(Shapes.Cube(0.02), Printer);
        Assert.True(model.Fits);
        Assert.True(model.Printable);
        Assert.Equal(8, model.Points.Count);
        Assert.Equal(12, model.Triangles.Count);
        Assert.True(Vec3.Near(new Vec3(100, 100, 0), model.Bounds.Min, 1e-9));
        Assert.True(Vec3.Near(new Vec3(120, 120, 20), model.Bounds.Max, 1e-9));
    }

    [Fact]
    public void Triangles_face_out_anticlockwise_with_z_up()
    {
        var model = Placement.Place(Shapes.Cube(0.02), Printer);
        var centre = model.Bounds.Center;
        foreach (var (a, b, c) in model.Triangles)
        {
            Vec3 pa = model.Points[a], pb = model.Points[b], pc = model.Points[c];
            var n = Vec3.Cross(pb - pa, pc - pa);
            Assert.True(Vec3.Dot(n, (pa + pb + pc) / 3 - centre) > 0);
        }
        // The cube's top (the Modeler's +Y) is the bed's +Z.
        Assert.Contains(model.Points, p => Math.Abs(p.Z - 20) < 1e-9);
    }

    [Fact]
    public void As_modelled_puts_the_origin_at_the_bed_centre_and_still_drops_it_onto_the_bed()
    {
        var doc = Shapes.Doc(m => Shapes.Box(m, new Vec3(0.01, 0.05, 0.01), new Vec3(0.03, 0.06, 0.02)));
        var model = Placement.Place(doc, Printer, new PlacementOptions { Position = BedPosition.AsModelled });
        Assert.True(Vec3.Near(new Vec3(120, 120, 0), model.Bounds.Min, 1e-9));
        Assert.True(Vec3.Near(new Vec3(140, 130, 10), model.Bounds.Max, 1e-9));
    }

    [Fact]
    public void Scale_and_what_does_not_fit_are_reported()
    {
        var model = Placement.Place(Shapes.Cube(0.02), Printer, new PlacementOptions { ScalePercent = 250 });
        Assert.True(Vec3.Near(new Vec3(50, 50, 50), model.Bounds.Size, 1e-9));
        Assert.True(model.Fits);

        var big = Placement.Place(Shapes.Cube(0.3), Printer);
        Assert.False(big.Fits);
        Assert.Contains(big.Problems, p => p.Contains("bigger than the 220 × 220 mm bed"));
        Assert.Contains(big.Problems, p => p.Contains("taller than the printer's 250 mm"));

        var doc = Shapes.Doc(m => Shapes.Box(m, new Vec3(0.1, 0, 0), new Vec3(0.12, 0.01, 0.01)));
        var off = Placement.Place(doc, Printer, new PlacementOptions { Position = BedPosition.AsModelled });
        Assert.Contains(off.Problems, p => p.Contains("hangs over the edge"));
    }

    [Fact]
    public void Only_the_foreground_layers_are_printed_and_an_open_mesh_is_not_called_printable()
    {
        var doc = Shapes.Cube(0.02);
        Shapes.Box(doc.Layers[1].Mesh, new Vec3(1, 0, 0), new Vec3(2, 1, 1));
        doc.Layers[1].State = LayerState.Background;
        var model = Placement.Place(doc, Printer);
        Assert.Equal(12, model.Triangles.Count);

        doc.Layers[0].Mesh.Polygons.RemoveAt(0);
        doc.Layers[0].Mesh.Touch();
        var open = Placement.Place(doc, Printer);
        Assert.False(open.Printable);
        Assert.Contains("Open", open.Verdict);

        var empty = Placement.Place(new Document(), Printer);
        Assert.False(empty.Fits);
        Assert.Contains("nothing to print", Assert.Single(empty.Problems));
    }
}
