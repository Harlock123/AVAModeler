using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// Measurements of the selection: the distance between two chosen points (and along each axis), the angle at the
/// middle of three (in the order they were picked), the area of the chosen polygons, and the bounds of what's active.
/// </summary>
public sealed record SelectionMeasure(
    int Points, int Polygons, Box3 Bounds, double? Distance, Vec3? Delta, double? Angle, double Area, double? Perimeter)
{
    /// <summary>Measures the foreground's selection (everything, with nothing selected).</summary>
    public static SelectionMeasure Of(Document doc)
    {
        var sel = doc.Selection;
        var picked = new List<Vec3>();
        var box = Box3.Empty;
        int points = 0, polygons = 0;
        double area = 0, perimeter = 0;
        foreach (int l in doc.ForegroundLayers)
        {
            var mesh = doc.Layers[l].Mesh;
            var active = sel.ActivePoints(l, mesh);
            points += active.Count;
            foreach (int i in active) box = box.Include(mesh.Points[i]);
            if (sel.Mode == SelectionMode.Points && sel.Any)
                picked.AddRange(sel.OrderedPoints(l).Where(i => i < mesh.Points.Count).Select(i => mesh.Points[i]));
            foreach (int pi in sel.ActivePolygons(l, mesh))
            {
                var p = mesh.Polygons[pi];
                if (p.Count < 3) continue;
                polygons++;
                area += mesh.Area(p);
                foreach (var (a, b) in p.Edges()) perimeter += (mesh.Points[a] - mesh.Points[b]).Length;
            }
        }
        double? distance = null, angle = null;
        Vec3? delta = null;
        if (picked.Count == 2)
        {
            delta = picked[1] - picked[0];
            distance = delta.Value.Length;
        }
        if (picked.Count == 3)
        {
            var u = picked[0] - picked[1];
            var v = picked[2] - picked[1];
            if (u.LengthSquared > 0 && v.LengthSquared > 0)
                angle = Math.Acos(Math.Clamp(Vec3.Dot(u, v) / (u.Length * v.Length), -1, 1)) * 180 / Math.PI;
        }
        return new SelectionMeasure(points, polygons, box, distance, delta, angle, area, polygons == 1 ? perimeter : null);
    }

    /// <summary>The measurements as lines of text, lengths in metres (or mm, cm, km where they read better).</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>();
        if (Distance is { } d && Delta is { } v)
            lines.Add($"Distance: {Length(d)} (X {Length(v.X)}, Y {Length(v.Y)}, Z {Length(v.Z)})");
        if (Angle is { } a) lines.Add(FormattableString.Invariant($"Angle at the middle point: {a:0.##}°"));
        if (Polygons > 0) lines.Add($"Area of {Polygons} {(Polygons == 1 ? "polygon" : "polygons")}: {AreaText(Area)}" + (Perimeter is { } p ? $", perimeter {Length(p)}" : ""));
        if (!Bounds.IsEmpty)
            lines.Add($"Size of {Points} {(Points == 1 ? "point" : "points")}: {Length(Bounds.Size.X)} × {Length(Bounds.Size.Y)} × {Length(Bounds.Size.Z)}");
        return lines;
    }

    /// <summary>A length with the unit that reads best: mm under a centimetre, cm under a metre, km past a kilometre.</summary>
    public static string Length(double metres)
    {
        double m = Math.Abs(metres);
        if (m == 0) return "0 m";
        if (m < 0.01) return FormattableString.Invariant($"{metres * 1000:0.###} mm");
        if (m < 1) return FormattableString.Invariant($"{metres * 100:0.###} cm");
        if (m < 1000) return FormattableString.Invariant($"{metres:0.####} m");
        return FormattableString.Invariant($"{metres / 1000:0.###} km");
    }

    private static string AreaText(double m2)
    {
        if (m2 < 1e-4) return FormattableString.Invariant($"{m2 * 1e6:0.###} mm²");
        if (m2 < 1) return FormattableString.Invariant($"{m2 * 1e4:0.###} cm²");
        return FormattableString.Invariant($"{m2:0.####} m²");
    }
}
