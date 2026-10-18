using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// Heading, pitch and bank in degrees, as LightWave's Layout uses them, in its left-handed space (X right, Y up,
/// Z forward). Heading turns about Y (positive turns +Z toward +X: the view turns right), pitch about X (positive tips
/// +Z down toward -Y: the nose goes down), and bank about Z (positive turns +X toward +Y). They apply as heading, then
/// pitch about the turned X axis, then bank about the turned Z axis: the matrix is
/// <see cref="Mat4.HeadingPitchBank"/>, Ry(h)·Rx(p)·Rz(b). A rotation is a <see cref="Vec3"/> holding (H, P, B).
/// </summary>
public static class Rotations
{
    public static Mat4 Matrix(Vec3 hpb) => Mat4.HeadingPitchBank(hpb.X, hpb.Y, hpb.Z);

    /// <summary>Where a rotation points an item's +Z axis (the way an unrotated camera, light or bone faces).</summary>
    public static Vec3 Forward(Vec3 hpb) => Matrix(hpb).TransformVector(Vec3.UnitZ);

    /// <summary>The heading and pitch (bank 0) that point +Z along a direction.</summary>
    public static Vec3 FromDirection(Vec3 direction)
    {
        var d = direction.Normalized();
        if (d.LengthSquared == 0) return Vec3.Zero;
        double h = Math.Abs(d.X) < 1e-15 && Math.Abs(d.Z) < 1e-15 ? 0 : Math.Atan2(d.X, d.Z);
        double p = Math.Asin(Math.Clamp(-d.Y, -1, 1));
        return new Vec3(ToDegrees(h), ToDegrees(p), 0);
    }

    /// <summary>The heading, pitch and bank of a rotation matrix (any scale in it is ignored).</summary>
    public static Vec3 FromMatrix(Mat4 m)
    {
        var f = m.TransformVector(Vec3.UnitZ).Normalized();
        var r = m.TransformVector(Vec3.UnitX).Normalized();
        var u = m.TransformVector(Vec3.UnitY).Normalized();
        double p = Math.Asin(Math.Clamp(-f.Y, -1, 1));
        double h, b;
        if (Math.Abs(Math.Cos(p)) > 1e-9)
        {
            h = Math.Atan2(f.X, f.Z);
            b = Math.Atan2(r.Y, u.Y);
        }
        else
        {
            // Looking straight up or down: heading and bank turn about the same axis, so put it all in heading.
            h = Math.Atan2(-r.Z, r.X);
            b = 0;
        }
        return new Vec3(ToDegrees(h), ToDegrees(p), ToDegrees(b));
    }

    public static double ToDegrees(double radians) => radians * 180 / Math.PI;
}
