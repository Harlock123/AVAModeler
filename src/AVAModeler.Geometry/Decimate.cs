using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// Polygon reduction by quadric error (Garland and Heckbert): the chosen faces are triangulated, then the edge whose
/// collapse moves the surface least is collapsed, again and again, until few enough triangles are left. Points on an
/// open border, between two surfaces, on a UV seam or on a face that isn't being reduced stay where they are; a
/// collapse that would flip a triangle, or pinch the surface into a non-manifold edge, isn't made. The point an edge
/// collapses to is one of its ends or its middle, whichever moves the surface least, so UVs and point maps carry
/// over (blended at the middle).
/// </summary>
public static class Decimate
{
    /// <summary>What a reduction did.</summary>
    public readonly record struct Result(int TrianglesBefore, int TrianglesAfter, int PointsRemoved);

    /// <summary>
    /// Reduces the chosen faces to about <paramref name="target"/> triangles (at least what the locked points allow).
    /// The faces left are triangles, on their faces' surfaces; other polygons are left alone.
    /// </summary>
    public static Result Reduce(Mesh mesh, IReadOnlyCollection<int> faces, int target)
    {
        var chosen = faces.Where(i => i >= 0 && i < mesh.Polygons.Count && mesh.Polygons[i].Count >= 3).ToHashSet();
        if (chosen.Count == 0) return new Result(0, 0, 0);

        // Triangulate the chosen faces, each triangle keeping its face's surface and corner UVs.
        var tris = new List<Tri>();
        foreach (int pi in chosen.Order())
        {
            var poly = mesh.Polygons[pi];
            var corners = Triangulator.TriangulatePolygon(mesh, poly);
            for (int k = 0; k + 2 < corners.Length; k += 3)
            {
                int a = poly.Points[corners[k]], b = poly.Points[corners[k + 1]], c = poly.Points[corners[k + 2]];
                if (a == b || b == c || a == c) continue;
                var t = new Polygon([a, b, c], poly.Surface);
                mesh.InheritUVs(t, poly, q => q);
                tris.Add(new Tri(t));
            }
        }
        int before = tris.Count;

        // Locked points: on the other polygons, open borders, surface borders and UV seams.
        var locked = new HashSet<int>();
        for (int pi = 0; pi < mesh.Polygons.Count; pi++)
            if (!chosen.Contains(pi)) locked.UnionWith(mesh.Polygons[pi].Points);
        var edgeFaces = new Dictionary<Edge, List<Tri>>();
        foreach (var t in tris)
            foreach (var e in t.Edges())
            {
                if (!edgeFaces.TryGetValue(e, out var l)) edgeFaces[e] = l = [];
                l.Add(t);
            }
        foreach (var (e, l) in edgeFaces)
            if (l.Count != 2 || l[0].Poly.Surface != l[1].Poly.Surface || Seam(mesh, e, l[0].Poly, l[1].Poly))
            {
                locked.Add(e.A);
                locked.Add(e.B);
            }

        // Each point's triangles and quadric.
        var of = new Dictionary<int, HashSet<Tri>>();
        foreach (var t in tris)
            foreach (int p in t.Poly.Points)
            {
                if (!of.TryGetValue(p, out var s)) of[p] = s = [];
                s.Add(t);
            }
        var q = new Dictionary<int, Quadric>();
        foreach (var t in tris)
        {
            var pts = t.Poly.Points;
            var n = Vec3.Cross(mesh.Points[pts[1]] - mesh.Points[pts[0]], mesh.Points[pts[2]] - mesh.Points[pts[0]]);
            double area = n.Length / 2;
            if (area <= 0) continue;
            var plane = Quadric.Of(n / (2 * area), mesh.Points[pts[0]]) * area;
            foreach (int p in pts) q[p] = q.GetValueOrDefault(p) + plane;
        }

        var version = new Dictionary<int, int>();
        var queue = new PriorityQueue<(int U, int V, int Vu, int Vv), double>();
        void Push(int a, int b)
        {
            if (a == b || locked.Contains(a) && locked.Contains(b)) return;
            var (cost, _, _) = Best(mesh, q, locked, a, b);
            queue.Enqueue((a, b, version.GetValueOrDefault(a), version.GetValueOrDefault(b)), cost);
        }
        foreach (var e in edgeFaces.Keys) Push(e.A, e.B);

        int alive = tris.Count, removed = 0;
        while (alive > target && queue.TryDequeue(out var item, out _))
        {
            var (a, b, va, vb) = item;
            if (a == b) continue;
            if (version.GetValueOrDefault(a) != va || version.GetValueOrDefault(b) != vb) continue;
            if (!of.TryGetValue(a, out var ta) || !of.TryGetValue(b, out var tb)) continue;
            var shared = ta.Where(tb.Contains).ToList();
            if (shared.Count == 0) continue;
            var (_, keep, place) = Best(mesh, q, locked, a, b);
            int gone = keep == a ? b : a;
            // The link condition: the two ends share only the points of the triangles on the edge.
            var na = Neighbours(of[a]); var nb = Neighbours(of[b]);
            na.Remove(b); nb.Remove(a);
            int common = na.Count(nb.Contains);
            if (common != shared.Count) continue;
            // No triangle that stays may turn over.
            bool flips = false;
            foreach (var t in of[a].Concat(of[b]).Distinct())
            {
                if (shared.Contains(t)) continue;
                var pts = t.Poly.Points;
                var before3 = Normal(mesh.Points[pts[0]], mesh.Points[pts[1]], mesh.Points[pts[2]]);
                Vec3 P(int i) => pts[i] == a || pts[i] == b ? place : mesh.Points[pts[i]];
                var after = Normal(P(0), P(1), P(2));
                if (after.LengthSquared == 0 || Vec3.Dot(before3, after) < 0.2) { flips = true; break; }
            }
            if (flips) continue;

            // Collapse: the kept point moves to the place (blending UVs and maps if it's the middle).
            double t2 = (place - mesh.Points[keep]).LengthSquared < 1e-24 ? 0 : 0.5;
            if (t2 > 0) Blend(mesh, keep, gone, 0.5);
            mesh.Points[keep] = place;
            foreach (var t in shared) { t.Dead = true; alive--; of[a].Remove(t); of[b].Remove(t); foreach (int p in t.Poly.Points) of.GetValueOrDefault(p)?.Remove(t); }
            foreach (var t in of[gone].ToList())
            {
                mesh.KeepCornerUVs(t.Poly, gone, keep);
                t.Poly.Points = [.. t.Poly.Points.Select(p => p == gone ? keep : p)];
                of[keep].Add(t);
            }
            of.Remove(gone);
            q[keep] = q.GetValueOrDefault(keep) + q.GetValueOrDefault(gone);
            removed++;
            version[keep] = version.GetValueOrDefault(keep) + 1;
            version[gone] = version.GetValueOrDefault(gone) + 1;
            foreach (int n in Neighbours(of[keep])) Push(keep, n);
        }

        // Put the triangles in place of the chosen faces.
        var keepPolys = new List<Polygon>();
        for (int pi = 0; pi < mesh.Polygons.Count; pi++)
            if (!chosen.Contains(pi)) keepPolys.Add(mesh.Polygons[pi]);
        keepPolys.AddRange(tris.Where(t => !t.Dead && t.Poly.Points.Distinct().Count() == 3).Select(t => t.Poly));
        mesh.Polygons = keepPolys;
        mesh.Touch();
        int pointsBefore = mesh.Points.Count;
        mesh.RemoveUnusedPoints();
        return new Result(before, alive, pointsBefore - mesh.Points.Count);
    }

