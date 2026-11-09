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

/// <summary>Which way up the object goes on the bed.</summary>
public enum LayFlat
{
    /// <summary>As modelled: the Modeler's up is the bed's up.</summary>
    AsModelled,
    /// <summary>Turned so its largest flat face on the outside lies on the bed.</summary>
    LargestFace,
    /// <summary>Turned so the selected polygon (the first, in the foreground layers) lies on the bed.</summary>
    SelectedFace,
}

/// <summary>How the object is put on the bed: which way up, turned how far, scaled how much, where, and how many.</summary>
public sealed record PlacementOptions
{
    public BedPosition Position { get; init; } = BedPosition.Centre;
    /// <summary>Scale, as a percentage: 100 prints the object at its modelled size (1 m = 1000 mm).</summary>
    public double ScalePercent { get; init; } = 100;
    public LayFlat LayFlat { get; init; } = LayFlat.AsModelled;
    /// <summary>Degrees the object is turned about the bed's up axis, anticlockwise seen from above, after laying it flat.</summary>
    public double RotateDegrees { get; init; }
    /// <summary>How many copies to print, laid out in rows (centred on the bed whatever <see cref="Position"/> says).</summary>
    public int Copies { get; init; } = 1;
    /// <summary>The gap between copies (mm).</summary>
    public double CopyGap { get; init; } = 5;

