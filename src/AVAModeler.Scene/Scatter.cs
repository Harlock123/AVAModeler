using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// Copies of another object scattered over an object's faces (trees on a hill, rocks on a beach, flowers in a bed):
/// how many, which object, their size and turn, standing upright or along the surface, only on one surface, and thinned
/// by a picture's brightness laid over the object from above. Immutable: change it with <c>with</c> and <see cref="Checked"/> it.
/// </summary>
public sealed record ScatterSettings
{
    /// <summary>The object copied, by name.</summary>
    public string Of { get; init; } = "";
    public int Count { get; init; } = 100;
    public int Seed { get; init; }
    public double ScaleMin { get; init; } = 0.8;
    public double ScaleMax { get; init; } = 1.2;
    /// <summary>Turn each about its up axis at random (else all face the same way).</summary>
    public bool RandomTurn { get; init; } = true;
    /// <summary>Tilt each along the surface it stands on (else upright, along the world's +Y).</summary>
    public bool AlongSurface { get; init; }
    /// <summary>Only on this surface's faces (by name); empty for every face.</summary>
    public string OnSurface { get; init; } = "";
    /// <summary>A picture laid over the object from above (X across, Z up the picture) whose brightness is how likely a copy is there; empty for even.</summary>
    public string DensityMap { get; init; } = "";
    /// <summary>Sink each copy this far into the ground (metres), so it doesn't float on a slope.</summary>
    public double Sink { get; init; }

    public const int MaxCount = 100_000;

    public ScatterSettings Checked()
    {
        if (string.IsNullOrWhiteSpace(Of)) throw new ArgumentException("A scatter needs the object to copy (Of).");
        if (Count < 1 || Count > MaxCount) throw new ArgumentException($"A scatter's count must be from 1 to {MaxCount}.");
        if (!(ScaleMin > 0) || !(ScaleMax >= ScaleMin) || ScaleMax > 1000) throw new ArgumentException("A scatter's scale must be more than 0, its most at least its least.");
        if (!double.IsFinite(Sink) || Math.Abs(Sink) > 1000) throw new ArgumentException("A scatter's sink must be a number of metres.");
        return this;
    }
}

/// <summary>Where a scatter's copies go and what they add to the frame.</summary>
public static class Scattering
{
    /// <summary>One copy: where on the object (its own coordinates), which way up, how it turns and its size.</summary>
    public readonly record struct Spot(Vec3 At, Vec3 Up, double Turn, double Scale);

    private static double Unit(int seed, int i, int k)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u ^ (uint)i * 0x85EBCA77u ^ (uint)k * 0xC2B2AE3Du;
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }

    /// <summary>The copies' spots on a mesh, the same for the same settings and mesh (a hash, not chance).</summary>
    public static List<Spot> Spots(Mesh mesh, IReadOnlyList<Surface> surfaces, ScatterSettings s, TextureImage? density)
    {
        int only = s.OnSurface.Length == 0 ? -2 : surfaces.ToList().FindIndex(x => x.Name == s.OnSurface);
        // Every face as triangles, with its area, to pick from by area.
        var tris = new List<(Vec3 A, Vec3 B, Vec3 C, Vec3 N, double Area)>();
        foreach (var p in mesh.Polygons)
        {
            if (p.Count < 3 || only != -2 && p.Surface != only) continue;
            var outward = mesh.Normal(p);
            var a = mesh.Points[p.Points[0]];
            for (int i = 1; i + 1 < p.Count; i++)
            {
                var b = mesh.Points[p.Points[i]];
                var c = mesh.Points[p.Points[i + 1]];
                var cross = Vec3.Cross(b - a, c - a);
                double area = cross.Length / 2;
                if (area <= 1e-12) continue;
                tris.Add((a, b, c, outward, area));
            }
        }
        var spots = new List<Spot>();
        if (tris.Count == 0) return spots;
        var cumulative = new double[tris.Count];
        double total = 0;
        for (int i = 0; i < tris.Count; i++) cumulative[i] = total += tris[i].Area;
        var box = mesh.Bounds();
        // Thinned by the density picture: up to 8 tries a copy for a spot the picture keeps.
        for (int i = 0, tries = 0; spots.Count < s.Count && tries < s.Count * 8; tries++, i++)
        {
            int t = Array.BinarySearch(cumulative, Unit(s.Seed, i, 0) * total);
            if (t < 0) t = ~t;
            t = Math.Min(t, tris.Count - 1);
            var (a, b, c, n, _) = tris[t];
            double u = Unit(s.Seed, i, 1), v = Unit(s.Seed, i, 2);
            if (u + v > 1) (u, v) = (1 - u, 1 - v);
            var at = a + (b - a) * u + (c - a) * v;
            if (density != null)
            {
                double px = box.Size.X > 0 ? (at.X - box.Min.X) / box.Size.X : 0.5, pz = box.Size.Z > 0 ? (at.Z - box.Min.Z) / box.Size.Z : 0.5;
                var (colour, alpha) = density.Sample(px, pz, TextureTiling.Edge, TextureTiling.Edge);
                if (Unit(s.Seed, i, 3) > TextureImage.Brightness(colour) * alpha) continue;
            }
            var up = s.AlongSurface ? n : Vec3.UnitY;
            if (s.AlongSurface && Vec3.Dot(up, Vec3.UnitY) < 0) up = -up;
            double turn = s.RandomTurn ? 2 * Math.PI * Unit(s.Seed, i, 4) : 0;
            double scale = s.ScaleMin + (s.ScaleMax - s.ScaleMin) * Unit(s.Seed, i, 5);
            spots.Add(new Spot(at - up * s.Sink, up, turn, scale));
        }
        return spots;
    }

    /// <summary>
    /// Adds the copies to a frame's mesh (in the scattering object's coordinates), the copy's surfaces from
    /// <paramref name="firstSurface"/>; past the face budget, only so many copies. Returns how many were added.
    /// </summary>
    public static int Add(Mesh mesh, IReadOnlyList<Spot> spots, Mesh copy, int firstSurface)
    {
        var faces = copy.Polygons.Where(p => p.Count >= 1).ToList();
        if (faces.Count == 0 || spots.Count == 0) return 0;
        int fit = Math.Max(1, ParticleLooks.MaxInstanceFaces / faces.Count);
        int made = 0;
        foreach (var spot in spots.Take(fit))
        {
            // The copy's own +Y to the spot's up, turned about it, scaled, its origin on the spot.
            var y = spot.Up.Normalized();
            var x0 = Vec3.Cross(Math.Abs(y.Y) < 0.99 ? Vec3.UnitY : Vec3.UnitX, y).Normalized();
            var z0 = Vec3.Cross(x0, y);
            var x = x0 * Math.Cos(spot.Turn) + z0 * Math.Sin(spot.Turn);
            var z = Vec3.Cross(x, y);
            if (Math.Abs(y.Y) > 0.999)
            {
                // Upright: turn about +Y with +X and +Z kept in the ground plane (as the copy was modelled).
                x = new Vec3(Math.Cos(spot.Turn), 0, -Math.Sin(spot.Turn));
                z = new Vec3(Math.Sin(spot.Turn), 0, Math.Cos(spot.Turn));
                y = Vec3.UnitY;
            }
            int first = mesh.Points.Count;
            foreach (var p in copy.Points) mesh.Points.Add(spot.At + (x * p.X + y * p.Y + z * p.Z) * spot.Scale);
            foreach (var f in faces) mesh.Polygons.Add(new Polygon([.. f.Points.Select(i => i + first)], firstSurface + f.Surface));
            made++;
        }
        return made;
    }
}
