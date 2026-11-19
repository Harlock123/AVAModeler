using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>How <see cref="QuadRemesh.Apply"/> remeshes.</summary>
public sealed record QuadRemeshOptions
{
    /// <summary>The new quads' edge length, metres.</summary>
    public double EdgeLength { get; init; } = 0.1;
    /// <summary>Edges whose faces meet more sharply than this (degrees) are kept as sharp edges the quads run along.</summary>
    public double SharpAngle { get; init; } = 40;
    /// <summary>Rounds of the triangle remesh (split, collapse, flip, relax).</summary>
    public int Iterations { get; init; } = 5;
    /// <summary>Passes smoothing the direction field.</summary>
    public int FieldPasses { get; init; } = 30;
    /// <summary>Passes evening out the final quads.</summary>
    public int Smoothing { get; init; } = 2;
}

/// <summary>What a quad remesh made.</summary>
public sealed record QuadRemeshResult(int Quads, int Points, double Aligned, int PairedShare);

/// <summary>
/// Quad remeshing that follows the shape. The faces are triangulated and remeshed to even triangles twice the target
/// length (Botsch and Kobbelt's isotropic remesh: split long edges, collapse short ones, flip toward valence six,
/// relax along the surface, pull back onto the original), with sharp edges, open edges and the selection's border
/// kept. A cross field (four directions at right angles at each point) is laid along the sharp edges and the
/// directions of greatest curvature and smoothed; triangles are paired into quads whose sides follow it best; then
/// one split (every face into quads round its centre) makes it all quads at the target length, pulled back onto the
/// surface and evened out. Closed stays closed; faces keep their surfaces and winding. UVs aren't kept.
/// </summary>
public static class QuadRemesh
{
    private sealed class Tri
    {
        public readonly List<Vec3> P = [];
        public readonly List<int[]?> T = [];
        public readonly List<int> Surface = [];
        public readonly List<HashSet<int>> VT = [];
        /// <summary>Edges kept as creases: sharp, open, or on the selection's border.</summary>
        public readonly HashSet<Edge> Feature = [];
        /// <summary>Edges never split or collapsed (shared with faces left out).</summary>
        public readonly HashSet<Edge> Border = [];
        /// <summary>Points that never move (on the border, or where three or more creases meet).</summary>
        public readonly HashSet<int> Fixed = [];

        public int AddPoint(Vec3 p) { P.Add(p); VT.Add([]); return P.Count - 1; }

        public int AddTri(int a, int b, int c, int surface)
        {
            T.Add([a, b, c]);
            Surface.Add(surface);
            int t = T.Count - 1;
            VT[a].Add(t); VT[b].Add(t); VT[c].Add(t);
            return t;
        }

        public void Remove(int t)
        {
            foreach (int v in T[t]!) VT[v].Remove(t);
            T[t] = null;
        }

        public List<int> EdgeTris(int a, int b) => VT[a].Where(t => VT[b].Contains(t)).ToList();

        public IEnumerable<int> Neighbours(int v)
        {
            var seen = new HashSet<int>();
            foreach (int t in VT[v])
                foreach (int w in T[t]!)
                    if (w != v && seen.Add(w)) yield return w;
        }

        public Vec3 Normal(int t)
        {
            var x = T[t]!;
            return Vec3.Cross(P[x[1]] - P[x[0]], P[x[2]] - P[x[0]]);
        }

        public Vec3 PointNormal(int v)
        {
            var n = Vec3.Zero;
            foreach (int t in VT[v]) n += Normal(t);
            return n.Normalized();
        }

        public bool IsFeatureVertex(int v) => FeatureEdgesAt(v) > 0;

        public int FeatureEdgesAt(int v) => Neighbours(v).Count(w => Feature.Contains(Edge.Of(v, w)));

        public IEnumerable<Edge> Edges()
        {
            var seen = new HashSet<Edge>();
            foreach (var t in T)
            {
                if (t == null) continue;
                for (int i = 0; i < 3; i++)
                {
                    var e = Edge.Of(t[i], t[(i + 1) % 3]);
                    if (seen.Add(e)) yield return e;
                }
            }
        }
    }

