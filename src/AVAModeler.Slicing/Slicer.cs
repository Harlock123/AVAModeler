using AVAModeler.Core;

namespace AVAModeler.Slicing;

/// <summary>A closed outline in a layer, in mm on the bed: anticlockwise seen from above for an outside, clockwise for a hole.</summary>
public sealed class Loop(IReadOnlyList<Vec2> points)
{
    public IReadOnlyList<Vec2> Points { get; } = points;

    /// <summary>The signed area (mm²): positive anticlockwise.</summary>
    public double Area => Geometry2D.SignedArea(Points);
    public bool IsHole => Area < 0;
    public double Length => Geometry2D.Perimeter(Points, closed: true);
}

/// <summary>One piece of solid in a layer: an outside outline and the holes in it.</summary>
public sealed record Island(Loop Outline, IReadOnlyList<Loop> Holes)
{
    /// <summary>The area of solid (mm²): the outline's less its holes'.</summary>
    public double Area => Outline.Area + Holes.Sum(h => h.Area);
}

/// <summary>A layer of the sliced object: its height band and the solid where the slicing plane cuts it.</summary>
public sealed record SliceLayer
{
    /// <summary>From 0, the first layer on the bed.</summary>
    public required int Index { get; init; }
    /// <summary>The bottom and top of the layer, in mm above the bed (the nozzle prints at <see cref="Top"/>).</summary>
    public required double Bottom { get; init; }
    public required double Top { get; init; }
    public double Height => Top - Bottom;
    /// <summary>Where the object was cut: halfway up the layer.</summary>
    public double CutAt => (Bottom + Top) / 2;
    public required IReadOnlyList<Island> Islands { get; init; }
    /// <summary>Cut edges that didn't join into a closed loop (from holes in the mesh), which are left out.</summary>
    public int OpenChains { get; init; }
}

/// <summary>The settings that decide how a print is sliced (spec of each in its own summary). Lengths in mm.</summary>
public sealed record SliceSettings
{
    /// <summary>The height of each layer after the first.</summary>
    public double LayerHeight { get; init; } = 0.2;
    /// <summary>The first layer's height: a little thicker sticks better to the bed.</summary>
    public double FirstLayerHeight { get; init; } = 0.24;

    /// <summary>The settings, or an <see cref="ArgumentException"/> saying what's wrong (some limits depend on the nozzle).</summary>
    public SliceSettings Checked(PrinterProfile printer)
    {
        double most = Math.Round(printer.NozzleDiameter * 0.8, 3);
        PrinterProfile.Range(LayerHeight, 0.04, most, "The layer height");
        PrinterProfile.Range(FirstLayerHeight, 0.04, most, "The first layer's height");
        return this;
    }
}

/// <summary>The object cut into layers.</summary>
public sealed record SlicedModel(PrintModel Model, SliceSettings Settings, IReadOnlyList<SliceLayer> Layers)
{
    /// <summary>Open chains in all the layers: more than none means the mesh has holes, and those parts are missing.</summary>
    public int OpenChains => Layers.Sum(l => l.OpenChains);
}

/// <summary>
/// Cuts a <see cref="PrintModel"/> into layers. Each layer is cut halfway up its height by a horizontal plane. A point
/// counts as above the plane when its Z is at or over it, so a point never lies on the plane and every edge is cut once
/// or not at all; the cut point on each edge is worked out once and shared by both triangles beside it, so the
/// segments join exactly, edge to edge, with no tolerance. Each triangle (anticlockwise from outside) gives a segment
/// from the edge it crosses going down to the edge it crosses going up, which makes outsides anticlockwise and holes
/// clockwise seen from above.
/// </summary>
public static class Slicer
{
    public static SlicedModel Slice(PrintModel model, SliceSettings settings, PrinterProfile printer)
    {
        settings.Checked(printer);
        var layers = new List<SliceLayer>();
        if (model.Triangles.Count == 0) return new SlicedModel(model, settings, layers);

        // Triangles by their lowest point, swept upward with the active set.
        var order = Enumerable.Range(0, model.Triangles.Count).OrderBy(t => MinZ(model, t)).ToArray();
        var active = new List<int>();
        int next = 0;
        double top = model.Bounds.Max.Z;
        double bottom = 0;
        for (int index = 0; bottom < top - 1e-9; index++)
        {
            double height = index == 0 ? settings.FirstLayerHeight : settings.LayerHeight;
            double layerTop = bottom + height;
            double cut = Math.Min((bottom + layerTop) / 2, top - 1e-6);
            while (next < order.Length && MinZ(model, order[next]) <= cut) active.Add(order[next++]);
            active.RemoveAll(t => MaxZ(model, t) < cut);
            var (loops, open) = Cut(model, active, cut);
            layers.Add(new SliceLayer { Index = index, Bottom = bottom, Top = layerTop, Islands = Islands(loops), OpenChains = open });
            bottom = layerTop;
        }
        return new SlicedModel(model, settings, layers);
    }

    private static double MinZ(PrintModel m, int t)
    {
        var (a, b, c) = m.Triangles[t];
        return Math.Min(m.Points[a].Z, Math.Min(m.Points[b].Z, m.Points[c].Z));
    }

    private static double MaxZ(PrintModel m, int t)
    {
        var (a, b, c) = m.Triangles[t];
        return Math.Max(m.Points[a].Z, Math.Max(m.Points[b].Z, m.Points[c].Z));
    }

