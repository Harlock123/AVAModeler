namespace AVAModeler.Core;

/// <summary>
/// Geometry kept from AI agents' changes: points by where they are and polygons by the places of their points and their
/// surface, so a lock holds whatever ids or indices any session uses, and survives tools renumbering the mesh around it.
/// An agent's call that would move, remove or rebuild locked geometry fails and changes nothing. The person locks a
/// selection in the Modeler (Macros › Lock Selection from AI Agents); an agent locks its own work with lock. Locks are
/// view state: not undo steps, not saved in the object file.
/// </summary>
public sealed record AgentLock(string Name, string Owner, int Layer, IReadOnlySet<(long, long, long)> Points,
    IReadOnlyList<AgentLock.LockedPolygon> Polygons, string? Note = null)
{
    /// <summary>The owner's name for locks the person makes.</summary>
    public const string Person = "the person";

    /// <summary>A polygon: the places of its points (from the least, either way round) and its surface's name.</summary>
    public sealed record LockedPolygon(string Key, string Surface);

    /// <summary>A place to a micrometre, as locks compare points.</summary>
    public static (long, long, long) Place(Vec3 p) =>
        ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));

    /// <summary>A lock on some of a layer's points and polygons (a polygon's points are locked with it).</summary>
    public static AgentLock Of(Document doc, int layer, IEnumerable<int> points, IEnumerable<int> polygons, string name, string owner, string? note = null)
    {
        var mesh = doc.Layers[layer].Mesh;
        var places = new HashSet<(long, long, long)>();
        var locked = new List<LockedPolygon>();
        foreach (int p in points) places.Add(Place(mesh.Points[p]));
        foreach (int i in polygons)
        {
            var poly = mesh.Polygons[i];
            foreach (int p in poly.Points) places.Add(Place(mesh.Points[p]));
            locked.Add(new LockedPolygon(KeyOf(mesh, poly), SurfaceName(doc, poly.Surface)));
        }
        return new AgentLock(name, owner, layer, places, locked, note);
    }

    /// <summary>What a document has done to this lock's geometry, or null if it's all there as it was.</summary>
    public string? Problem(Document doc)
    {
        if (Layer >= doc.Layers.Count) return $"layer {Layer + 1} is gone";
        var mesh = doc.Layers[Layer].Mesh;
        var places = new HashSet<(long, long, long)>(mesh.Points.Select(Place));
        int missingPoints = Points.Count(p => !places.Contains(p));
        int missingPolygons = 0;
        if (Polygons.Count > 0)
        {
            var now = new Dictionary<string, int>();
            foreach (var poly in mesh.Polygons)
            {
                var key = KeyOf(mesh, poly) + "|" + SurfaceName(doc, poly.Surface);
                now[key] = now.GetValueOrDefault(key) + 1;
            }
            foreach (var lp in Polygons)
            {
                var key = lp.Key + "|" + lp.Surface;
                if (now.GetValueOrDefault(key) > 0) now[key]--;
                else missingPolygons++;
            }
        }
        if (missingPoints + missingPolygons == 0) return null;
        var parts = new List<string>();
        if (missingPoints > 0) parts.Add($"{missingPoints} of its {Points.Count} points moved or gone");
        if (missingPolygons > 0) parts.Add($"{missingPolygons} of its {Polygons.Count} polygons changed or gone");
        return string.Join(" and ", parts);
    }

    private static string SurfaceName(Document doc, int s) => s >= 0 && s < doc.Surfaces.Count ? doc.Surfaces[s].Name : $"#{s}";

    private static string KeyOf(Mesh mesh, Polygon poly)
    {
        var keys = poly.Points.Select(p => Place(mesh.Points[p])).Select(k => $"{k.Item1},{k.Item2},{k.Item3}").ToList();
        if (keys.Count == 0) return "";
        string Rotated(List<string> list)
        {
            int start = list.IndexOf(list.Min(StringComparer.Ordinal)!);
            return string.Join(';', Enumerable.Range(0, list.Count).Select(k => list[(start + k) % list.Count]));
        }
        var forward = Rotated(keys);
        keys.Reverse();
        var backward = Rotated(keys);
        return string.CompareOrdinal(forward, backward) <= 0 ? forward : backward;
    }
}