    /// <summary>Replaces the faces given (or all) with an even skin of quads; returns what it made.</summary>
    public static QuadRemeshResult Apply(Mesh mesh, IReadOnlyCollection<int>? faces, QuadRemeshOptions? options = null)
    {
        options ??= new QuadRemeshOptions();
        if (!(options.EdgeLength > 0)) throw new ArgumentException("The edge length must be more than 0 metres.");
        var chosen = (faces ?? Enumerable.Range(0, mesh.Polygons.Count).ToList()).Where(f => f >= 0 && f < mesh.Polygons.Count && mesh.Polygons[f].Count >= 3).ToHashSet();
        if (chosen.Count == 0) throw new InvalidOperationException("Quad Remesh needs faces (polygons of three or more points).");

        // The original surface, to pull points back onto.
        var original = new Mesh { Points = mesh.Points };
        original.Polygons.AddRange(chosen.Select(f => mesh.Polygons[f]));
        var bvh = new TriangleBvh(original);
        Vec3 Project(Vec3 p) => bvh.Nearest(p) is { } hit ? hit.Point : p;

        var tri = Build(mesh, chosen, options.SharpAngle);
        double target = 2 * options.EdgeLength;
        var box = Box3.Of(tri.P);
        double size = Vec3.Distance(box.Max, box.Min);
        if (target > size) target = size / 2;
        for (int it = 0; it < Math.Max(1, options.Iterations); it++)
        {
            Split(tri, target * 4 / 3);
            Collapse(tri, target * 4 / 5, target * 4 / 3);
            Flip(tri);
            Relax(tri, Project, tri.Fixed.Union(Enumerable.Range(0, tri.P.Count).Where(tri.IsFeatureVertex)).ToHashSet());
        }

        var field = Field(tri, options.FieldPasses);
        // Points onto a grid along the field (the remesh's even triangles have twice the grid's density along
        // edges, so the grid is a little wider), then flips toward Delaunay: each grid cell becomes two triangles
        // split by its diagonal, which the pairing finds.
        var still = tri.Fixed.Union(Enumerable.Range(0, tri.P.Count).Where(tri.IsFeatureVertex)).ToHashSet();
        for (int round = 0; round < 3; round++)
        {
            Snap(tri, field, target * 1.07, Project, still, 12);
            DelaunayFlips(tri);
        }
        var (quads, triangles) = Pair(tri, field);
        return Write(mesh, chosen, tri, quads, triangles, Project, options.Smoothing, field);
    }

    private static Tri Build(Mesh mesh, HashSet<int> chosen, double sharpAngle)
    {
        var tri = new Tri();
        var index = new Dictionary<int, int>();
        int Point(int p)
        {
            if (!index.TryGetValue(p, out int i)) index[p] = i = tri.AddPoint(mesh.Points[p]);
            return i;
        }
        var all = Triangulator.TriangulateMesh(mesh);
        foreach (var t in all)
        {
            if (!chosen.Contains(t.Polygon)) continue;
            int a = Point(t.A), b = Point(t.B), c = Point(t.C);
            if (a == b || b == c || a == c) continue;
            tri.AddTri(a, b, c, mesh.Polygons[t.Polygon].Surface);
        }
        // Creases: open edges, the border with faces left out, edges between surfaces, and sharp edges.
        var edgeMap = mesh.EdgeMap();
        var reverse = index.ToDictionary(kv => kv.Value, kv => kv.Key);
        double cosLimit = Math.Cos(sharpAngle * Math.PI / 180);
        foreach (var e in tri.Edges().ToList())
        {
            var ts = tri.EdgeTris(e.A, e.B);
            var meshEdge = Edge.Of(reverse[e.A], reverse[e.B]);
            bool border = edgeMap.TryGetValue(meshEdge, out var users) && users.Any(f => !chosen.Contains(f) && mesh.Polygons[f].Count >= 3);
            if (border) { tri.Border.Add(e); tri.Feature.Add(e); tri.Fixed.Add(e.A); tri.Fixed.Add(e.B); continue; }
            if (ts.Count != 2) { tri.Feature.Add(e); continue; }
            if (tri.Surface[ts[0]] != tri.Surface[ts[1]] || Vec3.Dot(tri.Normal(ts[0]).Normalized(), tri.Normal(ts[1]).Normalized()) < cosLimit) tri.Feature.Add(e);
        }
        // The ends of open creases and points where three or more meet never move.
        for (int v = 0; v < tri.P.Count; v++)
            if (tri.FeatureEdgesAt(v) is var k && (k > 2 || k == 1)) tri.Fixed.Add(v);
        return tri;
    }

