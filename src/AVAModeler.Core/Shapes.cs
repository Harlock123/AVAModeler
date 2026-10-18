namespace AVAModeler.Core;

/// <summary>A plane: the points p with <c>Normal · p = D</c>; the normal's side is "in front".</summary>
public readonly record struct Plane(Vec3 Normal, double D)
{
    public static Plane FromPointNormal(Vec3 point, Vec3 normal)
    {
        var n = normal.Normalized();
        return new Plane(n, Vec3.Dot(n, point));
    }

    /// <summary>The plane through three points, wound anticlockwise as seen from the front.</summary>
    public static Plane FromPoints(Vec3 a, Vec3 b, Vec3 c) => FromPointNormal(a, Vec3.Cross(b - a, c - a));

    /// <summary>Signed distance: positive in front.</summary>
    public double Distance(Vec3 p) => Vec3.Dot(Normal, p) - D;

    public Vec3 Project(Vec3 p) => p - Normal * Distance(p);

    public Plane Flipped() => new(-Normal, -D);

    /// <summary>Where a line from a to b crosses the plane, as a fraction of the way from a.</summary>
    public double Crossing(Vec3 a, Vec3 b)
    {
        double da = Distance(a), db = Distance(b);
        return Math.Abs(da - db) < 1e-300 ? 0 : da / (da - db);
    }
}

/// <summary>An axis-aligned box. <see cref="Empty"/> contains nothing until a point is included.</summary>
public readonly record struct Box3(Vec3 Min, Vec3 Max)
{
    public static readonly Box3 Empty = new(new Vec3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new Vec3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;
    public Vec3 Center => IsEmpty ? Vec3.Zero : (Min + Max) / 2;
    public Vec3 Size => IsEmpty ? Vec3.Zero : Max - Min;
    /// <summary>The longest side.</summary>
    public double Extent => Math.Max(Size.X, Math.Max(Size.Y, Size.Z));

    public Box3 Include(Vec3 p) => IsEmpty ? new Box3(p, p) : new Box3(Vec3.Min(Min, p), Vec3.Max(Max, p));
    public Box3 Include(Box3 b) => b.IsEmpty ? this : IsEmpty ? b : new Box3(Vec3.Min(Min, b.Min), Vec3.Max(Max, b.Max));

    public static Box3 Of(IEnumerable<Vec3> points)
    {
        var b = Empty;
        foreach (var p in points) b = b.Include(p);
        return b;
    }

    /// <summary>A box from two opposite corners in any order.</summary>
    public static Box3 FromCorners(Vec3 a, Vec3 b) => new(Vec3.Min(a, b), Vec3.Max(a, b));

    public bool Contains(Vec3 p, double tolerance = 0) =>
        p.X >= Min.X - tolerance && p.X <= Max.X + tolerance &&
        p.Y >= Min.Y - tolerance && p.Y <= Max.Y + tolerance &&
        p.Z >= Min.Z - tolerance && p.Z <= Max.Z + tolerance;

    public bool Intersects(Box3 b) =>
        !IsEmpty && !b.IsEmpty && Min.X <= b.Max.X && Max.X >= b.Min.X && Min.Y <= b.Max.Y && Max.Y >= b.Min.Y && Min.Z <= b.Max.Z && Max.Z >= b.Min.Z;

    public Box3 Expanded(double by) => IsEmpty ? this : new Box3(Min - Vec3.One * by, Max + Vec3.One * by);
}

/// <summary>A ray: an origin and a direction (not necessarily of unit length).</summary>
public readonly record struct Ray(Vec3 Origin, Vec3 Direction)
{
    public Vec3 At(double t) => Origin + Direction * t;

    /// <summary>
    /// Möller–Trumbore: the distance along the ray to a triangle, with the hit's barycentric u and v, or null.
    /// Both sides count.
    /// </summary>
    public (double T, double U, double V)? IntersectTriangle(Vec3 a, Vec3 b, Vec3 c, double epsilon = 1e-12)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vec3.Cross(Direction, e2);
        double det = Vec3.Dot(e1, p);
        if (Math.Abs(det) < epsilon) return null;
        double inv = 1 / det;
        var s = Origin - a;
        double u = Vec3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return null;
        var q = Vec3.Cross(s, e1);
        double v = Vec3.Dot(Direction, q) * inv;
        if (v < 0 || u + v > 1) return null;
        double t = Vec3.Dot(e2, q) * inv;
        return t >= 0 ? (t, u, v) : null;
    }

    public double? IntersectPlane(Plane plane)
    {
        double d = Vec3.Dot(plane.Normal, Direction);
        if (Math.Abs(d) < 1e-15) return null;
        double t = (plane.D - Vec3.Dot(plane.Normal, Origin)) / d;
        return t >= 0 ? t : null;
    }

    /// <summary>The slab test: where the ray enters a box (0 if it starts inside), or null.</summary>
    public double? IntersectBox(Box3 box)
    {
        if (box.IsEmpty) return null;
        double tmin = 0, tmax = double.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            double o = Origin[i], d = Direction[i], lo = box.Min[i], hi = box.Max[i];
            if (Math.Abs(d) < 1e-300)
            {
                if (o < lo || o > hi) return null;
                continue;
            }
            double t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return null;
        }
        return tmin;
    }

    /// <summary>The distance from a point to the nearest place on the ray's line.</summary>
    public double DistanceTo(Vec3 p)
    {
        var d = Direction.Normalized();
        var v = p - Origin;
        return (v - d * Vec3.Dot(v, d)).Length;
    }
}
