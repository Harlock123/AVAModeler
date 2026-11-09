using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools;

/// <summary>Pen: click to place a curve's points; each click adds a point and the line to it.</summary>
public sealed class PenTool : Tool
{
    public override string Name => "Pen";
    public override string Tab => "Objects";
    public override string Summary => "Draws a curve (a chain of 2-point lines) through the points the mouse clicks in a view, closed back to the first if Closed is on, smooth through them if Smooth is on. Only works with the mouse: from a script add points and use Make Curve.";
    public override bool Scriptable => false;
    public override string Help => "Click to place each point of the curve (drag to adjust it); Enter keeps it.";

    [Param("Closed", Description = "Join the last point back to the first.")] public bool Closed { get; set; }
    [Param("Smooth", Description = "Run a smooth curve through the points, not straight lines between them.")] public bool Smooth { get; set; } = true;
    [Param("Pieces a span", Min = 2, Max = 64, Step = 1, Description = "For Smooth: the lines between each pair of points.")] public int PerSpan { get; set; } = 8;
    [Param("Surface")] public string Surface { get; set; } = "Default";

    private readonly List<Vec3> _points = [];

    /// <summary>The points placed so far.</summary>
    public IReadOnlyList<Vec3> Placed => _points;

    protected override void OnBegin() => _points.Clear();

    protected override void OnDrag(DragInfo d)
    {
        if (d.First || _points.Count == 0) _points.Add(Context.SnapPoint(d.Current));
        else _points[^1] = Context.SnapPoint(d.Current);
    }

    protected override void Apply()
    {
        if (_points.Count == 0) return;
        var mesh = Doc.Current.Mesh;
        var ids = _points.Select(mesh.AddPoint).ToList();
        Doc.Selection.Clear(allModes: true);
        if (ids.Count < 2 || Closed && ids.Count < 3)
        {
            // Not a curve yet: the points so far, selected.
            foreach (int i in ids) Doc.Selection.SelectPoint(Doc.CurrentLayer, i);
            return;
        }
        Doc.Selection.Mode = SelectionMode.Polygons;
        foreach (int l in Curves.AddCurve(mesh, ids, Closed, Smooth, PerSpan, SurfaceFor(Surface))) Doc.Selection.PolygonsIn(Doc.CurrentLayer).Add(l);
    }

    protected override IEnumerable<int> Layers => [Doc.CurrentLayer];
}

/// <summary>Make Curve: a curve through the selected points, in the order they were selected.</summary>
public sealed class MakeCurveTool : Tool
{
    public override string Name => "Make Curve";
    public override string Tab => "Objects";
    public override string Summary => "Makes a curve (a chain of 2-point lines) through the selected points (Points mode, two or more) in the order they were selected, closed back to the first if Closed is on (three or more), smooth through them if Smooth is on (the selected points stay where they are; points are added between). Selects the curve's lines, ready for Spline Patch, Loft, Rail Extrude or Smooth Curve.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "Select points in order, then make a curve through them.";

    [Param("Closed", Description = "Join the last point back to the first.")] public bool Closed { get; set; }
    [Param("Smooth", Description = "Run a smooth curve through the points, not straight lines between them.")] public bool Smooth { get; set; } = true;
    [Param("Pieces a span", Min = 2, Max = 64, Step = 1, Description = "For Smooth: the lines between each pair of points.")] public int PerSpan { get; set; } = 8;
    [Param("Surface")] public string Surface { get; set; } = "Default";

    protected override void Apply()
    {
        bool made = false;
        int surface = SurfaceFor(Surface);
        var chosen = Doc.ForegroundLayers.Select(l => (Layer: l, Points: OriginalSelection.OrderedPoints(l))).ToList();
        Doc.Selection.Clear(allModes: true);
        Doc.Selection.Mode = SelectionMode.Polygons;
        foreach (var (l, points) in chosen)
        {
            if (points.Count == 0) continue;
            if (points.Count < (Closed ? 3 : 2))
                throw new InvalidOperationException($"Make Curve needs {(Closed ? "three" : "two")} or more points selected, in order, in Points mode.");
            foreach (int line in Curves.AddCurve(Doc.Layers[l].Mesh, points, Closed, Smooth, PerSpan, surface)) Doc.Selection.PolygonsIn(l).Add(line);
            made = true;
        }
        if (!made) throw new InvalidOperationException("Make Curve needs two or more points selected, in order, in Points mode.");
    }
}

/// <summary>Smooth Curve: runs curves smoothly through their points, or evenly along them.</summary>
public sealed class SmoothCurveTool : Tool
{
    public override string Name => "Smooth Curve";
    public override string Tab => "Modify";
    public override string Summary => "Reshapes curves (chains of 2-point lines: the selected lines, or every line in the foreground) as smooth curves through their points, Pieces a span lines between each pair; with Even pieces above 0, the smooth curve is instead laid out in that many even pieces, its old points removed where nothing else uses them. Closed loops stay closed.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "Select a curve's lines (or none, for every curve), then Apply.";

    [Param("Pieces a span", Min = 2, Max = 64, Step = 1, Description = "The lines between each pair of the curve's points.")] public int PerSpan { get; set; } = 8;
    [Param("Even pieces", Min = 0, Max = 10000, Step = 1, Description = "0 keeps the curve's points; more lays the smooth curve out in that many even pieces instead.")] public int Pieces { get; set; }

