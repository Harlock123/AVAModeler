using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>How <see cref="Unwrap.Run"/> cuts and lays out the charts.</summary>
public sealed record UnwrapOptions
{
    /// <summary>Edges whose faces meet at more than this angle (degrees) become seams; 0 or 180 and over: none from angles.</summary>
    public double SeamAngle { get; init; } = 66;
    /// <summary>Whether edges between different surfaces become seams.</summary>
    public bool SurfaceSeams { get; init; } = true;
    /// <summary>The gap left round each chart, as a share of the 0-1 square.</summary>
    public double Margin { get; init; } = 0.01;
    /// <summary>Whether each chart is turned to its smallest bounding box before packing.</summary>
    public bool Rotate { get; init; } = true;
}

/// <summary>What <see cref="Unwrap.Run"/> made: each face's corner UVs (in its point order) and how good they are.</summary>
public sealed record UnwrapResult(IReadOnlyList<Vec2[]> UVs, int Charts, double Stretch, double Coverage, int Flipped, int CutCharts);

/// <summary>
/// UV unwrapping from seams. The faces are cut into charts along the seams given, open and non-manifold edges, edges
/// between surfaces and sharp edges; a chart that still isn't a disc (closed, or with holes) is split across its longest
/// side until it is. Each chart is flattened by least-squares conformal maps (Lévy et al. 2002: angles kept as well as a
/// flat piece can keep them, two points pinned at their true distance), scaled to its true area, turned to its smallest
/// box, and the charts are packed in rows into the 0-1 square at one scale, a margin apart. Faces come out wound as the
/// views see them (clockwise from the front), so a texture painted in the UV view isn't mirrored on the model.
/// </summary>
public static class Unwrap
{
    /// <summary>Unwraps the faces (3 or more points) given; <paramref name="seams"/> are extra cuts by point pair.</summary>
    public static UnwrapResult Run(Mesh mesh, IReadOnlyList<int> faces, IReadOnlyCollection<Edge>? seams = null, UnwrapOptions? options = null)
    {
        options ??= new UnwrapOptions();
        var faceList = faces.Where(f => f >= 0 && f < mesh.Polygons.Count && mesh.Polygons[f].Count >= 3).Distinct().ToList();
        var uvs = faces.Select(f => new Vec2[f >= 0 && f < mesh.Polygons.Count ? mesh.Polygons[f].Count : 0]).ToList();
        if (faceList.Count == 0) return new UnwrapResult(uvs, 0, 1, 0, 0, 0);
        var seamSet = seams?.ToHashSet() ?? [];

        // Which edges are cut, and the faces on each edge (within the set).
        var edgeFaces = new Dictionary<Edge, List<int>>();
        var normals = faceList.ToDictionary(f => f, f => mesh.Normal(mesh.Polygons[f]));
        foreach (int f in faceList)
            foreach (var (a, b) in mesh.Polygons[f].Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (!edgeFaces.TryGetValue(e, out var l)) edgeFaces[e] = l = [];
                if (!l.Contains(f)) l.Add(f);
            }
        double cosLimit = options.SeamAngle > 0 && options.SeamAngle < 180 ? Math.Cos(options.SeamAngle * Math.PI / 180) : -2;
        bool Cut(Edge e)
        {
            var l = edgeFaces[e];
            if (l.Count != 2 || seamSet.Contains(e)) return true;
            var (p, q) = (mesh.Polygons[l[0]], mesh.Polygons[l[1]]);
            if (options.SurfaceSeams && p.Surface != q.Surface) return true;
            return Vec3.Dot(normals[l[0]], normals[l[1]]) < cosLimit;
        }
        var cut = edgeFaces.Keys.Where(Cut).ToHashSet();

        // The charts: faces joined across uncut edges.
        var charts = Components(faceList, f => Neighbours(mesh, f, edgeFaces, cut));
        var final = new List<List<int>>();
        int cutCharts = 0;
        foreach (var chart in charts) Discs(mesh, chart, edgeFaces, cut, final, 0, ref cutCharts);

        var flat = new List<Chart>();
        foreach (var chart in final) flat.Add(Flatten(mesh, chart, edgeFaces, cut));
        Pack(flat, options);

        var index = faces.Select((f, i) => (f, i)).GroupBy(x => x.f).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        double coverage = 0, stretchSum = 0, areaSum = 0;
        int flipped = 0;
        foreach (var c in flat)
            foreach (var (face, corners) in c.Faces)
            {
                foreach (int i in index[face]) uvs[i] = corners.ToArray();
                var poly = mesh.Polygons[face];
                for (int k = 1; k + 1 < poly.Count; k++)
                {
                    var (u0, u1, u2) = (corners[0], corners[k], corners[k + 1]);
                    double uvArea = Vec2.Cross(u1 - u0, u2 - u0) / 2;
                    coverage += Math.Abs(uvArea);
                    // Clockwise from the front is the views' way round: a positive (anticlockwise) area is a flipped face.
                    if (uvArea > 1e-15) flipped++;
                    var (p0, p1, p2) = (mesh.Points[poly.Points[0]], mesh.Points[poly.Points[k]], mesh.Points[poly.Points[k + 1]]);
                    double area = Vec3.Cross(p1 - p0, p2 - p0).Length / 2;
                    if (area <= 1e-18) continue;
                    stretchSum += Distortion(p0, p1, p2, u0, u1, u2) * area;
                    areaSum += area;
                }
            }
        return new UnwrapResult(uvs, flat.Count, areaSum > 0 ? stretchSum / areaSum : 1, coverage, flipped, cutCharts);
    }