    public PlacementOptions Checked()
    {
        PrinterProfile.Range(ScalePercent, 0.1, 100000, "The scale", "%");
        PrinterProfile.Range(RotateDegrees, -360, 360, "The turn", "°");
        PrinterProfile.Range(Copies, 1, 100, "The copies", "");
        PrinterProfile.Range(CopyGap, 0, 100, "The gap between copies");
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

        var problems = new List<string>();
        if (triangles.Count == 0)
        {
            problems.Add("There's nothing to print: the foreground layers have no faces.");
            return new PrintModel { Points = [], Triangles = [], Bounds = Box3.Empty, Problems = problems, Verdict = report.Verdict, Printable = false };
        }

        // Which way up: the chosen face's outward normal turned to point down, about the object's middle (so the
        // origin keeps its place for As modelled when nothing is turned).
        Vec3? down = options.LayFlat switch
        {
            LayFlat.LargestFace => LargestFlatFace(points, triangles),
            LayFlat.SelectedFace => SelectedFaceNormal(doc, layers) ?? throw new ArgumentException("Select a polygon in the foreground layers to lay it on the bed (Polygons mode), or choose another way up."),
            _ => null,
        };
        var turn = Mat3.Identity;
        if (down is { } n) turn = Mat3.Turning(n.Normalized(), new Vec3(0, 0, -1));
        if (options.RotateDegrees != 0) turn = Mat3.AboutZ(options.RotateDegrees * Math.PI / 180) * turn;
        if (!turn.IsIdentity)
            for (int i = 0; i < points.Count; i++) points[i] = turn * points[i];

        var bounds = Bounds(points, triangles);
        var centre = new Vec3(printer.BedWidth / 2, printer.BedDepth / 2, 0);
        var shift = options.Position == BedPosition.Centre || options.Copies > 1
            ? new Vec3(centre.X - bounds.Center.X, centre.Y - bounds.Center.Y, -bounds.Min.Z)
            : new Vec3(centre.X, centre.Y, -bounds.Min.Z);
        for (int i = 0; i < points.Count; i++) points[i] += shift;
        bounds = new Box3(bounds.Min + shift, bounds.Max + shift);

        if (options.Copies > 1)
        {
            // Rows of copies, as square as can be, centred on the bed: each one the first moved by whole steps.
            int columns = (int)Math.Ceiling(Math.Sqrt(options.Copies)), rows = (int)Math.Ceiling(options.Copies / (double)columns);
            double stepX = bounds.Size.X + options.CopyGap, stepY = bounds.Size.Y + options.CopyGap;
            int count = points.Count, triCount = triangles.Count;
            var first = points.ToList();
            var firstTris = triangles.ToList();
            points.Clear();
            triangles.Clear();
            for (int c = 0; c < options.Copies; c++)
            {
                int col = c % columns, row = c / columns;
                // The last row centred too when it's short.
                int inRow = row == rows - 1 ? options.Copies - row * columns : columns;
                var offset = new Vec3((col - (inRow - 1) / 2.0) * stepX, ((rows - 1) / 2.0 - row) * stepY, 0);
                int baseIndex = points.Count;
                points.AddRange(first.Select(p => p + offset));
                triangles.AddRange(firstTris.Select(t => (t.Item1 + baseIndex, t.Item2 + baseIndex, t.Item3 + baseIndex)));
            }
            bounds = Bounds(points, triangles);
        }

        string Mm(double v) => PrinterProfile.Num(Math.Round(v, 2));
        var size = bounds.Size;
        if (printer.RoundBed)
        {
            double r = printer.BedWidth / 2, far = 0;
            foreach (var p in points) far = Math.Max(far, Math.Sqrt((p.X - r) * (p.X - r) + (p.Y - r) * (p.Y - r)));
            if (far > r + 1e-9)
                problems.Add($"It reaches {Mm(far)} mm from the middle of the round bed, past its {Mm(r)} mm edge: scale it down, or print fewer copies.");
        }
        else if (size.X > printer.BedWidth || size.Y > printer.BedDepth)
            problems.Add(options.Copies > 1
                ? $"The {options.Copies} copies take {Mm(size.X)} × {Mm(size.Y)} mm, more than the {Mm(printer.BedWidth)} × {Mm(printer.BedDepth)} mm bed: print fewer, or scale them down."
                : $"It's {Mm(size.X)} × {Mm(size.Y)} mm across, bigger than the {Mm(printer.BedWidth)} × {Mm(printer.BedDepth)} mm bed: scale it down, or turn it.");
        else if (bounds.Min.X < -1e-9 || bounds.Min.Y < -1e-9 || bounds.Max.X > printer.BedWidth + 1e-9 || bounds.Max.Y > printer.BedDepth + 1e-9)
            problems.Add("It hangs over the edge of the bed where it was modelled: centre it on the bed, or move it nearer the origin.");
        if (size.Z > printer.MaxHeight)
            problems.Add($"It's {Mm(size.Z)} mm tall, taller than the printer's {Mm(printer.MaxHeight)} mm: scale it down.");

        if (options.Copies > 1 && (bounds.Size.X > printer.BedWidth || bounds.Size.Y > printer.BedDepth))
            problems.RemoveAll(p => p.Contains("hangs over"));
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

    private static Box3 Bounds(List<Vec3> points, List<(int, int, int)> triangles)
    {
        var b = Box3.Empty;
        foreach (var t in triangles) b = b.Include(points[t.Item1]).Include(points[t.Item2]).Include(points[t.Item3]);
        return b;
    }

    /// <summary>
    /// The outward normal of the largest flat face the object could stand on: triangles grouped by direction (within
    /// about half a degree) and by plane, and only planes the whole object lies behind (so it can rest on them).
    /// </summary>
    public static Vec3? LargestFlatFace(IReadOnlyList<Vec3> points, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        var groups = new List<(Vec3 Normal, double Offset, double Area)>();
        double size = 0;
        foreach (var p in points) size = Math.Max(size, p.Length);
        double tol = Math.Max(1e-6, size * 1e-6);
        foreach (var (a, b, c) in triangles)
        {
            var cross = Vec3.Cross(points[b] - points[a], points[c] - points[a]);
            double area = cross.Length / 2;
            if (area < 1e-12) continue;
            var n = cross.Normalized();
            double d = Vec3.Dot(n, points[a]);
            int found = groups.FindIndex(g => Vec3.Dot(g.Normal, n) > 0.99996 && Math.Abs(g.Offset - d) < 10 * tol);
            if (found >= 0) groups[found] = (groups[found].Normal, groups[found].Offset, groups[found].Area + area);
            else groups.Add((n, d, area));
        }
        foreach (var g in groups.OrderByDescending(g => g.Area))
            if (points.All(p => Vec3.Dot(g.Normal, p) <= g.Offset + 10 * tol)) return g.Normal;
        return null;
    }

    /// <summary>The first selected polygon's outward normal in the printer's space, if a face in the foreground is selected.</summary>
    private static Vec3? SelectedFaceNormal(Document doc, List<int> layers)
    {
        foreach (int l in layers)
        {
            var mesh = doc.Layers[l].Mesh;
            foreach (int i in doc.Selection.PolygonsIn(l).Order())
                if (i < mesh.Polygons.Count && mesh.Polygons[i].Count >= 3)
                {
                    var n = mesh.Normal(i);
                    // The same swap as the points (Y up becomes Z up); a direction keeps its sense through it.
                    if (n.LengthSquared > 0) return new Vec3(n.X, n.Z, n.Y).Normalized();
                }
        }
        return null;
    }
}

/// <summary>A 3 × 3 rotation, for turning the object on the bed.</summary>
public readonly record struct Mat3(double M00, double M01, double M02, double M10, double M11, double M12, double M20, double M21, double M22)
{
    public static readonly Mat3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public bool IsIdentity => this == Identity;

    public static Vec3 operator *(Mat3 m, Vec3 v) => new(
        m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
        m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
        m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

    public static Mat3 operator *(Mat3 a, Mat3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    /// <summary>Anticlockwise about +Z, seen from above.</summary>
    public static Mat3 AboutZ(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    /// <summary>The rotation taking unit vector <paramref name="from"/> to unit vector <paramref name="to"/> (Rodrigues), the shortest way.</summary>
    public static Mat3 Turning(Vec3 from, Vec3 to)
    {
        var axis = Vec3.Cross(from, to);
        double sin = axis.Length, cos = Vec3.Dot(from, to);
        if (sin < 1e-12)
        {
            if (cos > 0) return Identity;
            // Opposite: half a turn about any axis square to them (X, unless they lie along it).
            var any = Math.Abs(from.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            axis = Vec3.Cross(from, any).Normalized();
            return About(axis, -1, 0);
        }
        return About(axis / sin, cos, sin);
    }

    private static Mat3 About(Vec3 k, double cos, double sin)
    {
        double t = 1 - cos;
        return new(
            cos + k.X * k.X * t, k.X * k.Y * t - k.Z * sin, k.X * k.Z * t + k.Y * sin,
            k.Y * k.X * t + k.Z * sin, cos + k.Y * k.Y * t, k.Y * k.Z * t - k.X * sin,
            k.Z * k.X * t - k.Y * sin, k.Z * k.Y * t + k.X * sin, cos + k.Z * k.Z * t);
    }
}
