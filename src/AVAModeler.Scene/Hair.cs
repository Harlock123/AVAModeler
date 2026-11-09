using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// Hair or fur grown on an object: how many strands a square metre, how long, how thick at the root (they taper to a
/// point), how many joints, how much they droop under their weight, how wild they grow, and their colour. Strands grow
/// from the object's deformed surface, so they follow bones, morphs and displacements. Lengths in metres. Immutable:
/// change it with <c>with</c> and <see cref="Checked"/> it.
/// </summary>
public sealed record HairSettings
{
    /// <summary>Strands a square metre of surface.</summary>
    public double Density { get; init; } = 20000;
    public double Length { get; init; } = 0.05;
    /// <summary>How much lengths vary, as a fraction (0: all alike).</summary>
    public double LengthJitter { get; init; } = 0.3;
    /// <summary>The thickness at the root; strands taper to the tip.</summary>
    public double Thickness { get; init; } = 0.001;
    /// <summary>Joints along each strand: more bend more smoothly.</summary>
    public int Segments { get; init; } = 4;
    /// <summary>How far strands sag under their weight, 0 (straight out) to 1 (hanging down).</summary>
    public double Droop { get; init; } = 0.3;
    /// <summary>How far from straight out of the surface strands lean (°), at random.</summary>
    public double Frizz { get; init; } = 15;
    /// <summary>Grow only from this surface's faces (by name); empty for every face.</summary>
    public string OnSurface { get; init; } = "";
    public Rgb Color { get; init; } = new(120, 80, 50);
    /// <summary>Changes where strands grow and how they lean, keeping the same settings.</summary>
    public int Seed { get; init; }
    /// <summary>Swing as the object moves: strands lag behind its starts, stops and turns and settle again.</summary>
    public bool Dynamics { get; init; }
    /// <summary>For dynamics: how fast strands swing back (Hz), 0.1 (long and floppy) to 20 (short and stiff).</summary>
    public double Stiffness { get; init; } = 2;
    /// <summary>For dynamics: how soon a swing dies away, 0 (swings on) to 2 (no overshoot past 1).</summary>
    public double Damping { get; init; } = 0.3;
    /// <summary>How far strands are combed toward <see cref="CombDirection"/>, 0 (not at all) to 1 (flat along it).</summary>
    public double Comb { get; init; }
    /// <summary>The way they're combed, in the object's own coordinates (toward −Z, the back, by default).</summary>
    public Vec3 CombDirection { get; init; } = new(0, 0, -1);

    /// <summary>The most strands grown on one object.</summary>
    public const int MaxStrands = 30000;

    public HairSettings Checked()
    {
        static void In(double v, double min, double max, string what)
        {
            if (!double.IsFinite(v) || v < min || v > max) throw new ArgumentException($"The hair's {what} must be from {min} to {max} (it's {v}).");
        }
        In(Density, 0, 1e9, "density (strands a square metre)");
        In(Length, 0.0001, 100, "length (m)");
        In(LengthJitter, 0, 1, "length jitter (a fraction)");
        In(Thickness, 0.000001, 1, "thickness (m)");
        In(Segments, 1, 32, "segments");
        In(Droop, 0, 1, "droop");
        In(Frizz, 0, 90, "frizz (°)");
        In(Stiffness, 0.1, 20, "stiffness (Hz)");
        In(Damping, 0, 2, "damping");
        In(Comb, 0, 1, "comb");
        if (!double.IsFinite(CombDirection.X + CombDirection.Y + CombDirection.Z)) throw new ArgumentException("The hair's comb direction must be three numbers.");
        if (Comb > 0 && CombDirection.LengthSquared < 1e-12) throw new ArgumentException("The hair's comb direction can't be nothing (0, 0, 0).");
        return this;
    }
}