    private static void Split(Tri tri, double longer)
    {
        var edges = tri.Edges().Where(e => !tri.Border.Contains(e)).Select(e => (e, L: Vec3.Distance(tri.P[e.A], tri.P[e.B]))).Where(x => x.L > longer).OrderByDescending(x => x.L).ToList();
        foreach (var (e, _) in edges)
        {
            var ts = tri.EdgeTris(e.A, e.B);
            if (ts.Count == 0) continue;
            int m = tri.AddPoint((tri.P[e.A] + tri.P[e.B]) / 2);
            bool feature = tri.Feature.Remove(e);
            if (feature) { tri.Feature.Add(Edge.Of(e.A, m)); tri.Feature.Add(Edge.Of(m, e.B)); }
            foreach (int t in ts)
            {
                var x = tri.T[t]!;
                int s = tri.Surface[t];
                // Keep the winding: the edge runs a→b in this triangle, with c after.
                int i = Array.FindIndex(x, v => v == e.A || v == e.B);
                int j = (i + 1) % 3;
                if (x[j] != e.A && x[j] != e.B) { i = (i + 2) % 3; j = (i + 1) % 3; }
                int a = x[i], b = x[j], c = x[(j + 1) % 3];
                tri.Remove(t);
                tri.AddTri(a, m, c, s);
                tri.AddTri(m, b, c, s);
            }
        }
    }

    private static void Collapse(Tri tri, double shorter, double longer)
    {
        var edges = tri.Edges().Where(e => !tri.Border.Contains(e)).Select(e => (e, L: Vec3.Distance(tri.P[e.A], tri.P[e.B]))).Where(x => x.L < shorter).OrderBy(x => x.L).ToList();
        foreach (var (e0, _) in edges)
        {
            int a = e0.A, b = e0.B;
            if (tri.VT[a].Count == 0 || tri.VT[b].Count == 0) continue;
            var ts = tri.EdgeTris(a, b);
            if (ts.Count == 0) continue;
            var e = Edge.Of(a, b);
            bool fa = tri.Fixed.Contains(a), fb = tri.Fixed.Contains(b);
            bool ca = tri.IsFeatureVertex(a), cb = tri.IsFeatureVertex(b);
            bool onFeature = tri.Feature.Contains(e);
            // Where b goes: into a (or a into b), keeping fixed points and creases where they are.
            if (fa && fb) continue;
            if (!onFeature && ca && cb) continue;
            if (fb || (cb && !ca)) (a, b) = (b, a);
            else if (onFeature && tri.Fixed.Contains(b)) (a, b) = (b, a);
            if (tri.Fixed.Contains(b)) continue;
            var to = tri.P[a];
            // The link condition: a and b share only the points across their triangles.
            var na = tri.Neighbours(a).ToHashSet();
            var shared = tri.Neighbours(b).Where(na.Contains).Count();
            if (shared != ts.Count) continue;
            // Nothing made too long, no triangle turned over, no point left with fewer than three edges.
            var around = tri.VT[b].Where(t => !ts.Contains(t)).ToList();
            bool ok = true;
            foreach (int w in tri.Neighbours(b))
                if (w != a && Vec3.Distance(to, tri.P[w]) > longer) { ok = false; break; }
            if (!ok) continue;
            foreach (int t in around)
            {
                var x = tri.T[t]!;
                var before = tri.Normal(t);
                var moved = x.Select(v => v == b ? to : tri.P[v]).ToArray();
                var after = Vec3.Cross(moved[1] - moved[0], moved[2] - moved[0]);
                if (after.LengthSquared < 1e-24 || Vec3.Dot(before.Normalized(), after.Normalized()) < 0.3) { ok = false; break; }
            }
            if (!ok) continue;
            foreach (int t in ts)
                foreach (int c in tri.T[t]!)
                    if (c != a && c != b && tri.Neighbours(c).Count() <= 3) ok = false;
            if (!ok) continue;
            // A crease's edges at b move to a.
            foreach (int w in tri.Neighbours(b).ToList())
                if (tri.Feature.Remove(Edge.Of(b, w)) && w != a) tri.Feature.Add(Edge.Of(a, w));
            foreach (int t in ts) tri.Remove(t);
            foreach (int t in around)
            {
                var x = tri.T[t]!;
                for (int i = 0; i < 3; i++) if (x[i] == b) x[i] = a;
                tri.VT[b].Remove(t);
                tri.VT[a].Add(t);
            }
        }
    }

