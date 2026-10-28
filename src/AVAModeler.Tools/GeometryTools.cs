using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Tools;

// ───────────────────────────── the Polygon tab ─────────────────────────────

/// <summary>Triple: cuts faces of four or more points into triangles.</summary>
public sealed class TripleTool : Tool
{
    public override string Name => "Triple";
    public override string Tab => "Polygon";
    public override string Summary => "Cuts the selected faces of four or more points into triangles.";
    public override ToolKind Kind => ToolKind.Immediate;

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) Triangulator.Triple(mesh, polys.ToList());
    }
}

/// <summary>Align: makes the faces of each connected piece face the same way (outward, for closed pieces).</summary>
public sealed class AlignTool : Tool
{
    public override string Name => "Align";
    public override string Tab => "Polygon";
    public override string Summary => "Turns faces so that each connected piece faces one way (outward, for closed pieces).";
    public override ToolKind Kind => ToolKind.Immediate;

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) PolygonOps.Align(mesh, polys.ToList());
    }
}

/// <summary>Unify: removes polygons that use the same points as another.</summary>
public sealed class UnifyTool : Tool
{
    public override string Name => "Unify";
    public override string Tab => "Polygon";
    public override string Summary => "Removes polygons that use exactly the same points as another. Clears the selection.";
    public override ToolKind Kind => ToolKind.Immediate;

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) PolygonOps.Unify(mesh, polys.ToList());
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>How Subdivide splits faces.</summary>
public enum SubdivideMode { Faceted, Smooth }

/// <summary>Subdivide: splits faces into four, flat (faceted) or smoothed (one Catmull–Clark step, like Metaform).</summary>
public sealed class SubdivideTool : Tool
{
    public override string Name => "Subdivide";
    public override string Tab => "Polygon";
    public override string Summary => "Splits the selected faces: Faceted cuts each triangle into four and each larger face into quads round its centre, one per corner, without moving anything; Smooth does one Catmull-Clark step instead, rounding the shape (as Metaform). Clears the selection.";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Mode")] public SubdivideMode Mode { get; set; } = SubdivideMode.Faceted;

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons())
        {
            if (Mode == SubdivideMode.Faceted) Subdivision.Faceted(mesh, polys.ToList());
            else Subdivision.Metaform(mesh, polys.ToList());
        }
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>Metaform: bakes the MetaNURBS smoothing into the mesh (one level).</summary>
public sealed class MetaformTool : Tool
{
    public override string Name => "Metaform";
    public override string Tab => "Polygon";
    public override string Summary => "Does one Catmull-Clark subdivision step on the selected faces, rounding the cage into a smoother surface (bakes MetaNURBS). Clears the selection.";
    public override ToolKind Kind => ToolKind.Immediate;

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) Subdivision.Metaform(mesh, polys.ToList());
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>Merge Polygons: joins connected faces that lie flat together and share a surface into one.</summary>
public sealed class MergePolygonsTool : Tool
{
    public override string Name => "Merge Polygons";
    public override string Tab => "Polygon";
    public override string Summary => "Joins neighbouring selected faces that share a surface and lie flat to each other (within Max angle degrees) into single polygons. Clears the selection.";
    public override ToolKind Kind => ToolKind.Immediate;

    [Param("Max angle", Angle = true, Min = 0, Max = 90)] public double MaxAngle { get; set; } = 1;

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) PolygonOps.Merge(mesh, polys.ToList(), MaxAngle);
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>Split: cuts each face that holds two of the selected points into two, between them.</summary>
public sealed class SplitTool : Tool
{
    public override string Name => "Split";
    public override string Tab => "Polygon";
    public override string Summary => "Cuts each face that holds two of the selected points (Points mode) into two, along the line between them.";
    public override ToolKind Kind => ToolKind.Immediate;
    public override string Help => "Select two points on a face (Points mode), then Split.";

    protected override void Apply()
    {
        foreach (int l in Doc.ForegroundLayers)
        {
            var points = OriginalSelection.PointsIn(l).ToList();
            if (points.Count >= 2) PolygonOps.SplitAtPoints(Doc.Layers[l].Mesh, points);
        }
    }
}

