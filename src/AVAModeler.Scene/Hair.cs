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
        return this;
    }
}

/// <summary>Grows hair on a mesh: strands placed by a hash over the faces by area, each a tapering three-sided tube.</summary>
public static class HairGrowth
{
    /// <summary>
    /// Adds the strands to <paramref name="mesh"/> (in its own coordinates), on <paramref name="surface"/>.
    /// <paramref name="down"/> is the world's down in those coordinates, for the droop. Only faces whose surface is
    /// <paramref name="onSurface"/> (an index) grow hair, or every face when it's null. Returns how many strands grew.
    /// </summary>
    public static int Grow(Mesh mesh, HairSettings s, Vec3 down, int surface, int? onSurface)
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
