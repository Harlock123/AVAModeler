using System.Text.Json.Nodes;
using AVAModeler.Core;

namespace AVAModeler.Mcp;

/// <summary>
/// Ids for points and polygons that stay with them while tools renumber the mesh. Indices shift whenever a tool deletes,
/// merges or rebuilds geometry, so an agent holding "polygon 812" from one call may be pointing at another polygon by
/// the next. Each point and polygon of each layer gets an id (a whole number from 1, never used again in that layer) the
/// first time the server sees it, and keeps it:
/// <list type="bullet">
/// <item>while the layer's polygons keep their points (a move, a sculpt, a surface change): by index;</item>
/// <item>otherwise, a point while it stays where it was (to a micrometre), and a polygon while it joins the same points
/// (either way round, so Flip keeps it);</item>
/// <item>and geometry that comes back exactly as it was (undo, restore_checkpoint) gets its old id back.</item>
/// </list>
/// A point a tool moves while it also renumbers the mesh (a Bevel's cap, a Smooth Subdivide) is new as far as ids go.
/// The ids are the server's, not the object's: they aren't saved, and each MCP session has its own.
/// </summary>
internal sealed class GeometryIds
{
    /// <summary>How many removed points and polygons a layer remembers, to give their ids back if they return.</summary>
    private const int Remembered = 200_000;

    private Document? _doc;
    private int _version = int.MinValue;
    private List<LayerIds> _layers = [];

    /// <summary>One layer as last seen: its points, polygons and their ids.</summary>
    internal sealed class LayerIds
    {
        public Vec3[] Points = [];
        public int[] PointIds = [];
        public int[][] Polygons = [];
        public int[] Surfaces = [];
        public int[] PolygonIds = [];
        public int NextPoint = 1, NextPolygon = 1;
        public Dictionary<(long, long, long), int> GonePoints = [];
        public Dictionary<string, int> GonePolygons = [];
        public Queue<(long, long, long)> GonePointOrder = new();
        public Queue<string> GonePolygonOrder = new();

        public LayerIds Clone() => new()
        {
            Points = Points, PointIds = PointIds, Polygons = Polygons, Surfaces = Surfaces, PolygonIds = PolygonIds,
            NextPoint = NextPoint, NextPolygon = NextPolygon,
            GonePoints = new(GonePoints), GonePolygons = new(GonePolygons),
            GonePointOrder = new(GonePointOrder), GonePolygonOrder = new(GonePolygonOrder),
        };

        /// <summary>The point ids of a polygon (from the stored point indices).</summary>
        public int[] PolygonPointIds(int polygon) => Array.ConvertAll(Polygons[polygon], i => PointIds[i]);
    }

    /// <summary>The ids as they stand (layers' arrays aren't changed once made, so a copy shares them).</summary>
    internal sealed class State(List<LayerIds> layers)
    {
        public IReadOnlyList<LayerIds> Layers { get; } = layers;
    }

    /// <summary>What changed in one layer since the last look.</summary>
    internal sealed record LayerChange(int Layer, List<int> PointsAdded, int PointsRemoved, int PointsMoved,
        List<int> PolygonsAdded, int PolygonsRemoved)
    {
        public bool Any => PointsAdded.Count + PointsRemoved + PointsMoved + PolygonsAdded.Count + PolygonsRemoved > 0;
    }

    /// <summary>A copy of the ids now, for a checkpoint.</summary>
    public State Save(Document doc)
    {
        Sync(doc);
        return new State(_layers.Select(l => l.Clone()).ToList());
    }

    /// <summary>
    /// Puts the ids back as a checkpoint had them, after its geometry was put back exactly (the ids' counters keep
    /// counting up, so ids given out since aren't used again).
    /// </summary>
    public void Load(Document doc, State state)
    {
        var next = _layers.Select(l => (l.NextPoint, l.NextPolygon)).ToList();
        _layers = state.Layers.Select(l => l.Clone()).ToList();
        for (int i = 0; i < _layers.Count && i < next.Count; i++)
        {
            _layers[i].NextPoint = Math.Max(_layers[i].NextPoint, next[i].NextPoint);
            _layers[i].NextPolygon = Math.Max(_layers[i].NextPolygon, next[i].NextPolygon);
        }
        _doc = doc;
        _version = int.MinValue;   // look again: the restore was itself a change
        Sync(doc);
    }

