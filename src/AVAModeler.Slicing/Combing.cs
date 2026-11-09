using AVAModeler.Core;

namespace AVAModeler.Slicing;

/// <summary>
/// Travel that stays inside the object (combing): from one point to another in the same island of a layer's comb
/// area (the islands shrunk a line in from their walls), the straight line where it stays inside, else along the edge
/// of the area round whatever is in the way (a hole, a notch), the shorter way each time. The nozzle then never
/// crosses a wall or leaves the object, so there's nothing to string and no need to retract. A point just outside the
/// area (on the outer wall, say) is joined to it by a step to the nearest point of its edge.
/// </summary>
public static class Combing
{
    /// <summary>
    /// The points to travel through from <paramref name="a"/> to <paramref name="b"/> (ending at b), or null when they
    /// aren't in the same island, or the way round is much longer than going straight (then travel straight and retract).
    /// </summary>
    public static List<Vec2>? Route(IReadOnlyList<Island> comb, Vec2 a, Vec2 b, double reach)
    {
        if (comb.Count == 0) return null;
        double direct = (b - a).Length;
        foreach (var island in comb)
        {
            var sa = Snap(island, a, reach);
            var sb = Snap(island, b, reach);
            if (sa is not { } pa || sb is not { } pb) continue;
            var loops = new List<IReadOnlyList<Vec2>> { island.Outline.Points };
            loops.AddRange(island.Holes.Select(h => h.Points));
            var path = new List<Vec2>();
            if ((pa - a).LengthSquared > 1e-12) path.Add(pa);
            var middle = Around(loops, pa, pb);
            if (middle == null) return null;
            path.AddRange(middle);
            path.Add(pb);
            if ((pb - b).LengthSquared > 1e-12) path.Add(b);
            double length = Geometry2D.Perimeter([a, .. path], closed: false);
            if (length > Math.Max(3 * direct, direct + 20)) return null;
            return path;
        }
        return null;
    }

    /// <summary>The point if it's inside the island, else the nearest point of its edge within reach, else null.</summary>
    private static Vec2? Snap(Island island, Vec2 p, double reach)
    {
        if (Inside(island, p)) return p;
        Vec2? best = null;
        double bestD = reach * reach;
        foreach (var loop in new[] { island.Outline }.Concat(island.Holes))
        {
            var pts = loop.Points;
            for (int i = 0; i < pts.Count; i++)
            {
                var q = Nearest(pts[i], pts[(i + 1) % pts.Count], p);
                double d = (q - p).LengthSquared;
                if (d <= bestD)
                {
                    bestD = d;
                    best = q;
                }
            }
        }
        return best;
    }

    private static bool Inside(Island island, Vec2 p) =>
        Geometry2D.Contains(island.Outline.Points, p) && !island.Holes.Any(h => Geometry2D.Contains(h.Points, p));

    private static Vec2 Nearest(Vec2 a, Vec2 b, Vec2 p)
    {
        var d = b - a;
        double len = d.LengthSquared;
        if (len < 1e-18) return a;
        double t = Math.Clamp(Vec2.Dot(p - a, d) / len, 0, 1);
        return a + d * t;
    }

    /// <summary>The corners to pass between two points inside an island: none if the straight line crosses no edge.</summary>
    private static List<Vec2>? Around(List<IReadOnlyList<Vec2>> loops, Vec2 a, Vec2 b)
    {
        var crossings = new List<(double T, int Loop, int Edge, Vec2 At)>();
        for (int l = 0; l < loops.Count; l++)
        {
            var pts = loops[l];
            for (int i = 0; i < pts.Count; i++)
                if (Cross(a, b, pts[i], pts[(i + 1) % pts.Count]) is { } hit)
                    crossings.Add((hit.T, l, i, hit.At));
        }
        var via = new List<Vec2>();
        if (crossings.Count == 0) return via;
        if (crossings.Count % 2 != 0) return null;
        crossings.Sort((x, y) => x.T.CompareTo(y.T));
        for (int k = 0; k < crossings.Count; k += 2)
        {
            var (_, l1, e1, p1) = crossings[k];
            var (_, l2, e2, p2) = crossings[k + 1];
            if (l1 != l2) return null;
            var pts = loops[l1];
            int n = pts.Count;
            // Forward: the corners after edge e1 up to the start of edge e2's end; backward: the other way round.
            var forward = new List<Vec2>();
            for (int i = (e1 + 1) % n; ; i = (i + 1) % n)
            {
                forward.Add(pts[i]);
                if (i == e2 || forward.Count > n) break;
            }
            var backward = new List<Vec2>();
            for (int i = e1; ; i = (i - 1 + n) % n)
            {
                backward.Add(pts[i]);
                if (i == (e2 + 1) % n || backward.Count > n) break;
            }
            if (e1 == e2) forward.Clear();
            double lf = Geometry2D.Perimeter([p1, .. forward, p2], false), lb = Geometry2D.Perimeter([p1, .. backward, p2], false);
            via.Add(p1);
            via.AddRange(lf <= lb ? forward : backward);
            via.Add(p2);
        }
        return via;
    }

    /// <summary>Where segment ab crosses segment cd (strictly inside both), as a fraction along ab and the point.</summary>
    private static (double T, Vec2 At)? Cross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        var r = b - a;
        var q = d - c;
        double den = Geometry2D.Cross(r, q);
        if (Math.Abs(den) < 1e-15) return null;
        double t = Geometry2D.Cross(c - a, q) / den, u = Geometry2D.Cross(c - a, r) / den;
        if (t <= 1e-9 || t >= 1 - 1e-9 || u < 0 || u >= 1) return null;
        return (t, a + r * t);
    }
}
