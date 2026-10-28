using AVAModeler.Core;

namespace AVAModeler.Rendering;

/// <summary>
/// The four classic views: three looking straight down an axis, and the perspective; and the UV view, which looks at
/// a UV map laid out flat (u across, v up) as the Front view looks at the XY plane.
/// </summary>
public enum ViewKind { Top, Front, Side, Perspective, UV }

/// <summary>A point on the screen, with how far into the scene it lies (larger is further).</summary>
public readonly record struct ScreenPoint(double X, double Y, double Depth, bool Visible);

/// <summary>
/// How a view looks at the world. The orthographic views look down an axis: Top down -Y (X right, Z up), Front down
/// +Z (X right, Y up), Side from the right down -X (Z right, Y up), as in classic Modeler. The perspective view
/// orbits a target by heading and pitch. Every view maps the world through a right/up/into-screen basis, so the
/// same code projects, picks and pans in all four.
/// </summary>
public sealed class ViewCamera
{
    public ViewKind Kind { get; }
    public int Width { get; private set; } = 1;
    public int Height { get; private set; } = 1;

    /// <summary>The orthographic views' centre, or the perspective view's target.</summary>
    public Vec3 Center;
    /// <summary>Orthographic zoom: screen pixels per metre.</summary>
    public double PixelsPerUnit = 100;
    /// <summary>The perspective view's heading and pitch (degrees) and distance from its target.</summary>
    public double Heading = 35, Pitch = 25, Distance = 6;
    /// <summary>The perspective view's vertical field of view (degrees).</summary>
    public double FieldOfView = 45;

    public ViewCamera(ViewKind kind) => Kind = kind;

    public bool IsPerspective => Kind == ViewKind.Perspective;

