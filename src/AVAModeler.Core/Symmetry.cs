namespace AVAModeler.Core;

/// <summary>
/// Modeling with symmetry: each point's mirror partner across the plane through the origin square to an axis (X = 0,
/// Y = 0 or Z = 0), found by position within a tolerance, and each polygon's partner (the polygon on the mirrored
/// points). Tools act on a selection widened by the partners (<see cref="Selection.Symmetry"/>), and tools that move
/// points move each partner to the mirror of where its point went.
/// </summary>
public static class Symmetry
{
    /// <summary>How close a point must be to the mirror of another to count as its partner (and to the plane to lie on it).</summary>
    public const double Tolerance = 1e-5;

    /// <summary>A point mirrored across the plane square to an axis.</summary>
    public static Vec3 Mirror(Vec3 p, Axis axis) => p.With(axis, -p[axis]);

    /// <summary>Whether a point lies on the mirror plane.</summary>
    public static bool OnPlane(Vec3 p, Axis axis) => Math.Abs(p[axis]) <= Tolerance;

    private sealed record Cached(int Version, Axis Axis, int[] Points, Dictionary<int, int> Polygons);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, Cached> Cache = new();

    /// <summary>
    /// Each point's partner: the point nearest its mirror within <see cref="Tolerance"/>, itself for a point on the
    /// plane, or -1 when there's none. Cached until the mesh changes.
    /// </summary>
    public static int[] Pairs(Mesh mesh, Axis axis) => Of(mesh, axis).Points;

    /// <summary>Each polygon's partner (a polygon on its points' partners), for polygons that have one; on the plane, itself.</summary>
    public static IReadOnlyDictionary<int, int> PolygonPairs(Mesh mesh, Axis axis) => Of(mesh, axis).Polygons;

    private static Cached Of(Mesh mesh, Axis axis)
    {
        if (Cache.TryGetValue(mesh, out var c) && c.Version == mesh.Version && c.Axis == axis) return c;
        var points = FindPairs(mesh, axis);
        var byKey = new Dictionary<string, int>();
        for (int i = 0; i < mesh.Polygons.Count; i++) byKey.TryAdd(Key(mesh.Polygons[i].Points), i);
        var polygons = new Dictionary<int, int>();
        for (int i = 0; i < mesh.Polygons.Count; i++)
        {
            var p = mesh.Polygons[i].Points;
            if (p.Count == 0 || p.Any(x => x < 0 || x >= points.Length || points[x] < 0)) continue;
            if (byKey.TryGetValue(Key(p.Select(x => points[x])), out int j)) polygons[i] = j;
        }
        c = new Cached(mesh.Version, axis, points, polygons);
        Cache.AddOrUpdate(mesh, c);
        return c;
    }

    private static string Key(IEnumerable<int> points) => string.Join(",", points.Distinct().Order());

