namespace AVAModeler.Core;

/// <summary>
/// A named UV map: texture coordinates for some of a mesh's points (as an LWO2 VMAP of type TXUV keeps them). U runs
/// across an image from its left edge (0) to its right (1), V up it from its bottom (0) to its top (1). Points not in
/// the map have no UVs. Where a polygon's corner needs other coordinates than its point (along a seam), the polygon
/// keeps them itself (<see cref="Polygon.CornerUVs"/>).
/// </summary>
public sealed class UVMap(string name)
{
    public string Name = name;
    /// <summary>Each mapped point's coordinates, by point index.</summary>
    public Dictionary<int, Vec2> Points = [];

    public UVMap Clone() => new(Name) { Points = new Dictionary<int, Vec2>(Points) };

    public override string ToString() => $"{Name} ({Points.Count} points)";
}

public sealed partial class Mesh
{
    /// <summary>
    /// How close two UVs must be to count as the same, when deciding whether a corner needs coordinates of its own.
    /// </summary>
    public const double UVTolerance = 1e-9;

    /// <summary>The mesh's UV maps. Edits that renumber points (removing, merging, appending) keep them in step.</summary>
    public List<UVMap> UVMaps = [];

    /// <summary>The UV map of a name, or null.</summary>
    public UVMap? FindUVMap(string name) => UVMaps.Find(m => m.Name == name);

    /// <summary>The UV map of a name, made (empty) if there isn't one.</summary>
    public UVMap AddUVMap(string name)
    {
        var map = FindUVMap(name);
        if (map != null) return map;
        map = new UVMap(name);
        UVMaps.Add(map);
        Touch();
        return map;
    }

    /// <summary>Removes a UV map and every corner's own coordinates in it.</summary>
    public bool RemoveUVMap(string name)
    {
        if (UVMaps.RemoveAll(m => m.Name == name) == 0) return false;
        foreach (var p in Polygons) p.CornerUVs?.Remove(name);
        Touch();
        return true;
    }

    /// <summary>
    /// A polygon corner's UVs in a map: the corner's own if it has them, else its point's. False when neither has any
    /// (or the map doesn't exist).
    /// </summary>
    public bool TryGetUV(string map, Polygon polygon, int point, out Vec2 uv)
    {
        if (polygon.CornerUVs != null && polygon.CornerUVs.TryGetValue(map, out var corners) && corners.TryGetValue(point, out uv)) return true;
        if (FindUVMap(map) is { } m && m.Points.TryGetValue(point, out uv)) return true;
        uv = default;
        return false;
    }

    /// <summary>Every corner's UVs in a polygon, in its point order (null for a corner without any).</summary>
    public Vec2?[] CornerUVsOf(string map, Polygon polygon)
    {
        var uvs = new Vec2?[polygon.Count];
        for (int i = 0; i < uvs.Length; i++)
            uvs[i] = TryGetUV(map, polygon, polygon.Points[i], out var uv) ? uv : null;
        return uvs;
    }

    /// <summary>Whether every corner of a polygon has UVs in a map.</summary>
    public bool IsMapped(string map, Polygon polygon)
    {
        foreach (int p in polygon.Points)
            if (!TryGetUV(map, polygon, p, out _)) return false;
        return polygon.Count > 0;
    }

    /// <summary>
    /// Gives a polygon's corner UVs in a map (made if new), storing them the leanest way: on the point if it has none
    /// yet, nowhere extra if the point's already match, otherwise on the corner, as a seam.
    /// </summary>
    public void SetCornerUV(string map, Polygon polygon, int point, Vec2 uv)
    {
        var m = AddUVMap(map);
        polygon.CornerUVs?.GetValueOrDefault(map)?.Remove(point);
        if (!m.Points.TryGetValue(point, out var at)) m.Points[point] = uv;
        else if (!Near(at, uv))
        {
            polygon.CornerUVs ??= [];
            if (!polygon.CornerUVs.TryGetValue(map, out var corners)) polygon.CornerUVs[map] = corners = [];
            corners[point] = uv;
        }
        if (polygon.CornerUVs is { } c && c.TryGetValue(map, out var left) && left.Count == 0)
        {
            c.Remove(map);
            if (c.Count == 0) polygon.CornerUVs = null;
        }
        Touch();
    }