    /// <summary>
    /// Brings the ids up to date with the document (cheap when it hasn't changed since the last look) and says what
    /// changed in each layer that did.
    /// </summary>
    public List<LayerChange> Sync(Document doc)
    {
        if (ReferenceEquals(doc, _doc) && doc.Version == _version) return [];
        if (!ReferenceEquals(doc, _doc))
        {
            // Another object (new or open): its geometry is all new, but counters go on so old ids aren't reused.
            foreach (var l in _layers)
            {
                (l.Points, l.PointIds, l.Polygons, l.Surfaces, l.PolygonIds) = ([], [], [], [], []);
                l.GonePoints.Clear(); l.GonePolygons.Clear(); l.GonePointOrder.Clear(); l.GonePolygonOrder.Clear();
            }
            _doc = doc;
            // An object saved with its ids (keep_ids) gives each layer's points and polygons the ids they had.
            while (_layers.Count < doc.Layers.Count) _layers.Add(new LayerIds());
            for (int i = 0; i < doc.Layers.Count; i++)
                if (doc.Layers[i].Ids is { } kept && kept.Fits(doc.Layers[i].Mesh))
                {
                    var mesh = doc.Layers[i].Mesh;
                    var l = _layers[i];
                    l.Points = [.. mesh.Points];
                    l.PointIds = [.. kept.Points];
                    l.Polygons = [.. mesh.Polygons.Select(p => p.Points.ToArray())];
                    l.Surfaces = [.. mesh.Polygons.Select(p => p.Surface)];
                    l.PolygonIds = [.. kept.Polygons];
                    l.NextPoint = Math.Max(l.NextPoint, kept.Points.DefaultIfEmpty(0).Max() + 1);
                    l.NextPolygon = Math.Max(l.NextPolygon, kept.Polygons.DefaultIfEmpty(0).Max() + 1);
                }
        }
        _version = doc.Version;
        var changes = new List<LayerChange>();
        while (_layers.Count < doc.Layers.Count) _layers.Add(new LayerIds());
        for (int i = 0; i < doc.Layers.Count; i++)
            if (Reconcile(_layers[i], doc.Layers[i].Mesh, i + 1) is { Any: true } change)
                changes.Add(change);
        return changes;
    }

    /// <summary>Puts the ids as they stand into the document's layers, to be saved with it (keep_ids).</summary>
    public void Keep(Document doc)
    {
        Sync(doc);
        for (int i = 0; i < doc.Layers.Count && i < _layers.Count; i++)
            doc.Layers[i].Ids = doc.Layers[i].Mesh.IsEmpty ? null : new GeometryIdTable([.. _layers[i].PointIds], [.. _layers[i].PolygonIds]);
    }

    /// <summary>The layer's ids as they stand (Sync first).</summary>
    public LayerIds Of(int layer) => layer < _layers.Count ? _layers[layer] : new LayerIds();

    /// <summary>The current index of each point id (null for one that's gone).</summary>
    public static Dictionary<int, int> IndexOf(int[] ids)
    {
        var map = new Dictionary<int, int>(ids.Length);
        for (int i = 0; i < ids.Length; i++) map[ids[i]] = i;
        return map;
    }

