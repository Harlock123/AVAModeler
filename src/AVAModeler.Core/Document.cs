namespace AVAModeler.Core;

/// <summary>An 8-bit colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Grey = new(200, 200, 200);
    public Vec3 ToVec() => new(R / 255.0, G / 255.0, B / 255.0);
    public static Rgb FromVec(Vec3 c) => new(ToByte(c.X), ToByte(c.Y), ToByte(c.Z));
    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>A sub-chunk of a file kept as it was read, so a file can be written back byte for byte.</summary>
public sealed record RawChunk(string Id, byte[] Data);

/// <summary>
/// A surface: a named group of polygons and how they look. Every polygon names one by index into the document's
/// surface table.
/// </summary>
public sealed class Surface
{
    public string Name;
    public Rgb Color = Rgb.Grey;
    public double Diffuse = 1, Specular, Luminosity, Reflection, Transparency;
    /// <summary>Specular glossiness as LWOB stores it: 16 low, 64 medium, 256 high, 1024 very high.</summary>
    public double Glossiness = 64;
    /// <summary>Faces meeting at less than this angle (degrees) are shaded as one smooth surface.</summary>
    public double SmoothingAngle = 89.5;
    public bool Smoothing, DoubleSided;
    /// <summary>The surface's sub-chunks as read from a file, for writing back unchanged; cleared on any edit.</summary>
    public List<RawChunk>? Raw;
    /// <summary>The values <see cref="Raw"/> was read with: if they still match, the raw chunks are written back.</summary>
    public string? RawSignature;

    public Surface(string name) => Name = name;

    public Surface Clone()
    {
        var s = (Surface)MemberwiseClone();
        s.Raw = Raw == null ? null : [.. Raw];
        return s;
    }

    /// <summary>The surface's settings in one string, to tell whether it has changed since it was read.</summary>
    public string Signature() => FormattableString.Invariant(
        $"{Name}|{Color}|{Diffuse:R}|{Specular:R}|{Luminosity:R}|{Reflection:R}|{Transparency:R}|{Glossiness:R}|{SmoothingAngle:R}|{Smoothing}|{DoubleSided}");
}

/// <summary>A layer's part in the work: off, in the foreground (being edited), or in the background (shown dimmed, the operand for booleans).</summary>
public enum LayerState { Off, Foreground, Background }

/// <summary>A layer: a mesh of its own, and whether it's in the foreground, the background, or neither.</summary>
public sealed class Layer
{
    public string Name = "";
    public Mesh Mesh = new();
    public LayerState State;
    /// <summary>The layer's pivot (LWO2 files keep one).</summary>
    public Vec3 Pivot;

    public Layer Clone() => new() { Name = Name, Mesh = Mesh.Clone(), State = State, Pivot = Pivot };
}

/// <summary>What clicks select, and what tools act on.</summary>
public enum SelectionMode { Points, Polygons, Edges, Volume }

/// <summary>
/// The selection, per foreground layer: points, polygons or edges by index, or a volume (a box) in Volume mode. As in
/// classic Modeler, a tool given an empty selection works on everything in the foreground.
/// </summary>
public sealed class Selection
{
    public SelectionMode Mode = SelectionMode.Points;
    public readonly Dictionary<int, HashSet<int>> Points = [];
    public readonly Dictionary<int, HashSet<int>> Polygons = [];
    public readonly Dictionary<int, HashSet<Edge>> Edges = [];
    /// <summary>The order points were selected in, per layer (Make Polygon follows it).</summary>
    public readonly Dictionary<int, List<int>> PointOrder = [];
    /// <summary>The volume in Volume mode: what's inside (or, when <see cref="VolumeExclusive"/>, what's wholly inside).</summary>
    public Box3 Volume = Box3.Empty;
    public bool VolumeExclusive;

    public Selection Clone()
    {
        var s = new Selection { Mode = Mode, Volume = Volume, VolumeExclusive = VolumeExclusive };
        foreach (var (k, v) in Points) s.Points[k] = [.. v];
        foreach (var (k, v) in Polygons) s.Polygons[k] = [.. v];
        foreach (var (k, v) in Edges) s.Edges[k] = [.. v];
        foreach (var (k, v) in PointOrder) s.PointOrder[k] = [.. v];
        return s;
    }