    /// <summary>Sets a point's UVs in a map (made if new); corners with their own UVs keep them.</summary>
    public void SetPointUV(string map, int point, Vec2 uv)
    {
        AddUVMap(map).Points[point] = uv;
        Touch();
    }

    /// <summary>
    /// Gives a new point the UVs of another in every map (where it has any): for tools that copy points. Corners with
    /// their own UVs are a polygon's business (see <see cref="InheritUVs"/>).
    /// </summary>
    public void CopyPointUVs(int from, int to)
    {
        foreach (var m in UVMaps)
            if (m.Points.TryGetValue(from, out var uv)) m.Points[to] = uv;
    }

    /// <summary>
    /// Gives a polygon made from another the UVs of the corners it came from, in every map: each of its points takes
    /// the UVs <paramref name="source"/> has at the point <paramref name="origin"/> names (a negative origin leaves the
    /// corner alone). For tools that build polygons out of existing ones.
    /// </summary>
    public void InheritUVs(Polygon target, Polygon source, Func<int, int> origin)
    {
        foreach (var m in UVMaps.ToList())
            foreach (int q in target.Points.Distinct().ToList())
            {
                int s = origin(q);
                if (s >= 0 && TryGetUV(m.Name, source, s, out var uv)) SetCornerUV(m.Name, target, q, uv);
            }
    }

    /// <summary>
    /// When a polygon's corner is about to change from one point to another (as merging points does), keeps the UVs
    /// it had: if they differ from the new point's, the corner gets them as its own.
    /// </summary>
    public void KeepCornerUVs(Polygon polygon, int oldPoint, int newPoint)
    {
        if (UVMaps.Count == 0) return;
        foreach (var m in UVMaps)
        {
            if (!TryGetUV(m.Name, polygon, oldPoint, out var uv)) continue;
            polygon.CornerUVs?.GetValueOrDefault(m.Name)?.Remove(oldPoint);
            SetCornerUV(m.Name, polygon, newPoint, uv);
        }
    }

    /// <summary>The bounds of a map's coordinates, as a box with z = 0 (empty if nothing is mapped).</summary>
    public Box3 UVBounds(string map)
    {
        var b = Box3.Empty;
        if (FindUVMap(map) is { } m)
            foreach (var uv in m.Points.Values) b = b.Include(new Vec3(uv.X, uv.Y, 0));
        foreach (var p in Polygons)
            if (p.CornerUVs != null && p.CornerUVs.TryGetValue(map, out var corners))
                foreach (var uv in corners.Values) b = b.Include(new Vec3(uv.X, uv.Y, 0));
        return b;
    }

    private static bool Near(Vec2 a, Vec2 b) => Math.Abs(a.X - b.X) <= UVTolerance && Math.Abs(a.Y - b.Y) <= UVTolerance;

    /// <summary>Renumbers the maps' points after points were removed (old index → new, -1 for gone).</summary>
    private void RemapUVs(int[] map)
    {
        foreach (var m in UVMaps)
        {
            var moved = new Dictionary<int, Vec2>(m.Points.Count);
            foreach (var (i, uv) in m.Points)
                if (i >= 0 && i < map.Length && map[i] >= 0) moved[map[i]] = uv;
            m.Points = moved;
        }
    }

    /// <summary>Renumbers a polygon's corner UVs (old point index → new, -1 for gone).</summary>
    private static void RemapCorners(Polygon p, int[] map)
    {
        if (p.CornerUVs == null) return;
        foreach (var name in p.CornerUVs.Keys.ToList())
        {
            var moved = new Dictionary<int, Vec2>();
            foreach (var (i, uv) in p.CornerUVs[name])
                if (i >= 0 && i < map.Length && map[i] >= 0) moved[map[i]] = uv;
            if (moved.Count > 0) p.CornerUVs[name] = moved;
            else p.CornerUVs.Remove(name);
        }
        if (p.CornerUVs.Count == 0) p.CornerUVs = null;
    }
}
