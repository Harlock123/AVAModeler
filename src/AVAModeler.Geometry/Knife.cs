using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// The knife: cuts faces along a plane through a line, between the line's ends. The plane holds the line and a depth
/// direction (the view's, so a line drawn across a view cuts straight through what's under it). Each edge the plane
/// crosses there gets a new point, put into every polygon on that edge, so a closed mesh stays closed; each chosen
/// face the plane crosses is split along it, its pieces keeping its surface, winding and corner UVs.
/// </summary>
public static class Knife
{
    /// <summary>What a cut did.</summary>
    public readonly record struct Result(int PointsAdded, int FacesCut);

    /// <summary>
    /// Cuts the chosen faces of a mesh along the plane through <paramref name="start"/> and <paramref name="end"/>
    /// that holds <paramref name="depth"/>. Only crossings between the line's ends (seen down the depth) count.
    /// </summary>
    public static Result Cut(Mesh mesh, IReadOnlyCollection<int> faces, Vec3 start, Vec3 end, Vec3 depth)
    {
        if (depth.LengthSquared < 1e-24) throw new ArgumentException("The knife needs a direction to cut along.");
        var d = depth.Normalized();
        var u = end - start;
        u -= d * Vec3.Dot(u, d);
        double length2 = u.LengthSquared;
        if (length2 < 1e-18) throw new ArgumentException("Draw the knife's line longer: its ends are at the same place as the view sees them.");
        var normal = Vec3.Cross(u, d).Normalized();
        var chosen = faces.Where(i => i >= 0 && i < mesh.Polygons.Count && mesh.Polygons[i].Count >= 3).ToHashSet();
        if (chosen.Count == 0) return new Result(0, 0);

        double scale = Math.Max(mesh.Bounds(chosen.SelectMany(i => mesh.Polygons[i].Points)).Extent, 1e-9);
        double eps = 1e-9 * scale;
        double Side(int p) => Vec3.Dot(mesh.Points[p] - start, normal);
        double Along(Vec3 p) => Vec3.Dot(p - start, u) / length2;
        bool Within(Vec3 p) { double t = Along(p); return t >= -1e-9 && t <= 1 + 1e-9; }

        // The edges the plane crosses between the ends, each given one new point.
        var cuts = new Dictionary<Edge, int>();
        int before = mesh.Points.Count;
        foreach (int pi in chosen)
            foreach (var (a, b) in mesh.Polygons[pi].Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (cuts.ContainsKey(e)) continue;
                double sa = Side(e.A), sb = Side(e.B);
                if (Math.Abs(sa) <= eps || Math.Abs(sb) <= eps || (sa > 0) == (sb > 0)) continue;
                double t = sa / (sa - sb);
                if (!Within(Vec3.Lerp(mesh.Points[e.A], mesh.Points[e.B], t))) continue;
                cuts[e] = mesh.AddPointBetween(e.A, e.B, t);
            }

        // Every polygon on a cut edge takes its new point, with its own corner UVs in between.
        if (cuts.Count > 0)
            foreach (var poly in mesh.Polygons)
            {
                if (poly.Count < 2) continue;
                List<int>? rebuilt = null;
                var uvs = new List<(int Point, int A, int B, double T)>();
                for (int k = 0; k < poly.Count; k++)
                {
                    int a = poly.Points[k], b = poly.Points[(k + 1) % poly.Count];
                    if (poly.Count == 2 && k == 1) b = -1;
                    rebuilt?.Add(a);
                    if (b < 0 || a == b || !cuts.TryGetValue(Edge.Of(a, b), out int n)) continue;
                    if (rebuilt == null) rebuilt = [.. poly.Points.Take(k + 1)];
                    rebuilt.Add(n);
                    var e = Edge.Of(a, b);
                    double sa = Side(e.A), sb = Side(e.B);
                    uvs.Add((n, e.A, e.B, sa / (sa - sb)));
                }
                if (rebuilt == null) continue;
                var corner = mesh.UVMaps.Select(m => (m.Name, Values: uvs.Select(x => mesh.TryGetUV(m.Name, poly, x.A, out var ua) && mesh.TryGetUV(m.Name, poly, x.B, out var ub) ? ua + (ub - ua) * x.T : (Vec2?)null).ToList())).ToList();
                poly.Points = rebuilt;
                foreach (var (name, values) in corner)
                    for (int i = 0; i < uvs.Count; i++)
                        if (values[i] is { } uv) mesh.SetCornerUV(name, poly, uvs[i].Point, uv);
            }

        // Split the chosen faces at their cut points: the new ones and any of their own points on the plane.
        int cutFaces = 0;
        var added = new List<Polygon>();
        foreach (int pi in chosen.Order())
        {
            var poly = mesh.Polygons[pi];
            var at = poly.Points.Where(p => p >= before || p < before && Math.Abs(Side(p)) <= eps && Within(mesh.Points[p])).Distinct()
                .OrderBy(p => Along(mesh.Points[p])).ToList();
            if (at.Count < 2) continue;
            var pieces = new List<List<int>> { new(poly.Points) };
            bool split = false;
            for (int k = 0; k + 1 < at.Count; k += 2)
            {
                int a = at[k], b = at[k + 1];
                int which = pieces.FindIndex(piece => piece.Contains(a) && piece.Contains(b) && !Adjacent(piece, a, b));
                if (which < 0) continue;
                var piece = pieces[which];
                int i = piece.IndexOf(a), j = piece.IndexOf(b);
                if (i > j) (i, j) = (j, i);
                var first = piece.GetRange(i, j - i + 1);
                var second = piece.Skip(j).Concat(piece.Take(i + 1)).ToList();
                pieces[which] = first;
                pieces.Add(second);
                split = true;
            }
            if (!split) continue;
            cutFaces++;
            var corners = poly.CornerUVs;
            for (int k = 0; k < pieces.Count; k++)
            {
                var target = k == 0 ? poly : new Polygon(pieces[k], poly.Surface);
                target.Points = pieces[k];
                var keep = pieces[k].ToHashSet();
                target.CornerUVs = corners?.ToDictionary(kv => kv.Key, kv => kv.Value.Where(c => keep.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value))
                    .Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value) is { Count: > 0 } c ? c : null;
                if (k > 0) added.Add(target);
            }
        }
        mesh.Polygons.AddRange(added);
        mesh.Touch();
        return new Result(mesh.Points.Count - before, cutFaces);
    }

    private static bool Adjacent(List<int> loop, int a, int b)
    {
        int i = loop.IndexOf(a), j = loop.IndexOf(b), n = loop.Count;
        return (i + 1) % n == j || (j + 1) % n == i;
    }
}
