using AVAModeler.Core;

namespace AVAModeler.Tools;

/// <summary>Shared by the Modify tools: an axis's unit vector, and a range along an axis measured from the selection.</summary>
internal static class Axes
{
    public static Axis Of(Vec3 v) => Math.Abs(v.X) >= Math.Abs(v.Y) && Math.Abs(v.X) >= Math.Abs(v.Z) ? Axis.X : Math.Abs(v.Y) >= Math.Abs(v.Z) ? Axis.Y : Axis.Z;

    /// <summary>Where a coordinate lies between the low and high ends of a range, 0 to 1.</summary>
    public static double Fraction(double v, double low, double high) => high - low < 1e-12 ? 0 : Math.Clamp((v - low) / (high - low), 0, 1);

    public static double Smooth(double t) => t * t * (3 - 2 * t);

    /// <summary>Keeps only the largest component (a drag constrained to one axis).</summary>
    public static Vec3 Dominant(Vec3 v) => Of(v) switch { Axis.X => new Vec3(v.X, 0, 0), Axis.Y => new Vec3(0, v.Y, 0), _ => new Vec3(0, 0, v.Z) };
}

/// <summary>Move: shifts the selection by an offset.</summary>
public sealed class MoveTool : Tool
{
    public override string Name => "Move";
    public override string Tab => "Modify";
    public override string Summary => "Moves the selected points (or, with nothing selected, everything in the foreground layers) by Offset, in metres.";
    public override string Help => "Drag to move; Shift keeps to one axis.";

    [Param("Offset", Distance = true)] public Vec3 Offset { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        var delta = d.Constrain ? Axes.Dominant(d.Delta) : d.Delta;
        Offset = Context.Snap > 0 ? Context.SnapPoint(delta) : delta;
    }

    protected override void Apply() => MapPoints(p => p + Offset);
}

/// <summary>Rotate: turns the selection about an axis through the action centre.</summary>
public sealed class RotateTool : Tool
{
    public override string Name => "Rotate";
    public override string Tab => "Modify";
    public override string Summary => "Turns the selection about Axis through Center by Angle degrees. Right-handed: a positive angle about Z turns +X toward +Y, about Y turns +Z toward +X, about X turns +Y toward +Z.";
    public override string Help => "Drag left and right to turn about the axis the view looks down; Shift turns in 15° steps.";

    [Param("Angle", Angle = true, Step = 1)] public double Angle { get; set; }
    [Param("Axis", Description = "The axis it turns about.")] public Axis Axis { get; set; } = Axis.Z;
    [Param("Center", Distance = true, Description = "The point it turns about.")] public Vec3 Center { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) Center = Context.Center();
        Axis = d.ViewAxis;
        double a = d.DxPixels * 0.5 * Math.Sign(Vec3.Dot(d.Forward, Vec3.Unit(Axis)));
        Angle = d.Constrain ? Math.Round(a / 15) * 15 : Math.Round(a, 1);
    }

    protected override void Apply()
    {
        var m = Mat4.About(Center, Mat4.Rotation(Vec3.Unit(Axis), Mat4.Deg(Angle)));
        MapPoints(m.TransformPoint);
    }
}

/// <summary>Size: scales the selection evenly about the action centre.</summary>
public sealed class SizeTool : Tool
{
    public override string Name => "Size";
    public override string Tab => "Modify";
    public override string Summary => "Scales the selection evenly about Center by Factor percent (100 leaves it as it is, 200 doubles it).";
    public override string Help => "Drag right to grow, left to shrink.";

    [Param("Factor %", Step = 1, Description = "Percent: 100 leaves it, 50 halves it.")] public double Factor { get; set; } = 100;
    [Param("Center", Distance = true, Description = "The point it scales about.")] public Vec3 Center { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) Center = Context.Center();
        Factor = Math.Round(100 * Math.Exp(d.DxPixels / 150), 2);
    }
    protected override void Apply() => MapPoints(p => Center + (p - Center) * (Factor / 100));
}

