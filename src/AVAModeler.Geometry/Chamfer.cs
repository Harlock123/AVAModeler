using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// Chamfer (an edge bevel): each edge given, between two faces, is replaced by a strip <see cref="Apply"/>'s width
/// across, flat or rounded in segments. The faces either side pull back from it; the faces at its ends take the new
/// points into their edges.
/// </summary>
public static class Chamfer
{
    /// <summary>
    /// Chamfers edges. Only edges shared by exactly two faces are chamfered (others are left out). Where an edge's face
    /// has another chamfered edge at the same point, the face's corner moves in from both edges by
    /// <paramref name="width"/> (as an inset corner); otherwise it slides along the face's other edge there, far enough
    /// to be <paramref name="width"/> from the chamfered edge (never more than most of that edge, or half of it where its
    /// far end slides too). The strip is flat with one segment; with more, it follows a circular arc (a rational
    /// quadratic curve) between its sides, bowed toward the old edge. Chamfered edges meeting at a point share their
    /// strips' ends; a hole left at a corner (where three chamfered edges meet, or a chamfer ends at a point with more
    /// than three edges) is closed with one polygon. Old points no face uses any more are removed. New corners take UVs
    /// interpolated from the faces they come from; the strip's run between its two sides. Returns how many edges were
    /// chamfered.
    /// </summary>
    public static int Apply(Mesh mesh, IEnumerable<Edge> edges, double width, int segments = 1)
    {
        segments = Math.Max(segments, 1);
        var map = mesh.EdgeMap();
        var chamfered = new HashSet<Edge>();
        foreach (var e in edges.Distinct())
        {
            if (!map.TryGetValue(e, out var owners)) continue;
            var faces = EdgeLoops.Faces(mesh, owners);
            if (faces.Count == 2 && faces.All(f => Uses(mesh.Polygons[f], e) == 1)) chamfered.Add(e);
        }
        if (chamfered.Count == 0 || width <= 0) return 0;
        var original = mesh.Clone();
        var boundaryBefore = MeshCheck.Mesh(original).BoundaryEdges.ToHashSet();
        var ends = chamfered.SelectMany(e => new[] { e.A, e.B }).ToHashSet();
        var touched = ends.SelectMany(v => EdgeLoops.Faces(original, original.PolygonsOf(v))).Distinct().Order().ToList();
        // Faces that use a point twice aren't safe to rebuild.
        touched.RemoveAll(f => original.Polygons[f].Points.Distinct().Count() != original.Polygons[f].Count);
        int firstNew = original.Points.Count;

        bool IsChamfered(int a, int b) => chamfered.Contains(Edge.Of(a, b));

        // The slides: how far along an edge (v → x) each face beside a chamfered edge at v wants its corner to go.
        var wanted = new Dictionary<(int V, int X), List<double>>();
        foreach (int f in touched)
        {
            var p = original.Polygons[f].Points;
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                int v = p[i], prev = p[(i + n - 1) % n], next = p[(i + 1) % n];
                if (!ends.Contains(v)) continue;
                bool inC = IsChamfered(prev, v), outC = IsChamfered(v, next);
                if (inC == outC) continue;
                int c = inC ? prev : next, x = inC ? next : prev;
                var along = original.Points[x] - original.Points[v];
                var chamferDir = (original.Points[c] - original.Points[v]).Normalized();
                double sin = Vec3.Cross(along.Normalized(), chamferDir).Length;
                double t = sin > 1e-6 ? width / sin : width;
                (wanted.TryGetValue((v, x), out var l) ? l : wanted[(v, x)] = []).Add(t);
            }
        }
        var slides = new Dictionary<(int V, int X), (int Point, double Fraction)>();
        foreach (var ((v, x), ts) in wanted.OrderBy(kv => kv.Key.V).ThenBy(kv => kv.Key.X))
        {
            double length = Vec3.Distance(original.Points[v], original.Points[x]);
            if (length <= 0) continue;
            bool bothEnds = wanted.ContainsKey((x, v));
            double fraction = Math.Min(ts.Average() / length, bothEnds ? 0.49 : 0.98);
            slides[(v, x)] = (mesh.AddPoint(Vec3.Lerp(original.Points[v], original.Points[x], fraction)), fraction);
        }

