using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools;

/// <summary>Flip: turns the selected polygons to face the other way.</summary>
public sealed class FlipTool : Tool
{
    public override string Name => "Flip";
    public override string Tab => "Polygon";
    public override string Summary => "Reverses the selected polygons so they face the other way.";
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
    public override string Summary => "Welds points that lie within Distance of each other (0: only points in exactly the same place) into one, among the selected points (or all). Clears the selection.";
    public override ToolKind Kind => ToolKind.Requester;

    /// <summary>How close points must be to merge (0: exactly the same place).</summary>
    [Param("Distance", Distance = true, Min = 0, Description = "Points closer than this merge (0: only coincident points).")] public double Distance { get; set; }

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
    public override string Summary => "Deletes: in Points mode with points selected, those points and every polygon that uses them; otherwise the selected polygons (every polygon in the foreground if none are selected), and with Remove loose points, the points they leave unused.";
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
    public override string Summary => "Gives the selected polygons (all of them if none are selected) the named surface, made if new. A new surface takes Color (0-255 each), Smoothing and Double sided; an existing one keeps its look unless Set look is on. The surface editor sets the rest (diffuse, specular and so on).";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Name")] public string SurfaceName { get; set; } = "Default";
    /// <summary>The colour, 0 to 255 per channel; set only when the surface is new or "Set look" is on.</summary>
    [Param("Color", Step = 1, Min = 0, Max = 255, Description = "Red, green, blue, 0 to 255 each.")] public Vec3 Color { get; set; } = new(200, 200, 200);
    [Param("Smoothing")] public bool Smoothing { get; set; }
    [Param("Double sided")] public bool DoubleSided { get; set; }
    [Param("Set look", Description = "Also set Color, Smoothing and Double sided on a surface that already exists.")] public bool SetLook { get; set; }

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

    /// <summary>
    /// Select Loop: adds the edge loop through each selected edge (in Points mode, each edge whose two points are
    /// selected): straight on across points where four quads meet, or round a hole's rim for a rim edge. Switches to
    /// Edges mode. Returns how many edges are selected afterwards.
    /// </summary>
    public static int Loop(Document doc) => Extend(doc, EdgeLoops.Loop);

    /// <summary>
    /// Select Ring: adds the edge ring through each selected edge (in Points mode, each edge whose two points are
    /// selected): the parallel edges across the strip of quads it crosses. Switches to Edges mode. Returns how many
    /// edges are selected afterwards.
    /// </summary>
    public static int Ring(Document doc) => Extend(doc, EdgeLoops.Ring);