/// <summary>Stretch: scales the selection by a different factor on each axis.</summary>
public sealed class StretchTool : Tool
{
    public override string Name => "Stretch";
    public override string Tab => "Modify";
    public override string Summary => "Scales the selection about Center by a different percentage along each axis (Factor is [x, y, z] percent; 100 leaves an axis as it is, -100 mirrors it).";
    public override string Help => "Drag across to stretch along the view's horizontal axis, up and down for its vertical; Shift for both alike.";

    [Param("Factor %", Step = 1, Description = "Percent along X, Y and Z: 100 leaves an axis, -100 mirrors it.")] public Vec3 Factor { get; set; } = new(100, 100, 100);
    [Param("Center", Distance = true, Description = "The point it scales about.")] public Vec3 Center { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) Center = Context.Center();
        double fx = 100 * Math.Exp(d.DxPixels / 150), fy = 100 * Math.Exp(-d.DyPixels / 150);
        if (d.Constrain) fx = fy = Math.Abs(d.DxPixels) > Math.Abs(d.DyPixels) ? fx : fy;
        var f = new Vec3(100, 100, 100).With(Axes.Of(d.Right), Math.Round(fx, 2)).With(Axes.Of(d.Up), Math.Round(fy, 2));
        Factor = f;
    }

    protected override void Apply() => MapPoints(p => Center + Vec3.Scale(p - Center, Factor / 100));
}

/// <summary>Shear: slides the selection by an offset that grows from nothing at one end of an axis to all of it at the other.</summary>
public sealed class ShearTool : Tool
{
    public override string Name => "Shear";
    public override string Tab => "Modify";
    public override string Summary => "Slides the selection by Offset in proportion to how far each point lies along the Along axis: none at the selection's low end, all of it at its high end (Smooth falloff eases in and out).";
    public override string Help => "Drag to slide the top of the selection (as the view shows it) against its bottom.";

    [Param("Offset", Distance = true)] public Vec3 Offset { get; set; }
    /// <summary>The axis along which the shear grows.</summary>
    [Param("Along", Description = "The axis along which the shear grows from none to all of Offset.")] public Axis Along { get; set; } = Axis.Y;
    [Param("Smooth falloff")] public bool SmoothFalloff { get; set; }

    private double _low, _high;

    protected override void OnBegin() => Measure();

    private void Measure()
    {
        var b = ActiveBounds();
        _low = b.IsEmpty ? 0 : b.Min[Along];
        _high = b.IsEmpty ? 0 : b.Max[Along];
    }

    protected override void OnDrag(DragInfo d)
    {
        bool across = Math.Abs(d.DxPixels) >= Math.Abs(d.DyPixels);
        Along = Axes.Of(across ? d.Up : d.Right);
        Offset = across ? d.Right * Vec3.Dot(d.Delta, d.Right) : d.Up * Vec3.Dot(d.Delta, d.Up);
    }

    protected override void Apply()
    {
        Measure();
        MapPoints(p =>
        {
            double t = Axes.Fraction(p[Along], _low, _high);
            return p + Offset * (SmoothFalloff ? Axes.Smooth(t) : t);
        });
    }
}

/// <summary>Twist: turns the selection about an axis, by nothing at one end of it and the whole angle at the other.</summary>
public sealed class TwistTool : Tool
{
    public override string Name => "Twist";
    public override string Tab => "Modify";
    public override string Summary => "Turns the selection about Axis through Center, by nothing at the selection's low end along the axis and the whole Angle (degrees, right-handed) at its high end.";
    public override string Help => "Drag left and right to twist about the view's vertical axis.";

