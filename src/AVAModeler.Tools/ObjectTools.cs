using AVAModeler.Core;

namespace AVAModeler.Tools;

/// <summary>Sets a box's extent along the two axes of the view a drag is made in, leaving the third as it was.</summary>
internal static class DragShapes
{
    public static void Span(DragInfo d, ToolContext ctx, ref Vec3 center, ref Vec3 size)
    {
        var a = ctx.SnapPoint(d.Start);
        var b = ctx.SnapPoint(d.Current);
        foreach (var axis in new[] { d.Right, d.Up })
        {
            Axis ax = Math.Abs(axis.X) > 0.5 ? Axis.X : Math.Abs(axis.Y) > 0.5 ? Axis.Y : Axis.Z;
            center = center.With(ax, (a[ax] + b[ax]) / 2);
            size = size.With(ax, Math.Abs(b[ax] - a[ax]));
        }
    }

    /// <summary>A radius per view axis from a drag out from a centre (Shift: the same on every axis).</summary>
    public static void Radius(DragInfo d, ToolContext ctx, ref Vec3 center, ref Vec3 radius, bool first)
    {
        if (first) center = ctx.SnapPoint(d.Start);
        var delta = ctx.SnapPoint(d.Current) - center;
        foreach (var axis in new[] { d.Right, d.Up })
        {
            Axis ax = Math.Abs(axis.X) > 0.5 ? Axis.X : Math.Abs(axis.Y) > 0.5 ? Axis.Y : Axis.Z;
            radius = radius.With(ax, Math.Abs(delta[ax]));
        }
        if (d.Constrain)
        {
            double r = Math.Max(radius.X, Math.Max(radius.Y, radius.Z));
            radius = new Vec3(r, r, r);
        }
    }
}

/// <summary>Box: drag out a box in one view, then its depth in another. With no depth it's a flat grid.</summary>
public sealed class BoxTool : Tool
{
    public override string Name => "Box";
    public override string Tab => "Objects";
    public override string Help => "Drag to set the box in a view; drag in another view for its depth. Enter makes it.";

    [Param("Center", Distance = true)] public Vec3 Center { get; set; }
    [Param("Size", Distance = true, Min = 0)] public Vec3 Size { get; set; } = new(1, 1, 1);
    [Param("Segments X", Min = 1, Max = 1000, Step = 1)] public int SegmentsX { get; set; } = 1;
    [Param("Segments Y", Min = 1, Max = 1000, Step = 1)] public int SegmentsY { get; set; } = 1;
    [Param("Segments Z", Min = 1, Max = 1000, Step = 1)] public int SegmentsZ { get; set; } = 1;
    [Param("Surface")] public string Surface { get; set; } = "Default";

    private bool _dragged;

    protected override void OnBegin() => _dragged = false;

    protected override void OnDrag(DragInfo d)
    {
        var c = Center;
        var s = _dragged ? Size : Vec3.Zero;
        DragShapes.Span(d, Context, ref c, ref s);
        Center = c;
        Size = s;
        _dragged = true;
    }

    protected override void Apply() => Primitives.Box(Doc.Current.Mesh, Center, Size, SegmentsX, SegmentsY, SegmentsZ, SurfaceFor(Surface));
    protected override IEnumerable<int> Layers => [Doc.CurrentLayer];
}

/// <summary>The two kinds of ball.</summary>
public enum BallKind { Globe, Tessellated }

/// <summary>Ball: a globe of rings and segments, or a tessellated (geodesic) ball.</summary>
public sealed class BallTool : Tool
{
    public override string Name => "Ball";
    public override string Tab => "Objects";
    public override string Help => "Drag out from the centre; Shift keeps it round.";

    [Param("Kind")] public BallKind Style { get; set; } = BallKind.Globe;
    [Param("Center", Distance = true)] public Vec3 Center { get; set; }
    [Param("Radius", Distance = true, Min = 0)] public Vec3 Radius { get; set; } = new(0.5, 0.5, 0.5);
    [Param("Sides", Min = 3, Max = 1000, Step = 1)] public int Sides { get; set; } = 24;
    [Param("Segments", Min = 2, Max = 1000, Step = 1)] public int Segments { get; set; } = 12;
    [Param("Level", Min = 0, Max = 6, Step = 1)] public int Level { get; set; } = 2;
    [Param("Axis")] public Axis Axis { get; set; } = Axis.Y;
    [Param("Surface")] public string Surface { get; set; } = "Default";

    protected override void OnDrag(DragInfo d)
    {
        var c = Center;
        var r = Radius;
        if (d.First) r = Vec3.Zero;
        DragShapes.Radius(d, Context, ref c, ref r, d.First);
        // The radius across the view's depth follows the larger of the other two.
        Axis depth = d.ViewAxis;
        if (d.First || r[depth] == 0) r = r.With(depth, Math.Max(r.X, Math.Max(r.Y, r.Z)));
        Center = c;
        Radius = r;
    }

