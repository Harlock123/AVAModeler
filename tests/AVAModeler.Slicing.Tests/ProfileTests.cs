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
            .SaveMaterial(new MaterialProfile { Name = "My silk PLA", NozzleTemp = 215 })
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

public class LayFlatAndCopiesTests
{
    private static readonly PrinterProfile Printer = new();

    /// <summary>A box 10 mm wide (X), 30 mm tall (Y up) and 20 mm deep (Z): its largest faces are its sides, 30 × 20.</summary>
    private static Document TallBox() => Shapes.Doc(m => Shapes.Box(m, new Vec3(0, 0, 0), new Vec3(0.01, 0.03, 0.02)));

    [Fact]
    public void Laid_on_its_largest_face_a_tall_box_lies_down()
    {
        var model = Placement.Place(TallBox(), Printer, new PlacementOptions { LayFlat = LayFlat.LargestFace });
        // Now 10 mm high, on its 30 × 20 side.
        Assert.Equal(10, model.Bounds.Size.Z, 6);
        Assert.Equal([20.0, 30.0], new[] { model.Bounds.Size.X, model.Bounds.Size.Y }.Select(v => Math.Round(v, 6)).Order());
        Assert.Equal(0, model.Bounds.Min.Z, 9);
        // Still facing out.
        var c = model.Bounds.Center;
        foreach (var (a, b, cc) in model.Triangles)
        {
            Vec3 pa = model.Points[a], pb = model.Points[b], pc = model.Points[cc];
            Assert.True(Vec3.Dot(Vec3.Cross(pb - pa, pc - pa), (pa + pb + pc) / 3 - c) > 0);
        }
        // A pyramid already stands on its largest flat face.
        var doc = Shapes.Doc(m =>
        {
            m.Points.AddRange([new(-0.01, 0, -0.01), new(0.01, 0, -0.01), new(0.01, 0, 0.01), new(-0.01, 0, 0.01), new(0, 0.01, 0)]);
            foreach (var f in (int[][])[[0, 1, 2, 3], [0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]])
            {
                var poly = new Polygon(f);
                m.Polygons.Add(poly);
                m.Touch();
                if (Vec3.Dot(m.Normal(poly), m.Centroid(poly) - new Vec3(0, 0.003, 0)) < 0) poly.Points.Reverse();
            }
        });
        Assert.Equal(10, Placement.Place(doc, Printer, new PlacementOptions { LayFlat = LayFlat.LargestFace }).Bounds.Size.Z, 6);
    }

    [Fact]
    public void A_selected_face_goes_down_on_the_bed()
    {
        var doc = TallBox();
        var mesh = doc.Layers[0].Mesh;
        // The +X face (10 mm from the origin), 30 × 20.
        int face = Enumerable.Range(0, mesh.Polygons.Count).Single(i => mesh.Normal(i).X > 0.9);
        doc.Selection.PolygonsIn(0).Add(face);
        var model = Placement.Place(doc, Printer, new PlacementOptions { LayFlat = LayFlat.SelectedFace });
        Assert.Equal(10, model.Bounds.Size.Z, 6);
        // That face's points are the ones on the bed now: the four at X = 10 mm in the Modeler.
        Assert.Equal(4, model.Points.Count(p => Math.Abs(p.Z) < 1e-9));
        // The top face instead: on its head, still 30 tall.
        doc.Selection.PolygonsIn(0).Clear();
        doc.Selection.PolygonsIn(0).Add(Enumerable.Range(0, mesh.Polygons.Count).Single(i => mesh.Normal(i).Y > 0.9));
        Assert.Equal(30, Placement.Place(doc, Printer, new PlacementOptions { LayFlat = LayFlat.SelectedFace }).Bounds.Size.Z, 6);
        doc.Selection.PolygonsIn(0).Clear();
        var e = Assert.Throws<ArgumentException>(() => Placement.Place(doc, Printer, new PlacementOptions { LayFlat = LayFlat.SelectedFace }));
        Assert.Contains("Select a polygon", e.Message);
    }

    [Fact]
    public void Turning_swaps_the_footprint_and_copies_are_laid_out_in_rows()
    {
        var turned = Placement.Place(TallBox(), Printer, new PlacementOptions { RotateDegrees = 90 });
        Assert.Equal(20, turned.Bounds.Size.X, 6);
        Assert.Equal(10, turned.Bounds.Size.Y, 6);
        Assert.Equal(110, turned.Bounds.Center.X, 6);

        // Four 20 mm cubes 5 mm apart: two rows of two, 45 mm across, centred.
        var four = Placement.Place(Shapes.Cube(0.02), Printer, new PlacementOptions { Copies = 4 });
        Assert.Equal(32, four.Points.Count);
        Assert.Equal(48, four.Triangles.Count);
        Assert.Equal(45, four.Bounds.Size.X, 6);
        Assert.Equal(45, four.Bounds.Size.Y, 6);
        Assert.Equal(110, four.Bounds.Center.X, 6);
        Assert.Equal(110, four.Bounds.Center.Y, 6);
        var sliced = Slicer.Slice(four, new SliceSettings(), Printer);
        Assert.Equal(4, sliced.Layers[0].Islands.Count);
        // Three: a row of two and one under it, centred.
        var three = Placement.Place(Shapes.Cube(0.02), Printer, new PlacementOptions { Copies = 3, CopyGap = 10 });
        Assert.Equal(50, three.Bounds.Size.X, 6);
        Assert.Equal(3, Slicer.Slice(three, new SliceSettings(), Printer).Layers[0].Islands.Count);
        // Too many for the bed.
        var many = Placement.Place(Shapes.Cube(0.05), Printer, new PlacementOptions { Copies = 25 });
        Assert.Contains(many.Problems, p => p.Contains("25 copies take"));
        Assert.Throws<ArgumentException>(() => new PlacementOptions { Copies = 0 }.Checked());
    }

    [Fact]
    public void Turning_one_direction_onto_another_is_a_true_rotation()
    {
        Vec3[] dirs = [new(1, 0, 0), new(0, 0, 1), new(0, 0, -1), new Vec3(1, 2, 3).Normalized(), new Vec3(-0.2, 0.1, -0.97).Normalized()];
        foreach (var from in dirs)
            foreach (var to in dirs)
            {
                var m = Mat3.Turning(from, to);
                Assert.True(Vec3.Near(to, m * from, 1e-9), $"{from} to {to}");
                // Lengths and handedness kept: the axes stay a right-handed set of unit vectors.
                var x = m * new Vec3(1, 0, 0);
                var y = m * new Vec3(0, 1, 0);
                Assert.Equal(1, x.Length, 9);
                Assert.True(Vec3.Near(m * new Vec3(0, 0, 1), Vec3.Cross(x, y), 1e-9));
            }
    }
}