// ───────────────────────────── the Multiply tab ─────────────────────────────

/// <summary>Extrude: pushes the selected faces out along an offset, with walls round the region's edge.</summary>
public sealed class ExtrudeTool : Tool
{
    public override string Name => "Extrude";
    public override string Tab => "Multiply";
    public override string Summary => "Pushes the selected faces along Offset (metres), in Segments steps, adding walls round the region's edge. Lines sweep into strips, points into lines. With Keep the base, a flipped copy stays at the start, closing the shape (for flat outlines).";
    public override string Help => "Drag to set how far and which way.";

    [Param("Offset", Distance = true, Description = "Direction and distance to push the faces.")] public Vec3 Offset { get; set; } = new(0, 0, 1);
    [Param("Segments", Min = 1, Max = 1000, Step = 1)] public int Segments { get; set; } = 1;
    [Param("Keep the base", Description = "Leave a flipped copy at the start, closing the shape.")] public bool CapBase { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        var delta = d.Constrain ? Axes.Dominant(d.Delta) : d.Delta;
        Offset = Context.Snap > 0 ? Context.SnapPoint(delta) : delta;
    }

    protected override void Apply()
    {
        if (Offset.LengthSquared < 1e-24) return;
        foreach (var (_, mesh, polys) in ActivePolygons()) Multiply.Extrude(mesh, polys.ToList(), Offset, Segments, CapBase);
    }
}

/// <summary>Lathe: sweeps the selection round an axis, through an angle, in so many sides.</summary>
public sealed class LatheTool : Tool
{
    public override string Name => "Lathe";
    public override string Tab => "Multiply";
    public override string Summary => "Sweeps the selection (usually a profile of lines, or a flat face) round Axis through Center, from Start to End degrees (right-handed) in Sides steps; Offset per turn rises along the axis for a spiral. A full turn closes on itself, merging points on the axis. Clears the selection.";
    public override string Help => "Click where the axis runs, in the view whose up-and-down it should follow.";

    [Param("Axis", Description = "The axis it sweeps round.")] public Axis Axis { get; set; } = Axis.Y;
    [Param("Center", Distance = true, Description = "A point the axis passes through.")] public Vec3 Center { get; set; }
    [Param("Start", Angle = true, Step = 1, Description = "Start angle (right-handed about Axis).")] public double Start { get; set; }
    [Param("End", Angle = true, Step = 1, Description = "End angle; End - Start of 360 is a full turn.")] public double End { get; set; } = 360;
    [Param("Sides", Min = 1, Max = 1000, Step = 1, Description = "Steps round the sweep.")] public int Sides { get; set; } = 24;
    [Param("Offset per turn", Distance = true, Description = "Rise along the axis per full turn (for spirals).")] public double Offset { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        Axis = Axes.Of(d.Up);
        Center = Context.SnapPoint(d.Current);
    }

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons())
            Multiply.Lathe(mesh, polys.ToList(), Axis, Center, Start, End, Sides, Offset);
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>Mirror: copies the selection reflected across a plane, sharing the points on the plane.</summary>
public sealed class MirrorTool : Tool
{
    public override string Name => "Mirror";
    public override string Tab => "Multiply";
    public override string Summary => "Adds a mirror image of the selected polygons reflected in the plane at right angles to Axis through Center on it (e.g. Axis X, Center 0: the plane x = 0). Points on the plane are shared, so the halves join.";
    public override string Help => "Click where the mirror plane is: across the view's horizontal axis.";

    [Param("Axis", Description = "The axis the mirror plane is at right angles to.")] public Axis Axis { get; set; } = Axis.X;
    [Param("Center", Distance = true, Description = "Where the plane crosses Axis (a coordinate on that axis).")] public double Center { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        Axis = Axes.Of(d.Right);
        Center = Context.SnapValue(d.Current[Axis]);
    }

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) Multiply.Mirror(mesh, polys.ToList(), Axis, Center);
    }
}