/// <summary>
/// Hair dynamics as a spring: a copy of the object's motion that lags behind it as a weight on a spring does,
/// natural frequency <see cref="HairSettings.Stiffness"/> and damping ratio <see cref="HairSettings.Damping"/>. The
/// spring is linear, so it's run once on the object's whole world transform (its twelve numbers), not per strand: a
/// strand's lag is then that lagging transform less the real one, applied to its root. Worked out from the scene's
/// first frame (or as far back as a swing could still be seen), the same however the frame is reached.
/// </summary>
public static class HairSwing
{
    /// <summary>
    /// The lag at <paramref name="frame"/>: for a root in the object's coordinates, how far behind its tip is, in the
    /// object's coordinates. <paramref name="worldAt"/> gives the object's world transform at a frame.
    /// </summary>
    public static Func<Vec3, Vec3> Lag(HairSettings s, double frame, double fps, double firstFrame, Func<double, Mat4> worldAt)
    {
        double omega = 2 * Math.PI * s.Stiffness, zeta = s.Damping;
        double t1 = frame / fps, window = zeta > 0 ? Math.Min(10, 8 / (zeta * omega)) : 10;
        double t0 = Math.Max(firstFrame / fps, t1 - window);
        var now = worldAt(frame);
        if (t1 <= t0 || fps <= 0) return _ => Vec3.Zero;
        // Steps short enough for the spring (a tenth of its period at least, and a quarter frame), at most 20000.
        int n = (int)Math.Clamp(Math.Ceiling(Math.Max((t1 - t0) * fps * 4, (t1 - t0) * omega * 2)), 1, 20000);
        double dt = (t1 - t0) / n;
        static double[] Of(Mat4 m)
        {
            var a = new double[12];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++) a[r * 4 + c] = m[r, c];
            return a;
        }
        var x = Of(worldAt(t0 * fps));
        var lag = (double[])x.Clone();
        var next = Of(worldAt((t0 + dt) * fps));
        var v = new double[12];
        for (int i = 0; i < 12; i++) v[i] = (next[i] - x[i]) / dt;
        for (int k = 1; k <= n; k++)
        {
            var xk = k == n ? Of(now) : Of(worldAt((t0 + k * dt) * fps));
            for (int i = 0; i < 12; i++)
            {
                // The spring and damper between the lagging copy and the real one at the step's start.
                double xv = (xk[i] - x[i]) / dt;
                double a = omega * omega * (x[i] - lag[i]) + 2 * zeta * omega * (xv - v[i]);
                v[i] += a * dt;
                lag[i] += v[i] * dt;
            }
            x = xk;
        }
        var d = new double[12];
        for (int i = 0; i < 12; i++) d[i] = lag[i] - x[i];
        var toLocal = now.Inverted();
        return root =>
        {
            var w = new Vec3(d[0] * root.X + d[1] * root.Y + d[2] * root.Z + d[3], d[4] * root.X + d[5] * root.Y + d[6] * root.Z + d[7],
                d[8] * root.X + d[9] * root.Y + d[10] * root.Z + d[11]);
            return toLocal.TransformVector(w);
        };
    }
}

