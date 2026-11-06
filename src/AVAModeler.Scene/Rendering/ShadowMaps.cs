using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>For tests: what a render did, counted across its threads.</summary>
internal sealed class RenderCounters
{
    /// <summary>Shadow rays traced toward lights (each may meet several surfaces on its way).</summary>
    public long ShadowRays;
    /// <summary>Shadow map lookups (each filters several map pixels).</summary>
    public long ShadowMapLookups;
}

/// <summary>
/// One depth picture of what a light sees: <see cref="Size"/> pixels square, each the distance along the view's
/// forward axis to the nearest shadow-casting surface there (+∞ where there's none). A perspective view looks out of
/// the light through a square window <c>2 · Scale</c> wide at distance 1; an orthographic one (a distant light's) looks
/// along its axis through a square <c>2 · Scale</c> metres wide.
/// </summary>
internal sealed class DepthView
{
    public readonly int Size;
    private readonly float[] _depth;
    private readonly bool _ortho;
    private readonly Vec3 _origin, _right, _up, _forward;
    private readonly double _scale, _near;

    public DepthView(int size, bool ortho, Vec3 origin, Vec3 forward, double scale, double near)
    {
        Size = size;
        _ortho = ortho;
        _origin = origin;
        _forward = forward.Normalized();
        _right = _forward.AnyPerpendicular();
        _up = Vec3.Cross(_forward, _right);
        _scale = scale;
        _near = near;
        _depth = new float[size * size];
        Array.Fill(_depth, float.PositiveInfinity);
    }

    /// <summary>Where a point is in the map, in pixels (0 to Size across and up), and its depth; false behind a perspective view.</summary>
    public bool Project(Vec3 p, out double px, out double py, out double z)
    {
        var q = p - _origin;
        z = Vec3.Dot(q, _forward);
        double x = Vec3.Dot(q, _right), y = Vec3.Dot(q, _up);
        px = py = 0;
        if (_ortho)
        {
            x /= _scale;
            y /= _scale;
        }
        else
        {
            if (z <= _near) return false;
            x /= z * _scale;
            y /= z * _scale;
        }
        px = (x + 1) * 0.5 * Size;
        py = (y + 1) * 0.5 * Size;
        return true;
    }

    /// <summary>How wide a map pixel is in the world at a depth.</summary>
    public double Texel(double z) => (_ortho ? 2 * _scale : 2 * Math.Max(z, _near) * _scale) / Size;

    /// <summary>Draws a triangle's depth into the map (the nearest surface wins).</summary>
    public void Draw(Vec3 a, Vec3 b, Vec3 c)
    {
        Span<Vec3> view = [ToView(a), ToView(b), ToView(c)];
        Span<Vec3> poly = stackalloc Vec3[4];
        int n;
        if (_ortho)
        {
            view.CopyTo(poly);
            n = 3;
        }
        else
        {
            // Clip to the near plane (z ≥ near): a triangle becomes at most a quadrilateral.
            n = 0;
            for (int i = 0; i < 3; i++)
            {
                var p = view[i];
                var q = view[(i + 1) % 3];
                bool pin = p.Z >= _near, qin = q.Z >= _near;
                if (pin) poly[n++] = p;
                if (pin != qin) poly[n++] = Vec3.Lerp(p, q, (_near - p.Z) / (q.Z - p.Z));
            }
            if (n < 3) return;
        }
        // To pixels: x and y in the map, and what interpolates linearly across it (1/z in perspective, z orthographic).
        Span<Vec3> s = stackalloc Vec3[4];
        for (int i = 0; i < n; i++)
        {
            var v = poly[i];
            s[i] = _ortho
                ? new Vec3((v.X / _scale + 1) * 0.5 * Size, (v.Y / _scale + 1) * 0.5 * Size, v.Z)
                : new Vec3((v.X / (v.Z * _scale) + 1) * 0.5 * Size, (v.Y / (v.Z * _scale) + 1) * 0.5 * Size, 1 / v.Z);
        }
        for (int i = 1; i + 1 < n; i++) Fill(s[0], s[i], s[i + 1]);
    }