    /// <summary>Where an edge would collapse to, and its cost: an end or the middle, the least moved; a locked end stays.</summary>
    private static (double Cost, int Keep, Vec3 Place) Best(Mesh mesh, Dictionary<int, Quadric> q, HashSet<int> locked, int a, int b)
    {
        var qa = q.GetValueOrDefault(a) + q.GetValueOrDefault(b);
        Vec3 pa = mesh.Points[a], pb = mesh.Points[b];
        if (locked.Contains(a)) return (qa.Error(pa), a, pa);
        if (locked.Contains(b)) return (qa.Error(pb), b, pb);
        var mid = (pa + pb) / 2;
        double ea = qa.Error(pa), eb = qa.Error(pb), em = qa.Error(mid);
        // Ties (flat ground) go to the middle, which keeps the mesh even; then the longer edge first.
        double len = (pa - pb).LengthSquared * 1e-12;
        if (em <= ea + 1e-15 && em <= eb + 1e-15) return (em + len, a, mid);
        return ea <= eb ? (ea + len, a, pa) : (eb + len, b, pb);
    }

    private static void Blend(Mesh mesh, int keep, int gone, double t)
    {
        foreach (var m in mesh.UVMaps)
            if (m.Points.TryGetValue(keep, out var uk) && m.Points.TryGetValue(gone, out var ug)) m.Points[keep] = uk + (ug - uk) * t;
        foreach (var m in mesh.PointMaps)
        {
            bool hk = m.Values.TryGetValue(keep, out var vk), hg = m.Values.TryGetValue(gone, out var vg);
            if (!hk && !hg) continue;
            var v = new float[m.Dimension];
            for (int i = 0; i < v.Length; i++) v[i] = (float)((hk ? vk![i] : m.IsColor ? vg![i] : 0) * (1 - t) + (hg ? vg![i] : m.IsColor ? vk![i] : 0) * t);
            m.Values[keep] = v;
        }
    }