/// <summary>Grows hair on a mesh: strands placed by a hash over the faces by area, each a tapering three-sided tube.</summary>
public static class HairGrowth
{
    /// <summary>
    /// Adds the strands to <paramref name="mesh"/> (in its own coordinates), on <paramref name="surface"/>.
    /// <paramref name="down"/> is the world's down in those coordinates, for the droop. Only faces whose surface is
    /// <paramref name="onSurface"/> (an index) grow hair, or every face when it's null. <paramref name="swing"/>, if
    /// given, is how far a strand's tip lags behind where it would be (in the mesh's coordinates), by its root, for
    /// dynamics. Returns how many strands grew.
    /// </summary>
    public static int Grow(Mesh mesh, HairSettings s, Vec3 down, int surface, int? onSurface, Func<Vec3, Vec3>? swing = null)
    {
        // The faces to grow on, as triangles weighed by area (taken before any strands are added).
        var tris = new List<(Vec3 A, Vec3 B, Vec3 C, Vec3 N)>();
        var cumulative = new List<double>();
        double total = 0;
        int faces = mesh.Polygons.Count;
        for (int f = 0; f < faces; f++)
        {
            var poly = mesh.Polygons[f];
            if (poly.Count < 3 || onSurface is { } only && poly.Surface != only) continue;
            var n = mesh.Normal(poly);
            for (int i = 1; i + 1 < poly.Count; i++)
            {
                Vec3 a = mesh.Points[poly.Points[0]], b = mesh.Points[poly.Points[i]], c = mesh.Points[poly.Points[i + 1]];
                double area = Vec3.Cross(b - a, c - a).Length / 2;
                if (area <= 0) continue;
                tris.Add((a, b, c, n));
                total += area;
                cumulative.Add(total);
            }
        }
        if (tris.Count == 0 || total <= 0) return 0;
        int count = (int)Math.Min(HairSettings.MaxStrands, Math.Round(total * s.Density));
        var weights = cumulative.ToArray();
        down = down.LengthSquared > 0 ? down.Normalized() : new Vec3(0, -1, 0);
        for (int k = 0; k < count; k++)
        {
            double pick = ParticleSystem.Unit(s.Seed, k, 10) * total;
            int t = Array.BinarySearch(weights, pick);
            if (t < 0) t = ~t;
            t = Math.Min(t, tris.Count - 1);
            var (a, b, c, n) = tris[t];
            double u = ParticleSystem.Unit(s.Seed, k, 11), v = ParticleSystem.Unit(s.Seed, k, 12);
            if (u + v > 1) (u, v) = (1 - u, 1 - v);
            var root = a + (b - a) * u + (c - a) * v;
            // Leaning out within Frizz of the normal, the length jittered.
            var x = Vec3.Cross(n, Math.Abs(n.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY).Normalized();
            var y = Vec3.Cross(n, x);
            double lean = s.Frizz * Math.PI / 180 * ParticleSystem.Unit(s.Seed, k, 13), turn = 2 * Math.PI * ParticleSystem.Unit(s.Seed, k, 14);
            var dir = (n * Math.Cos(lean) + (x * Math.Cos(turn) + y * Math.Sin(turn)) * Math.Sin(lean)).Normalized();
            if (s.Comb > 0)
            {
                // Combed: turned toward the comb's way along the surface (its part across the normal), up to lying flat.
                var along = s.CombDirection - n * Vec3.Dot(s.CombDirection, n);
                if (along.LengthSquared > 1e-12) dir = (dir * (1 - s.Comb) + (along.Normalized() * 0.98 + n * 0.02).Normalized() * s.Comb).Normalized();
            }
            double length = s.Length * (1 - s.LengthJitter * ParticleSystem.Unit(s.Seed, k, 15));
            // The spine: each joint turning a little more toward down, by the droop.
            var spine = new Vec3[s.Segments + 1];
            spine[0] = root;
            var heading = dir;
            double step = length / s.Segments;
            // Each joint turns toward down by up to Droop × 180° over the strand, never past down. A strand pointing
            // straight away from down bends the way it leans (or, standing dead straight, the way its root turns).
            double bend = s.Droop * Math.PI / s.Segments;
            var side = x * Math.Cos(turn) + y * Math.Sin(turn);
            for (int j = 1; j <= s.Segments; j++)
            {
                var axis = Vec3.Cross(heading, down);
                if (axis.LengthSquared < 1e-12) axis = Vec3.Cross(heading, side);
                double toDown = Math.Acos(Math.Clamp(Vec3.Dot(heading, down), -1, 1));
                if (axis.LengthSquared > 1e-12 && toDown > 1e-9)
                    heading = Rotate(heading, axis.Normalized(), Math.Min(bend, toDown));
                spine[j] = spine[j - 1] + heading * step;
            }
            if (swing != null)
            {
                // The lag, more toward the tip (as a strand bends from its root), then each joint put back at its length.
                var lag = swing(root);
                for (int j = 1; j <= s.Segments; j++)
                {
                    double w = (double)j / s.Segments;
                    var moved = spine[j] + lag * (w * w);
                    var d = moved - spine[j - 1];
                    spine[j] = spine[j - 1] + (d.LengthSquared > 1e-24 ? d.Normalized() : heading) * step;
                }
            }
            AddStrand(mesh, spine, s.Thickness, surface);
        }
        mesh.Touch();
        return count;
    }

    /// <summary>A vector turned about a unit axis (Rodrigues).</summary>
    private static Vec3 Rotate(Vec3 v, Vec3 k, double a) =>
        (v * Math.Cos(a) + Vec3.Cross(k, v) * Math.Sin(a) + k * (Vec3.Dot(k, v) * (1 - Math.Cos(a)))).Normalized();

    /// <summary>A tube of three sides along a spine, its width tapering from <paramref name="thickness"/> at the root to nothing at the tip.</summary>
    private static void AddStrand(Mesh mesh, Vec3[] spine, double thickness, int surface)
    {
        int n = spine.Length;
        int first = mesh.Points.Count;
        for (int j = 0; j < n - 1; j++)
        {
            var along = (spine[Math.Min(j + 1, n - 1)] - spine[Math.Max(j - 1, 0)]).Normalized();
            var x = Vec3.Cross(along, Math.Abs(along.Y) < 0.9 ? Vec3.UnitY : Vec3.UnitX).Normalized();
            var y = Vec3.Cross(along, x);
            double r = thickness / 2 * (1 - (double)j / (n - 1));
            for (int side = 0; side < 3; side++)
            {
                double a = side * 2 * Math.PI / 3;
                mesh.Points.Add(spine[j] + (x * Math.Cos(a) + y * Math.Sin(a)) * r);
            }
        }
        int tip = mesh.Points.Count;
        mesh.Points.Add(spine[^1]);
        for (int j = 0; j < n - 1; j++)
            for (int side = 0; side < 3; side++)
            {
                int a = first + j * 3 + side, b = first + j * 3 + (side + 1) % 3;
                if (j + 1 < n - 1) mesh.Polygons.Add(new Polygon([a, a + 3, b + 3, b], surface));
                else mesh.Polygons.Add(new Polygon([a, tip, b], surface));
            }
    }
}