    private Vec3 ToView(Vec3 p)
    {
        var q = p - _origin;
        return new Vec3(Vec3.Dot(q, _right), Vec3.Dot(q, _up), Vec3.Dot(q, _forward));
    }

    private void Fill(Vec3 a, Vec3 b, Vec3 c)
    {
        double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(area) < 1e-12 || !double.IsFinite(area)) return;
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int x1 = Math.Min(Size - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int y1 = Math.Min(Size - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        double inv = 1 / area;
        for (int y = y0; y <= y1; y++)
        {
            double py = y + 0.5;
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5;
                double wa = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * inv;
                double wb = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * inv;
                double wc = 1 - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0) continue;
                double w = wa * a.Z + wb * b.Z + wc * c.Z;
                float z = (float)(_ortho ? w : 1 / w);
                int i = y * Size + x;
                if (z < _depth[i]) _depth[i] = z;
            }
        }
    }

    /// <summary>
    /// How lit a point at map position (px, py) and depth z is, 0 to 1: percentage-closer filtering over a box
    /// <paramref name="radius"/> map pixels each way (taps spread evenly, at most 7 × 7, each comparing the four
    /// nearest map pixels bilinearly), so shadows' edges soften over about twice the radius.
    /// </summary>
    public double Lit(double px, double py, double z, double radius)
    {
        if (radius <= 0) return Bilinear(px, py, z);
        int n = Math.Min(3, (int)Math.Ceiling(radius));
        double step = radius / n, sum = 0;
        for (int j = -n; j <= n; j++)
            for (int i = -n; i <= n; i++)
                sum += Bilinear(px + i * step, py + j * step, z);
        return sum / ((2 * n + 1) * (2 * n + 1));
    }

    private double Bilinear(double px, double py, double z)
    {
        double fx = px - 0.5, fy = py - 0.5;
        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
        double tx = fx - ix, ty = fy - iy;
        double top = Test(ix, iy, z) * (1 - tx) + Test(ix + 1, iy, z) * tx;
        double bottom = Test(ix, iy + 1, z) * (1 - tx) + Test(ix + 1, iy + 1, z) * tx;
        return top * (1 - ty) + bottom * ty;
    }

    private double Test(int x, int y, double z)
    {
        x = Math.Clamp(x, 0, Size - 1);
        y = Math.Clamp(y, 0, Size - 1);
        return _depth[y * Size + x] < z ? 0 : 1;
    }
}

/// <summary>
/// A light's shadow map at one moment: one perspective view for a spot light (up to 60° cones), a cube of six 90°
/// views for a point light (and a wider spot), an orthographic view fitted to the scene's bounds for a distant light.
/// Only surfaces that a shadow ray could meet are drawn: double-sided ones, and single-sided ones facing away from the
/// light (a shadow ray can't hit a face from behind), so a surface facing the light never shadows itself.
/// </summary>
internal sealed class LightShadowMap
{
    private readonly DepthView[] _views;
    private readonly bool _cube;
    private readonly Vec3 _position;
    private readonly double _fuzziness;

    private LightShadowMap(DepthView[] views, bool cube, Vec3 position, double fuzziness)
    {
        _views = views;
        _cube = cube;
        _position = position;
        _fuzziness = fuzziness;
    }

    /// <summary>The widest spot cone (half-angle, degrees) drawn in one view; wider ones use a cube.</summary>
    public const double WidestSingleCone = 60;

    /// <summary>The axes of a cube's six faces: +X, −X, +Y, −Y, +Z, −Z.</summary>
    private static readonly Vec3[] Faces = [Vec3.UnitX, -Vec3.UnitX, Vec3.UnitY, -Vec3.UnitY, Vec3.UnitZ, -Vec3.UnitZ];