    /// <summary>The closed loops where the plane at <paramref name="z"/> cuts these triangles, and how many chains stayed open.</summary>
    internal static (List<Loop> Loops, int Open) Cut(PrintModel model, IEnumerable<int> triangles, double z)
    {
        var pts = model.Points;
        var cutPoint = new Dictionary<long, Vec2>();
        // Each segment by the edge it starts on (going down) to the edge it ends on (going up).
        var segments = new Dictionary<long, long>();
        int clashes = 0;
        long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        Vec2 Point(int a, int b)
        {
            long k = Key(a, b);
            if (!cutPoint.TryGetValue(k, out var p))
            {
                // Always from the lower-numbered point, so both triangles get the same bits.
                var (p0, p1) = a < b ? (pts[a], pts[b]) : (pts[b], pts[a]);
                double t = (z - p0.Z) / (p1.Z - p0.Z);
                cutPoint[k] = p = new Vec2(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);
            }
            return p;
        }
        foreach (int t in triangles)
        {
            var (a, b, c) = model.Triangles[t];
            int[] v = [a, b, c];
            long down = -1, up = -1;
            for (int e = 0; e < 3; e++)
            {
                int from = v[e], to = v[(e + 1) % 3];
                bool fromAbove = pts[from].Z >= z, toAbove = pts[to].Z >= z;
                if (fromAbove == toAbove) continue;
                Point(from, to);
                if (fromAbove) down = Key(from, to); else up = Key(from, to);
            }
            if (down < 0 || up < 0) continue;
            if (!segments.TryAdd(down, up)) clashes++;
        }

        var loops = new List<Loop>();
        int open = clashes;
        var used = new HashSet<long>();
        // Chains that start where no segment ends are open: walk those first so what's left is closed loops.
        var ends = new HashSet<long>(segments.Values);
        foreach (var start in segments.Keys.Where(k => !ends.Contains(k)).ToList())
        {
            for (long e = start; segments.TryGetValue(e, out long n) && used.Add(e); e = n) { }
            open++;
        }
        foreach (var start in segments.Keys)
        {
            if (used.Contains(start)) continue;
            var ring = new List<Vec2>();
            long e = start;
            bool closed = false;
            while (used.Add(e))
            {
                ring.Add(cutPoint[e]);
                if (!segments.TryGetValue(e, out long n)) break;
                if (n == start) { closed = true; break; }
                e = n;
            }
            if (!closed) { open++; continue; }
            var clean = Geometry2D.Clean(ring);
            if (clean.Count >= 3 && Math.Abs(Geometry2D.SignedArea(clean)) > 1e-6) loops.Add(new Loop(clean));
        }
        return (loops, open);
    }

    /// <summary>Outsides (anticlockwise) with the holes (clockwise) that fall inside them, each hole in the smallest outside round it.</summary>
    internal static List<Island> Islands(List<Loop> loops)
    {
        var outsides = loops.Where(l => !l.IsHole).OrderBy(l => l.Area).ToList();
        var holes = outsides.ToDictionary(o => o, _ => new List<Loop>());
        foreach (var hole in loops.Where(l => l.IsHole))
        {
            var owner = outsides.FirstOrDefault(o => Geometry2D.Contains(o.Points, hole.Points[0]));
            if (owner != null) holes[owner].Add(hole);
        }
        return outsides.OrderByDescending(o => o.Area).Select(o => new Island(o, holes[o])).ToList();
    }
}

/// <summary>Plain 2D polygon sums the slicer needs (mm).</summary>
public static class Geometry2D
{
    /// <summary>The shoelace area: positive for anticlockwise.</summary>
    public static double SignedArea(IReadOnlyList<Vec2> p)
    {
        double s = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) s += p[j].X * p[i].Y - p[i].X * p[j].Y;
        return s / 2;
    }

    public static double Perimeter(IReadOnlyList<Vec2> p, bool closed)
    {
        double s = 0;
        for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length;
        if (closed && p.Count > 1) s += (p[0] - p[^1]).Length;
        return s;
    }

    /// <summary>Whether a point is inside a polygon (crossings, so either winding).</summary>
    public static bool Contains(IReadOnlyList<Vec2> poly, Vec2 q)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vec2 a = poly[i], b = poly[j];
            if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// A ring without points closer than a micron to the one before, and without points in the middle of a straight
    /// run (within a micron of the line through their neighbours).
    /// </summary>
    public static List<Vec2> Clean(IReadOnlyList<Vec2> ring, double tolerance = 1e-3)
    {
        var r = new List<Vec2>(ring.Count);
        foreach (var p in ring)
            if (r.Count == 0 || (p - r[^1]).Length > tolerance) r.Add(p);
        while (r.Count > 1 && (r[0] - r[^1]).Length <= tolerance) r.RemoveAt(r.Count - 1);
        bool changed = true;
        while (changed && r.Count > 3)
        {
            changed = false;
            for (int i = 0; i < r.Count && r.Count > 3; i++)
            {
                Vec2 prev = r[(i + r.Count - 1) % r.Count], cur = r[i], next = r[(i + 1) % r.Count];
                var d = next - prev;
                double len = d.Length;
                double off = len < 1e-12 ? (cur - prev).Length : Math.Abs(Cross(d, cur - prev)) / len;
                bool between = Vec2.Dot(cur - prev, d) >= 0 && Vec2.Dot(next - cur, d) >= 0;
                if (off <= tolerance && between)
                {
                    r.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }
        return r;
    }

    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
}
