using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Geometry;

namespace AVAModeler.Slicing;

/// <summary>Where on the bed the object goes.</summary>
public enum BedPosition
{
    /// <summary>Its footprint centred on the bed.</summary>
    Centre,
    /// <summary>The Modeler's origin at the bed's centre, so the object sits where it was modelled round the origin.</summary>
    AsModelled,
}

/// <summary>How the object is put on the bed: where, and scaled how much.</summary>
public sealed record PlacementOptions
{
    public BedPosition Position { get; init; } = BedPosition.Centre;
    /// <summary>Scale, as a percentage: 100 prints the object at its modelled size (1 m = 1000 mm).</summary>
    public double ScalePercent { get; init; } = 100;

    public PlacementOptions Checked()
    {
        PrinterProfile.Range(ScalePercent, 0.1, 100000, "The scale", "%");
        return this;
    }
}

/// <summary>
/// The object ready to slice: triangles in the printer's space (millimetres, Z up, wound anticlockwise from outside, as
/// in STL), welded into shared points so the slicer can follow edges from one triangle to the next, resting on the bed
/// (its lowest point at Z = 0) and placed in X and Y. It says whether it fits the printer and whether the mesh check
/// thinks it's printable; it slices either way, so the person can see what they'd get.
/// </summary>
public sealed class PrintModel
{
    /// <summary>The welded points, in mm on the bed.</summary>
    public required IReadOnlyList<Vec3> Points { get; init; }
    /// <summary>Triangles as three point indices each, anticlockwise seen from outside.</summary>
    public required IReadOnlyList<(int A, int B, int C)> Triangles { get; init; }
    /// <summary>The bounds on the bed, in mm (empty if there are no triangles).</summary>
    public required Box3 Bounds { get; init; }
    /// <summary>What's wrong with the placement (doesn't fit the bed or the height, nothing to print), as sentences.</summary>
    public required IReadOnlyList<string> Problems { get; init; }
    /// <summary>The mesh check's verdict on the layers printed, and whether it calls them printable.</summary>
    public required string Verdict { get; init; }
    public required bool Printable { get; init; }

    public bool Fits => Problems.Count == 0;
}

/// <summary>Puts the Modeler's foreground layers on a printer's bed.</summary>
public static class Placement
{
    /// <summary>
    /// The foreground layers' faces as a <see cref="PrintModel"/>: converted to the printer's space as STL export does
    /// (<see cref="Stl.ToStl"/>: Y up becomes Z up, metres become millimetres), scaled, dropped onto the bed and placed.
    /// Points and lines are left out, as in STL.
    /// </summary>
    public static PrintModel Place(Document doc, PrinterProfile printer, PlacementOptions? options = null)
    {
        options = (options ?? new PlacementOptions()).Checked();
        printer.Checked();
        var layers = doc.ForegroundLayers.ToList();
        var report = MeshCheck.Layers(doc, layers);
        var tris = Stl.Triangles(layers.Select(i => doc.Layers[i].Mesh));

        double scale = options.ScalePercent / 100;
        var points = new List<Vec3>();
        var index = new Dictionary<Vec3, int>();
        var triangles = new List<(int, int, int)>();
        int Weld(Vec3 p)
        {
            p *= scale;
            if (!index.TryGetValue(p, out int i))
            {
                index[p] = i = points.Count;
                points.Add(p);
            }
            return i;
        }
        foreach (var (_, a, b, c) in tris)
        {
            int ia = Weld(a), ib = Weld(b), ic = Weld(c);
            if (ia != ib && ib != ic && ia != ic) triangles.Add((ia, ib, ic));
        }

        var bounds = Box3.Empty;
        foreach (var t in triangles)
            bounds = bounds.Include(points[t.Item1]).Include(points[t.Item2]).Include(points[t.Item3]);

        var problems = new List<string>();
        if (triangles.Count == 0)
        {
            problems.Add("There's nothing to print: the foreground layers have no faces.");
            return new PrintModel { Points = [], Triangles = [], Bounds = Box3.Empty, Problems = problems, Verdict = report.Verdict, Printable = false };
        }

        var centre = new Vec3(printer.BedWidth / 2, printer.BedDepth / 2, 0);
        var shift = options.Position == BedPosition.Centre
            ? new Vec3(centre.X - bounds.Center.X, centre.Y - bounds.Center.Y, -bounds.Min.Z)
            : new Vec3(centre.X, centre.Y, -bounds.Min.Z);
        for (int i = 0; i < points.Count; i++) points[i] += shift;
        bounds = new Box3(bounds.Min + shift, bounds.Max + shift);

        string Mm(double v) => PrinterProfile.Num(Math.Round(v, 2));
        var size = bounds.Size;
        if (size.X > printer.BedWidth || size.Y > printer.BedDepth)
            problems.Add($"It's {Mm(size.X)} × {Mm(size.Y)} mm across, bigger than the {Mm(printer.BedWidth)} × {Mm(printer.BedDepth)} mm bed: scale it down.");
        else if (bounds.Min.X < -1e-9 || bounds.Min.Y < -1e-9 || bounds.Max.X > printer.BedWidth + 1e-9 || bounds.Max.Y > printer.BedDepth + 1e-9)
            problems.Add("It hangs over the edge of the bed where it was modelled: centre it on the bed, or move it nearer the origin.");
        if (size.Z > printer.MaxHeight)
            problems.Add($"It's {Mm(size.Z)} mm tall, taller than the printer's {Mm(printer.MaxHeight)} mm: scale it down.");

        return new PrintModel
        {
            Points = points,
            Triangles = triangles,
            Bounds = bounds,
            Problems = problems,
            Verdict = report.Verdict,
            Printable = report.Printable,
        };
    }
}