        // The inset corners: a face with chamfered edges both sides of a point moves its corner in from both.
        var offsets = new Dictionary<(int V, int Face), int>();
        var offsetAt = new Dictionary<int, Vec3>();
        foreach (int f in touched)
        {
            var p = original.Polygons[f].Points;
            int n = p.Count;
            var normal = original.Normal(f);
            for (int i = 0; i < n; i++)
            {
                int v = p[i], prev = p[(i + n - 1) % n], next = p[(i + 1) % n];
                if (!IsChamfered(prev, v) || !IsChamfered(v, next)) continue;
                var at = original.Points[v];
                var m1 = Vec3.Cross(normal, (at - original.Points[prev]).Normalized()).Normalized();
                var m2 = Vec3.Cross(normal, (original.Points[next] - at).Normalized()).Normalized();
                double denom = 1 + Vec3.Dot(m1, m2);
                var d = denom > 1e-6 ? (m1 + m2) * (width / denom) : m1 * width;
                int q = mesh.AddPoint(at + d);
                offsets[(v, f)] = q;
                offsetAt[q] = at + d;
            }
        }

        // The point standing for a face's corner beside a chamfered edge at v.
        int Rail(int v, int f)
        {
            if (offsets.TryGetValue((v, f), out int o)) return o;
            var p = original.Polygons[f].Points;
            int n = p.Count, i = p.IndexOf(v);
            int prev = p[(i + n - 1) % n], next = p[(i + 1) % n];
            int x = IsChamfered(prev, v) ? next : prev;
            return slides.TryGetValue((v, x), out var s) ? s.Point : v;
        }

        // The strips, each end following the curve between its two sides; strips meeting at a point share those curves.
        var curves = new Dictionary<(int V, int Low, int High, int Step), int>();
        int CurvePoint(int v, int from, int to, int j)
        {
            if (j == 0) return from;
            if (j == segments) return to;
            var key = from < to ? (v, from, to, j) : (v, to, from, segments - j);
            if (curves.TryGetValue(key, out int q)) return q;
            var a = mesh.Points[from];
            var b = mesh.Points[to];
            var c = original.Points[v];
            double cos = Vec3.Dot((a - c).Normalized(), (b - c).Normalized());
            double w = Math.Sqrt(Math.Max(0, (1 - cos) / 2)); // sin of half the angle at the old point
            double t = (double)j / segments, u = 1 - t;
            var point = (a * (u * u) + c * (2 * t * u * w) + b * (t * t)) / (u * u + 2 * t * u * w + t * t);
            return curves[key] = mesh.AddPoint(point);
        }
        // Make every strip's curves now, so the faces at its ends can take them in.
        var arcs = new HashSet<(int V, int Low, int High)>();
        void Arc(int v, int from, int to)
        {
            arcs.Add((v, Math.Min(from, to), Math.Max(from, to)));
            for (int j = 1; j < segments; j++) CurvePoint(v, from, to, j);
        }
        foreach (var e in chamfered.OrderBy(e => e.A).ThenBy(e => e.B))
        {
            var (f1, f2) = (EdgeLoops.Faces(original, original.EdgeMap()[e])[0], EdgeLoops.Faces(original, original.EdgeMap()[e])[1]);
            Arc(e.A, Rail(e.A, f1), Rail(e.A, f2));
            Arc(e.B, Rail(e.B, f1), Rail(e.B, f2));
        }

