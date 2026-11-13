using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools;

// ───────────────────────────── edge and face tools of the modern kind ─────────────────────────────

/// <summary>Loop Cut: new edge loops across the strip of quads a selected edge's ring crosses.</summary>
public sealed class LoopCutTool : Tool
{
    public override string Name => "Loop Cut";
    public override string Tab => "Polygon";
    public override string Help => "Select an edge (Edges mode, or two points joined by one), then drag across to slide the cut.";
    public override string Summary => "Cuts new edge loops across the strip of quads that each selected edge's ring crosses (the parallel edges across the quads, round a cylinder or a box all the way): every quad in the strip splits into Count + 1. In Points mode the edges between selected points count. One cut sits at Position along the ring's edges (0 to 1; 0.5 is the middle); more are spread evenly. Triangles and n-gons at the ends of an open strip take the new points into their edges, so a closed mesh stays closed. UVs are interpolated. Selects the new edges (Edges mode). Needs a selected edge.";

    [Param("Position", Min = 0, Max = 1, Step = 0.05, Description = "Where a single cut sits along the ring's edges, 0 to 1 (0.5: the middle).")] public double Position { get; set; } = 0.5;
    [Param("Count", Min = 1, Max = 100, Step = 1, Description = "How many evenly spaced cuts (more than one ignores Position).")] public int Count { get; set; } = 1;

    protected override void OnDrag(DragInfo d) => Position = Math.Clamp(0.5 + d.DxPixels / 400, 0, 1);

    protected override void Apply()
    {
        var sel = Doc.Selection;
        var seeds = new Dictionary<int, List<Edge>>();
        if (sel.Any && sel.Mode is SelectionMode.Edges or SelectionMode.Points)
            foreach (int l in Doc.ForegroundLayers)
                seeds[l] = [.. sel.ActiveEdges(l, Doc.Layers[l].Mesh).OrderBy(e => e.A).ThenBy(e => e.B)];
        if (seeds.Values.Sum(s => s.Count) == 0)
            throw new InvalidOperationException("Loop Cut needs an edge to cut across: select one in Edges mode (Select Ring shows the strip it will cut), or two points joined by an edge.");
        sel.Mode = SelectionMode.Edges;
        sel.Edges.Clear();
        foreach (var (l, edges) in seeds)
        {
            var mesh = Doc.Layers[l].Mesh;
            var done = new HashSet<Edge>();
            foreach (var seed in edges)
            {
                // A seed in a ring already cut is gone (split), and its ring with it.
                if (done.Contains(seed) || !mesh.EdgeMap().ContainsKey(seed)) continue;
                done.UnionWith(EdgeLoops.Ring(mesh, seed));
                sel.EdgesIn(l).UnionWith(EdgeLoops.LoopCut(mesh, seed, Position, Count));
            }
        }
        if (sel.Edges.Values.Sum(e => e.Count) == 0)
            throw new InvalidOperationException("Loop Cut cuts across quads, but the selected edges have none beside them: pick an edge of a quad.");
    }
}

/// <summary>Bridge: joins two edge loops, two faces or two holes with a band of quads.</summary>
public sealed class BridgeTool : Tool
{
    public override string Name => "Bridge";
    public override string Tab => "Multiply";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "Select the rims of two holes (Edges mode), or two faces; with nothing selected, it joins the foreground's two holes.";
    public override string Summary => "Joins two things with a band of quads Segments deep: two loops (or chains) of selected edges, such as the rims of two holes (Edges mode); two selected faces or pieces of faces (Polygons mode), which are removed and their outlines joined; or, with nothing selected, the foreground's two holes. Both sides need the same number of points (else it says so: Loop Cut or Subdivide the side with fewer). Points are paired the way that twists least, and the band winds to match the faces beside it, so bridging the open ends of a tube closes it. Clears the selection.";

    [Param("Segments", Min = 1, Max = 1000, Step = 1, Description = "How many rows of quads across the band.")] public int Segments { get; set; } = 1;