    private static int Extend(Document doc, Func<Mesh, Edge, List<Edge>> walk)
    {
        var s = doc.Selection;
        var seeds = new Dictionary<int, List<Edge>>();
        foreach (int l in doc.ForegroundLayers)
            seeds[l] = s.Any && s.Mode is SelectionMode.Edges or SelectionMode.Points ? [.. s.ActiveEdges(l, doc.Layers[l].Mesh)] : [];
        s.Mode = SelectionMode.Edges;
        int count = 0;
        foreach (var (l, edges) in seeds)
        {
            var mesh = doc.Layers[l].Mesh;
            var set = s.EdgesIn(l);
            foreach (var e in edges) set.UnionWith(walk(mesh, e));
            count += set.Count;
        }
        doc.Notify();
        return count;
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

/// <summary>Edge clean-up shared by Spin Edge and Dissolve Edges: the outline of two faces that share an edge.</summary>
internal static class EdgeCleanup
{
    /// <summary>
    /// The two faces on an edge, and their joined outline in the first face's winding, starting at the edge's second
    /// point in that face (so the edge's first point is at index <c>first.Count - 1</c>); null when the edge isn't
    /// shared by exactly two faces that run it opposite ways.
    /// </summary>
    public static (int First, int Second, List<int> Outline)? Join(Mesh mesh, Edge e)
    {
        if (!mesh.EdgeMap().TryGetValue(e, out var faces)) return null;
        var both = faces.Where(f => mesh.Polygons[f].Count >= 3).ToList();
        if (both.Count != 2) return null;
        var f1 = mesh.Polygons[both[0]].Points;
        var f2 = mesh.Polygons[both[1]].Points;
        if (f1.Distinct().Count() != f1.Count || f2.Distinct().Count() != f2.Count) return null;
        // In the first face the edge runs a→b; in the second, b→a.
        int i = f1.IndexOf(e.A);
        int a = f1[(i + 1) % f1.Count] == e.B ? e.A : e.B, b = a == e.A ? e.B : e.A;
        int ia = f1.IndexOf(a), jb = f2.IndexOf(b);
        if (f1[(ia + 1) % f1.Count] != b || f2[(jb + 1) % f2.Count] != a) return null;
        var outline = new List<int>();
        for (int k = 0; k < f1.Count; k++) outline.Add(f1[(ia + 1 + k) % f1.Count]);     // b … a
        for (int k = 2; k < f2.Count; k++) outline.Add(f2[(jb + k) % f2.Count]);         // after a, up to before b
        if (outline.Distinct().Count() != outline.Count) return null;
        return (both[0], both[1], outline);
    }

    /// <summary>Gives a new face the UVs its points had in the faces it came from.</summary>
    public static void CarryUVs(Mesh mesh, Polygon made, Polygon a, Polygon b)
    {
        mesh.InheritUVs(made, a, q => a.Points.Contains(q) ? q : -1);
        mesh.InheritUVs(made, b, q => b.Points.Contains(q) && !a.Points.Contains(q) ? q : -1);
    }
}

/// <summary>
/// Spin Edge: turns each selected edge shared by two faces one step round them: the two faces' shared edge moves to
/// the next points along their joined outline. Two triangles swap their diagonal; two quads turn their shared edge.
/// Both faces keep their point counts, surfaces and winding; UVs come along.
/// </summary>
public sealed class SpinEdgeTool : Tool
{
    public override string Name => "Spin Edge";
    public override string Tab => "Polygon";
    public override string Summary => "Turns each selected edge (Edges mode) that two faces share one step round them: the shared edge moves on to the next points of their joined outline, so two triangles swap their diagonal and two quads turn the edge between them. The faces keep their point counts, surfaces and winding, and their UVs. Clears the selection.";
    public override ToolKind Kind => ToolKind.Immediate;

    /// <summary>How many edges the last run turned.</summary>
    public int Spun { get; private set; }

    protected override void Apply()
    {
        if (Doc.Selection.Mode != SelectionMode.Edges || !Doc.Selection.Any) throw new InvalidOperationException("Spin Edge turns selected edges: select some in Edges mode first.");
        int spun = 0;
        foreach (int layer in Layers)
        {
            var mesh = Doc.Layers[layer].Mesh;
            foreach (var e in Doc.Selection.ActiveEdges(layer, mesh).ToList())
            {
                if (EdgeCleanup.Join(mesh, e) is not var (f1, f2, outline)) continue;
                int k = mesh.Polygons[f1].Count - 1;
                int n = outline.Count;
                int from = outline[1], to = outline[(k + 1) % n];
                if (mesh.EdgeMap().ContainsKey(Edge.Of(from, to))) continue;
                var first = outline.Skip(1).Take(k + 1).ToList();
                var second = outline.Skip(k + 1).Concat(outline.Take(2)).ToList();
                var (pa, pb) = (mesh.Polygons[f1], mesh.Polygons[f2]);
                var a = new Polygon(first, pa.Surface);
                var b = new Polygon(second, pb.Surface);
                EdgeCleanup.CarryUVs(mesh, a, pa, pb);
                EdgeCleanup.CarryUVs(mesh, b, pb, pa);
                mesh.Polygons[f1] = a;
                mesh.Polygons[f2] = b;
                mesh.Touch();
                spun++;
            }
        }
        if (spun == 0) throw new InvalidOperationException("None of the selected edges could turn: an edge turns only when exactly two faces share it, and not onto an edge that's already there.");
        Spun = spun;
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>
/// Dissolve Edges: removes each selected edge that two faces share, joining them into one face, and (with Clean up)
/// the points left in a straight line between two edges of that face that nothing else uses.
/// </summary>
public sealed class DissolveEdgesTool : Tool
{
    public override string Name => "Dissolve Edges";
    public override string Tab => "Polygon";
    public override string Summary => "Removes each selected edge (Edges mode) that two faces share, joining the two into one face with the first's surface (UVs kept). With Clean up, points the joined faces leave in a straight line, used by no other face, go too. A closed mesh stays closed. Clears the selection.";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Clean up", Description = "Remove points left in a straight line that nothing else uses.")] public bool CleanUp { get; set; } = true;

    /// <summary>How many edges the last run removed.</summary>
    public int Dissolved { get; private set; }

    protected override void Apply()
    {
        if (Doc.Selection.Mode != SelectionMode.Edges || !Doc.Selection.Any) throw new InvalidOperationException("Dissolve Edges removes selected edges: select some in Edges mode first.");
        int dissolved = 0;
        foreach (int layer in Layers)
        {
            var mesh = Doc.Layers[layer].Mesh;
            var made = new HashSet<Polygon>();
            foreach (var e in Doc.Selection.ActiveEdges(layer, mesh).ToList())
            {
                if (EdgeCleanup.Join(mesh, e) is not var (f1, f2, outline)) continue;
                var (pa, pb) = (mesh.Polygons[f1], mesh.Polygons[f2]);
                var joined = new Polygon(outline, pa.Surface);
                EdgeCleanup.CarryUVs(mesh, joined, pa, pb);
                mesh.Polygons[f1] = joined;
                mesh.Polygons.RemoveAt(f2);
                made.Remove(pa);
                made.Remove(pb);
                made.Add(joined);
                mesh.Touch();
                dissolved++;
            }
            if (CleanUp) foreach (var poly in made) Straighten(mesh, poly);
            mesh.RemoveUnusedPoints();
        }
        if (dissolved == 0) throw new InvalidOperationException("None of the selected edges could go: an edge dissolves only when exactly two faces share it.");
        Dissolved = dissolved;
        Doc.Selection.Clear(allModes: true);
    }

    /// <summary>Drops a face's points that lie in a straight line between their neighbours and that no other face uses.</summary>
    private static void Straighten(Mesh mesh, Polygon poly)
    {
        for (int i = 0; i < poly.Points.Count && poly.Points.Count > 3;)
        {
            int p = poly.Points[i];
            var prev = mesh.Points[poly.Points[(i + poly.Points.Count - 1) % poly.Points.Count]];
            var next = mesh.Points[poly.Points[(i + 1) % poly.Points.Count]];
            var here = mesh.Points[p];
            var u = (here - prev).Normalized();
            var v = (next - here).Normalized();
            bool straight = Vec3.Cross(u, v).Length < 1e-6 && Vec3.Dot(u, v) > 0;
            if (straight && mesh.PolygonsOf(p).Count == 1)
            {
                poly.Points.RemoveAt(i);
                mesh.Touch();
                continue;
            }
            i++;
        }
    }
}

/// <summary>
/// Weld: merges points within a distance as Merge Points does, but with symmetry on it never merges a point with one on
/// the other side of the mirror plane, and points within the distance of the plane are put on it first, so a model
/// built in halves joins cleanly down its middle.
/// </summary>
public sealed class WeldTool : Tool
{
    public override string Name => "Weld";
    public override string Tab => "Polygon";
    public override string Summary => "Merges points within Distance of each other (the selected points, or all), as Merge Points does; with symmetry on (set_symmetry), points within Distance of the mirror plane are put on it first and no point merges with one across the plane, so halves join cleanly down the middle without collapsing thin parts across it. Clears the selection.";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Distance", Distance = true, Min = 0, Description = "Points closer than this merge, and points this near the mirror plane go onto it.")] public double Distance { get; set; } = 0.001;

    /// <summary>How many points the last run merged, and how many it put on the mirror plane.</summary>
    public (int Merged, int OnPlane) Result { get; private set; }

    protected override void Apply()
    {
        double d = Math.Max(Distance, 1e-9);
        int merged = 0, onPlane = 0;
        foreach (var (_, mesh, points) in ActivePoints().ToList())
        {
            if (Doc.Symmetry is not { } axis)
            {
                merged += mesh.MergePoints(d, points.Count == mesh.Points.Count ? null : points);
                continue;
            }
            foreach (int i in points)
                if (Math.Abs(mesh.Points[i][axis]) <= d && mesh.Points[i][axis] != 0)
                {
                    mesh.Points[i] = mesh.Points[i].With(axis, 0);
                    onPlane++;
                }
            mesh.Touch();
            // Each side, and the plane, on its own: each merge renumbers the points, so they're found again by place.
            var chosen = points.Select(i => mesh.Points[i]).ToHashSet();
            foreach (int side in new[] { -1, 0, 1 })
            {
                var group = Enumerable.Range(0, mesh.Points.Count)
                    .Where(i => chosen.Contains(mesh.Points[i]) && (side == 0 ? mesh.Points[i][axis] == 0 : Math.Sign(mesh.Points[i][axis]) == side)).ToList();
                if (group.Count > 1) merged += mesh.MergePoints(d, group);
            }
        }
        Result = (merged, onPlane);
        Doc.Selection.Clear(allModes: true);
    }
}
