using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools;

/// <summary>Reduce Polygons: fewer triangles, the shape kept as closely as can be.</summary>
public sealed class ReducePolygonsTool : Tool
{
    public override string Name => "Reduce Polygons";
    public override string Tab => "Polygon";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Reduces the selected faces (every face, with none selected) to triangles, then collapses the edges that change the shape least until about Percent of the triangles are left (or Target, if it's more than 0). Open borders, edges between surfaces and UV seams stay where they are, no triangle is turned over, and UVs and weight and colour maps carry over. For game assets, heavy scans and imports, and keeping particles and scatter copies light.";

    [Param("Percent", Min = 1, Max = 100, Step = 5, Description = "The share of the triangles to keep.")] public double Percent { get; set; } = 50;
    [Param("Target", Min = 0, Step = 100, Description = "A number of triangles to reduce to instead (0: use Percent).")] public int Target { get; set; }

    /// <summary>What the last run did, for the status bar and agents.</summary>
    public Decimate.Result LastResult { get; private set; }

    protected override void Apply()
    {
        var total = new Decimate.Result();
        foreach (var (_, mesh, polys) in ActivePolygons().ToList())
        {
            int tris = polys.Sum(i => Math.Max(0, mesh.Polygons[i].Count - 2));
            int target = Target > 0 ? Target : (int)Math.Ceiling(tris * Percent / 100);
            var r = Decimate.Reduce(mesh, polys, target);
            total = new Decimate.Result(total.TrianglesBefore + r.TrianglesBefore, total.TrianglesAfter + r.TrianglesAfter, total.PointsRemoved + r.PointsRemoved);
        }
        if (total.TrianglesBefore == 0) throw new InvalidOperationException("Reduce Polygons needs faces: select some (or none, for all of them).");
        Doc.Selection.Clear(allModes: true);
        LastResult = total;
    }
}

/// <summary>Remesh: an even skin of quads over a closed solid.</summary>
public sealed class RemeshTool : Tool
{
    public override string Name => "Remesh";
    public override string Tab => "Polygon";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Rebuilds the selected faces (every face, with none selected), which must make closed solids, as an even skin of quads Voxel metres across (surface nets on a grid of cubes), smoothed Smoothing times and pulled back onto the original surface each time. A clean base for sculpting, and a repair for messy Boolean results. UVs aren't kept; the new faces take the first face's surface.";

    [Param("Voxel", Distance = true, Min = 0, Description = "The size of the cubes, and so of the new quads, in metres.")] public double Voxel { get; set; } = 0.05;
    [Param("Smoothing", Min = 0, Max = 20, Step = 1, Description = "Passes evening the quads out.")] public int Smoothing { get; set; } = 2;

    protected override void Apply()
    {
        bool any = false;
        foreach (var (_, mesh, polys) in ActivePolygons().ToList())
        {
            if (polys.Count == 0) continue;
            Remesh.Apply(mesh, polys, Voxel, Smoothing);
            any = true;
        }
        if (!any) throw new InvalidOperationException("Remesh needs faces making a closed solid.");
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>Measure: a ruler between two places in a view.</summary>
public sealed class MeasureTool : Tool
{
    public override string Name => "Measure";
    public override string Tab => "Tools";
    public override string Help => "Drag from one place to another to measure between them (ends snap to points near the mouse); Keep Ruler leaves it drawn.";
    public override string Summary => "Measures from From to To: the distance and its X, Y and Z parts, drawn as a ruler in the views. Changes nothing in the object. From the mouse each end snaps to a point within a few pixels, so measuring corner to corner is exact.";
    protected override bool SelectsOnly => true;

    [Param("From", Distance = true)] public Vec3 From { get; set; }
    [Param("To", Distance = true)] public Vec3 To { get; set; }
    [Param("Snap to points", Description = "From the mouse: each end goes to a point of the model within a few pixels.")] public bool SnapToPoints { get; set; } = true;

    public double Length => (To - From).Length;

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) From = Snap(d, d.Start);
        To = Snap(d, d.Constrain ? d.Start + Axes.Dominant(d.Delta) : d.Current);
    }

    /// <summary>The nearest point of the visible layers to a place, as the view sees it, if it's within a few pixels.</summary>
    private Vec3 Snap(DragInfo d, Vec3 at)
    {
        if (!SnapToPoints) return Context.SnapPoint(at);
        double reach = d.UnitsPerPixel * 8, best = reach * reach;
        Vec3? found = null;
        foreach (var layer in Doc.Layers.Where(l => l.State != LayerState.Off))
            foreach (var p in layer.Mesh.Points)
            {
                var v = p - at;
                double dist = Math.Pow(Vec3.Dot(v, d.Right), 2) + Math.Pow(Vec3.Dot(v, d.Up), 2);
                if (dist < best) { best = dist; found = p; }
            }
        return found ?? Context.SnapPoint(at);
    }

    protected override void Apply() => Doc.Measuring = new Dimension(From, To);
}