    [Param("Angle", Angle = true, Step = 1)] public double Angle { get; set; }
    [Param("Axis", Description = "The axis it twists about (and along which the twist grows).")] public Axis Axis { get; set; } = Axis.Y;
    [Param("Center", Distance = true, Description = "A point the twist axis passes through.")] public Vec3 Center { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) Center = Context.Center();
        Axis = Axes.Of(d.Up);
        Angle = Math.Round(d.DxPixels * 0.5, 1);
    }

    protected override void Apply()
    {
        var b = ActiveBounds();
        double low = b.IsEmpty ? 0 : b.Min[Axis], high = b.IsEmpty ? 0 : b.Max[Axis];
        var axis = Vec3.Unit(Axis);
        MapPoints(p => Mat4.About(Center, Mat4.Rotation(axis, Mat4.Deg(Angle * Axes.Fraction(p[Axis], low, high)))).TransformPoint(p));
    }
}

/// <summary>Taper: scales across an axis by nothing at one end and the whole factor at the other.</summary>
public sealed class TaperTool : Tool
{
    public override string Name => "Taper";
    public override string Tab => "Modify";
    public override string Summary => "Scales the selection across Axis about Center: not at all at its low end along the axis, by Factor percent at its high end (Smooth falloff eases in and out).";
    public override string Help => "Drag left and right to narrow or widen the top of the selection (as the view shows it).";

    [Param("Factor %", Step = 1, Description = "Percent at the high end of the axis: 0 brings it to a point.")] public double Factor { get; set; } = 100;
    [Param("Axis", Description = "The axis along which the taper grows; scaling is across it.")] public Axis Axis { get; set; } = Axis.Y;
    [Param("Center", Distance = true, Description = "The point it scales toward.")] public Vec3 Center { get; set; }
    [Param("Smooth falloff")] public bool SmoothFalloff { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) Center = Context.Center();
        Axis = Axes.Of(d.Up);
        Factor = Math.Round(100 * Math.Exp(d.DxPixels / 150), 2);
    }

    protected override void Apply()
    {
        var b = ActiveBounds();
        double low = b.IsEmpty ? 0 : b.Min[Axis], high = b.IsEmpty ? 0 : b.Max[Axis];
        MapPoints(p =>
        {
            double t = Axes.Fraction(p[Axis], low, high);
            if (SmoothFalloff) t = Axes.Smooth(t);
            double s = 1 + (Factor / 100 - 1) * t;
            return Center + Scaled(p - Center, s);
        });
    }

    private Vec3 Scaled(Vec3 v, double s) => Axis switch
    {
        Axis.X => new Vec3(v.X, v.Y * s, v.Z * s),
        Axis.Y => new Vec3(v.X * s, v.Y, v.Z * s),
        _ => new Vec3(v.X * s, v.Y * s, v.Z),
    };
}

/// <summary>
/// Bend: curves the selection along an axis into an arc toward another axis, the base staying put and the whole
/// angle reached at the far end.
/// </summary>
public sealed class BendTool : Tool
{
    public override string Name => "Bend";
    public override string Tab => "Modify";
    public override string Summary => "Curves the selection, which lies along Axis, into an arc toward the Toward axis: the low end stays put and the high end is turned by Angle degrees (positive bends toward +Toward). Center sets where across the arc is measured from.";
    public override string Help => "Drag left and right to bend the selection over (as the view shows it).";

    [Param("Angle", Angle = true, Step = 1)] public double Angle { get; set; }
    /// <summary>The axis the selection lies along.</summary>
    [Param("Axis", Description = "The axis the selection lies along.")] public Axis Axis { get; set; } = Axis.Y;
    /// <summary>The axis it bends toward (positive angles toward its positive end).</summary>
    [Param("Toward", Description = "The axis it bends toward (positive angles toward its + end).")] public Axis Toward { get; set; } = Axis.X;
    [Param("Center", Distance = true)] public Vec3 Center { get; set; }

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) Center = Context.Center();
        Axis = Axes.Of(d.Up);
        Toward = Axes.Of(d.Right);
        Angle = Math.Round(d.DxPixels * 0.5 * Math.Sign(d.Right[Toward]), 1);
    }

    protected override void Apply()
    {
        if (Axis == Toward || Math.Abs(Angle) < 1e-9) return;
        var b = ActiveBounds();
        if (b.IsEmpty) return;
        double low = b.Min[Axis], length = b.Max[Axis] - low;
        if (length < 1e-12) return;
        double theta = Mat4.Deg(Angle), radius = length / theta;
        double c = Center[Toward];
        MapPoints(p =>
        {
            double s = p[Axis] - low;
            double d = p[Toward] - c;
            double phi = theta * Math.Clamp(s / length, 0, 1);
            // Past the top of the range the rest carries on straight, along the end's tangent.
            double over = Math.Max(0, s - length);
            double along = low + (radius - d) * Math.Sin(phi) + over * Math.Cos(phi);
            double across = c + radius - (radius - d) * Math.Cos(phi) + over * Math.Sin(phi);
            return p.With(Axis, along).With(Toward, across);
        });
    }
}