    protected override void Apply()
    {
        var sel = Doc.Selection;
        int bridged = 0;
        foreach (int l in Doc.ForegroundLayers)
        {
            var mesh = Doc.Layers[l].Mesh;
            if (!sel.Any)
            {
                if (Holes.BoundaryLoops(mesh).Count == 0) continue;
                Holes.BridgeHoles(mesh, Segments);
            }
            else if (sel.Mode is SelectionMode.Polygons or SelectionMode.Volume)
            {
                var polys = sel.ActivePolygons(l, mesh);
                if (polys.Count == 0) continue;
                Holes.BridgeFaces(mesh, polys, Segments);
            }
            else
            {
                var edges = sel.ActiveEdges(l, mesh);
                if (edges.Count == 0) continue;
                Holes.BridgeEdges(mesh, edges, Segments);
            }
            bridged++;
        }
        if (bridged == 0) throw new InvalidOperationException("Bridge found nothing to join: select the rims of two holes (Edges mode) or two faces.");
        sel.Clear(allModes: true);
    }
}

/// <summary>Fill Hole: closes holes with a polygon (or triangles, or quads) wound to match their neighbours.</summary>
public sealed class FillHoleTool : Tool
{
    public override string Name => "Fill Hole";
    public override string Tab => "Polygon";
    public override ToolKind Kind => ToolKind.Immediate;
    public override string Summary => "Closes holes (rims of edges with a face on one side only, as the mesh check finds them) with new faces wound to match the faces round them, so a box missing a face becomes closed again. Fills the holes the selection touches (a selected rim edge, both points of one, or a selected face beside it), or every hole in the foreground with nothing selected. Mode NGon makes one polygon per hole, Triangles cuts it into triangles (for holes that aren't flat or convex), Quads makes a ladder of quads across it (a triangle ends an odd one). Clears the selection.";

    [Param("Mode", Description = "NGon: one polygon; Triangles: triangulated; Quads: a ladder of quads.")] public FillMode Mode { get; set; } = FillMode.NGon;

    /// <summary>How many holes the last run closed.</summary>
    public int Filled { get; private set; }

    protected override void Apply()
    {
        Filled = 0;
        var sel = Doc.Selection;
        foreach (int l in Doc.ForegroundLayers)
        {
            var mesh = Doc.Layers[l].Mesh;
            var near = sel.ActiveEdges(l, mesh).ToHashSet();
            var loops = Holes.BoundaryLoops(mesh)
                .Where(loop => loop.Select((p, i) => Edge.Of(p, loop[(i + 1) % loop.Count])).Any(near.Contains))
                .Select(loop => (IReadOnlyList<int>)loop).ToList();
            Holes.Fill(mesh, loops, Mode);
            Filled += loops.Count;
        }
        sel.Clear(allModes: true);
    }
}

/// <summary>Inset: shrinks faces inward, face by face or as regions, leaving a border band of quads.</summary>
public sealed class InsetTool : Tool
{
    public override string Name => "Inset";
    public override string Tab => "Multiply";
    public override string Help => "Drag across for the thickness, up and down for the depth.";
    public override string Summary => "Shrinks the selected faces inward by Thickness (metres; negative grows them), leaving a border band of quads round them, and moves them Depth along their normals (negative goes in). Mode Individual does each face on its own (Bevel's per-face way); Group treats each connected region of selected faces as one, with the band only round the region's outline and the region moved along its averaged normals: with a Depth, that is a group bevel. UVs are interpolated across the band. Keeps the inset faces selected.";

    [Param("Mode", Description = "Individual: each face on its own; Group: each connected region as one (a group bevel with a Depth).")] public InsetMode Mode { get; set; } = InsetMode.Group;
    [Param("Thickness", Distance = true, Description = "How far the outline moves in across the faces (the border band's width).")] public double Thickness { get; set; } = 0.1;
    [Param("Depth", Distance = true, Description = "How far the inset faces move along their normals (negative: in).")] public double Depth { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        Thickness = Context.SnapValue(d.DxPixels * d.UnitsPerPixel);
        Depth = Context.SnapValue(-d.DyPixels * d.UnitsPerPixel);
    }

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) Insets.Apply(mesh, polys.ToList(), Thickness, Depth, Mode);
    }
}