    /// <summary>Selects a point, remembering the order.</summary>
    public void SelectPoint(int layer, int point)
    {
        if (!PointsIn(layer).Add(point)) return;
        if (!PointOrder.TryGetValue(layer, out var order)) PointOrder[layer] = order = [];
        order.Add(point);
    }

    public void DeselectPoint(int layer, int point)
    {
        PointsIn(layer).Remove(point);
        if (PointOrder.TryGetValue(layer, out var order)) order.Remove(point);
    }

    /// <summary>A layer's selected points in the order they were picked (any picked other ways come last).</summary>
    public List<int> OrderedPoints(int layer)
    {
        var set = PointsIn(layer);
        var order = PointOrder.TryGetValue(layer, out var o) ? o.Where(set.Contains).ToList() : [];
        order.AddRange(set.Where(i => !order.Contains(i)).Order());
        return order;
    }

    public HashSet<int> PointsIn(int layer) => Points.TryGetValue(layer, out var s) ? s : Points[layer] = [];
    public HashSet<int> PolygonsIn(int layer) => Polygons.TryGetValue(layer, out var s) ? s : Polygons[layer] = [];
    public HashSet<Edge> EdgesIn(int layer) => Edges.TryGetValue(layer, out var s) ? s : Edges[layer] = [];

    /// <summary>Whether anything is selected in the current mode.</summary>
    public bool Any => Mode switch
    {
        SelectionMode.Points => Points.Values.Any(s => s.Count > 0),
        SelectionMode.Polygons => Polygons.Values.Any(s => s.Count > 0),
        SelectionMode.Edges => Edges.Values.Any(s => s.Count > 0),
        _ => !Volume.IsEmpty,
    };

    /// <summary>Clears the current mode's selection (or every mode's).</summary>
    public void Clear(bool allModes = false)
    {
        if (allModes || Mode == SelectionMode.Points) { Points.Clear(); PointOrder.Clear(); }
        if (allModes || Mode == SelectionMode.Polygons) Polygons.Clear();
        if (allModes || Mode == SelectionMode.Edges) Edges.Clear();
        if (allModes || Mode == SelectionMode.Volume) Volume = Box3.Empty;
    }

    /// <summary>Drops indices that no longer exist in a layer's mesh (after an edit removed some).</summary>
    public void Prune(int layer, Mesh mesh)
    {
        if (Points.TryGetValue(layer, out var p)) p.RemoveWhere(i => i < 0 || i >= mesh.Points.Count);
        if (PointOrder.TryGetValue(layer, out var o)) o.RemoveAll(i => i < 0 || i >= mesh.Points.Count);
        if (Polygons.TryGetValue(layer, out var q)) q.RemoveWhere(i => i < 0 || i >= mesh.Polygons.Count);
        if (Edges.TryGetValue(layer, out var e))
        {
            var edges = mesh.EdgeMap();
            e.RemoveWhere(x => !edges.ContainsKey(x));
        }
    }

    /// <summary>Whether a polygon is in the volume: any of its points (inclusive) or all of them (exclusive).</summary>
    private bool InVolume(Mesh mesh, Polygon p) => VolumeExclusive
        ? p.Points.All(i => Volume.Contains(mesh.Points[i]))
        : p.Points.Any(i => Volume.Contains(mesh.Points[i]));