/// <summary>Clone: makes copies of the selection, each one stepped by an offset, a rotation and a scale.</summary>
public sealed class CloneTool : Tool
{
    public override string Name => "Clone";
    public override string Tab => "Multiply";
    public override string Summary => "Adds Copies copies of the selected polygons, each one step further on than the last: a step turns by Rotation (heading, pitch, bank in degrees) and scales by Scale about Center, then moves by Offset.";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Copies", Min = 1, Max = 10000, Step = 1, Description = "How many copies to add (the original stays).")] public int Copies { get; set; } = 1;
    [Param("Offset", Distance = true, Description = "How far each copy moves on from the last.")] public Vec3 Offset { get; set; } = new(1, 0, 0);
    /// <summary>Heading, pitch and bank per copy, in degrees.</summary>
    [Param("Rotation (HPB)", Angle = true, Description = "Heading (about Y), pitch (about X) and bank (about Z) per copy.")] public Vec3 Rotation { get; set; }
    [Param("Scale", Description = "Scale factors per copy along X, Y and Z (1: unchanged).")] public Vec3 Scale { get; set; } = Vec3.One;
    [Param("Center", Distance = true, Description = "The point copies turn and scale about.")] public Vec3 Center { get; set; }

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) Multiply.Clone(mesh, polys.ToList(), Copies, Offset, Rotation, Scale, Center);
    }
}

/// <summary>Array: copies in a rectangular block, or round an axis.</summary>
public sealed class ArrayTool : Tool
{
    public enum ArrayMode { Rectangular, Radial }

    public override string Name => "Array";
    public override string Tab => "Multiply";
    public override string Summary => "Copies the selected polygons: Rectangular makes a Count X by Count Y by Count Z grid Spacing apart (the original is one cell: counts of 2, 1, 2 give 4 in all); Radial makes Radial count evenly spaced copies round Axis through Center (the original is one of them).";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Mode")] public ArrayMode Mode { get; set; } = ArrayMode.Rectangular;
    [Param("Count X", Min = 1, Step = 1)] public int CountX { get; set; } = 3;
    [Param("Count Y", Min = 1, Step = 1)] public int CountY { get; set; } = 1;
    [Param("Count Z", Min = 1, Step = 1)] public int CountZ { get; set; } = 1;
    [Param("Spacing", Distance = true, Description = "Rectangular: distance between cells along X, Y and Z.")] public Vec3 Spacing { get; set; } = new(1, 1, 1);
    [Param("Radial count", Min = 1, Step = 1, Description = "Radial: how many in all round the full turn, the original included.")] public int RadialCount { get; set; } = 6;
    [Param("Axis", Description = "Radial: the axis copies are spread round.")] public Axis Axis { get; set; } = Axis.Y;
    [Param("Center", Distance = true, Description = "Radial: a point the axis passes through.")] public Vec3 Center { get; set; }

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons())
        {
            if (Mode == ArrayMode.Rectangular) Multiply.Array(mesh, polys.ToList(), CountX, CountY, CountZ, Spacing);
            else Multiply.RadialArray(mesh, polys.ToList(), RadialCount, Axis, Center);
        }
    }
}

/// <summary>Reads a path from the background layers: a chain of lines (2-point polygons), or failing that the points of the first polygon, or all the points in order.</summary>
public static class BackgroundPath
{
    public static List<Vec3> Of(Document doc)
    {
        var mesh = doc.BackgroundMesh();
        var lines = mesh.Polygons.Where(p => p.Count == 2).ToList();
        if (lines.Count > 0)
        {
            mesh.MergePoints(1e-9);
            lines = mesh.Polygons.Where(p => p.Count == 2).ToList();
            var next = new Dictionary<int, List<int>>();
            foreach (var l in lines)
            {
                (next.TryGetValue(l.Points[0], out var a) ? a : next[l.Points[0]] = []).Add(l.Points[1]);
                (next.TryGetValue(l.Points[1], out var b) ? b : next[l.Points[1]] = []).Add(l.Points[0]);
            }
            int start = next.FirstOrDefault(kv => kv.Value.Count == 1).Key;
            if (!next.ContainsKey(start)) start = lines[0].Points[0];
            var path = new List<int> { start };
            var seen = new HashSet<int> { start };
            while (next.TryGetValue(path[^1], out var ns) && ns.FirstOrDefault(n => !seen.Contains(n), -1) is int n and >= 0)
            {
                path.Add(n);
                seen.Add(n);
            }
            return path.Select(i => mesh.Points[i]).ToList();
        }
        var poly = mesh.Polygons.FirstOrDefault(p => p.Count > 2);
        return poly != null ? poly.Points.Select(i => mesh.Points[i]).ToList() : [.. mesh.Points];
    }
}