/// <summary>Magnet: pulls the points near a centre by an offset, fading to nothing at a radius.</summary>
public sealed class MagnetTool : Tool
{
    public override string Name => "Magnet";
    public override string Tab => "Modify";
    public override string Summary => "Pulls the points within Radius (per axis, an ellipsoid) of Center by Offset, fully at the centre and fading smoothly to nothing at the radius.";
    public override string Help => "Drag from where the pull should be strongest.";

    [Param("Offset", Distance = true)] public Vec3 Offset { get; set; }
    [Param("Center", Distance = true)] public Vec3 Center { get; set; }
    [Param("Radius", Distance = true, Min = 0, Description = "How far the pull reaches along X, Y and Z.")] public Vec3 Radius { get; set; }

    protected override void OnBegin()
    {
        var b = ActiveBounds();
        if (Radius == Vec3.Zero) Radius = Vec3.One * Math.Max(b.Extent / 3, 0.25);
    }

    protected override void OnDrag(DragInfo d)
    {
        if (d.First) Center = d.Start;
        Offset = d.Delta;
    }

    protected override void Apply()
    {
        MapPoints(p =>
        {
            var v = p - Center;
            double rx = Math.Max(Radius.X, 1e-12), ry = Math.Max(Radius.Y, 1e-12), rz = Math.Max(Radius.Z, 1e-12);
            double r = Math.Sqrt(v.X * v.X / (rx * rx) + v.Y * v.Y / (ry * ry) + v.Z * v.Z / (rz * rz));
            if (r >= 1) return p;
            double w = (1 - r * r) * (1 - r * r);
            return p + Offset * w;
        });
    }
}

/// <summary>Drag: moves the one point nearest where the drag begins.</summary>
public sealed class DragTool : Tool
{
    public override string Name => "Drag";
    public override string Tab => "Modify";
    public override string Summary => "Moves the single point nearest where a mouse drag starts. Only works with the mouse: from a script select the point and use Move.";
    public override bool Scriptable => false;
    public override string Help => "Drag a point to move just that point.";

    [Param("Offset", Distance = true)] public Vec3 Offset { get; set; }

    private (int Layer, int Point)? _target;

    /// <summary>The point being dragged, if one has been picked.</summary>
    public (int Layer, int Point)? Target => _target;

    protected override void OnBegin() => _target = null;

    protected override void OnDrag(DragInfo d)
    {
        if (d.First || _target == null)
        {
            double best = double.PositiveInfinity;
            foreach (var (layer, mesh, points) in ActivePoints())
                foreach (int i in points)
                {
                    var v = Original(layer).Points[i] - d.Start;
                    // Distance as the view sees it: ignore depth.
                    double dist = Math.Pow(Vec3.Dot(v, d.Right), 2) + Math.Pow(Vec3.Dot(v, d.Up), 2);
                    if (dist < best) { best = dist; _target = (layer, i); }
                }
        }
        Offset = Context.Snap > 0 ? Context.SnapPoint(d.Delta) : d.Delta;
    }

    protected override void Apply()
    {
        if (_target is not { } t || t.Layer >= Doc.Layers.Count) return;
        var mesh = Doc.Layers[t.Layer].Mesh;
        if (t.Point >= mesh.Points.Count) return;
        mesh.Points[t.Point] += Offset;
        mesh.Touch();
    }
}