    private static bool Seam(Mesh mesh, Edge e, Polygon x, Polygon y)
    {
        foreach (var m in mesh.UVMaps)
            foreach (int p in new[] { e.A, e.B })
            {
                bool hx = mesh.TryGetUV(m.Name, x, p, out var ux), hy = mesh.TryGetUV(m.Name, y, p, out var uy);
                if (hx != hy || hx && (ux - uy).Length > 1e-9) return true;
            }
        return false;
    }

    private static HashSet<int> Neighbours(IEnumerable<Tri> tris)
    {
        var s = new HashSet<int>();
        foreach (var t in tris) s.UnionWith(t.Poly.Points);
        return s;
    }

    private static Vec3 Normal(Vec3 a, Vec3 b, Vec3 c) => Vec3.Cross(b - a, c - a).Normalized();

    private sealed class Tri(Polygon poly)
    {
        public readonly Polygon Poly = poly;
        public bool Dead;

        public IEnumerable<Edge> Edges()
        {
            var p = Poly.Points;
            yield return Edge.Of(p[0], p[1]);
            yield return Edge.Of(p[1], p[2]);
            yield return Edge.Of(p[2], p[0]);
        }
    }

    /// <summary>A plane's squared distance as a symmetric 4 × 4 matrix (ten numbers), summed over planes.</summary>
    private readonly record struct Quadric(double A2, double AB, double AC, double AD, double B2, double BC, double BD, double C2, double CD, double D2)
    {
        public static Quadric Of(Vec3 n, Vec3 p)
        {
            double d = -Vec3.Dot(n, p);
            return new(n.X * n.X, n.X * n.Y, n.X * n.Z, n.X * d, n.Y * n.Y, n.Y * n.Z, n.Y * d, n.Z * n.Z, n.Z * d, d * d);
        }

        public double Error(Vec3 v) =>
            A2 * v.X * v.X + 2 * AB * v.X * v.Y + 2 * AC * v.X * v.Z + 2 * AD * v.X
            + B2 * v.Y * v.Y + 2 * BC * v.Y * v.Z + 2 * BD * v.Y
            + C2 * v.Z * v.Z + 2 * CD * v.Z + D2;

        public static Quadric operator +(Quadric x, Quadric y) =>
            new(x.A2 + y.A2, x.AB + y.AB, x.AC + y.AC, x.AD + y.AD, x.B2 + y.B2, x.BC + y.BC, x.BD + y.BD, x.C2 + y.C2, x.CD + y.CD, x.D2 + y.D2);

        public static Quadric operator *(Quadric x, double s) =>
            new(x.A2 * s, x.AB * s, x.AC * s, x.AD * s, x.B2 * s, x.BC * s, x.BD * s, x.C2 * s, x.CD * s, x.D2 * s);
    }
}