    /// <summary>
    /// The points a tool should move in a layer: those selected in Points mode; those of the selected polygons or
    /// edges; those in the volume; or, with nothing selected, every point.
    /// </summary>
    public IReadOnlyCollection<int> ActivePoints(int layer, Mesh mesh)
    {
        HashSet<int>? chosen = Mode switch
        {
            SelectionMode.Points => Points.TryGetValue(layer, out var s) && s.Count > 0 ? s : null,
            SelectionMode.Polygons => Polygons.TryGetValue(layer, out var s) && s.Count > 0 ? mesh.PointsOf(s) : null,
            SelectionMode.Edges => Edges.TryGetValue(layer, out var s) && s.Count > 0 ? [.. s.SelectMany(e => new[] { e.A, e.B })] : null,
            _ => Volume.IsEmpty ? null : VolumeExclusive
                ? mesh.PointsOf(ActivePolygons(layer, mesh))
                : [.. Enumerable.Range(0, mesh.Points.Count).Where(i => Volume.Contains(mesh.Points[i]))],
        };
        if (chosen == null && !Any) return Enumerable.Range(0, mesh.Points.Count).ToArray();
        return chosen ?? [];
    }

    /// <summary>
    /// The polygons a tool should work on in a layer: those selected; those using a selected point (Points mode)
    /// or edge (Edges mode); those in the volume; or, with nothing selected, every polygon.
    /// </summary>
    public IReadOnlyCollection<int> ActivePolygons(int layer, Mesh mesh)
    {
        if (!Any) return Enumerable.Range(0, mesh.Polygons.Count).ToArray();
        switch (Mode)
        {
            case SelectionMode.Polygons:
                return Polygons.TryGetValue(layer, out var s) ? s.Where(i => i < mesh.Polygons.Count).ToArray() : [];
            case SelectionMode.Points:
            {
                if (!Points.TryGetValue(layer, out var pts) || pts.Count == 0) return [];
                return Enumerable.Range(0, mesh.Polygons.Count).Where(i => mesh.Polygons[i].Points.All(pts.Contains)).ToArray();
            }
            case SelectionMode.Edges:
            {
                if (!Edges.TryGetValue(layer, out var es) || es.Count == 0) return [];
                var map = mesh.EdgeMap();
                return es.Where(map.ContainsKey).SelectMany(e => map[e]).Distinct().ToArray();
            }
            default:
                return Enumerable.Range(0, mesh.Polygons.Count).Where(i => InVolume(mesh, mesh.Polygons[i])).ToArray();
        }
    }
}

/// <summary>
/// A multi-level undo stack of snapshots: before an edit, copies of the layers it will change, the selection and
/// the surfaces; undoing swaps them back (and keeps what was there for redo).
/// </summary>
public sealed class UndoStack
{
    public sealed class Snapshot
    {
        public string Label = "";
        public Dictionary<int, Layer> Layers = [];
        public Selection Selection = new();
        public List<Surface> Surfaces = [];
    }

    private readonly List<Snapshot> _undo = [], _redo = [];
    public int Limit { get; set; } = 100;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;
    public int Count => _undo.Count;

    internal static Snapshot Take(Document doc, string label, IEnumerable<int> layers) => new()
    {
        Label = label,
        Layers = layers.Distinct().ToDictionary(i => i, i => doc.Layers[i].Clone()),
        Selection = doc.Selection.Clone(),
        Surfaces = doc.Surfaces.Select(s => s.Clone()).ToList(),
    };

    internal static void Restore(Document doc, Snapshot s)
    {
        foreach (var (i, layer) in s.Layers) doc.Layers[i] = layer.Clone();
        doc.Selection = s.Selection.Clone();
        doc.Surfaces = s.Surfaces.Select(x => x.Clone()).ToList();
    }

    internal void Push(Snapshot s)
    {
        _undo.Add(s);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    internal bool Undo(Document doc)
    {
        if (_undo.Count == 0) return false;
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(Take(doc, s.Label, s.Layers.Keys));
        Restore(doc, s);
        return true;
    }

    internal bool Redo(Document doc)
    {
        if (_redo.Count == 0) return false;
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Take(doc, s.Label, s.Layers.Keys));
        Restore(doc, s);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}

/// <summary>
/// An object being modeled: ten layers (more can be added), one surface table they all share, the selection, and
/// the undo history.
/// </summary>
public sealed class Document
{
    public const int DefaultLayers = 10;