    public void Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    /// <summary>The view's right, up and into-the-screen directions in world space.</summary>
    public (Vec3 Right, Vec3 Up, Vec3 Forward) Basis()
    {
        switch (Kind)
        {
            case ViewKind.Top: return (Vec3.UnitX, Vec3.UnitZ, -Vec3.UnitY);
            case ViewKind.Front or ViewKind.UV: return (Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
            case ViewKind.Side: return (Vec3.UnitZ, Vec3.UnitY, -Vec3.UnitX);
        }
        double h = Mat4.Deg(Heading), p = Mat4.Deg(Pitch);
        var forward = new Vec3(Math.Sin(h) * Math.Cos(p), -Math.Sin(p), Math.Cos(h) * Math.Cos(p));
        var right = new Vec3(Math.Cos(h), 0, -Math.Sin(h));
        var up = Vec3.Cross(forward, right);
        return (right, up, forward);
    }

    /// <summary>Where the perspective eye is.</summary>
    public Vec3 Eye => IsPerspective ? Center - Basis().Forward * Distance : Center - Basis().Forward * 1e6;

    /// <summary>The perspective focal length in pixels.</summary>
    public double Focal => Height / 2.0 / Math.Tan(Mat4.Deg(FieldOfView) / 2);

    public double Near => Math.Max(1e-4, Distance * 1e-3);

    public ScreenPoint Project(Vec3 p)
    {
        var (r, u, f) = Basis();
        if (!IsPerspective)
        {
            var v = p - Center;
            return new ScreenPoint(Width / 2.0 + Vec3.Dot(v, r) * PixelsPerUnit, Height / 2.0 - Vec3.Dot(v, u) * PixelsPerUnit, Vec3.Dot(v, f), true);
        }
        var e = p - Eye;
        double z = Vec3.Dot(e, f);
        if (z < Near) return new ScreenPoint(0, 0, z, false);
        double k = Focal / z;
        return new ScreenPoint(Width / 2.0 + Vec3.Dot(e, r) * k, Height / 2.0 - Vec3.Dot(e, u) * k, z, true);
    }

    /// <summary>The ray through a screen point (orthographic rays start well in front of everything).</summary>
    public Ray RayAt(double sx, double sy)
    {
        var (r, u, f) = Basis();
        double dx = sx - Width / 2.0, dy = Height / 2.0 - sy;
        if (!IsPerspective)
        {
            var onPlane = Center + r * (dx / PixelsPerUnit) + u * (dy / PixelsPerUnit);
            return new Ray(onPlane - f * 1e6, f);
        }
        var dir = (f * Focal + r * dx + u * dy).Normalized();
        return new Ray(Eye, dir);
    }

    /// <summary>
    /// The world point under a screen point, on the plane through <paramref name="through"/> facing the view (for an
    /// orthographic view, the screen axes set two coordinates and the third is kept from it).
    /// </summary>
    public Vec3 Unproject(double sx, double sy, Vec3 through)
    {
        var (r, u, f) = Basis();
        if (!IsPerspective)
        {
            var v = through - Center;
            double depth = Vec3.Dot(v, f);
            return Center + r * ((sx - Width / 2.0) / PixelsPerUnit) + u * ((Height / 2.0 - sy) / PixelsPerUnit) + f * depth;
        }
        var ray = RayAt(sx, sy);
        var plane = Plane.FromPointNormal(through, f);
        var t = ray.IntersectPlane(plane) ?? Vec3.Dot(through - Eye, f);
        return ray.At(t);
    }

    /// <summary>
    /// How far the world moves for a drag of so many pixels, on the plane through a point facing the view. For a
    /// perspective view the size of a pixel depends on the distance.
    /// </summary>
    public Vec3 DragDelta(double dxPixels, double dyPixels, Vec3 at)
    {
        var (r, u, f) = Basis();
        double perPixel = IsPerspective ? Math.Max(Near, Vec3.Dot(at - Eye, f)) / Focal : 1 / PixelsPerUnit;
        return (r * dxPixels - u * dyPixels) * perPixel;
    }

    /// <summary>Moves the view by a drag of so many pixels (the scene follows the mouse).</summary>
    public void Pan(double dxPixels, double dyPixels)
    {
        Center -= DragDelta(dxPixels, dyPixels, Center);
    }

    /// <summary>Zooms by a factor (above 1 is closer), keeping the world point under a screen point where it is.</summary>
    public void Zoom(double factor, double sx, double sy)
    {
        if (factor <= 0) return;
        if (IsPerspective)
        {
            Distance = Math.Clamp(Distance / factor, 1e-3, 1e7);
            return;
        }
        var before = Unproject(sx, sy, Center);
        PixelsPerUnit = Math.Clamp(PixelsPerUnit * factor, 1e-5, 1e9);
        var after = Unproject(sx, sy, Center);
        Center += before - after;
    }

    /// <summary>Turns the perspective view about its target.</summary>
    public void Orbit(double dHeading, double dPitch)
    {
        Heading = (Heading + dHeading) % 360;
        Pitch = Math.Clamp(Pitch + dPitch, -89.9, 89.9);
    }

    /// <summary>Frames a box: centred, with a margin.</summary>
    public void Fit(Box3 box)
    {
        if (box.IsEmpty) box = new Box3(-Vec3.One, Vec3.One);
        Center = box.Center;
        var (r, u, _) = Basis();
        double extent = Math.Max(box.Extent, 1e-3);
        if (IsPerspective)
        {
            Distance = extent * 0.9 / Math.Tan(Mat4.Deg(FieldOfView) / 2) + extent / 2;
            return;
        }
        double w = Math.Max(Span(box, r), 1e-3), h = Math.Max(Span(box, u), 1e-3);
        PixelsPerUnit = 0.85 * Math.Min(Width / w, Height / h);
    }

    private static double Span(Box3 b, Vec3 axis) => Math.Abs(b.Size.X * axis.X) + Math.Abs(b.Size.Y * axis.Y) + Math.Abs(b.Size.Z * axis.Z);

    /// <summary>
    /// The grid spacing for the current zoom: the smallest step of 1, 2 or 5 times a power of ten that leaves at
    /// least <paramref name="minPixels"/> between lines.
    /// </summary>
    public double GridStep(double minPixels = 14)
    {
        double ppu = IsPerspective ? Focal / Math.Max(Distance, 1e-6) : PixelsPerUnit;
        double raw = minPixels / ppu;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double m in new[] { 1.0, 2, 5, 10 })
            if (m * pow >= raw) return m * pow;
        return 10 * pow;
    }

    /// <summary>A length as Modeler shows it: 1 µm to kilometres.</summary>
    public static string FormatLength(double metres)
    {
        double a = Math.Abs(metres);
        string s = a switch
        {
            0 => "0 m",
            < 1e-3 => FormattableString.Invariant($"{metres * 1e6:0.###} um"),
            < 1 => FormattableString.Invariant($"{metres * 1e3:0.###} mm"),
            < 1000 => FormattableString.Invariant($"{metres:0.###} m"),
            _ => FormattableString.Invariant($"{metres / 1000:0.###} km"),
        };
        return s;
    }

    public ViewCamera Clone() => new(Kind)
    {
        Width = Width, Height = Height, Center = Center, PixelsPerUnit = PixelsPerUnit, Heading = Heading, Pitch = Pitch,
        Distance = Distance, FieldOfView = FieldOfView,
    };

    public string Title => Kind switch
    {
        ViewKind.Top => "Top (XZ)", ViewKind.Front => "Front (XY)", ViewKind.Side => "Side (ZY)", ViewKind.UV => "UV", _ => "Perspective",
    };
}
