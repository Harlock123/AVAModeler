namespace AVAModeler.Core;

/// <summary>The three axes. Modeler's views look down them: Top down Y, Front down Z, Side down X.</summary>
public enum Axis { X, Y, Z }

/// <summary>A point or direction in 2D, in double precision.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    /// <summary>The z of the 3D cross product: positive when b turns anticlockwise from a.</summary>
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    public Vec2 Normalized() { double l = Length; return l > 0 ? this / l : Zero; }

    public override string ToString() => FormattableString.Invariant($"({X:0.######}, {Y:0.######})");
}

/// <summary>A point or direction in 3D, in double precision so that modeling never drifts.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 One = new(1, 1, 1);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    /// <summary>Component by component.</summary>
    public static Vec3 Scale(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;
    public static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public Vec3 Normalized()
    {
        double l = Length;
        return l > 0 ? this / l : Zero;
    }

    public double this[Axis a] => a switch { Axis.X => X, Axis.Y => Y, _ => Z };
    public double this[int i] => i switch { 0 => X, 1 => Y, _ => Z };

    public Vec3 With(Axis a, double v) => a switch { Axis.X => this with { X = v }, Axis.Y => this with { Y = v }, _ => this with { Z = v } };

    public static Vec3 Unit(Axis a) => a switch { Axis.X => UnitX, Axis.Y => UnitY, _ => UnitZ };

    /// <summary>Whether two points are the same to within a distance.</summary>
    public static bool Near(Vec3 a, Vec3 b, double tolerance = 1e-9) => (a - b).LengthSquared <= tolerance * tolerance;

    /// <summary>Some direction at right angles to this one.</summary>
    public Vec3 AnyPerpendicular()
    {
        var other = Math.Abs(X) < 0.9 ? UnitX : UnitY;
        return Cross(this, other).Normalized();
    }

    public override string ToString() => FormattableString.Invariant($"({X:0.######}, {Y:0.######}, {Z:0.######})");
}
