namespace AVAModeler.Core;

/// <summary>
/// A 4×4 matrix in double precision, row-major, for column vectors: <c>p' = M · p</c>, so <c>A * B</c> applies B
/// first, then A.
/// </summary>
public readonly struct Mat4 : IEquatable<Mat4>
{
    private readonly double[] _m;

    private Mat4(double[] m) => _m = m;

    public double this[int row, int col] => (_m ?? IdentityValues)[row * 4 + col];

    private static readonly double[] IdentityValues = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public static Mat4 Identity => new((double[])IdentityValues.Clone());

    public static Mat4 FromRows(params double[] values)
    {
        if (values.Length != 16) throw new ArgumentException("A 4×4 matrix needs 16 values.", nameof(values));
        return new((double[])values.Clone());
    }

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++)
        {
            double s = 0;
            for (int k = 0; k < 4; k++) s += a[i, k] * b[k, j];
            r[i * 4 + j] = s;
        }
        return new Mat4(r);
    }

    /// <summary>Transforms a point (w = 1), dividing through by w when it isn't 1.</summary>
    public Vec3 TransformPoint(Vec3 p)
    {
        double x = this[0, 0] * p.X + this[0, 1] * p.Y + this[0, 2] * p.Z + this[0, 3];
        double y = this[1, 0] * p.X + this[1, 1] * p.Y + this[1, 2] * p.Z + this[1, 3];
        double z = this[2, 0] * p.X + this[2, 1] * p.Y + this[2, 2] * p.Z + this[2, 3];
        double w = this[3, 0] * p.X + this[3, 1] * p.Y + this[3, 2] * p.Z + this[3, 3];
        return w is 1 or 0 ? new Vec3(x, y, z) : new Vec3(x / w, y / w, z / w);
    }

    /// <summary>Transforms a point and returns its w as well (for clipping before the divide).</summary>
    public (Vec3 Xyz, double W) TransformHomogeneous(Vec3 p)
    {
        double x = this[0, 0] * p.X + this[0, 1] * p.Y + this[0, 2] * p.Z + this[0, 3];
        double y = this[1, 0] * p.X + this[1, 1] * p.Y + this[1, 2] * p.Z + this[1, 3];
        double z = this[2, 0] * p.X + this[2, 1] * p.Y + this[2, 2] * p.Z + this[2, 3];
        double w = this[3, 0] * p.X + this[3, 1] * p.Y + this[3, 2] * p.Z + this[3, 3];
        return (new Vec3(x, y, z), w);
    }

    /// <summary>Transforms a direction (w = 0): no translation.</summary>
    public Vec3 TransformVector(Vec3 v) => new(
        this[0, 0] * v.X + this[0, 1] * v.Y + this[0, 2] * v.Z,
        this[1, 0] * v.X + this[1, 1] * v.Y + this[1, 2] * v.Z,
        this[2, 0] * v.X + this[2, 1] * v.Y + this[2, 2] * v.Z);

    public static Mat4 Translation(Vec3 t) => FromRows(1, 0, 0, t.X, 0, 1, 0, t.Y, 0, 0, 1, t.Z, 0, 0, 0, 1);
    public static Mat4 Scaling(Vec3 s) => FromRows(s.X, 0, 0, 0, 0, s.Y, 0, 0, 0, 0, s.Z, 0, 0, 0, 0, 1);
    public static Mat4 Scaling(double s) => Scaling(new Vec3(s, s, s));

    /// <summary>A rotation by an angle in radians about an axis through the origin (right-handed).</summary>
    public static Mat4 Rotation(Vec3 axis, double radians)
    {
        var a = axis.Normalized();
        double c = Math.Cos(radians), s = Math.Sin(radians), t = 1 - c;
        return FromRows(
            t * a.X * a.X + c, t * a.X * a.Y - s * a.Z, t * a.X * a.Z + s * a.Y, 0,
            t * a.X * a.Y + s * a.Z, t * a.Y * a.Y + c, t * a.Y * a.Z - s * a.X, 0,
            t * a.X * a.Z - s * a.Y, t * a.Y * a.Z + s * a.X, t * a.Z * a.Z + c, 0,
            0, 0, 0, 1);
    }

    public static Mat4 RotationX(double radians) => Rotation(Vec3.UnitX, radians);
    public static Mat4 RotationY(double radians) => Rotation(Vec3.UnitY, radians);
    public static Mat4 RotationZ(double radians) => Rotation(Vec3.UnitZ, radians);

    /// <summary>A transform about a centre: move the centre to the origin, apply, move back.</summary>
    public static Mat4 About(Vec3 center, Mat4 m) => Translation(center) * m * Translation(-center);

    /// <summary>
    /// LightWave's heading, pitch and bank (degrees): bank about Z first, then pitch about X, then heading about Y.
    /// </summary>
    public static Mat4 HeadingPitchBank(double heading, double pitch, double bank) =>
        RotationY(Deg(heading)) * RotationX(Deg(pitch)) * RotationZ(Deg(bank));

    public static double Deg(double degrees) => degrees * Math.PI / 180;

    public Mat4 Transposed()
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++)
            r[j * 4 + i] = this[i, j];
        return new Mat4(r);
    }

    public double Determinant()
    {
        Cofactors(out double det);
        return det;
    }

    /// <summary>The inverse; a singular matrix gives the identity.</summary>
    public Mat4 Inverted()
    {
        var c = Cofactors(out double det);
        if (Math.Abs(det) < 1e-300) return Identity;
        for (int i = 0; i < 16; i++) c[i] /= det;
        return new Mat4(c);
    }

    private double[] Cofactors(out double det)
    {
        var m = _m ?? IdentityValues;
        var inv = new double[16];
        inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
        inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
        inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
        inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
        inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
        inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
        inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
        inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
        inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
        inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
        inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
        inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
        inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
        inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
        inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
        inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
        det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
        return inv;
    }

    /// <summary>A view matrix: the camera at <paramref name="eye"/> looking at <paramref name="target"/>, looking down its -Z.</summary>
    public static Mat4 LookAt(Vec3 eye, Vec3 target, Vec3 up)
    {
        var f = (target - eye).Normalized();
        var s = Vec3.Cross(f, up).Normalized();
        if (s.LengthSquared == 0) s = f.AnyPerpendicular();
        var u = Vec3.Cross(s, f);
        return FromRows(
            s.X, s.Y, s.Z, -Vec3.Dot(s, eye),
            u.X, u.Y, u.Z, -Vec3.Dot(u, eye),
            -f.X, -f.Y, -f.Z, Vec3.Dot(f, eye),
            0, 0, 0, 1);
    }

    /// <summary>An OpenGL-style perspective projection (clip z from -1 to 1).</summary>
    public static Mat4 Perspective(double fovYRadians, double aspect, double near, double far)
    {
        double f = 1 / Math.Tan(fovYRadians / 2);
        return FromRows(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), 2 * far * near / (near - far),
            0, 0, -1, 0);
    }

    /// <summary>An OpenGL-style orthographic projection.</summary>
    public static Mat4 Orthographic(double left, double right, double bottom, double top, double near, double far) => FromRows(
        2 / (right - left), 0, 0, -(right + left) / (right - left),
        0, 2 / (top - bottom), 0, -(top + bottom) / (top - bottom),
        0, 0, -2 / (far - near), -(far + near) / (far - near),
        0, 0, 0, 1);

    /// <summary>The values in row order, as floats (for a GPU upload, transpose for column-major).</summary>
    public float[] ToFloats()
    {
        var r = new float[16];
        for (int i = 0; i < 16; i++) r[i] = (float)(_m ?? IdentityValues)[i];
        return r;
    }

    public bool Equals(Mat4 other)
    {
        for (int i = 0; i < 16; i++)
            if (Math.Abs((_m ?? IdentityValues)[i] - (other._m ?? IdentityValues)[i]) > 1e-12) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Mat4 m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(this[0, 0], this[1, 1], this[2, 2], this[0, 3], this[1, 3], this[2, 3]);
    public static bool operator ==(Mat4 a, Mat4 b) => a.Equals(b);
    public static bool operator !=(Mat4 a, Mat4 b) => !a.Equals(b);
}