    private static void Flip(Tri tri)
    {
        var boundary = new HashSet<int>();
        foreach (var e in tri.Edges())
            if (tri.EdgeTris(e.A, e.B).Count == 1) { boundary.Add(e.A); boundary.Add(e.B); }
        int Ideal(int v) => boundary.Contains(v) ? 4 : 6;
        int Valence(int v) => tri.Neighbours(v).Count();
        foreach (var e in tri.Edges().ToList())
        {
            if (tri.Feature.Contains(e) || tri.Border.Contains(e)) continue;
            var ts = tri.EdgeTris(e.A, e.B);
            if (ts.Count != 2 || tri.Surface[ts[0]] != tri.Surface[ts[1]]) continue;
            // Order: t1 runs a→b, t2 runs b→a.
            int t1 = ts[0], t2 = ts[1];
            var x1 = tri.T[t1]!;
            int i = Array.IndexOf(x1, e.A);
            int a = e.A, b = e.B;
            if (x1[(i + 1) % 3] != b) (a, b, t1, t2) = (b, a, t2, t1);
            x1 = tri.T[t1]!;
            var x2 = tri.T[t2]!;
            int c = x1.First(v => v != a && v != b), d = x2.First(v => v != a && v != b);
            if (c == d || tri.Neighbours(c).Contains(d)) continue;
            int before = Sq(Valence(a) - Ideal(a)) + Sq(Valence(b) - Ideal(b)) + Sq(Valence(c) - Ideal(c)) + Sq(Valence(d) - Ideal(d));
            int after = Sq(Valence(a) - 1 - Ideal(a)) + Sq(Valence(b) - 1 - Ideal(b)) + Sq(Valence(c) + 1 - Ideal(c)) + Sq(Valence(d) + 1 - Ideal(d));
            if (after >= before) continue;
            var n = (tri.Normal(t1) + tri.Normal(t2)).Normalized();
            var n1 = Vec3.Cross(tri.P[d] - tri.P[a], tri.P[c] - tri.P[a]);
            var n2 = Vec3.Cross(tri.P[b] - tri.P[d], tri.P[c] - tri.P[d]);
            if (Vec3.Dot(n1.Normalized(), n) < 0.5 || Vec3.Dot(n2.Normalized(), n) < 0.5) continue;
            int s = tri.Surface[t1];
            tri.Remove(t1);
            tri.Remove(t2);
            tri.AddTri(a, d, c, s);
            tri.AddTri(d, b, c, s);
        }
    }

    private static int Sq(int x) => x * x;