    private static int[] FindPairs(Mesh mesh, Axis axis)
    {
        var pts = mesh.Points;
        var partner = new int[pts.Count];
        double cell = Tolerance * 4;
        (long, long, long) Cell(Vec3 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
        var grid = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < pts.Count; i++)
        {
            var k = Cell(pts[i]);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = [];
            list.Add(i);
        }
        for (int i = 0; i < pts.Count; i++)
        {
            if (OnPlane(pts[i], axis)) { partner[i] = i; continue; }
            var m = Mirror(pts[i], axis);
            var (x, y, z) = Cell(m);
            int best = -1;
            double bestD = Tolerance * Tolerance;
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((x + dx, y + dy, z + dz), out var list))
                    foreach (int j in list)
                    {
                        if (j == i) continue;
                        double d = (pts[j] - m).LengthSquared;
                        if (d <= bestD) { bestD = d; best = j; }
                    }
            partner[i] = best;
        }
        return partner;
    }

    /// <summary>The points with their partners added.</summary>
    public static HashSet<int> Widen(Mesh mesh, Axis axis, IEnumerable<int> points)
    {
        var pairs = Pairs(mesh, axis);
        var set = new HashSet<int>();
        foreach (int i in points)
        {
            set.Add(i);
            if (i >= 0 && i < pairs.Length && pairs[i] >= 0) set.Add(pairs[i]);
        }
        return set;
    }

    /// <summary>The polygons with their partners added.</summary>
    public static HashSet<int> WidenPolygons(Mesh mesh, Axis axis, IEnumerable<int> polygons)
    {
        var pairs = PolygonPairs(mesh, axis);
        var set = new HashSet<int>();
        foreach (int i in polygons)
        {
            set.Add(i);
            if (pairs.TryGetValue(i, out int j)) set.Add(j);
        }
        return set;
    }

    /// <summary>The edges with their mirrored edges added (where the mesh has them).</summary>
    public static HashSet<Edge> WidenEdges(Mesh mesh, Axis axis, IEnumerable<Edge> edges)
    {
        var pairs = Pairs(mesh, axis);
        var map = mesh.EdgeMap();
        var set = new HashSet<Edge>();
        foreach (var e in edges)
        {
            set.Add(e);
            if (e.A < pairs.Length && e.B < pairs.Length && pairs[e.A] >= 0 && pairs[e.B] >= 0)
            {
                var m = Edge.Of(pairs[e.A], pairs[e.B]);
                if (map.ContainsKey(m)) set.Add(m);
            }
        }
        return set;
    }

    /// <summary>
    /// After a pick changed the selection from <paramref name="before"/>, picks the partners alike: what was added has
    /// its partners added, what was taken away has its partners taken away. Does nothing with symmetry off.
    /// </summary>
    public static void MirrorPick(Document doc, Selection before)
    {
        if (doc.Symmetry is not { } axis) return;
        var sel = doc.Selection;
        foreach (int l in doc.ForegroundLayers)
        {
            var mesh = doc.Layers[l].Mesh;
            switch (sel.Mode)
            {
                case SelectionMode.Points:
                {
                    var pairs = Pairs(mesh, axis);
                    var now = sel.PointsIn(l).ToList();
                    var was = before.PointsIn(l);
                    foreach (int i in now)
                        if (!was.Contains(i) && i < pairs.Length && pairs[i] >= 0) sel.SelectPoint(l, pairs[i]);
                    foreach (int i in was)
                        if (!sel.PointsIn(l).Contains(i) && i < pairs.Length && pairs[i] >= 0) sel.DeselectPoint(l, pairs[i]);
                    break;
                }
                case SelectionMode.Polygons:
                {
                    var pairs = PolygonPairs(mesh, axis);
                    var now = sel.PolygonsIn(l);
                    var was = before.PolygonsIn(l);
                    var added = now.Where(i => !was.Contains(i)).ToList();
                    var removed = was.Where(i => !now.Contains(i)).ToList();
                    foreach (int i in added) if (pairs.TryGetValue(i, out int j)) now.Add(j);
                    foreach (int i in removed) if (pairs.TryGetValue(i, out int j)) now.Remove(j);
                    break;
                }
                case SelectionMode.Edges:
                {
                    var now = sel.EdgesIn(l);
                    var was = before.EdgesIn(l);
                    var added = now.Where(e => !was.Contains(e)).ToList();
                    var removed = was.Where(e => !now.Contains(e)).ToList();
                    foreach (var e in WidenEdges(mesh, axis, added)) now.Add(e);
                    foreach (var e in WidenEdges(mesh, axis, removed)) now.Remove(e);
                    break;
                }
            }
        }
    }

    /// <summary>The axis named "x", "y" or "z" (any case), or null for "off", "none" or "".</summary>
    public static Axis? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "off" or "none" => null,
        "x" => Axis.X,
        "y" => Axis.Y,
        "z" => Axis.Z,
        _ => throw new ArgumentException($"Symmetry must be x, y, z or off, not \"{name}\"."),
    };
}
