using AVAModeler.Core;

namespace AVAModeler.Tools;

/// <summary>Flip: turns the selected polygons to face the other way.</summary>
public sealed class FlipTool : Tool
{
    public override string Name => "Flip";
    public override string Tab => "Polygon";
    public override ToolKind Kind => ToolKind.Immediate;

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons())
            foreach (int pi in polys) mesh.Flip(pi);
    }
}

/// <summary>Merge Points: welds points that lie within a distance of each other.</summary>
public sealed class MergePointsTool : Tool
{
    public override string Name => "Merge Points";
    public override string Tab => "Polygon";
    public override ToolKind Kind => ToolKind.Requester;

    /// <summary>How close points must be to merge (0: exactly the same place).</summary>
    [Param("Distance", Distance = true, Min = 0)] public double Distance { get; set; }

    /// <summary>How many points the last run merged.</summary>
    public int Merged { get; private set; }

    protected override void Apply()
    {
        Merged = 0;
        foreach (var (_, mesh, points) in ActivePoints())
        {
            bool all = points.Count == mesh.Points.Count;
            Merged += mesh.MergePoints(Math.Max(Distance, 1e-9), all ? null : points);
        }
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>Remove: deletes the selected polygons (Polygons mode) or the selected points and what uses them (Points mode).</summary>
public sealed class RemoveTool : Tool
{
    public override string Name => "Remove";
    public override string Tab => "Polygon";
    public override ToolKind Kind => ToolKind.Immediate;

    /// <summary>Also delete points no polygon uses any more.</summary>
    [Param("Remove loose points")] public bool LoosePoints { get; set; } = true;

    protected override void Apply()
    {
        var sel = Doc.Selection;
        foreach (int l in Doc.ForegroundLayers)
        {
            var mesh = Doc.Layers[l].Mesh;
            if (sel.Mode == SelectionMode.Points && sel.Any)
                mesh.RemovePoints(sel.ActivePoints(l, mesh).ToList());
            else
            {
                var polys = sel.ActivePolygons(l, mesh).ToList();
                var pts = mesh.PointsOf(polys);
                mesh.RemovePolygons(polys);
                if (LoosePoints)
                {
                    var used = mesh.PointsOf(Enumerable.Range(0, mesh.Polygons.Count));
                    mesh.RemovePoints(pts.Where(p => !used.Contains(p)));
                }
            }
        }
        sel.Clear(allModes: true);
    }
}

/// <summary>Surface: gives the selected polygons a named surface (made if new) and sets how it looks.</summary>
public sealed class SurfaceTool : Tool
{
    public override string Name => "Surface";
    public override string Tab => "Polygon";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Name")] public string SurfaceName { get; set; } = "Default";
    /// <summary>The colour, 0 to 255 per channel; set only when the surface is new or "Set look" is on.</summary>
    [Param("Color", Step = 1, Min = 0, Max = 255)] public Vec3 Color { get; set; } = new(200, 200, 200);
    [Param("Smoothing")] public bool Smoothing { get; set; }
    [Param("Double sided")] public bool DoubleSided { get; set; }
    [Param("Set look")] public bool SetLook { get; set; }

    protected override void Apply()
    {
        bool isNew = !Doc.Surfaces.Any(s => s.Name == SurfaceName);
        int index = Doc.SurfaceIndex(SurfaceName);
        if (isNew || SetLook)
        {
            var s = Doc.Surfaces[index];
            s.Color = Rgb.FromVec(Color / 255);
            s.Smoothing = Smoothing;
            s.DoubleSided = DoubleSided;
        }
        foreach (var (_, mesh, polys) in ActivePolygons())
        {
            foreach (int pi in polys) mesh.Polygons[pi].Surface = index;
            mesh.Touch();
        }
    }
}

/// <summary>The layer operations: copy, cut and paste between layers, and swapping the foreground and background.</summary>
public static class LayerOps
{
    /// <summary>Copies the selection (or the whole foreground) to the clipboard.</summary>
    public static void Copy(Document doc)
    {
        var clip = new Mesh();
        foreach (int l in doc.ForegroundLayers) clip.Append(Extract(doc, l, remove: false));
        doc.Clipboard = clip;
    }

    /// <summary>Cuts the selection (or the whole foreground) to the clipboard.</summary>
    public static void Cut(Document doc)
    {
        doc.Edit("Cut", () =>
        {
            var clip = new Mesh();
            foreach (int l in doc.ForegroundLayers.ToList()) clip.Append(Extract(doc, l, remove: true));
            doc.Clipboard = clip;
            doc.Selection.Clear(allModes: true);
        });
    }

    /// <summary>Pastes the clipboard into the current layer.</summary>
    public static void Paste(Document doc)
    {
        if (doc.Clipboard is not { } clip || clip.IsEmpty) return;
        int layer = doc.CurrentLayer;
        doc.Edit("Paste", () =>
        {
            doc.Layers[layer].Mesh.Append(clip);
        }, [layer]);
    }

    /// <summary>The selected polygons (with their points and UVs) of a layer as a mesh of their own, optionally taken out.</summary>
    private static Mesh Extract(Document doc, int layer, bool remove)
    {
        var mesh = doc.Layers[layer].Mesh;
        var polys = doc.Selection.ActivePolygons(layer, mesh).ToList();
        var used = mesh.PointsOf(polys).Order().ToList();
        // Points selected on their own (no polygon) come too.
        if (doc.Selection.Mode == SelectionMode.Points && doc.Selection.Any)
            used = used.Union(doc.Selection.ActivePoints(layer, mesh)).Distinct().Order().ToList();
        var map = new Dictionary<int, int>();
        var part = new Mesh();
        foreach (int p in used) map[p] = part.AddPoint(mesh.Points[p]);
        foreach (var uvs in mesh.UVMaps)
        {
            var copy = part.AddUVMap(uvs.Name);
            foreach (int p in used)
                if (uvs.Points.TryGetValue(p, out var uv)) copy.Points[map[p]] = uv;
        }
        foreach (int pi in polys)
        {
            var c = mesh.Polygons[pi].Clone();
            c.SetPoints(c.Points.Select(i => map[i]).ToList());
            c.Details = null;
            part.Polygons.Add(c);
        }
        part.Touch();
        if (remove)
        {
            mesh.RemovePolygons(polys);
            var stillUsed = mesh.PointsOf(Enumerable.Range(0, mesh.Polygons.Count));
            mesh.RemovePoints(used.Where(p => !stillUsed.Contains(p)));
        }
        return part;
    }

    /// <summary>Swaps the foreground and background layers.</summary>
    public static void SwapForegroundBackground(Document doc)
    {
        var fg = doc.ForegroundLayers.ToList();
        var bg = doc.BackgroundLayers.ToList();
        if (bg.Count == 0) return;
        doc.SetLayers(bg, fg);
    }
}

/// <summary>Selection commands: all, none, invert, connected, and growing or shrinking.</summary>
public static class SelectionOps
{
    public static void All(Document doc)
    {
        var s = doc.Selection;
        foreach (int l in doc.ForegroundLayers)
        {
            var mesh = doc.Layers[l].Mesh;
            switch (s.Mode)
            {
                case SelectionMode.Points: for (int i = 0; i < mesh.Points.Count; i++) s.SelectPoint(l, i); break;
                case SelectionMode.Polygons: s.PolygonsIn(l).UnionWith(Enumerable.Range(0, mesh.Polygons.Count)); break;
                case SelectionMode.Edges: s.EdgesIn(l).UnionWith(mesh.EdgeMap().Keys); break;
            }
        }
        doc.Notify();
    }

    public static void None(Document doc)
    {
        doc.Selection.Clear();
        doc.Notify();
    }

    public static void Invert(Document doc)
    {
        var s = doc.Selection;
        foreach (int l in doc.ForegroundLayers)
        {
            var mesh = doc.Layers[l].Mesh;
            switch (s.Mode)
            {
                case SelectionMode.Points:
                {
                    var was = s.PointsIn(l).ToHashSet();
                    s.PointsIn(l).Clear();
                    if (s.PointOrder.TryGetValue(l, out var o)) o.Clear();
                    for (int i = 0; i < mesh.Points.Count; i++) if (!was.Contains(i)) s.SelectPoint(l, i);
                    break;
                }
                case SelectionMode.Polygons:
                {
                    var set = s.PolygonsIn(l);
                    var was = set.ToHashSet();
                    set.Clear();
                    set.UnionWith(Enumerable.Range(0, mesh.Polygons.Count).Where(i => !was.Contains(i)));
                    break;
                }
                case SelectionMode.Edges:
                {
                    var set = s.EdgesIn(l);
                    var was = set.ToHashSet();
                    set.Clear();
                    set.UnionWith(mesh.EdgeMap().Keys.Where(e => !was.Contains(e)));
                    break;
                }
            }
        }
        doc.Notify();
    }

    /// <summary>Adds everything connected to what's selected (through shared points).</summary>
    public static void Connected(Document doc)
    {
        var s = doc.Selection;
        foreach (int l in doc.ForegroundLayers)
        {
            var mesh = doc.Layers[l].Mesh;
            var seedPoints = s.Mode switch
            {
                SelectionMode.Points => s.PointsIn(l).ToHashSet(),
                SelectionMode.Polygons => mesh.PointsOf(s.PolygonsIn(l)),
                SelectionMode.Edges => s.EdgesIn(l).SelectMany(e => new[] { e.A, e.B }).ToHashSet(),
                _ => [],
            };
            var points = Flood(mesh, seedPoints);
            switch (s.Mode)
            {
                case SelectionMode.Points: foreach (int p in points.Order()) s.SelectPoint(l, p); break;
                case SelectionMode.Polygons:
                    s.PolygonsIn(l).UnionWith(Enumerable.Range(0, mesh.Polygons.Count).Where(i => mesh.Polygons[i].Points.Any(points.Contains)));
                    break;
                case SelectionMode.Edges:
                    s.EdgesIn(l).UnionWith(mesh.EdgeMap().Keys.Where(e => points.Contains(e.A)));
                    break;
            }
        }
        doc.Notify();
    }

    /// <summary>Every point reachable from some points through polygons.</summary>
    public static HashSet<int> Flood(Mesh mesh, IEnumerable<int> seeds)
    {
        var seen = new HashSet<int>(seeds);
        var queue = new Queue<int>(seen);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (int pi in mesh.PolygonsOf(p))
                foreach (int q in mesh.Polygons[pi].Points)
                    if (seen.Add(q)) queue.Enqueue(q);
        }
        return seen;
    }

    /// <summary>Grows the polygon selection by the polygons that share a point with it.</summary>
    public static void Grow(Document doc)
    {
        var s = doc.Selection;
        foreach (int l in doc.ForegroundLayers)
        {
            var mesh = doc.Layers[l].Mesh;
            if (s.Mode == SelectionMode.Points)
            {
                foreach (int p in s.PointsIn(l).ToList())
                    foreach (int q in mesh.Neighbours(p)) s.SelectPoint(l, q);
                continue;
            }
            var pts = mesh.PointsOf(s.PolygonsIn(l));
            s.PolygonsIn(l).UnionWith(pts.SelectMany(p => mesh.PolygonsOf(p)));
        }
        doc.Notify();
    }

    /// <summary>Shrinks the polygon selection by those on its edge (sharing a point with something unselected).</summary>
    public static void Shrink(Document doc)
    {
        var s = doc.Selection;
        foreach (int l in doc.ForegroundLayers)
        {
            var mesh = doc.Layers[l].Mesh;
            var set = s.PolygonsIn(l);
            var edge = set.Where(pi => mesh.Polygons[pi].Points.Any(p => mesh.PolygonsOf(p).Any(q => !set.Contains(q)))).ToList();
            set.ExceptWith(edge);
        }
        doc.Notify();
    }
}