    protected override void Apply()
    {
        bool made = false;
        foreach (var (layer, mesh, polys) in ActivePolygons())
        {
            var lines = polys.Where(i => mesh.Polygons[i].Count == 2).ToList();
            if (lines.Count == 0) continue;
            var chains = Curves.Chains(mesh, lines).Where(c => c.Points.Count >= 3 || c.Closed && c.Points.Count >= 3).ToList();
            if (chains.Count == 0) continue;
            int surface = mesh.Polygons[lines[0]].Surface;
            var gone = new HashSet<int>(lines);
            var newLines = new List<(List<int> Points, bool Closed)>();
            foreach (var (chain, closed) in chains)
            {
                var smooth = Curves.Smooth([.. chain.Select(i => mesh.Points[i])], PerSpan, closed);
                if (Pieces > 0)
                {
                    // Measured along the curve finely, so the even pieces are even along it.
                    var even = Curves.Even(Curves.Smooth([.. chain.Select(i => mesh.Points[i])], 64, closed), Pieces, closed);
                    // The ends of an open curve keep their points (other things may join there).
                    var ids = even.Select((p, k) => !closed && k == 0 ? chain[0] : !closed && k == even.Count - 1 ? chain[^1] : mesh.AddPoint(p)).ToList();
                    newLines.Add((ids, closed));
                }
                else
                {
                    var ids = smooth.Select((p, k) => k % PerSpan == 0 && k / PerSpan < chain.Count ? chain[k / PerSpan] : mesh.AddPoint(p)).ToList();
                    newLines.Add((ids, closed));
                }
            }
            int keepFrom = mesh.Polygons.Count;
            foreach (var (ids, closed) in newLines)
                for (int k = 0; k + 1 < ids.Count || closed && k < ids.Count; k++) mesh.AddPolygon([ids[k], ids[(k + 1) % ids.Count]], surface);
            int added = mesh.Polygons.Count - keepFrom;
            var oldPoints = chains.SelectMany(c => c.Points).ToHashSet();
            mesh.RemovePolygons(gone);
            // The old points that nothing uses now (Even pieces), and only those.
            var used = mesh.Polygons.SelectMany(p => p.Points).ToHashSet();
            mesh.RemovePoints(oldPoints.Where(p => !used.Contains(p)));
            Doc.Selection.Clear(allModes: true);
            Doc.Selection.Mode = SelectionMode.Polygons;
            for (int i = mesh.Polygons.Count - added; i < mesh.Polygons.Count; i++) Doc.Selection.PolygonsIn(layer).Add(i);
            mesh.Touch();
            made = true;
        }
        if (!made) throw new InvalidOperationException("Smooth Curve needs a curve of three or more points: a chain of 2-point lines (Make Curve or Pen makes one).");
    }
}

/// <summary>Loft: a skin across two or more curves.</summary>
public sealed class LoftTool : Tool
{
    public override string Name => "Loft";
    public override string Tab => "Multiply";
    public override string Summary => "Skins two or more curves (chains of 2-point lines: the selected lines, or every line in the foreground) with a grid of quads: each curve laid out in Across even pieces, the curves joined in the order they stack up by Along rows between each pair, on smooth curves through them all if Smooth is on. All open (a sheet) or all closed (a tube: a hull, a vase, a bottle from its cross-sections). Flip faces the skin the other way.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "Draw the cross-sections as curves (Pen or Make Curve), select their lines, then Apply.";

    [Param("Across", Min = 1, Max = 500, Step = 1, Description = "Pieces along each curve.")] public int Across { get; set; } = 16;
    [Param("Along", Min = 1, Max = 200, Step = 1, Description = "Rows between each pair of curves.")] public int Along { get; set; } = 4;
    [Param("Smooth", Description = "Run the skin smoothly through the curves, not straight from one to the next.")] public bool Smooth { get; set; } = true;
    [Param("Flip", Description = "Face the skin the other way.")] public bool Flip { get; set; }
    [Param("Keep the curves")] public bool KeepCurves { get; set; } = true;
    [Param("Surface")] public string Surface { get; set; } = "Default";

    protected override void Apply()
    {
        bool made = false;
        int surface = SurfaceFor(Surface);
        foreach (var (layer, mesh, polys) in ActivePolygons())
        {
            var lines = polys.Where(i => mesh.Polygons[i].Count == 2).ToList();
            if (lines.Count == 0) continue;
            var chains = Curves.Chains(mesh, lines);
            if (chains.Count < 2) throw new InvalidOperationException("Loft needs two or more separate curves (chains of 2-point lines): it found " + chains.Count + ".");
            bool closed = chains[0].Closed;
            if (chains.Any(c => c.Closed != closed)) throw new InvalidOperationException("Loft needs the curves all open or all closed.");
            var faces = Curves.Loft(mesh, [.. chains.Select(c => (IReadOnlyList<Vec3>)c.Points.Select(i => mesh.Points[i]).ToList())], closed, Across, Along, Smooth, surface);
            if (Flip) foreach (int f in faces) mesh.Polygons[f].Points.Reverse();
            int first = faces.Count > 0 ? faces[0] : mesh.Polygons.Count;
            if (!KeepCurves)
            {
                mesh.RemovePolygons(lines);
                first -= lines.Count(l => l < first);
            }
            Doc.Selection.Clear(allModes: true);
            Doc.Selection.Mode = SelectionMode.Polygons;
            for (int i = first; i < first + faces.Count; i++) Doc.Selection.PolygonsIn(layer).Add(i);
            mesh.Touch();
            made = true;
        }
        if (!made) throw new InvalidOperationException("Loft needs two or more curves (chains of 2-point lines) in the foreground: select their lines in Polygons mode.");
    }
}
