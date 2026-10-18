namespace AVAModeler.Core;

/// <summary>
/// A polygon: a list of point indices and a surface. Coordinates are LightWave's: X to the right, Y up, Z away from
/// the viewer in the Front view (a left-handed space). As in classic Modeler, a polygon can have any number of points:
/// one (a single point, drawn as a dot), two (a line), three or more (a face, wound clockwise as seen from the front).
/// LWOB "detail" polygons, drawn on top of their parent, are kept so a file reads and writes back unchanged.
/// </summary>
public sealed class Polygon
{
    public List<int> Points;
    public int Surface;
    public List<Polygon>? Details;

    public Polygon(IEnumerable<int> points, int surface = 0)
    {
        Points = [.. points];
        Surface = surface;
    }

    public int Count => Points.Count;

    public Polygon Clone() => new(Points, Surface) { Details = Details?.Select(d => d.Clone()).ToList() };

    /// <summary>The edges as point-index pairs, in order (a 2-point polygon has one; a 1-point polygon none).</summary>
    public IEnumerable<(int A, int B)> Edges()
    {
        if (Points.Count == 2) { yield return (Points[0], Points[1]); yield break; }
        if (Points.Count < 3) yield break;
        for (int i = 0; i < Points.Count; i++) yield return (Points[i], Points[(i + 1) % Points.Count]);
    }

    public override string ToString() => $"[{string.Join(",", Points)}] surface {Surface}";
}

/// <summary>An undirected edge: the smaller point index first.</summary>
public readonly record struct Edge(int A, int B)
{
    public static Edge Of(int a, int b) => a < b ? new Edge(a, b) : new Edge(b, a);
    public int Other(int p) => p == A ? B : A;
}

/// <summary>
/// A mesh: an indexed list of points and a list of polygons that use them. The edge map and the
/// point-to-polygon map are built when something asks for them and thrown away when the mesh changes. Anything
/// that changes <see cref="Points"/> or <see cref="Polygons"/> directly must call <see cref="Touch"/>.
/// </summary>
public sealed class Mesh
{
    public List<Vec3> Points = [];
    public List<Polygon> Polygons = [];

    /// <summary>Goes up on every change; the lazy maps are rebuilt when it moves.</summary>
    public int Version { get; private set; }

    private int _mapsVersion = -1;
    private Dictionary<Edge, List<int>>? _edges;
    private List<int>[]? _pointPolygons;

    public void Touch() => Version++;

    public Mesh Clone() => new() { Points = [.. Points], Polygons = Polygons.Select(p => p.Clone()).ToList() };

    public bool IsEmpty => Points.Count == 0 && Polygons.Count == 0;

    public int AddPoint(Vec3 p)
    {
        Points.Add(p);
        Touch();
        return Points.Count - 1;
    }

    public int AddPolygon(IEnumerable<int> points, int surface = 0)
    {
        Polygons.Add(new Polygon(points, surface));
        Touch();
        return Polygons.Count - 1;
    }

    /// <summary>Adds points and a polygon through them in one go.</summary>
    public int AddFace(int surface, params Vec3[] points)
    {
        int first = Points.Count;
        Points.AddRange(points);
        return AddPolygon(Enumerable.Range(first, points.Length), surface);
    }

    public void Clear()
    {
        Points.Clear();
        Polygons.Clear();
        Touch();
    }

    /// <summary>Adds another mesh's points and polygons; returns the index its points start at here.</summary>
    public int Append(Mesh other, Func<int, int>? surfaceMap = null)
    {
        int offset = Points.Count;
        Points.AddRange(other.Points);
        foreach (var p in other.Polygons)
        {
            var c = p.Clone();
            for (int i = 0; i < c.Points.Count; i++) c.Points[i] += offset;
            if (c.Details != null)
                foreach (var d in c.Details)
                    for (int i = 0; i < d.Points.Count; i++) d.Points[i] += offset;
            if (surfaceMap != null) c.Surface = surfaceMap(c.Surface);
            Polygons.Add(c);
        }
        Touch();
        return offset;
    }

    public Box3 Bounds() => Box3.Of(Points);

    public Box3 Bounds(IEnumerable<int> points) => Box3.Of(points.Select(i => Points[i]));