/// <summary>Chamfer: an edge bevel, each selected edge replaced by a flat or rounded strip.</summary>
public sealed class ChamferTool : Tool
{
    public override string Name => "Chamfer";
    public override string Tab => "Multiply";
    public override string Help => "Select edges (Edges mode), then drag across for the width.";
    public override string Summary => "Bevels edges: each selected edge shared by two faces (Edges mode; in Points mode, edges whose two points are selected; in Polygons mode, every edge of the selected faces; with nothing selected, every edge in the foreground) becomes a strip Width across (metres, measured from the old edge into each face), flat with one Segment and rounded along a circular arc with more. The faces either side pull back from it; faces at its ends take the new points. Where three chamfered edges meet (a box's corner) the corner is closed with one polygon. UVs are interpolated. Clears the selection.";

    [Param("Width", Distance = true, Min = 0, Description = "How far the strip reaches into each face from the old edge.")] public double Width { get; set; } = 0.05;
    [Param("Segments", Min = 1, Max = 64, Step = 1, Description = "Pieces across the strip: 1 is a flat chamfer, more round it off.")] public int Segments { get; set; } = 1;

    /// <summary>How many edges the last run chamfered.</summary>
    public int Chamfered { get; private set; }

    protected override void OnDrag(DragInfo d) => Width = Math.Max(0, Context.SnapValue(d.DxPixels * d.UnitsPerPixel));

    protected override void Apply()
    {
        Chamfered = 0;
        if (Width <= 0) return;
        foreach (int l in Doc.ForegroundLayers)
        {
            var mesh = Doc.Layers[l].Mesh;
            Chamfered += Chamfer.Apply(mesh, Doc.Selection.ActiveEdges(l, mesh).ToList(), Width, Segments);
        }
        if (Chamfered == 0)
            throw new InvalidOperationException("Chamfer found no edge to bevel: it works on edges with a face on each side. Select some in Edges mode.");
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>Knife: cuts the faces under a line drawn across a view, along the view's depth.</summary>
public sealed class KnifeTool : Tool
{
    public override string Name => "Knife";
    public override string Tab => "Polygon";
    public override string Help => "Drag a line across the faces to cut them along it, straight through the view.";
    public override string Summary => "Cuts the selected faces (every face, with none selected) along the plane through the line from Start to End that runs along Axis (the view's depth: Z for the Front view, Y for Top, X for Side). Only crossings between the line's ends count. Each crossed edge gets a new point, put into every polygon on that edge so a closed mesh stays closed, and each face the plane crosses splits along it, keeping its surface and winding; UVs are interpolated. With symmetry on, the mirrored line cuts too.";

    [Param("Start", Distance = true, Description = "One end of the knife's line, in metres.")] public Vec3 Start { get; set; }
    [Param("End", Distance = true, Description = "The other end of the line.")] public Vec3 End { get; set; }
    [Param("Axis", Description = "The direction the cut runs through the model (the view's depth).")] public Axis Axis { get; set; } = Axis.Z;

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) Start = Context.SnapPoint(d.Start);
        End = Context.SnapPoint(d.Constrain ? d.Start + Axes.Dominant(d.Delta) : d.Current);
        Axis = d.ViewAxis;
    }

    protected override void Apply()
    {
        var depth = Vec3.Unit(Axis);
        var along = End - Start;
        if ((along - depth * Vec3.Dot(along, depth)).LengthSquared < 1e-18) return; // a click, not yet a line
        foreach (var (_, mesh, polygons) in ActivePolygons().ToList())
        {
            var faces = polygons.ToHashSet();
            int count = mesh.Polygons.Count;
            Knife.Cut(mesh, faces, Start, End, depth);
            if (Doc.Symmetry is { } axis)
            {
                faces.UnionWith(Enumerable.Range(count, mesh.Polygons.Count - count));
                Knife.Cut(mesh, faces, Symmetry.Mirror(Start, axis), Symmetry.Mirror(End, axis), Symmetry.Mirror(depth, axis));
            }
        }
    }
}
