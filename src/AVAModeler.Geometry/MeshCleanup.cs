using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// Tidying passes for geometry that has been cut up (booleans, drills, lathes): weld points, drop slivers, close the
/// T-junctions that cutting leaves, and stitch fragments of one original face back together.
/// </summary>
internal static class MeshCleanup
{
    /// <summary>
    /// The usual sequence after a cut: weld, drop slivers, close T-junctions, rejoin each group's fragments, then
    /// dissolve the straight-through points that cutting made and nothing needs. Points no polygon uses are removed.
    /// </summary>
    public static void AfterCut(Mesh mesh, double tolerance, Dictionary<Polygon, int> groups, HashSet<Vec3> originalPositions)
    {
        Weld(mesh, tolerance);
        DropDegenerate(mesh, tolerance);
        FixTJunctions(mesh, tolerance);
        Rejoin(mesh, groups);
        DissolveCollinear(mesh, tolerance, i => !originalPositions.Contains(mesh.Points[i]));
        mesh.RemoveUnusedPoints();
        mesh.Touch();
    }

    /// <summary>
    /// Merges points within a distance of each other (all, or only the candidates among themselves) and drops repeats
    /// from polygons. A face left with fewer than three points, or a line left with one, is removed. Unlike
    /// <see cref="Mesh.MergePoints"/>, points that were already unused are kept.
    /// </summary>
    public static void Weld(Mesh mesh, double tolerance, IEnumerable<int>? candidates = null)
    {
        var list = candidates?.Distinct().OrderBy(i => i).ToList() ?? Enumerable.Range(0, mesh.Points.Count).ToList();
        if (list.Count < 2) return;
        double cell = Math.Max(tolerance, 1e-12) * 2;
        var grid = new Dictionary<(long, long, long), List<int>>();
        var target = Enumerable.Range(0, mesh.Points.Count).ToArray();
        var gone = new List<int>();
        foreach (int i in list)
        {
            var p = mesh.Points[i];
            var key = ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
            int found = -1;
            for (long dx = -1; dx <= 1 && found < 0; dx++)
            for (long dy = -1; dy <= 1 && found < 0; dy++)
            for (long dz = -1; dz <= 1 && found < 0; dz++)
                if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket))
                    foreach (int j in bucket)
                        if (Vec3.Near(mesh.Points[j], p, tolerance)) { found = j; break; }
            if (found >= 0) { target[i] = found; gone.Add(i); continue; }
            if (!grid.TryGetValue(key, out var b)) grid[key] = b = [];
            b.Add(i);
        }
        if (gone.Count == 0) return;
        mesh.Polygons.RemoveAll(poly =>
        {
            int before = poly.Count;
            var pts = new List<int>(before);
            foreach (int i in poly.Points)
                if (pts.Count == 0 || pts[^1] != target[i]) pts.Add(target[i]);
            while (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
            poly.Points = pts;
            return before >= 3 ? pts.Count < 3 : pts.Count < before;
        });
        mesh.RemovePoints(gone);
    }

    /// <summary>Removes faces whose points all lie within a distance of one line (no area to speak of).</summary>
    public static void DropDegenerate(Mesh mesh, double tolerance)
    {
        int before = mesh.Polygons.Count;
        mesh.Polygons.RemoveAll(p => p.Count >= 3 && IsDegenerate(mesh, p.Points, tolerance));
        if (mesh.Polygons.Count != before) mesh.Touch();
    }

    public static bool IsDegenerate(Mesh mesh, IReadOnlyList<int> pts, double tolerance)
    {
        var a = mesh.Points[pts[0]];
        var b = a;
        foreach (int i in pts)
            if ((mesh.Points[i] - a).LengthSquared > (b - a).LengthSquared) b = mesh.Points[i];
        var d = b - a;
        if (d.LengthSquared <= tolerance * tolerance) return true;
        var ray = new Ray(a, d);
        return pts.All(i => ray.DistanceTo(mesh.Points[i]) <= tolerance);
    }

    /// <summary>
    /// Where a point of one face lies on the middle of another face's edge, puts the point into that edge too, so the
    /// faces share edges end to end. Only edges with no partner running the other way are looked at, and only the
    /// ends of such edges can be the points that belong on them.
    /// </summary>
    public static void FixTJunctions(Mesh mesh, double tolerance)
    {
        var directed = new HashSet<(int, int)>();
        foreach (var poly in mesh.Polygons)
            if (poly.Count >= 3)
                foreach (var e in poly.Edges()) directed.Add(e);
        var lonely = new List<(Polygon Poly, int Index)>();
        var candidates = new HashSet<int>();
        foreach (var poly in mesh.Polygons)
        {
            if (poly.Count < 3) continue;
            for (int i = 0; i < poly.Count; i++)
            {
                int a = poly.Points[i], b = poly.Points[(i + 1) % poly.Count];
                if (directed.Contains((b, a))) continue;
                lonely.Add((poly, i));
                candidates.Add(a);
                candidates.Add(b);
            }
        }
        if (lonely.Count == 0) return;
        var cand = candidates.ToArray();
        var inserts = new Dictionary<Polygon, List<(int After, double T, int Point)>>();
        foreach (var (poly, i) in lonely)
        {
            int a = poly.Points[i], b = poly.Points[(i + 1) % poly.Count];
            Vec3 pa = mesh.Points[a], pb = mesh.Points[b], d = pb - pa;
            double len2 = d.LengthSquared;
            if (len2 == 0) continue;
            var box = Box3.FromCorners(pa, pb).Expanded(tolerance);
            double margin = tolerance / Math.Sqrt(len2);
            foreach (int c in cand)
            {
                if (c == a || c == b) continue;
                var pc = mesh.Points[c];
                if (!box.Contains(pc)) continue;
                double t = Vec3.Dot(pc - pa, d) / len2;
                if (t <= margin || t >= 1 - margin) continue;
                if ((pa + d * t - pc).LengthSquared > tolerance * tolerance) continue;
                if (!inserts.TryGetValue(poly, out var l)) inserts[poly] = l = [];
                l.Add((i, t, c));
            }
        }
        foreach (var (poly, list) in inserts)
        {
            var pts = new List<int>(poly.Count + list.Count);
            for (int i = 0; i < poly.Count; i++)
            {
                pts.Add(poly.Points[i]);
                foreach (var x in list.Where(x => x.After == i).OrderBy(x => x.T))
                    if (!pts.Contains(x.Point)) pts.Add(x.Point);
            }
            poly.Points = pts;
        }
        if (inserts.Count > 0) mesh.Touch();
    }

    /// <summary>
    /// Merges faces of the same group that share an edge, two at a time, wherever the pair's outline is still one
    /// simple loop; a group whose pieces would need a hole stays in more than one piece. Faces not in the map are
    /// left alone. The merged face keeps the first face's place.
    /// </summary>
    public static void Rejoin(Mesh mesh, Dictionary<Polygon, int> groups)
    {
        var removed = new HashSet<Polygon>();
        foreach (var members in mesh.Polygons.Where(p => p.Count >= 3 && groups.ContainsKey(p)).GroupBy(p => groups[p]))
        {
            var alive = members.ToList();
            if (alive.Count < 2) continue;
            bool changed = true;
            while (changed)
            {
                changed = false;
                var owner = new Dictionary<(int, int), Polygon>();
                foreach (var p in alive)
                    foreach (var e in p.Edges()) owner.TryAdd(e, p);
                foreach (var p in alive)
                {
                    foreach (var (a, b) in p.Edges())
                    {
                        if (!owner.TryGetValue((b, a), out var q) || q == p) continue;
                        var merged = TryMerge(p, q);
                        if (merged == null) continue;
                        p.Points = merged;
                        alive.Remove(q);
                        removed.Add(q);
                        changed = true;
                        break;
                    }
                    if (changed) break;
                }
            }
        }
        if (removed.Count == 0) return;
        mesh.Polygons.RemoveAll(removed.Contains);
        mesh.Touch();
    }

    /// <summary>The outline of two faces that share edges, or null when it isn't a single loop through distinct points.</summary>
    public static List<int>? TryMerge(Polygon p, Polygon q)
    {
        var ep = p.Edges().ToHashSet();
        var eq = q.Edges().ToHashSet();
        var next = new Dictionary<int, int>();
        int count = 0;
        foreach (var (a, b) in p.Edges().Where(e => !eq.Contains((e.B, e.A))).Concat(q.Edges().Where(e => !ep.Contains((e.B, e.A)))))
        {
            if (!next.TryAdd(a, b)) return null;
            count++;
        }
        if (count < 3) return null;
        int start = next.Keys.First(), cur = start;
        var loop = new List<int>(count);
        do
        {
            loop.Add(cur);
            if (!next.TryGetValue(cur, out cur) || loop.Count > count) return null;
        } while (cur != start);
        return loop.Count == count ? loop : null;
    }

    /// <summary>
    /// Removes points that sit on a straight run of a face's outline and do no work: used by one face, or by two faces
    /// that both pass straight through them between the same neighbours. Only points <paramref name="removable"/>
    /// allows are touched.
    /// </summary>
    public static void DissolveCollinear(Mesh mesh, double tolerance, Func<int, bool> removable)
    {
        var users = new List<Polygon>[mesh.Points.Count];
        foreach (var poly in mesh.Polygons)
            foreach (int i in poly.Points.Distinct())
                (users[i] ??= []).Add(poly);
        bool any = false;
        for (int v = 0; v < users.Length; v++)
        {
            var polys = users[v];
            if (polys == null || polys.Count > 2 || polys.Any(p => p.Count < 4) || !removable(v)) continue;
            if (!Neighbours(polys[0], v, out int u, out int w) || !StraightThrough(mesh, u, v, w, tolerance)) continue;
            if (polys.Count == 2 && !(Neighbours(polys[1], v, out int u2, out int w2) && u2 == w && w2 == u)) continue;
            foreach (var p in polys) p.Points.Remove(v);
            any = true;
        }
        if (any) mesh.Touch();
    }

    private static bool Neighbours(Polygon p, int v, out int before, out int after)
    {
        int i = p.Points.IndexOf(v);
        before = after = -1;
        if (i < 0 || p.Points.LastIndexOf(v) != i) return false;
        before = p.Points[(i + p.Count - 1) % p.Count];
        after = p.Points[(i + 1) % p.Count];
        return true;
    }

    private static bool StraightThrough(Mesh mesh, int u, int v, int w, double tolerance)
    {
        Vec3 a = mesh.Points[u], b = mesh.Points[w], p = mesh.Points[v], d = b - a;
        double len2 = d.LengthSquared;
        if (len2 == 0) return false;
        double t = Vec3.Dot(p - a, d) / len2;
        return t > 0 && t < 1 && (a + d * t - p).LengthSquared <= tolerance * tolerance;
    }

    /// <summary>Adds a mesh's points and lines (1- and 2-point polygons), with just the points they use, to another.</summary>
    public static void AppendPointsAndLines(Mesh target, Mesh source)
    {
        var small = source.Polygons.Where(p => p.Count < 3).Select(p => p.Clone()).ToList();
        if (small.Count == 0) return;
        var only = new Mesh { Points = [.. source.Points], Polygons = small };
        only.RemoveUnusedPoints();
        target.Append(only);
    }
}