        // Rebuild the faces round the chamfered points, with their new corners' UVs.
        var faceUVs = new Dictionary<int, Dictionary<string, Dictionary<int, Vec2>>>();
        foreach (int f in touched)
        {
            var poly = mesh.Polygons[f];
            var src = original.Polygons[f];
            var uvs = FaceUVs.Of(original, src);
            var fits = new Dictionary<string, UVTransfer.Interpolant?>();
            var p = src.Points;
            int n = p.Count;
            var pts = new List<int>();
            void Add(int q, Func<Dictionary<int, Vec2>, string, Vec2> uv)
            {
                if (pts.Count > 0 && pts[^1] == q) return;
                pts.Add(q);
                foreach (var (name, at) in uvs)
                    if (!at.ContainsKey(q)) at[q] = uv(at, name);
            }
            void Slide(int v, int x)
            {
                var (q, t) = slides[(v, x)];
                Add(q, (at, _) => EdgeLoops.Lerp(at[v], at[x], t));
            }
            for (int i = 0; i < n; i++)
            {
                int v = p[i], prev = p[(i + n - 1) % n], next = p[(i + 1) % n];
                if (!ends.Contains(v)) { Add(v, (at, _) => at[v]); continue; }
                bool inC = IsChamfered(prev, v), outC = IsChamfered(v, next);
                if (inC && outC)
                {
                    int q = offsets[(v, f)];
                    Add(q, (at, name) =>
                    {
                        if (!fits.TryGetValue(name, out var fit)) fits[name] = fit = UVTransfer.Interpolant.Of(original, src, name);
                        return fit?.At(offsetAt[q]) ?? at[v];
                    });
                }
                else if (inC && slides.ContainsKey((v, next))) Slide(v, next);
                else if (outC && slides.ContainsKey((v, prev))) Slide(v, prev);
                else if (inC || outC) Add(v, (at, _) => at[v]);
                else
                {
                    bool sIn = slides.ContainsKey((v, prev)), sOut = slides.ContainsKey((v, next));
                    if (sIn) Slide(v, prev);
                    if (sIn && sOut)
                    {
                        // A face across the end of a strip takes the strip's curve in place of its corner.
                        int from = slides[(v, prev)].Point, to = slides[(v, next)].Point;
                        if (arcs.Contains((v, Math.Min(from, to), Math.Max(from, to))))
                            for (int j = 1; j < segments; j++)
                            {
                                int q = CurvePoint(v, from, to, j);
                                Add(q, (at, name) =>
                                {
                                    if (!fits.TryGetValue(name, out var fit)) fits[name] = fit = UVTransfer.Interpolant.Of(original, src, name);
                                    return fit?.At(mesh.Points[q]) ?? at[v];
                                });
                            }
                    }
                    else Add(v, (at, _) => at[v]);
                    if (sOut) Slide(v, next);
                }
            }
            while (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
            poly.Points = pts;
            poly.CornerUVs = null;
            poly.Details = null;
            FaceUVs.Set(mesh, poly, uvs);
            faceUVs[f] = uvs;
        }

        var strips = new List<Polygon>();
        foreach (var e in chamfered.OrderBy(e => e.A).ThenBy(e => e.B))
        {
            var faces = EdgeLoops.Faces(original, original.EdgeMap()[e]);
            // f1 runs along the edge from a to b, f2 from b to a.
            int f1 = faces[0], f2 = faces[1];
            var p1 = original.Polygons[f1].Points;
            int a = e.A, b = e.B;
            if (p1[(p1.IndexOf(a) + 1) % p1.Count] != b) (a, b) = (b, a);
            int a1 = Rail(a, f1), a2 = Rail(a, f2), b1 = Rail(b, f1), b2 = Rail(b, f2);
            var surface = original.Polygons[f1].Surface;
            for (int j = 0; j < segments; j++)
            {
                int[] corners = [CurvePoint(a, a1, a2, j), CurvePoint(a, a1, a2, j + 1), CurvePoint(b, b1, b2, j + 1), CurvePoint(b, b1, b2, j)];
                var strip = new Polygon(corners.Distinct(), surface);
                if (strip.Count < 3) continue;
                foreach (var uvMap in original.UVMaps)
                {
                    if (!faceUVs.TryGetValue(f1, out var s1) || !faceUVs.TryGetValue(f2, out var s2)
                        || !s1.TryGetValue(uvMap.Name, out var u1) || !s2.TryGetValue(uvMap.Name, out var u2)) continue;
                    if (!u1.ContainsKey(a1) || !u2.ContainsKey(a2) || !u1.ContainsKey(b1) || !u2.ContainsKey(b2)) continue;
                    double t0 = (double)j / segments, t1 = (double)(j + 1) / segments;
                    mesh.SetCornerUV(uvMap.Name, strip, corners[0], EdgeLoops.Lerp(u1[a1], u2[a2], t0));
                    mesh.SetCornerUV(uvMap.Name, strip, corners[1], EdgeLoops.Lerp(u1[a1], u2[a2], t1));
                    mesh.SetCornerUV(uvMap.Name, strip, corners[2], EdgeLoops.Lerp(u1[b1], u2[b2], t1));
                    mesh.SetCornerUV(uvMap.Name, strip, corners[3], EdgeLoops.Lerp(u1[b1], u2[b2], t0));
                }
                strips.Add(strip);
            }
        }
        mesh.Polygons.AddRange(strips);
        mesh.Touch();

        // Corners left open (three chamfers meeting, or a chamfer ending at a busy point) get a polygon each.
        var holes = Holes.BoundaryLoops(mesh).Where(loop =>
            loop.Any(q => q >= firstNew) &&
            loop.Select((q, i) => Edge.Of(q, loop[(i + 1) % loop.Count])).All(x => !boundaryBefore.Contains(x)));
        Holes.Fill(mesh, holes.Select(l => (IReadOnlyList<int>)l).ToList());

        // The old points the chamfer replaced.
        var used = mesh.PointsOf(Enumerable.Range(0, mesh.Polygons.Count));
        mesh.RemovePoints(ends.Where(v => !used.Contains(v)));
        mesh.Touch();
        return chamfered.Count;
    }

    /// <summary>How many times a polygon runs along an edge.</summary>
    private static int Uses(Polygon poly, Edge e) => poly.Edges().Count(x => Edge.Of(x.A, x.B) == e);
}