    public List<Layer> Layers = [];
    public List<Surface> Surfaces = [new Surface("Default")];
    public Selection Selection = new();
    public UndoStack Undo { get; } = new();
    public string? FilePath;
    public bool Modified;
    /// <summary>A layer's mesh copied or cut, ready to paste.</summary>
    public Mesh? Clipboard;
    /// <summary>Goes up on every change, so views know to redraw.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after any change (an edit, an undo, a layer change).</summary>
    public event Action? Changed;

    public Document()
    {
        for (int i = 0; i < DefaultLayers; i++) Layers.Add(new Layer { State = i == 0 ? LayerState.Foreground : LayerState.Off });
    }

    public IEnumerable<int> ForegroundLayers => Enumerable.Range(0, Layers.Count).Where(i => Layers[i].State == LayerState.Foreground);
    public IEnumerable<int> BackgroundLayers => Enumerable.Range(0, Layers.Count).Where(i => Layers[i].State == LayerState.Background);

    /// <summary>The first foreground layer (where new geometry goes).</summary>
    public int CurrentLayer => ForegroundLayers.DefaultIfEmpty(0).First();

    public Layer Current => Layers[CurrentLayer];

    /// <summary>All the background layers' geometry in one mesh (booleans and drills work against it).</summary>
    public Mesh BackgroundMesh()
    {
        var m = new Mesh();
        foreach (int i in BackgroundLayers) m.Append(Layers[i].Mesh);
        return m;
    }

    public int SurfaceIndex(string name)
    {
        int i = Surfaces.FindIndex(s => s.Name == name);
        if (i >= 0) return i;
        Surfaces.Add(new Surface(name));
        return Surfaces.Count - 1;
    }

    public void Notify()
    {
        Version++;
        Changed?.Invoke();
    }

    /// <summary>A snapshot of some layers (and the selection and surfaces) before an edit.</summary>
    public UndoStack.Snapshot Snapshot(string label, IEnumerable<int>? layers = null) =>
        UndoStack.Take(this, label, layers ?? ForegroundLayers);

    /// <summary>Puts a snapshot back (an edit cancelled), without touching the history.</summary>
    public void Revert(UndoStack.Snapshot s)
    {
        UndoStack.Restore(this, s);
        Notify();
    }

    /// <summary>Records a finished edit in the history, from the snapshot taken before it.</summary>
    public void Commit(UndoStack.Snapshot before)
    {
        Undo.Push(before);
        foreach (int i in before.Layers.Keys) Selection.Prune(i, Layers[i].Mesh);
        Modified = true;
        Notify();
    }

    /// <summary>An edit in one go: a snapshot, the change, then the history.</summary>
    public void Edit(string label, Action change, IEnumerable<int>? layers = null)
    {
        var before = Snapshot(label, layers?.ToList());
        change();
        Commit(before);
    }

    public bool UndoLast()
    {
        if (!Undo.Undo(this)) return false;
        Modified = true;
        Notify();
        return true;
    }

    public bool RedoLast()
    {
        if (!Undo.Redo(this)) return false;
        Modified = true;
        Notify();
        return true;
    }

    /// <summary>Sets which layers are in the foreground and background (the rest off). Not an undoable edit.</summary>
    public void SetLayers(IEnumerable<int> foreground, IEnumerable<int>? background = null)
    {
        var fg = foreground.ToHashSet();
        var bg = background?.ToHashSet() ?? [];
        while (Layers.Count <= Math.Max(fg.DefaultIfEmpty(0).Max(), bg.DefaultIfEmpty(0).Max())) Layers.Add(new Layer());
        for (int i = 0; i < Layers.Count; i++)
            Layers[i].State = fg.Contains(i) ? LayerState.Foreground : bg.Contains(i) ? LayerState.Background : LayerState.Off;
        if (!fg.Any()) Layers[0].State = LayerState.Foreground;
        Notify();
    }

    public Box3 Bounds(bool foregroundOnly = false)
    {
        var b = Box3.Empty;
        for (int i = 0; i < Layers.Count; i++)
            if (Layers[i].State == LayerState.Foreground || !foregroundOnly && Layers[i].State == LayerState.Background)
                b = b.Include(Layers[i].Mesh.Bounds());
        return b;
    }
}