    /// <summary>Builds the map, or returns null when there's nothing to fit a distant light's map to.</summary>
    public static LightShadowMap? Build(LightType type, Vec3 position, Vec3 direction, double coneAngle, ShadowMapSettings settings,
        IReadOnlyList<Triangle> casters, Box3 bounds)
    {
        double size = bounds.IsEmpty ? 1 : Math.Max(1, Math.Max(bounds.Extent, Math.Max(bounds.Max.Length, bounds.Min.Length)));
        double near = 1e-4 * size;
        DepthView[] views;
        bool cube = false;
        if (type == LightType.Distant)
        {
            if (bounds.IsEmpty) return null;
            var f = direction.Normalized();
            var probe = new DepthView(1, true, Vec3.Zero, f, 1, 0);
            // The bounds' corners seen along the light: a square window round them, starting just before the nearest.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, minZ = double.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vec3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
                probe.Project(corner, out double px, out double py, out double z);
                // With size 1 and scale 1 the pixel position is (x + 1) / 2.
                double x = px * 2 - 1, y = py * 2 - 1;
                (minX, maxX, minY, maxY, minZ) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y), Math.Min(minZ, z));
            }
            double half = Math.Max(maxX - minX, maxY - minY) / 2 * 1.01 + near;
            var right = f.AnyPerpendicular();
            var up = Vec3.Cross(f, right);
            var origin = right * ((minX + maxX) / 2) + up * ((minY + maxY) / 2) + f * (minZ - 0.01 * size);
            views = [new DepthView(settings.Size, true, origin, f, half, 0)];
        }
        else if (type == LightType.Spot && coneAngle <= WidestSingleCone)
            views = [new DepthView(settings.Size, false, position, direction, Math.Tan((coneAngle + 1) * Math.PI / 180), near)];
        else
        {
            cube = true;
            int face = Math.Min(settings.Size, LightEffects.MaxCubeFace);
            views = Faces.Select(axis => new DepthView(face, false, position, axis, 1, near)).ToArray();
        }

        var dir = direction.Normalized();
        foreach (var t in casters)
        {
            if (!t.DoubleSided)
            {
                // Only faces a shadow ray toward the light could meet: those facing away from it.
                var away = type == LightType.Distant ? dir : (t.A + t.B + t.C) / 3 - position;
                if (Vec3.Dot(t.Normal, away) <= 0) continue;
            }
            foreach (var v in views) v.Draw(t.A, t.B, t.C);
        }
        return new LightShadowMap(views, cube, position, settings.Fuzziness);
    }

    /// <summary>The view a point is looked up in: a cube's face along the largest component of the way from the light.</summary>
    private DepthView ViewOf(Vec3 p)
    {
        if (!_cube) return _views[0];
        var d = p - _position;
        double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
        if (ax >= ay && ax >= az) return _views[d.X >= 0 ? 0 : 1];
        if (ay >= az) return _views[d.Y >= 0 ? 2 : 3];
        return _views[d.Z >= 0 ? 4 : 5];
    }

    /// <summary>
    /// How much of the light reaches a point, 0 to 1. <paramref name="normal"/> is the surface's (zero for a point in
    /// the air): the point is moved off the surface by a map pixel toward the light, and compared with a depth bias that
    /// grows with the surface's slope to the light and the filter's width, so lit surfaces don't shadow themselves.
    /// Points outside the map (beside a spot's cone, beyond a distant light's fitted window) are lit.
    /// </summary>
    public double Lit(Vec3 p, Vec3 normal, Vec3 toLight, RenderCounters? counters)
    {
        if (counters != null) Interlocked.Increment(ref counters.ShadowMapLookups);
        var view = ViewOf(p);
        if (!view.Project(p, out _, out _, out double z0)) return 1;
        double texel = view.Texel(z0);
        double slope = 0;
        if (normal.LengthSquared > 0)
        {
            double cos = Vec3.Dot(normal, toLight);
            if (cos < 0)
            {
                normal = -normal;
                cos = -cos;
            }
            p += normal * texel;
            slope = Math.Min(5, Math.Sqrt(Math.Max(0, 1 - cos * cos)) / Math.Max(cos, 1e-3));
        }
        if (_cube) view = ViewOf(p);
        if (!view.Project(p, out double px, out double py, out double z)) return 1;
        if (px < 0 || py < 0 || px > view.Size || py > view.Size) return 1;
        double bias = texel * (1.5 + (1 + _fuzziness) * slope);
        return view.Lit(px, py, z - bias, _fuzziness);
    }
}