/// <summary>Path Extrude: extrudes the selection along a path drawn in the background layer, turning to follow it.</summary>
public sealed class PathExtrudeTool : Tool
{
    public override string Name => "Path Extrude";
    public override string Tab => "Multiply";
    public override string Summary => "Extrudes the selected faces along a path held in a background layer (a chain of 2-point lines, or a polygon's points), turning them to follow it if Follow the path is on. The profile is taken to sit at the path's first point.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "Put the path (a chain of lines or points) in a background layer, the profile in the foreground.";

    [Param("Follow the path")] public bool Orient { get; set; } = true;
    [Param("Keep the base")] public bool CapBase { get; set; }

    protected override void Apply()
    {
        var path = BackgroundPath.Of(Doc);
        if (path.Count < 2) throw new InvalidOperationException("Path Extrude needs a path of two or more points in a background layer.");
        foreach (var (_, mesh, polys) in ActivePolygons()) Multiply.PathExtrude(mesh, polys.ToList(), path, Orient, CapBase);
    }
}

/// <summary>Rail Extrude: extrudes the selection along a rail curve in the background, in even segments.</summary>
public sealed class RailExtrudeTool : Tool
{
    public override string Name => "Rail Extrude";
    public override string Tab => "Multiply";
    public override string Summary => "Extrudes the selected faces along a rail held in a background layer (a chain of 2-point lines), resampled into Segments even steps.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "Put the rail (a chain of lines) in a background layer.";

    [Param("Segments", Min = 1, Max = 1000, Step = 1)] public int Segments { get; set; } = 12;
    [Param("Follow the rail")] public bool Orient { get; set; }
    [Param("Keep the base")] public bool CapBase { get; set; }

    protected override void Apply()
    {
        var rail = BackgroundPath.Of(Doc);
        if (rail.Count < 2) throw new InvalidOperationException("Rail Extrude needs a rail of two or more points in a background layer.");
        foreach (var (_, mesh, polys) in ActivePolygons()) Multiply.RailExtrude(mesh, polys.ToList(), rail, Segments, Orient, CapBase);
    }
}

/// <summary>Bevel: insets each selected face and shifts it out along its normal, with sides.</summary>
public sealed class BevelTool : Tool
{
    public override string Name => "Bevel";
    public override string Tab => "Multiply";
    public override string Summary => "Insets each selected face by Inset and moves it out along its normal by Shift (metres; negative goes in), adding sides. Works face by face.";
    public override string Help => "Drag across for the inset, up and down for the shift.";

    [Param("Inset", Distance = true, Description = "How far each face's edges move in (negative: out).")] public double Inset { get; set; } = 0.05;
    [Param("Shift", Distance = true, Description = "How far the face moves out along its normal (negative: in).")] public double Shift { get; set; } = 0.05;

    protected override void OnDrag(DragInfo d)
    {
        Inset = Context.SnapValue(d.DxPixels * d.UnitsPerPixel);
        Shift = Context.SnapValue(-d.DyPixels * d.UnitsPerPixel);
    }

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) Multiply.Bevel(mesh, polys.ToList(), Inset, Shift);
    }
}