    private sealed class Chart
    {
        public readonly List<(int Face, Vec2[] Corners)> Faces = [];
        public Vec2 Min, Max;
        public double Width => Max.X - Min.X;
        public double Height => Max.Y - Min.Y;
    }

    private static IEnumerable<int> Neighbours(Mesh mesh, int f, Dictionary<Edge, List<int>> edgeFaces, HashSet<Edge> cut)
    {
        foreach (var (a, b) in mesh.Polygons[f].Edges())
        {
            if (a == b) continue;
            var e = Edge.Of(a, b);
            if (cut.Contains(e)) continue;
            foreach (int g in edgeFaces[e]) if (g != f) yield return g;
        }
    }

    private static List<List<int>> Components(IEnumerable<int> faces, Func<int, IEnumerable<int>> next)
    {
        var all = faces.ToHashSet();
        var seen = new HashSet<int>();
        var result = new List<List<int>>();
        foreach (int start in all.OrderBy(f => f))
        {
            if (!seen.Add(start)) continue;
            var part = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int f = stack.Pop();
                part.Add(f);
                foreach (int g in next(f))
                    if (all.Contains(g) && seen.Add(g)) stack.Push(g);
            }
            part.Sort();
            result.Add(part);
        }
        return result;
    }

    /// <summary>
    /// The corners of a chart grouped into its flat points: corners of one mesh point joined across the chart's uncut
    /// edges are one flat point (a seam running into a chart, a slit, gives a point two).
    /// </summary>
    private static (Dictionary<(int Face, int Corner), int> VertexOf, int Count) Vertices(Mesh mesh, List<int> chart, Dictionary<Edge, List<int>> edgeFaces, HashSet<Edge> cut)
    {
        var parent = new Dictionary<(int, int), (int, int)>();
        (int, int) Find((int, int) x)
        {
            while (!parent[x].Equals(x)) x = parent[x] = parent[parent[x]];
            return x;
        }
        void Union((int, int) a, (int, int) b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (!ra.Equals(rb)) parent[ra] = rb;
        }
        var inChart = chart.ToHashSet();
        foreach (int f in chart)
            for (int c = 0; c < mesh.Polygons[f].Count; c++) parent[(f, c)] = (f, c);
        int CornerOf(int f, int point) => mesh.Polygons[f].Points.IndexOf(point);
        foreach (int f in chart)
            foreach (var (a, b) in mesh.Polygons[f].Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (cut.Contains(e)) continue;
                foreach (int g in edgeFaces[e])
                {
                    if (g <= f || !inChart.Contains(g)) continue;
                    Union((f, CornerOf(f, a)), (g, CornerOf(g, a)));
                    Union((f, CornerOf(f, b)), (g, CornerOf(g, b)));
                }
            }
        // A face that visits a point twice keeps those corners together.
        foreach (int f in chart)
        {
            var pts = mesh.Polygons[f].Points;
            for (int c = 0; c < pts.Count; c++)
                for (int d = c + 1; d < pts.Count; d++)
                    if (pts[c] == pts[d]) Union((f, c), (f, d));
        }
        var ids = new Dictionary<(int, int), int>();
        var result = new Dictionary<(int Face, int Corner), int>();
        foreach (var key in parent.Keys)
        {
            var r = Find(key);
            if (!ids.TryGetValue(r, out int id)) ids[r] = id = ids.Count;
            result[key] = id;
        }
        return (result, ids.Count);
    }

    /// <summary>Splits a chart until each piece is a disc (Euler characteristic 1, one boundary), then adds the pieces.</summary>
    private static void Discs(Mesh mesh, List<int> chart, Dictionary<Edge, List<int>> edgeFaces, HashSet<Edge> cut, List<List<int>> into, int depth, ref int cutCharts)
    {
        if (chart.Count <= 1 || depth >= 8 || IsDisc(mesh, chart, edgeFaces, cut)) { into.Add(chart); return; }
        cutCharts++;
        // Across the longest side, by the faces' centres: the cut runs along the edges between the halves.
        var centres = chart.ToDictionary(f => f, f => mesh.Centroid(mesh.Polygons[f]));
        var box = Box3.Of(centres.Values);
        var size = box.Max - box.Min;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        var ordered = chart.OrderBy(f => centres[f][axis]).ThenBy(f => f).ToList();
        var low = ordered.Take(ordered.Count / 2).ToHashSet();
        var inChart = chart.ToHashSet();
        foreach (int f in chart)
            foreach (var (a, b) in mesh.Polygons[f].Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (edgeFaces[e].Any(g => g != f && inChart.Contains(g) && low.Contains(g) != low.Contains(f))) cut.Add(e);
            }
        foreach (var half in new[] { chart.Where(low.Contains), chart.Where(f => !low.Contains(f)) })
            foreach (var part in Components(half, f => Neighbours(mesh, f, edgeFaces, cut)))
                Discs(mesh, part, edgeFaces, cut, into, depth + 1, ref cutCharts);
    }

    private static bool IsDisc(Mesh mesh, List<int> chart, Dictionary<Edge, List<int>> edgeFaces, HashSet<Edge> cut)
    {
        var (vertexOf, v) = Vertices(mesh, chart, edgeFaces, cut);
        var edges = new Dictionary<(int, int), int>();
        foreach (int f in chart)
        {
            int n = mesh.Polygons[f].Count;
            for (int c = 0; c < n; c++)
            {
                int a = vertexOf[(f, c)], b = vertexOf[(f, (c + 1) % n)];
                if (a == b) continue;
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }
        if (v - edges.Count + chart.Count != 1) return false;
        // One boundary loop: the boundary edges form a single cycle.
        var boundary = edges.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        if (boundary.Count == 0) return false;
        var adj = new Dictionary<int, List<int>>();
        foreach (var (a, b) in boundary)
        {
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = [];
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = [];
            la.Add(b);
            lb.Add(a);
        }
        var seen = new HashSet<int>();
        var stack = new Stack<int>([boundary[0].Item1]);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if (!seen.Add(x)) continue;
            foreach (int y in adj[x]) stack.Push(y);
        }
        return seen.Count == adj.Count;
    }

    /// <summary>Flattens one chart by least-squares conformal maps; falls back to a plane's projection if that fails.</summary>
    private static Chart Flatten(Mesh mesh, List<int> chart, Dictionary<Edge, List<int>> edgeFaces, HashSet<Edge> cut)
    {
        var (vertexOf, n) = Vertices(mesh, chart, edgeFaces, cut);
        var position = new Vec3[n];
        foreach (var ((f, c), id) in vertexOf) position[id] = mesh.Points[mesh.Polygons[f].Points[c]];

        // The triangles (a fan per face) in each one's own flat frame.
        var tris = new List<(int A, int B, int C, Vec2 P, Vec2 Q, Vec2 R)>();
        foreach (int f in chart)
        {
            var poly = mesh.Polygons[f];
            for (int k = 1; k + 1 < poly.Count; k++)
            {
                int a = vertexOf[(f, 0)], b = vertexOf[(f, k)], c = vertexOf[(f, k + 1)];
                if (a == b || b == c || a == c) continue;
                var (p0, p1, p2) = (position[a], position[b], position[c]);
                var e1 = p1 - p0;
                double l = e1.Length;
                if (l < 1e-15) continue;
                var x = e1 / l;
                var d = p2 - p0;
                double dx = Vec3.Dot(d, x);
                var yv = d - x * dx;
                double dy = yv.Length;
                if (dy < 1e-15) continue;
                tris.Add((a, b, c, new Vec2(0, 0), new Vec2(l, 0), new Vec2(dx, dy)));
            }
        }

        var uv = new Vec2[n];
        bool ok = tris.Count > 0 && n >= 3 && Lscm(position, tris, uv);
        if (!ok) Planar(position, uv, chart.Select(f => mesh.Normal(mesh.Polygons[f])));
        // The views' way round: clockwise from the front. LSCM lays each triangle anticlockwise, so mirror across.
        for (int i = 0; i < n; i++) uv[i] = uv[i] with { X = -uv[i].X };
        if (ok) Rescale(position, tris, uv);

        var result = new Chart();
        foreach (int f in chart)
        {
            var poly = mesh.Polygons[f];
            var corners = new Vec2[poly.Count];
            for (int c = 0; c < poly.Count; c++) corners[c] = uv[vertexOf[(f, c)]];
            result.Faces.Add((f, corners));
        }
        return result;
    }

    /// <summary>The least-squares conformal map, two far-apart points pinned; false if it didn't settle.</summary>
    private static bool Lscm(Vec3[] position, List<(int A, int B, int C, Vec2 P, Vec2 Q, Vec2 R)> tris, Vec2[] uv)
    {
        int n = position.Length;
        // Pins: the two points farthest apart along the chart's longest side.
        var box = Box3.Of(position);
        var size = box.Max - box.Min;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        int pinA = 0, pinB = 0;
        for (int i = 1; i < n; i++)
        {
            if (position[i][axis] < position[pinA][axis]) pinA = i;
            if (position[i][axis] > position[pinB][axis]) pinB = i;
        }
        if (pinA == pinB) return false;
        var pinned = new Dictionary<int, Vec2> { [pinA] = new Vec2(0, 0), [pinB] = new Vec2(Vec3.Distance(position[pinA], position[pinB]), 0) };

        // Unknowns: (u, v) of each free point, 2 per point.
        var free = new int[n];
        int m = 0;
        for (int i = 0; i < n; i++) free[i] = pinned.ContainsKey(i) ? -1 : m++;
        if (m == 0) { foreach (var (i, p) in pinned) uv[i] = p; return true; }
        int unknowns = 2 * m;

        // Two rows a triangle: Re and Im of Σ W_j (u_j + i v_j) / √(2A).
        var rows = new List<(int[] Cols, double[] Vals, double Rhs)>(tris.Count * 2);
        foreach (var t in tris)
        {
            var z = new[] { t.P, t.Q, t.R };
            int[] id = [t.A, t.B, t.C];
            double dt = Vec2.Cross(t.Q - t.P, t.R - t.P);
            if (dt <= 1e-20) continue;
            double s = 1 / Math.Sqrt(dt);
            var re = new List<(int, double)>();
            var im = new List<(int, double)>();
            double rhsRe = 0, rhsIm = 0;
            for (int j = 0; j < 3; j++)
            {
                var w = z[(j + 2) % 3] - z[(j + 1) % 3];
                double wr = w.X * s, wi = w.Y * s;
                int k = id[j];
                if (free[k] < 0)
                {
                    var p = pinned[k];
                    rhsRe -= wr * p.X - wi * p.Y;
                    rhsIm -= wi * p.X + wr * p.Y;
                    continue;
                }
                int cu = 2 * free[k], cv = cu + 1;
                re.Add((cu, wr)); re.Add((cv, -wi));
                im.Add((cu, wi)); im.Add((cv, wr));
            }
            rows.Add(([.. re.Select(x => x.Item1)], [.. re.Select(x => x.Item2)], rhsRe));
            rows.Add(([.. im.Select(x => x.Item1)], [.. im.Select(x => x.Item2)], rhsIm));
        }

        // Start from a plane's projection, then conjugate gradients on the normal equations, with a diagonal preconditioner.
        var start = new Vec2[n];
        Planar(position, start, null);
        var x0 = new double[unknowns];
        for (int i = 0; i < n; i++)
            if (free[i] >= 0) { x0[2 * free[i]] = start[i].X; x0[2 * free[i] + 1] = start[i].Y; }
        var diag = new double[unknowns];
        foreach (var (cols, vals, _) in rows)
            for (int k = 0; k < cols.Length; k++) diag[cols[k]] += vals[k] * vals[k];
        for (int i = 0; i < unknowns; i++) diag[i] = diag[i] > 1e-30 ? 1 / diag[i] : 1;

        double[] AtA(double[] x)
        {
            var y = new double[unknowns];
            foreach (var (cols, vals, _) in rows)
            {
                double r = 0;
                for (int k = 0; k < cols.Length; k++) r += vals[k] * x[cols[k]];
                for (int k = 0; k < cols.Length; k++) y[cols[k]] += vals[k] * r;
            }
            return y;
        }
        var b = new double[unknowns];
        foreach (var (cols, vals, rhs) in rows)
            for (int k = 0; k < cols.Length; k++) b[cols[k]] += vals[k] * rhs;

        var xk = x0;
        var ax = AtA(xk);
        var r0 = new double[unknowns];
        for (int i = 0; i < unknowns; i++) r0[i] = b[i] - ax[i];
        var zk = new double[unknowns];
        for (int i = 0; i < unknowns; i++) zk[i] = r0[i] * diag[i];
        var p0 = (double[])zk.Clone();
        double rz = Dot(r0, zk);
        double bNorm = Math.Sqrt(Dot(b, b)) + 1e-30;
        int limit = Math.Max(500, Math.Min(20000, unknowns * 4));
        for (int it = 0; it < limit && Math.Sqrt(Dot(r0, r0)) > 1e-12 * bNorm + 1e-20; it++)
        {
            var ap = AtA(p0);
            double pap = Dot(p0, ap);
            if (pap <= 0 || !double.IsFinite(pap)) break;
            double alpha = rz / pap;
            for (int i = 0; i < unknowns; i++) { xk[i] += alpha * p0[i]; r0[i] -= alpha * ap[i]; }
            for (int i = 0; i < unknowns; i++) zk[i] = r0[i] * diag[i];
            double rzNew = Dot(r0, zk);
            double beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < unknowns; i++) p0[i] = zk[i] + beta * p0[i];
        }
        for (int i = 0; i < n; i++)
            uv[i] = free[i] < 0 ? pinned[i] : new Vec2(xk[2 * free[i]], xk[2 * free[i] + 1]);
        if (uv.Any(u => !double.IsFinite(u.X) || !double.IsFinite(u.Y))) return false;
        // A map that folded most of the chart over isn't worth keeping.
        int bad = tris.Count(t => Vec2.Cross(uv[t.B] - uv[t.A], uv[t.C] - uv[t.A]) <= 0);
        return bad * 4 < tris.Count;
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>A flat projection along the points' (or the normals') main direction, anticlockwise as seen from it.</summary>
    private static void Planar(Vec3[] position, Vec2[] uv, IEnumerable<Vec3>? normals)
    {
        var n = normals?.Aggregate(Vec3.Zero, (s, x) => s + x) ?? Vec3.Zero;
        if (n.LengthSquared < 1e-20)
        {
            // The smallest spread of the points.
            var box = Box3.Of(position);
            var size = box.Max - box.Min;
            n = size.X <= size.Y && size.X <= size.Z ? Vec3.UnitX : size.Y <= size.Z ? Vec3.UnitY : Vec3.UnitZ;
        }
        n = n.Normalized();
        var helper = Math.Abs(n.Y) < 0.9 ? Vec3.UnitY : Vec3.UnitX;
        var x = Vec3.Cross(helper, n).Normalized();
        var y = Vec3.Cross(n, x);
        for (int i = 0; i < position.Length; i++) uv[i] = new Vec2(Vec3.Dot(position[i], x), Vec3.Dot(position[i], y));
    }

    /// <summary>Scales a chart so its flat area is its true area.</summary>
    private static void Rescale(Vec3[] position, List<(int A, int B, int C, Vec2 P, Vec2 Q, Vec2 R)> tris, Vec2[] uv)
    {
        double flat = 0, real = 0;
        foreach (var t in tris)
        {
            flat += Math.Abs(Vec2.Cross(uv[t.B] - uv[t.A], uv[t.C] - uv[t.A])) / 2;
            real += Vec2.Cross(t.Q - t.P, t.R - t.P) / 2;
        }
        if (flat <= 1e-30 || real <= 0) return;
        double s = Math.Sqrt(real / flat);
        for (int i = 0; i < uv.Length; i++) uv[i] *= s;
    }

    /// <summary>
    /// How far a triangle's map is from keeping angles: the ratio of the larger to the smaller stretch of its flat frame
    /// into UV (1 for a perfect, conformal map).
    /// </summary>
    private static double Distortion(Vec3 p0, Vec3 p1, Vec3 p2, Vec2 u0, Vec2 u1, Vec2 u2)
    {
        var e1 = p1 - p0;
        double l = e1.Length;
        var x = e1 / l;
        var d = p2 - p0;
        double dx = Vec3.Dot(d, x), dy = (d - x * dx).Length;
        if (l < 1e-15 || dy < 1e-15) return 1;
        // J maps the flat frame (l,0), (dx,dy) to the UV edges.
        var a = u1 - u0;
        var b = u2 - u0;
        double j11 = a.X / l, j21 = a.Y / l;
        double j12 = (b.X - j11 * dx) / dy, j22 = (b.Y - j21 * dx) / dy;
        double e = (j11 * j11 + j21 * j21 + j12 * j12 + j22 * j22) / 2;
        double det = Math.Abs(j11 * j22 - j12 * j21);
        double disc = Math.Sqrt(Math.Max(0, e * e - det * det));
        double s1 = Math.Sqrt(e + disc), s2 = Math.Sqrt(Math.Max(0, e - disc));
        return s2 < 1e-15 ? 10 : Math.Min(10, s1 / s2);
    }

    /// <summary>Turns, places and scales every chart into the 0-1 square.</summary>
    private static void Pack(List<Chart> charts, UnwrapOptions options)
    {
        foreach (var c in charts)
        {
            if (options.Rotate) TurnToSmallestBox(c);
            Bound(c);
            // Lying flat: wider than tall packs into rows better.
            if (c.Height > c.Width * 1.0001) Turn(c, Math.PI / 2);
            Bound(c);
        }
        if (charts.Count == 0) return;
        double area = charts.Sum(c => Math.Max(c.Width * c.Height, 1e-30));
        double pad = Math.Clamp(options.Margin, 0, 0.2) * Math.Sqrt(area) * 1.2;
        var order = charts.OrderByDescending(c => c.Height).ThenByDescending(c => c.Width).ToList();

        // Try row widths from the widest chart to all in one row; keep the most square result.
        double widest = order.Max(c => c.Width) + 2 * pad;
        double best = double.MaxValue, bestWidth = widest;
        for (int k = 0; k <= 24; k++)
        {
            double w = widest * Math.Pow(Math.Max(1, (order.Sum(c => c.Width + 2 * pad)) / widest), k / 24.0);
            var (tw, th) = Shelves(order, pad, w, place: false);
            double side = Math.Max(tw, th);
            if (side < best - 1e-12) { best = side; bestWidth = w; }
        }
        var (totalW, totalH) = Shelves(order, pad, bestWidth, place: true);
        double scale = 1 / Math.Max(Math.Max(totalW, totalH), 1e-30);
        foreach (var c in charts)
            foreach (var (_, corners) in c.Faces)
                for (int i = 0; i < corners.Length; i++) corners[i] *= scale;
    }

    /// <summary>Lays charts in rows no wider than <paramref name="width"/>; returns the size used.</summary>
    private static (double W, double H) Shelves(List<Chart> order, double pad, double width, bool place)
    {
        double x = 0, y = 0, row = 0, used = 0;
        foreach (var c in order)
        {
            double w = c.Width + 2 * pad, h = c.Height + 2 * pad;
            if (x > 0 && x + w > width + 1e-12) { y += row; x = 0; row = 0; }
            if (place)
            {
                var shift = new Vec2(x + pad, y + pad) - c.Min;
                foreach (var (_, corners) in c.Faces)
                    for (int i = 0; i < corners.Length; i++) corners[i] += shift;
            }
            x += w;
            row = Math.Max(row, h);
            used = Math.Max(used, x);
        }
        return (used, y + row);
    }

    private static void Bound(Chart c)
    {
        var all = c.Faces.SelectMany(f => f.Corners).ToList();
        c.Min = new Vec2(all.Min(p => p.X), all.Min(p => p.Y));
        c.Max = new Vec2(all.Max(p => p.X), all.Max(p => p.Y));
    }

    private static void Turn(Chart c, double angle)
    {
        double cs = Math.Cos(angle), sn = Math.Sin(angle);
        foreach (var (_, corners) in c.Faces)
            for (int i = 0; i < corners.Length; i++)
                corners[i] = new Vec2(corners[i].X * cs - corners[i].Y * sn, corners[i].X * sn + corners[i].Y * cs);
    }

    /// <summary>Turns a chart so its bounding box is smallest (one of its convex hull's edges then lies along an axis).</summary>
    private static void TurnToSmallestBox(Chart c)
    {
        var hull = Hull(c.Faces.SelectMany(f => f.Corners).Distinct().ToList());
        if (hull.Count < 3) return;
        double bestArea = double.MaxValue, bestAngle = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var e = hull[(i + 1) % hull.Count] - hull[i];
            if (e.LengthSquared < 1e-30) continue;
            double angle = -Math.Atan2(e.Y, e.X);
            double cs = Math.Cos(angle), sn = Math.Sin(angle);
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (var p in hull)
            {
                double x = p.X * cs - p.Y * sn, y = p.X * sn + p.Y * cs;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
            double a = (x1 - x0) * (y1 - y0);
            if (a < bestArea - 1e-15) { bestArea = a; bestAngle = angle; }
        }
        if (bestAngle != 0) Turn(c, bestAngle);
    }

    /// <summary>The convex hull, anticlockwise (Andrew's monotone chain).</summary>
    private static List<Vec2> Hull(List<Vec2> points)
    {
        var p = points.OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        var h = new List<Vec2>();
        foreach (var q in p)
        {
            while (h.Count >= 2 && Vec2.Cross(h[^1] - h[^2], q - h[^2]) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(q);
        }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            var q = p[i];
            while (h.Count >= lower && Vec2.Cross(h[^1] - h[^2], q - h[^2]) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(q);
        }
        h.RemoveAt(h.Count - 1);
        return h;
    }
}