    private static (long, long, long) Key(Vec3 p) =>
        ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));

    /// <summary>A polygon's key from its point ids: started at the smallest, so where it starts doesn't matter.</summary>
    private static string Key(int[] ids, bool reversed = false)
    {
        if (ids.Length == 0) return "";
        var seq = reversed ? ids.Reverse().ToArray() : ids;
        int start = Array.IndexOf(seq, seq.Min());
        return string.Join(',', Enumerable.Range(0, seq.Length).Select(k => seq[(start + k) % seq.Length]));
    }

    private static LayerChange? Reconcile(LayerIds st, Mesh mesh, int layerNumber)
    {
        int n = mesh.Points.Count, m = mesh.Polygons.Count;
        var points = mesh.Points.ToArray();
        var polygons = new int[m][];
        var surfaces = new int[m];
        for (int j = 0; j < m; j++) { polygons[j] = [.. mesh.Polygons[j].Points]; surfaces[j] = mesh.Polygons[j].Surface; }

        bool sameTopology = n == st.Points.Length && m == st.Polygons.Length;
        for (int j = 0; sameTopology && j < m; j++) sameTopology = polygons[j].AsSpan().SequenceEqual(st.Polygons[j]);

        if (sameTopology)
        {
            int moved = 0;
            for (int j = 0; j < n; j++) if (points[j] != st.Points[j]) moved++;
            bool resurfaced = !surfaces.AsSpan().SequenceEqual(st.Surfaces);
            if (moved == 0 && !resurfaced) return null;
            (st.Points, st.Polygons, st.Surfaces) = (points, polygons, surfaces);
            return new LayerChange(layerNumber, [], 0, moved, [], 0);
        }

        // ── Points: same index and place, then same place, then (count unchanged) same index, then remembered ──
        int oldN = st.Points.Length;
        var byPlace = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < oldN; i++)
        {
            var k = Key(st.Points[i]);
            if (!byPlace.TryGetValue(k, out var list)) byPlace[k] = list = [];
            list.Add(i);
        }
        var claimed = new bool[oldN];
        var pointIds = new int[n];
        var keys = new (long, long, long)[n];
        for (int j = 0; j < n; j++)
        {
            keys[j] = Key(points[j]);
            if (j < oldN && !claimed[j] && Key(st.Points[j]) == keys[j]) { claimed[j] = true; pointIds[j] = st.PointIds[j]; }
        }
        for (int j = 0; j < n; j++)
            if (pointIds[j] == 0 && byPlace.TryGetValue(keys[j], out var list))
                foreach (int i in list)
                    if (!claimed[i]) { claimed[i] = true; pointIds[j] = st.PointIds[i]; break; }
        if (n == oldN)
            for (int j = 0; j < n; j++)
                if (pointIds[j] == 0 && !claimed[j]) { claimed[j] = true; pointIds[j] = st.PointIds[j]; }
        var added = new List<int>();
        for (int j = 0; j < n; j++)
        {
            if (pointIds[j] != 0) continue;
            if (st.GonePoints.Remove(keys[j], out int back)) pointIds[j] = back;
            else { pointIds[j] = st.NextPoint++; added.Add(pointIds[j]); }
        }
        int removed = 0;
        for (int i = 0; i < oldN; i++)
            if (!claimed[i])
            {
                removed++;
                Remember(st.GonePoints, st.GonePointOrder, Key(st.Points[i]), st.PointIds[i]);
            }
        int moved2 = 0;
        if (n == oldN) for (int j = 0; j < n; j++) if (pointIds[j] == st.PointIds[j] && points[j] != st.Points[j]) moved2++;

        // ── Polygons: by the point ids they join (either way round), then remembered ──
        int oldM = st.Polygons.Length;
        var oldKeys = new Dictionary<string, List<int>>();
        for (int i = 0; i < oldM; i++)
        {
            var k = Key(st.PolygonPointIds(i));
            if (!oldKeys.TryGetValue(k, out var list)) oldKeys[k] = list = [];
            list.Add(i);
        }
        var taken = new bool[oldM];
        var polygonIds = new int[m];
        var newKeys = new string[m];
        for (int j = 0; j < m; j++)
        {
            var ids = Array.ConvertAll(polygons[j], i => pointIds[i]);
            newKeys[j] = Key(ids);
            int found = Take(oldKeys, newKeys[j], taken, j);
            if (found < 0) found = Take(oldKeys, Key(ids, reversed: true), taken, j);
            if (found >= 0) polygonIds[j] = st.PolygonIds[found];
        }
        var polygonsAdded = new List<int>();
        for (int j = 0; j < m; j++)
        {
            if (polygonIds[j] != 0) continue;
            if (st.GonePolygons.Remove(newKeys[j], out int back)) polygonIds[j] = back;
            else { polygonIds[j] = st.NextPolygon++; polygonsAdded.Add(polygonIds[j]); }
        }
        int polygonsRemoved = 0;
        for (int i = 0; i < oldM; i++)
            if (!taken[i])
            {
                polygonsRemoved++;
                Remember(st.GonePolygons, st.GonePolygonOrder, Key(st.PolygonPointIds(i)), st.PolygonIds[i]);
            }

        (st.Points, st.PointIds, st.Polygons, st.Surfaces, st.PolygonIds) = (points, pointIds, polygons, surfaces, polygonIds);
        return new LayerChange(layerNumber, added, removed, moved2, polygonsAdded, polygonsRemoved);
    }

    /// <summary>An unclaimed old polygon with this key, preferring the one at the same index; -1 if none.</summary>
    private static int Take(Dictionary<string, List<int>> keys, string key, bool[] taken, int index)
    {
        if (!keys.TryGetValue(key, out var list)) return -1;
        int pick = -1;
        foreach (int i in list)
            if (!taken[i] && (pick < 0 || i == index)) { pick = i; if (i == index) break; }
        if (pick >= 0) taken[pick] = true;
        return pick;
    }

    private static void Remember<TKey>(Dictionary<TKey, int> gone, Queue<TKey> order, TKey key, int id) where TKey : notnull
    {
        gone[key] = id;
        order.Enqueue(key);
        while (gone.Count > Remembered && order.TryDequeue(out var old)) gone.Remove(old);
    }

    /// <summary>A call's changes for its answer: ids of what it made (the first 100 of each), counts of the rest.</summary>
    public static JsonObject? Describe(IReadOnlyList<LayerChange> changes)
    {
        if (changes.Count == 0) return null;
        var json = new JsonObject();
        foreach (var c in changes)
        {
            var layer = new JsonObject();
            if (c.PointsAdded.Count > 0)
            {
                layer["new_point_ids"] = new JsonArray([.. c.PointsAdded.Take(100).Select(i => (JsonNode)i)]);
                if (c.PointsAdded.Count > 100) layer["new_points"] = c.PointsAdded.Count;
            }
            if (c.PointsRemoved > 0) layer["points_removed"] = c.PointsRemoved;
            if (c.PointsMoved > 0) layer["points_moved"] = c.PointsMoved;
            if (c.PolygonsAdded.Count > 0)
            {
                layer["new_polygon_ids"] = new JsonArray([.. c.PolygonsAdded.Take(100).Select(i => (JsonNode)i)]);
                if (c.PolygonsAdded.Count > 100) layer["new_polygons"] = c.PolygonsAdded.Count;
            }
            if (c.PolygonsRemoved > 0) layer["polygons_removed"] = c.PolygonsRemoved;
            json[$"layer {c.Layer}"] = layer;
        }
        return json;
    }
}
