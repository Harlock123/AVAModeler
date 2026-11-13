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

/// <summary>Instance: linked copies of the current layer, placed in another layer, that follow it as it changes.</summary>
public sealed class InstanceTool : Tool
{
    public override string Name => "Instance";
    public override string Tab => "Multiply";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Summary => "Puts linked copies of the current layer's mesh in another layer (Layer, numbered from 1; 0: the first empty layer after it), stepped as Clone steps them: each by Offset, turned by Rotation (HPB degrees) and scaled by Scale about Center from the one before. They're drawn and rendered like real geometry and follow every change to the source, but stay one mesh and the places (kept in .lwo; every other format and 3D printing get real copies). Run it again to change them; Freeze Instances makes them real.";

    [Param("Layer", Min = 0, Max = 100, Step = 1, Description = "The layer to put the instances in (from 1); 0: the first empty one after the current layer.")] public int TargetLayer { get; set; }
    [Param("Copies", Min = 1, Max = 100000, Step = 1)] public int Copies { get; set; } = 4;
    [Param("Offset", Distance = true, Description = "How far each copy moves on from the last.")] public Vec3 Offset { get; set; } = new(1, 0, 0);
    [Param("Rotation (HPB)", Angle = true, Description = "Heading (about Y), pitch (about X) and bank (about Z) per copy.")] public Vec3 Rotation { get; set; }
    [Param("Scale", Description = "Scale factors per copy along X, Y and Z (1: unchanged).")] public Vec3 Scale { get; set; } = Vec3.One;
    [Param("Center", Distance = true, Description = "The point copies turn and scale about.")] public Vec3 Center { get; set; }

    private int Target()
    {
        int source = Doc.CurrentLayer;
        if (TargetLayer > 0) return TargetLayer - 1;
        for (int i = source + 1; i < Doc.Layers.Count; i++)
            if (Doc.Layers[i].Mesh.IsEmpty && (Doc.Layers[i].Instances == null || Doc.Layers[i].Instances!.Source == source)) return i;
        return Doc.Layers.Count;
    }

    protected override IEnumerable<int> Layers
    {
        get
        {
            int t = Target();
            while (Doc.Layers.Count <= t) Doc.Layers.Add(new Layer());
            return Doc.ForegroundLayers.Append(t).Distinct();
        }
    }

    protected override void Apply()
    {
        int source = Doc.CurrentLayer, target = Target();
        if (target == source) throw new ArgumentException("Instances go in another layer than the one they copy: choose a different Layer.");
        if (Doc.Layers[source].Instances != null) throw new InvalidOperationException("The current layer has instances of its own: instances copy a plain layer (Freeze Instances first).");
        while (Doc.Layers.Count <= target) Doc.Layers.Add(new Layer());
        var step = Mat4.Translation(Offset) * Mat4.About(Center, Mat4.HeadingPitchBank(Rotation.X, Rotation.Y, Rotation.Z) * Mat4.Scaling(Scale));
        var places = new List<Mat4>();
        var m = Mat4.Identity;
        for (int k = 1; k <= Copies; k++)
        {
            m = step * m;
            places.Add(m);
        }
        var layer = Doc.Layers[target];
        layer.Instances = new LayerInstances { Source = source, Places = places };
        if (layer.State == LayerState.Off) layer.State = LayerState.Background;
        Doc.Layers[target].Mesh.Touch();
    }
}

/// <summary>Freeze Instances: makes the foreground layers' instances real geometry.</summary>
public sealed class FreezeInstancesTool : Tool
{
    public override string Name => "Freeze Instances";
    public override string Tab => "Multiply";
    public override ToolKind Kind => ToolKind.Immediate;
    public override string Summary => "Turns the instances in the foreground layers into real geometry, copies of their source's mesh at each place, which no longer follow it.";

    protected override void Apply()
    {
        int frozen = 0;
        foreach (int l in Doc.ForegroundLayers.ToList())
        {
            if (!Instancing.Valid(Doc, l)) continue;
            var mesh = Doc.Layers[l].Mesh;
            mesh.Append(Instancing.Copies(Doc, l));
            Doc.Layers[l].Instances = null;
            frozen++;
        }
        if (frozen == 0) throw new InvalidOperationException("No foreground layer has instances to freeze: put the layer with them in the foreground.");
    }
}