    /// <summary>
    /// Moves points so their edges lie along the field at a grid's spacing: each neighbour says where the point would
    /// be one grid step (along, across or diagonally) from it, and the point goes halfway toward their average.
    /// </summary>
    private static void Snap(Tri tri, Vec3[] field, double h, Func<Vec3, Vec3> project, HashSet<int> still, int passes)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            var moved = tri.P.ToArray();
            for (int v = 0; v < tri.P.Count; v++)
            {
                if (still.Contains(v) || tri.VT[v].Count == 0) continue;
                var n = tri.PointNormal(v);
                var o = Tangent(field[v], n);
                if (o.LengthSquared == 0) continue;
                var b = Vec3.Cross(n, o);
                var sum = Vec3.Zero;
                int count = 0;
                foreach (int w in tri.Neighbours(v))
                {
                    var e = tri.P[v] - tri.P[w];
                    double u = Vec3.Dot(e, o) / h, t = Vec3.Dot(e, b) / h;
                    int su = Math.Clamp((int)Math.Round(u), -1, 1), st = Math.Clamp((int)Math.Round(t), -1, 1);
                    if (su == 0 && st == 0) { if (Math.Abs(u) >= Math.Abs(t)) su = u >= 0 ? 1 : -1; else st = t >= 0 ? 1 : -1; }
                    sum += tri.P[w] + (o * su + b * st) * h;
                    count++;
                }
                if (count == 0) continue;
                var p = tri.P[v] + (sum / count - tri.P[v]) * 0.5;
                var q = project(p);
                // Never turn a triangle over.
                bool ok = true;
                foreach (int t in tri.VT[v])
                {
                    var x = tri.T[t]!;
                    var before = tri.Normal(t);
                    var pts = x.Select(k => k == v ? q : tri.P[k]).ToArray();
                    var after = Vec3.Cross(pts[1] - pts[0], pts[2] - pts[0]);
                    if (Vec3.Dot(before, after) <= 0 || after.LengthSquared < 1e-24) { ok = false; break; }
                }
                if (ok) moved[v] = q;
            }
            for (int v = 0; v < tri.P.Count; v++) tri.P[v] = moved[v];
        }
    }

    /// <summary>Flips edges whose opposite angles add to more than a half turn (toward a Delaunay triangulation).</summary>
    private static void DelaunayFlips(Tri tri)
    {
        for (int pass = 0; pass < 6; pass++)
        {
            int flips = 0;
            foreach (var e in tri.Edges().ToList())
            {
                if (tri.Feature.Contains(e) || tri.Border.Contains(e)) continue;
                var ts = tri.EdgeTris(e.A, e.B);
                if (ts.Count != 2 || tri.Surface[ts[0]] != tri.Surface[ts[1]]) continue;
                int t1 = ts[0], t2 = ts[1], a = e.A, b = e.B;
                var x1 = tri.T[t1]!;
                if (x1[(Array.IndexOf(x1, a) + 1) % 3] != b) (t1, t2) = (t2, t1);
                x1 = tri.T[t1]!;
                var x2 = tri.T[t2]!;
                int c = x1.First(v => v != a && v != b), d = x2.First(v => v != a && v != b);
                if (c == d || tri.Neighbours(c).Contains(d)) continue;
                double Angle(int at, int p, int q)
                {
                    var u = (tri.P[p] - tri.P[at]).Normalized();
                    var w = (tri.P[q] - tri.P[at]).Normalized();
                    return Math.Acos(Math.Clamp(Vec3.Dot(u, w), -1, 1));
                }
                if (Angle(c, a, b) + Angle(d, a, b) <= Math.PI + 1e-9) continue;
                var n = (tri.Normal(t1) + tri.Normal(t2)).Normalized();
                var n1 = Vec3.Cross(tri.P[d] - tri.P[a], tri.P[c] - tri.P[a]);
                var n2 = Vec3.Cross(tri.P[b] - tri.P[d], tri.P[c] - tri.P[d]);
                if (Vec3.Dot(n1.Normalized(), n) < 0.5 || Vec3.Dot(n2.Normalized(), n) < 0.5) continue;
                // Keep every point with at least three edges.
                if (tri.Neighbours(a).Count() <= 3 || tri.Neighbours(b).Count() <= 3) continue;
                int s = tri.Surface[t1];
                tri.Remove(t1);
                tri.Remove(t2);
                tri.AddTri(a, d, c, s);
                tri.AddTri(d, b, c, s);
                flips++;
            }
            if (flips == 0) break;
        }
    }

    private static void Relax(Tri tri, Func<Vec3, Vec3> project, HashSet<int> still)
    {
        var moved = new Vec3[tri.P.Count];
        for (int v = 0; v < tri.P.Count; v++)
        {
            moved[v] = tri.P[v];
            if (tri.VT[v].Count == 0 || still.Contains(v)) continue;
            var nb = tri.Neighbours(v).ToList();
            if (nb.Count == 0) continue;
            var q = nb.Aggregate(Vec3.Zero, (s, w) => s + tri.P[w]) / nb.Count;
            var n = tri.PointNormal(v);
            var p = tri.P[v];
            moved[v] = project(q + n * Vec3.Dot(n, p - q));
        }
        for (int v = 0; v < tri.P.Count; v++) tri.P[v] = moved[v];
    }

    /// <summary>A unit tangent direction at each point: the cross field, one of its four directions.</summary>
    private static Vec3[] Field(Tri tri, int passes)
    {
        int n = tri.P.Count;
        var normal = new Vec3[n];
        var dir = new Vec3[n];
        var weight = new double[n];
        var fixedDir = new bool[n];
        for (int v = 0; v < n; v++)
        {
            if (tri.VT[v].Count == 0) continue;
            normal[v] = tri.PointNormal(v);
            var nb = tri.Neighbours(v).ToList();
            // Along a crease: its direction, held.
            var crease = nb.FirstOrDefault(w => tri.Feature.Contains(Edge.Of(v, w)), -1);
            if (crease >= 0)
            {
                dir[v] = Tangent(tri.P[crease] - tri.P[v], normal[v]);
                fixedDir[v] = dir[v].LengthSquared > 0;
                if (fixedDir[v]) continue;
            }
            // The direction of greatest curvature, by its normal curvatures along the edges.
            var t1 = Tangent(Math.Abs(normal[v].Y) < 0.9 ? Vec3.UnitY : Vec3.UnitX, normal[v]);
            var t2 = Vec3.Cross(normal[v], t1);
            double xx = 0, xy = 0, yy = 0;
            foreach (int w in nb)
            {
                var d = tri.P[w] - tri.P[v];
                double l2 = d.LengthSquared;
                if (l2 < 1e-24) continue;
                double k = 2 * Vec3.Dot(normal[v], d) / l2;
                var t = Tangent(d, normal[v]);
                double u = Vec3.Dot(t, t1), w2 = Vec3.Dot(t, t2);
                xx += k * u * u; xy += k * u * w2; yy += k * w2 * w2;
            }
            // The tensor's eigenvectors at right angles: the larger's direction, and how unlike the two are.
            double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
            double spread = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy) / Math.Max(1, nb.Count);
            dir[v] = (t1 * Math.Cos(angle) + t2 * Math.Sin(angle)).Normalized();
            weight[v] = spread;
        }
        double mean = weight.Where(w => w > 0).DefaultIfEmpty(1).Average();
        for (int v = 0; v < n; v++) weight[v] = mean > 0 ? Math.Min(weight[v] / mean, 4) * 0.5 : 0;
        var curvature = (Vec3[])dir.Clone();
        for (int pass = 0; pass < passes; pass++)
        {
            var next = (Vec3[])dir.Clone();
            for (int v = 0; v < n; v++)
            {
                if (fixedDir[v] || tri.VT[v].Count == 0) continue;
                var sum = Vec3.Zero;
                foreach (int w in tri.Neighbours(v)) sum += Align(Tangent(dir[w], normal[v]), dir[v], normal[v]);
                sum += Align(curvature[v], dir[v], normal[v]) * weight[v];
                var d = Tangent(sum, normal[v]);
                if (d.LengthSquared > 0) next[v] = d;
            }
            dir = next;
        }
        return dir;
    }

    private static Vec3 Tangent(Vec3 d, Vec3 n) => (d - n * Vec3.Dot(n, d)).Normalized();

    /// <summary>The one of a direction's four turns (by right angles about the normal) nearest another.</summary>
    private static Vec3 Align(Vec3 d, Vec3 to, Vec3 n)
    {
        var best = d;
        double score = double.MinValue;
        var perp = Vec3.Cross(n, d);
        foreach (var c in new[] { d, -d, perp, -perp })
        {
            double s = Vec3.Dot(c, to);
            if (s > score) { score = s; best = c; }
        }
        return best;
    }

    /// <summary>How well an edge follows the field: 1 along one of its directions, 0.71 halfway between.</summary>
    private static double Follows(Vec3 edge, Vec3 d, Vec3 n)
    {
        var t = Tangent(edge, n);
        if (t.LengthSquared == 0) return 0;
        return Math.Max(Math.Abs(Vec3.Dot(t, d)), Math.Abs(Vec3.Dot(t, Vec3.Cross(n, d))));
    }

    /// <summary>Pairs triangles into quads whose sides follow the field; returns the quads and the triangles left.</summary>
    private static (List<int[]> Quads, List<int> Triangles) Pair(Tri tri, Vec3[] field)
    {
        var candidates = new List<(double Score, int T1, int T2, int[] Quad)>();
        foreach (var e in tri.Edges())
        {
            if (tri.Feature.Contains(e)) continue;
            var ts = tri.EdgeTris(e.A, e.B);
            if (ts.Count != 2 || tri.Surface[ts[0]] != tri.Surface[ts[1]]) continue;
            int t1 = ts[0], t2 = ts[1], a = e.A, b = e.B;
            var x1 = tri.T[t1]!;
            if (x1[(Array.IndexOf(x1, a) + 1) % 3] != b) (t1, t2) = (t2, t1);
            x1 = tri.T[t1]!;
            var x2 = tri.T[t2]!;
            int c = x1.First(v => v != a && v != b), d = x2.First(v => v != a && v != b);
            int[] quad = [a, d, b, c];
            var n = (tri.Normal(t1).Normalized() + tri.Normal(t2).Normalized()).Normalized();
            if (Vec3.Dot(tri.Normal(t1).Normalized(), tri.Normal(t2).Normalized()) < 0.7) continue;
            var dir = Tangent(field[a], n);
            if (dir.LengthSquared == 0) continue;
            double score = 1, sum = 0;
            bool convex = true;
            for (int i = 0; i < 4; i++)
            {
                var p = tri.P[quad[i]];
                var next = tri.P[quad[(i + 1) % 4]];
                var prev = tri.P[quad[(i + 3) % 4]];
                double f = Follows(next - p, Align(Tangent(field[quad[i]], n), dir, n), n);
                score = Math.Min(score, f);
                sum += f;
                // Each corner turns the same way as the face, and isn't nearly straight.
                var turn = Vec3.Cross(next - p, prev - p);
                double sin = Vec3.Dot(turn, n) / Math.Max((next - p).Length * (prev - p).Length, 1e-24);
                if (sin < 0.17) convex = false;
            }
            if (!convex || score < 0.6) continue;
            candidates.Add((sum / 4 + score, t1, t2, quad));
        }
        var used = new HashSet<int>();
        var quads = new List<int[]>();
        // The well-aligned first, then the rest that will do: a triangle left over makes three slanting quads.
        foreach (double floor in new[] { 1.8, 1.2 })
            foreach (var (score, t1, t2, quad) in candidates.OrderByDescending(c => c.Score))
            {
                if (score < floor || used.Contains(t1) || used.Contains(t2)) continue;
                used.Add(t1);
                used.Add(t2);
                quads.Add(quad);
            }
        var triangles = Enumerable.Range(0, tri.T.Count).Where(t => tri.T[t] != null && !used.Contains(t)).ToList();
        return (quads, triangles);
    }

    private static QuadRemeshResult Write(Mesh mesh, HashSet<int> chosen, Tri tri, List<int[]> quads, List<int> triangles, Func<Vec3, Vec3> project, int smoothing, Vec3[] field)
    {
        // Each quad's (or left-over triangle's) surface: its triangles'.
        var faceList = new List<(int[] Points, int Surface)>();
        var surfaceOf = new Dictionary<Edge, int>();
        foreach (var (t, x) in tri.T.Select((x, i) => (i, x)))
            if (x != null)
                for (int i = 0; i < 3; i++) surfaceOf[Edge.Of(x[i], x[(i + 1) % 3])] = tri.Surface[t];
        foreach (var q in quads) faceList.Add((q, surfaceOf[Edge.Of(q[0], q[1])]));
        foreach (int t in triangles) faceList.Add((tri.T[t]!, tri.Surface[t]));

        // The border: edges shared with faces left out, in the mesh's own points.
        var borderPoints = new Dictionary<Vec3, int>();
        var edgeMap = mesh.EdgeMap();
        foreach (var (e, users) in edgeMap)
            if (users.Any(f => chosen.Contains(f)) && users.Any(f => !chosen.Contains(f) && mesh.Polygons[f].Count >= 3))
            {
                borderPoints[mesh.Points[e.A]] = e.A;
                borderPoints[mesh.Points[e.B]] = e.B;
            }

        // Split every face into quads round its centre, sharing the edges' middles.
        var points = new List<Vec3>(tri.P);
        var directions = new List<Vec3>(field);
        var mid = new Dictionary<Edge, int>();
        var still = new HashSet<int>();
        int Mid(int a, int b)
        {
            var e = Edge.Of(a, b);
            if (mid.TryGetValue(e, out int m)) return m;
            var p = (points[a] + points[b]) / 2;
            bool crease = tri.Feature.Contains(e);
            points.Add(crease ? p : project(p));
            directions.Add(field[a]);
            m = points.Count - 1;
            if (crease) still.Add(m);
            return mid[e] = m;
        }
        var newFaces = new List<(int[] Points, int Surface)>();
        foreach (var (f, s) in faceList)
        {
            var c = points.Count;
            points.Add(project(f.Aggregate(Vec3.Zero, (acc, v) => acc + points[v]) / f.Length));
            directions.Add(field[f[0]]);
            for (int i = 0; i < f.Length; i++)
            {
                int a = f[i], next = f[(i + 1) % f.Length], prev = f[(i + f.Length - 1) % f.Length];
                newFaces.Add(([a, Mid(a, next), c, Mid(prev, a)], s));
            }
        }
        // Creases and the border stay put; the rest are evened out over the surface.
        for (int v = 0; v < tri.P.Count; v++) if (tri.IsFeatureVertex(v) || tri.Fixed.Contains(v)) still.Add(v);
        var neighbours = new List<HashSet<int>>(points.Count);
        for (int i = 0; i < points.Count; i++) neighbours.Add([]);
        foreach (var (f, _) in newFaces)
            for (int i = 0; i < 4; i++) { neighbours[f[i]].Add(f[(i + 1) % 4]); neighbours[f[(i + 1) % 4]].Add(f[i]); }
        for (int pass = 0; pass < smoothing; pass++)
        {
            var moved = points.ToArray();
            for (int v = 0; v < points.Count; v++)
            {
                if (still.Contains(v) || neighbours[v].Count == 0) continue;
                var q = neighbours[v].Aggregate(Vec3.Zero, (acc, w) => acc + points[w]) / neighbours[v].Count;
                moved[v] = project(q);
            }
            points = [.. moved];
        }
        // Then each point is drawn to where its four edges lie along the field a quad's side apart.
        var facesAt = new List<List<int>>(points.Count);
        for (int i = 0; i < points.Count; i++) facesAt.Add([]);
        for (int f = 0; f < newFaces.Count; f++) foreach (int v in newFaces[f].Points) facesAt[v].Add(f);
        for (int pass = 0; pass < 4 * Math.Max(1, smoothing) + 8; pass++)
        {
            var moved = points.ToArray();
            for (int v = 0; v < points.Count; v++)
            {
                if (still.Contains(v) || neighbours[v].Count == 0) continue;
                var n = Vec3.Zero;
                foreach (int f in facesAt[v])
                {
                    var q = newFaces[f].Points;
                    n += Vec3.Cross(points[q[1]] - points[q[0]], points[q[3]] - points[q[0]]) + Vec3.Cross(points[q[3]] - points[q[2]], points[q[1]] - points[q[2]]);
                }
                n = n.Normalized();
                var o = Tangent(directions[v], n);
                if (o.LengthSquared == 0) continue;
                var b = Vec3.Cross(n, o);
                var sum = Vec3.Zero;
                double h = 0;
                foreach (int w in neighbours[v]) h += Vec3.Distance(points[v], points[w]);
                h /= neighbours[v].Count;
                foreach (int w in neighbours[v])
                {
                    var e = points[v] - points[w];
                    double u = Vec3.Dot(e, o), t = Vec3.Dot(e, b);
                    sum += points[w] + (Math.Abs(u) >= Math.Abs(t) ? o * (u >= 0 ? h : -h) : b * (t >= 0 ? h : -h));
                }
                var target = sum / neighbours[v].Count;
                var to = project(points[v] + (target - points[v]) * 0.5);
                // Never fold a quad over.
                bool ok = true;
                foreach (int f in facesAt[v])
                {
                    var q = newFaces[f].Points;
                    int i = Array.IndexOf(q, v);
                    var prev = points[q[(i + 3) % 4]];
                    var next = points[q[(i + 1) % 4]];
                    var opposite = points[q[(i + 2) % 4]];
                    var before = Vec3.Cross(next - points[v], prev - points[v]) + Vec3.Cross(prev - opposite, next - opposite);
                    var after = Vec3.Cross(next - to, prev - to) + Vec3.Cross(prev - opposite, next - opposite);
                    var corner = Vec3.Cross(next - to, prev - to);
                    if (Vec3.Dot(before, after) <= 0 || Vec3.Dot(corner, before) <= 0) { ok = false; break; }
                }
                if (ok) moved[v] = to;
            }
            points = [.. moved];
        }

        // Into the mesh: the chosen faces go, the new ones come, border points are the mesh's own.
        var final = new int[points.Count];
        for (int i = 0; i < points.Count; i++)
            final[i] = borderPoints.TryGetValue(points[i], out int own) ? own : mesh.AddPoint(points[i]);
        var keep = mesh.Polygons.Where((_, i) => !chosen.Contains(i)).ToList();
        var triAt = new Dictionary<Vec3, int>();
        for (int v = 0; v < tri.P.Count; v++) if (tri.Fixed.Contains(v)) triAt.TryAdd(tri.P[v], v);
        // A face left out that shared a border edge gets the edge's new middle, so the mesh stays stitched.
        foreach (var poly in keep)
        {
            if (poly.Count < 3) continue;
            List<int>? pts = null;
            for (int i = 0; i < poly.Count; i++)
            {
                int a = poly.Points[i], b = poly.Points[(i + 1) % poly.Count];
                int ta = triAt.GetValueOrDefault(mesh.Points[a], -1);
                int tb = ta < 0 ? -1 : triAt.GetValueOrDefault(mesh.Points[b], -1);
                if (ta >= 0 && tb >= 0 && tri.Border.Contains(Edge.Of(ta, tb)) && mid.TryGetValue(Edge.Of(ta, tb), out int m))
                {
                    pts ??= poly.Points.Take(i).ToList();
                    pts.Add(a);
                    pts.Add(final[m]);
                }
                else pts?.Add(a);
            }
            if (pts != null) poly.Points = pts;
        }
        mesh.Polygons = keep;
        foreach (var (f, s) in newFaces) mesh.Polygons.Add(new Polygon(f.Select(v => final[v]), s));
        mesh.RemoveUnusedPoints();
        foreach (var map in mesh.UVMaps.ToList()) mesh.RemoveUVMap(map.Name);
        mesh.Touch();

        // How much of the result follows the field (edges along one of its directions within 15 degrees).
        int along = 0, total = 0;
        foreach (var q in quads)
        {
            var n = Vec3.Cross(tri.P[q[1]] - tri.P[q[0]], tri.P[q[2]] - tri.P[q[0]]).Normalized();
            for (int i = 0; i < 4; i++)
            {
                total++;
                if (Follows(tri.P[q[(i + 1) % 4]] - tri.P[q[i]], Tangent(field[q[i]], n), n) > 0.966) along++;
            }
        }
        int faces = tri.T.Count(t => t != null);
        return new QuadRemeshResult(newFaces.Count, points.Count, total == 0 ? 0 : (double)along / total, faces == 0 ? 0 : (int)Math.Round(100.0 * quads.Count * 2 / faces));
    }
}