    protected override void Apply()
    {
        int s = SurfaceFor(Surface);
        if (Style == BallKind.Globe) Primitives.Globe(Doc.Current.Mesh, Center, Radius, Sides, Segments, Axis, s);
        else Primitives.TessellatedBall(Doc.Current.Mesh, Center, Radius, Level, s);
    }

    protected override IEnumerable<int> Layers => [Doc.CurrentLayer];
}

/// <summary>Disc: a cylinder (or a flat disc) along an axis.</summary>
public class DiscTool : Tool
{
    public override string Name => "Disc";
    public override string Tab => "Objects";
    public override string Help => "Drag out the radius in the view along whose axis it lies; set the height numerically or by dragging in another view.";

    [Param("Center", Distance = true)] public Vec3 Center { get; set; }
    [Param("Radius", Distance = true, Min = 0)] public Vec3 Radius { get; set; } = new(0.5, 0.5, 0.5);
    [Param("Height", Distance = true)] public double Height { get; set; } = 1;
    [Param("Sides", Min = 3, Max = 1000, Step = 1)] public int Sides { get; set; } = 24;
    [Param("Segments", Min = 1, Max = 1000, Step = 1)] public int Segments { get; set; } = 1;
    [Param("Axis")] public Axis Axis { get; set; } = Axis.Y;
    [Param("Caps")] public bool Caps { get; set; } = true;
    [Param("Surface")] public string Surface { get; set; } = "Default";

    protected virtual double TopScale => 1;

    protected override void OnDrag(DragInfo d)
    {
        if (d.ViewAxis == Axis || d.First)
        {
            if (d.First) Axis = d.ViewAxis;
            var c = Center;
            var r = Radius;
            DragShapes.Radius(d, Context, ref c, ref r, d.First);
            Center = c;
            Radius = r;
            return;
        }
        // In a side-on view, the drag along the axis sets the height.
        Height = Context.SnapValue(d.Current[Axis] - d.Start[Axis]) * 2;
    }

    protected override void Apply() =>
        Primitives.Disc(Doc.Current.Mesh, Center, Radius, Height, Sides, Segments, Axis, TopScale, Caps, SurfaceFor(Surface));

    protected override IEnumerable<int> Layers => [Doc.CurrentLayer];
}

/// <summary>Cone: a disc whose top comes to a point.</summary>
public sealed class ConeTool : DiscTool
{
    public override string Name => "Cone";
    protected override double TopScale => 0;
}

/// <summary>Points: click to add a point (as a point, not a polygon); drag to place it.</summary>
public sealed class PointsTool : Tool
{
    public override string Name => "Points";
    public override string Tab => "Objects";
    public override string Help => "Click to add points; drag to place each one. Enter keeps them.";

    private readonly List<Vec3> _points = [];

    /// <summary>The points added so far.</summary>
    public IReadOnlyList<Vec3> Added => _points;

    protected override void OnBegin() => _points.Clear();

    protected override void OnDrag(DragInfo d)
    {
        if (d.First || _points.Count == 0) _points.Add(Context.SnapPoint(d.Current));
        else _points[^1] = Context.SnapPoint(d.Current);
    }

    protected override void Apply()
    {
        var mesh = Doc.Current.Mesh;
        foreach (var p in _points)
        {
            int i = mesh.AddPoint(p);
            Doc.Selection.SelectPoint(Doc.CurrentLayer, i);
        }
    }

    protected override IEnumerable<int> Layers => [Doc.CurrentLayer];
}

/// <summary>Make Polygon: a polygon through the selected points, in the order they were selected.</summary>
public sealed class MakePolygonTool : Tool
{
    public override string Name => "Make Polygon";
    public override string Tab => "Objects";
    public override ToolKind Kind => ToolKind.Immediate;
    public override string Help => "Select points in order (one, two, or three or more), then make a polygon through them.";

    [Param("Surface")] public string Surface { get; set; } = "Default";

    protected override void Apply()
    {
        int surface = SurfaceFor(Surface);
        foreach (int l in Doc.ForegroundLayers)
        {
            var points = OriginalSelection.OrderedPoints(l);
            if (points.Count == 0) continue;
            var mesh = Doc.Layers[l].Mesh;
            int pi = mesh.AddPolygon(points, surface);
            Doc.Selection.Clear(allModes: true);
            Doc.Selection.Mode = SelectionMode.Polygons;
            Doc.Selection.PolygonsIn(l).Add(pi);
        }
    }
}