/// <summary>Jitter: moves each point by a random amount (the same every time for the same seed).</summary>
public sealed class JitterTool : Tool
{
    public override string Name => "Jitter";
    public override string Tab => "Modify";
    public override string Summary => "Moves each selected point by a random amount up to Amount along each axis. The same Seed always gives the same result.";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Amount", Distance = true, Min = 0, Description = "The most a point can move along X, Y and Z.")] public Vec3 Amount { get; set; } = new(0.05, 0.05, 0.05);
    [Param("Seed", Step = 1)] public int Seed { get; set; } = 1;

    protected override void Apply()
    {
        foreach (var (layer, mesh, points) in ActivePoints())
        {
            foreach (int i in points)
            {
                double R(int k)
                {
                    uint h = (uint)(i * 73856093) ^ (uint)(Seed * 19349663) ^ (uint)(k * 83492791) ^ (uint)(layer * 2654435761u);
                    h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                    return h / (double)uint.MaxValue * 2 - 1;
                }
                mesh.Points[i] += new Vec3(R(0) * Amount.X, R(1) * Amount.Y, R(2) * Amount.Z);
            }
            mesh.Touch();
        }
    }
}

/// <summary>Smooth: eases each point toward the average of its neighbours, a little at a time.</summary>
public sealed class SmoothTool : Tool
{
    public override string Name => "Smooth";
    public override string Tab => "Modify";
    public override string Summary => "Eases each selected point toward the average of its neighbours, Iterations times at Strength (0 to 1). Keep volume uses Taubin smoothing, which shrinks the shape less.";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Strength", Min = 0, Max = 1, Step = 0.05, Description = "How far each step moves a point toward its neighbours' average.")] public double Strength { get; set; } = 0.5;
    [Param("Iterations", Min = 1, Max = 1000, Step = 1)] public int Iterations { get; set; } = 3;
    /// <summary>Taubin smoothing: smooths without shrinking the shape as much.</summary>
    [Param("Keep volume")] public bool KeepVolume { get; set; }

    protected override void Apply()
    {
        foreach (var (_, mesh, points) in ActivePoints())
            Geometry.Subdivision.Smooth(mesh, points.ToList(), Strength, Iterations, KeepVolume);
    }
}

/// <summary>Center: moves the selection so the middle of its bounds is at the origin (on the chosen axes).</summary>
public sealed class CenterTool : Tool
{
    public override string Name => "Center";
    public override string Tab => "Modify";
    public override string Summary => "Moves the selection so the middle of its bounding box is at the origin, on the axes that are on.";
    public override ToolKind Kind => ToolKind.Immediate;

    [Param("X")] public bool X { get; set; } = true;
    [Param("Y")] public bool Y { get; set; } = true;
    [Param("Z")] public bool Z { get; set; } = true;

    protected override void Apply()
    {
        var c = ActiveBounds().Center;
        var shift = new Vec3(X ? -c.X : 0, Y ? -c.Y : 0, Z ? -c.Z : 0);
        MapPoints(p => p + shift);
    }
}

/// <summary>Quantize: snaps every point to a grid.</summary>
public sealed class QuantizeTool : Tool
{
    public override string Name => "Quantize";
    public override string Tab => "Modify";
    public override string Summary => "Snaps every selected point to a grid with Step spacing per axis (0: leave that axis).";
    public override ToolKind Kind => ToolKind.Requester;

    [Param("Step", Distance = true, Min = 0, Description = "Grid spacing along X, Y and Z (0: leave that axis).")] public Vec3 Step { get; set; } = new(0.1, 0.1, 0.1);

    protected override void Apply()
    {
        static double Q(double v, double s) => s > 0 ? Math.Round(v / s) * s : v;
        MapPoints(p => new Vec3(Q(p.X, Step.X), Q(p.Y, Step.Y), Q(p.Z, Step.Z)));
    }
}