    /// <summary>A polygon's normal by Newell's method (good for non-planar and concave n-gons); zero for 1- and 2-point polygons.</summary>
    public Vec3 Normal(Polygon p)
    {
        if (p.Count < 3) return Vec3.Zero;
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = Points[p.Points[i]];
            var b = Points[p.Points[(i + 1) % p.Count]];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }
        // LightWave's space is left-handed (Z runs away from you in the Front view) and its faces are wound
        // clockwise as seen from the side they face: Newell's sum then points out of that side as it stands.
        return new Vec3(x, y, z).Normalized();
    }

    public Vec3 Normal(int polygon) => Normal(Polygons[polygon]);

    public Vec3 Centroid(Polygon p)
    {
        if (p.Count == 0) return Vec3.Zero;
        var s = Vec3.Zero;
        foreach (int i in p.Points) s += Points[i];
        return s / p.Count;
    }

    /// <summary>The area of a face (0 for points and lines).</summary>
    public double Area(Polygon p)
    {
        if (p.Count < 3) return 0;
        var s = Vec3.Zero;
        for (int i = 0; i < p.Count; i++) s += Vec3.Cross(Points[p.Points[i]], Points[p.Points[(i + 1) % p.Count]]);
        return s.Length / 2;
    }

    /// <summary>How far a face is from flat: the largest distance of a point from the plane of the face.</summary>
    public double NonPlanarity(Polygon p)
    {
        if (p.Count < 4) return 0;
        var n = Normal(p);
        if (n.LengthSquared == 0) return 0;
        var plane = Plane.FromPointNormal(Centroid(p), n);
        return p.Points.Max(i => Math.Abs(plane.Distance(Points[i])));
    }

    private void BuildMaps()
    {
        if (_mapsVersion == Version && _edges != null) return;
        _edges = [];
        _pointPolygons = new List<int>[Points.Count];
        for (int i = 0; i < Points.Count; i++) _pointPolygons[i] = [];
        for (int pi = 0; pi < Polygons.Count; pi++)
        {
            var poly = Polygons[pi];
            foreach (int p in poly.Points.Distinct())
                if (p >= 0 && p < Points.Count) _pointPolygons[p].Add(pi);
            foreach (var (a, b) in poly.Edges())
            {
                if (a == b) continue;
                var e = Edge.Of(a, b);
                if (!_edges.TryGetValue(e, out var list)) _edges[e] = list = [];
                if (!list.Contains(pi)) list.Add(pi);
            }
        }
        _mapsVersion = Version;
    }

    /// <summary>Every edge, and the polygons that share it.</summary>
    public IReadOnlyDictionary<Edge, List<int>> EdgeMap()
    {
        BuildMaps();
        return _edges!;
    }

    /// <summary>The polygons that use a point.</summary>
    public IReadOnlyList<int> PolygonsOf(int point)
    {
        BuildMaps();
        return point >= 0 && point < _pointPolygons!.Length ? _pointPolygons[point] : [];
    }

    /// <summary>The points joined to a point by an edge.</summary>
    public IEnumerable<int> Neighbours(int point)
    {
        var seen = new HashSet<int>();
        foreach (int pi in PolygonsOf(point))
            foreach (var (a, b) in Polygons[pi].Edges())
            {
                if (a == point && b != point && seen.Add(b)) yield return b;
                if (b == point && a != point && seen.Add(a)) yield return a;
            }
    }

    /// <summary>Removes polygons (by index), leaving their points.</summary>
    public void RemovePolygons(IEnumerable<int> polygons)
    {
        var gone = new HashSet<int>(polygons);
        if (gone.Count == 0) return;
        Polygons = Polygons.Where((_, i) => !gone.Contains(i)).ToList();
        Touch();
    }

    /// <summary>
    /// Removes points (by index), with every polygon that uses one, and renumbers what's left. Returns the old
    /// index → new index map (-1 for removed).
    /// </summary>
    public int[] RemovePoints(IEnumerable<int> points)
    {
        var gone = new HashSet<int>(points);
        var map = new int[Points.Count];
        var kept = new List<Vec3>(Points.Count);
        for (int i = 0; i < Points.Count; i++)
        {
            if (gone.Contains(i)) { map[i] = -1; continue; }
            map[i] = kept.Count;
            kept.Add(Points[i]);
        }
        var polys = new List<Polygon>(Polygons.Count);
        foreach (var p in Polygons)
        {
            if (p.Points.Any(gone.Contains)) continue;
            Remap(p, map);
            polys.Add(p);
        }
        Points = kept;
        Polygons = polys;
        Touch();
        return map;
    }

    private static void Remap(Polygon p, int[] map)
    {
        for (int i = 0; i < p.Points.Count; i++) p.Points[i] = map[p.Points[i]];
        if (p.Details != null)
        {
            p.Details.RemoveAll(d => d.Points.Any(x => map[x] < 0));
            foreach (var d in p.Details) Remap(d, map);
        }
    }

    /// <summary>Removes points no polygon uses. Returns how many went.</summary>
    public int RemoveUnusedPoints()
    {
        var used = new bool[Points.Count];
        foreach (var p in Polygons)
        {
            foreach (int i in p.Points) used[i] = true;
            if (p.Details != null) foreach (var d in p.Details) foreach (int i in d.Points) used[i] = true;
        }
        var unused = Enumerable.Range(0, Points.Count).Where(i => !used[i]).ToList();
        if (unused.Count > 0) RemovePoints(unused);
        return unused.Count;
    }

    /// <summary>
    /// Merges points that lie within a distance of each other (all of them, or only those given), and drops
    /// repeated points from the polygons that used them; a polygon left with fewer distinct points than it needs
    /// becomes the smaller polygon it now is. Returns how many points went.
    /// </summary>
    public int MergePoints(double tolerance = 1e-9, IEnumerable<int>? only = null)
    {
        var candidates = only?.Distinct().OrderBy(i => i).ToList() ?? Enumerable.Range(0, Points.Count).ToList();
        if (candidates.Count < 2) return 0;
        // Bucket by a grid of the tolerance so this is close to linear.
        double cell = Math.Max(tolerance, 1e-12) * 2;
        var grid = new Dictionary<(long, long, long), List<int>>();
        var target = Enumerable.Range(0, Points.Count).ToArray();
        int merged = 0;
        foreach (int i in candidates)
        {
            var p = Points[i];
            var key = ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
            int found = -1;
            for (long dx = -1; dx <= 1 && found < 0; dx++)
            for (long dy = -1; dy <= 1 && found < 0; dy++)
            for (long dz = -1; dz <= 1 && found < 0; dz++)
                if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list))
                    foreach (int j in list)
                        if (Vec3.Near(Points[j], p, tolerance)) { found = j; break; }
            if (found >= 0) { target[i] = found; merged++; continue; }
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
            bucket.Add(i);
        }
        if (merged == 0) return 0;
        foreach (var poly in Polygons) Collapse(poly, target);
        RemoveUnusedPoints();
        Touch();
        return merged;
    }

    private static void Collapse(Polygon poly, int[] target)
    {
        var pts = new List<int>(poly.Points.Count);
        foreach (int i in poly.Points)
        {
            int t = target[i];
            if (pts.Count > 0 && pts[^1] == t) continue;
            pts.Add(t);
        }
        while (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
        poly.Points = pts;
        if (poly.Details != null) foreach (var d in poly.Details) Collapse(d, target);
    }

    /// <summary>Reverses a polygon's winding, so it faces the other way.</summary>
    public void Flip(int polygon)
    {
        Polygons[polygon].Points.Reverse();
        Touch();
    }

    /// <summary>Applies a transform to some points (all, if none are given).</summary>
    public void Transform(Mat4 m, IEnumerable<int>? points = null)
    {
        if (points == null)
            for (int i = 0; i < Points.Count; i++) Points[i] = m.TransformPoint(Points[i]);
        else
            foreach (int i in points.Distinct()) Points[i] = m.TransformPoint(Points[i]);
        Touch();
    }

    /// <summary>The points used by some polygons.</summary>
    public HashSet<int> PointsOf(IEnumerable<int> polygons)
    {
        var s = new HashSet<int>();
        foreach (int pi in polygons) s.UnionWith(Polygons[pi].Points);
        return s;
    }

    /// <summary>A count of polygons by how many points they have (1, 2, 3, 4, and more).</summary>
    public (int Points, int Lines, int Triangles, int Quads, int NGons) PolygonTypes(IEnumerable<int>? polygons = null)
    {
        int one = 0, two = 0, three = 0, four = 0, more = 0;
        foreach (var p in polygons?.Select(i => Polygons[i]) ?? Polygons)
            switch (p.Count)
            {
                case 1: one++; break;
                case 2: two++; break;
                case 3: three++; break;
                case 4: four++; break;
                default: if (p.Count > 4) more++; break;
            }
        return (one, two, three, four, more);
    }
}