/// <summary>Smooth Shift: moves the selected faces out together along their averaged normals, with sides only round the edge.</summary>
public sealed class SmoothShiftTool : Tool
{
    public override string Name => "Smooth Shift";
    public override string Tab => "Multiply";
    public override string Summary => "Moves the selected faces out together by Offset (metres) along their averaged normals, adding sides only round the region's edge; faces meeting at more than Max smoothing angle split apart.";
    public override string Help => "Drag across to set the offset.";

    [Param("Offset", Distance = true, Description = "How far the faces move out along their averaged normals.")] public double Offset { get; set; } = 0.1;
    [Param("Max smoothing angle", Angle = true, Min = 0, Max = 180)] public double MaxAngle { get; set; } = 89.5;

    protected override void OnDrag(DragInfo d) => Offset = Context.SnapValue(d.DxPixels * d.UnitsPerPixel);

    protected override void Apply()
    {
        foreach (var (_, mesh, polys) in ActivePolygons()) Multiply.SmoothShift(mesh, polys.ToList(), Offset, MaxAngle);
    }
}

// ───────────────────────────── the Tools tab: booleans and drills ─────────────────────────────

/// <summary>Boolean: combines the foreground with the background layers' solid: union, subtract, intersect or add.</summary>
public sealed class BooleanTool : Tool
{
    public override string Name => "Boolean";
    public override string Tab => "Tools";
    public override string Summary => "Combines the foreground layers with the solid in the background layers: Union (inside either), Subtract (foreground less background), Intersect (inside both) or Add (both together, uncut). Both should be closed and face outward. Clears the selection.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "The background layers are the other solid. Both should be closed and face outward.";

    [Param("Operation")] public BooleanOperation Operation { get; set; } = BooleanOperation.Subtract;
    /// <summary>A surface for the faces the background cuts (blank: the background's own).</summary>
    [Param("Cut surface", Description = "A surface for the faces the background cuts (blank: the background's own).")] public string CutSurface { get; set; } = "";

    protected override void Apply()
    {
        var operand = Doc.BackgroundMesh();
        if (operand.Polygons.Count == 0) throw new InvalidOperationException("Boolean needs the other solid in a background layer.");
        var options = new BooleanOptions { CutSurface = string.IsNullOrWhiteSpace(CutSurface) ? null : Doc.SurfaceIndex(CutSurface) };
        foreach (int l in Doc.ForegroundLayers)
        {
            var layer = Doc.Layers[l];
            layer.Mesh.TakeFrom(Booleans.Apply(Operation, layer.Mesh, operand, options));
        }
        Doc.Selection.Clear(allModes: true);
    }
}

/// <summary>Drill: cuts the foreground with the background's outline, seen along an axis: Core, Tunnel, Stencil or Slice.</summary>
public sealed class DrillTool : Tool
{
    public override string Name => "Drill";
    public override string Tab => "Tools";
    public override string Summary => "Cuts the foreground's polygons with the outline of the background layers seen along Axis: Core keeps what's inside the outline, Tunnel what's outside (holes), Stencil keeps all and gives the inside Stencil surface, Slice keeps all, cut along the outline. Clears the selection.";
    public override ToolKind Kind => ToolKind.Requester;
    public override string Help => "The background's outline, looking down the axis, is the drill.";

    [Param("Operation")] public DrillOperation Operation { get; set; } = DrillOperation.Tunnel;
    [Param("Axis", Description = "The axis the outline is seen along.")] public Axis Axis { get; set; } = Axis.Z;
    [Param("Stencil surface", Description = "Stencil: the surface the inside gets.")] public string StencilSurface { get; set; } = "Stencil";

    protected override void Apply()
    {
        var operand = Doc.BackgroundMesh();
        if (operand.Polygons.Count == 0) throw new InvalidOperationException("Drill needs an outline in a background layer.");
        int stencil = Operation == DrillOperation.Stencil ? Doc.SurfaceIndex(StencilSurface) : 0;
        foreach (int l in Doc.ForegroundLayers)
        {
            var layer = Doc.Layers[l];
            layer.Mesh.TakeFrom(Drill.Apply(layer.Mesh, operand, Axis, Operation, stencil));
        }
        Doc.Selection.Clear(allModes: true);
    }
}
