using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>How a brush's effect fades from its centre (full) to its radius (none).</summary>
public enum BrushFalloff { Smooth, Linear, Sharp, Dome, Constant }

/// <summary>
/// What the painting and sculpting brushes share: the falloff, the points under a dab, spacing a stroke into dabs, and
/// where a ray from the mouse meets the mesh.
/// </summary>
public static class Brushes
{
    /// <summary>The effect at <paramref name="r"/> (0 at the centre, 1 at the radius), from 1 down to 0.</summary>
    public static double Falloff(BrushFalloff falloff, double r)
    {
        if (r >= 1) return 0;
        r = Math.Max(0, r);
        return falloff switch
        {
            BrushFalloff.Linear => 1 - r,
            BrushFalloff.Sharp => (1 - r) * (1 - r) * (1 - r),
            BrushFalloff.Dome => Math.Sqrt(1 - r * r),
            BrushFalloff.Constant => 1,
            _ => (1 - r * r) * (1 - r * r),
        };
    }

    /// <summary>
    /// The points within <paramref name="radius"/> of a dab's centre, each with its falloff, among the allowed points
    /// (all, if null).
    /// </summary>
    public static List<(int Point, double Weight)> Under(Mesh mesh, IReadOnlySet<int>? allowed, Vec3 center, double radius, BrushFalloff falloff)
    {
        var list = new List<(int, double)>();
        if (!(radius > 0)) return list;
        double r2 = radius * radius;
        for (int i = 0; i < mesh.Points.Count; i++)
        {
            if (allowed != null && !allowed.Contains(i)) continue;
            double d2 = (mesh.Points[i] - center).LengthSquared;
            if (d2 >= r2) continue;
            double w = Falloff(falloff, Math.Sqrt(d2) / radius);
            if (w > 0) list.Add((i, w));
        }
        return list;
    }

    /// <summary>
    /// Dab centres along a path, <paramref name="spacing"/> apart (the first at the path's start), so a slow and a quick
    /// stroke over the same ground do the same.
    /// </summary>
    public static List<Vec3> Space(IReadOnlyList<Vec3> path, double spacing)
    {
        var dabs = new List<Vec3>();
        if (path.Count == 0) return dabs;
        dabs.Add(path[0]);
        if (!(spacing > 0)) { dabs.AddRange(path.Skip(1)); return dabs; }
        var last = path[0];
        for (int i = 1; i < path.Count; i++)
        {
            var to = path[i];
            double left = (to - last).Length;
            while (left >= spacing * (1 - 1e-9))
            {
                last += (to - last).Normalized() * spacing;
                dabs.Add(last);
                left -= spacing;
            }
        }
        return dabs;
    }

    /// <summary>The nearest place a ray meets any of some meshes: which one (its index in the list) and where.</summary>
    public static (int Mesh, RayHit Hit)? Raycast(IReadOnlyList<Mesh> meshes, Ray ray)
    {
        (int, RayHit)? best = null;
        for (int i = 0; i < meshes.Count; i++)
            if (BvhOf(meshes[i]).Raycast(ray) is { } hit && (best == null || hit.Distance < best.Value.Item2.Distance)) best = (i, hit);
        return best;
    }

    private sealed record Cached(int Version, TriangleBvh Bvh);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, Cached> Bvhs = new();

    /// <summary>A mesh's triangle BVH, kept until the mesh changes.</summary>
    public static TriangleBvh BvhOf(Mesh mesh)
    {
        if (Bvhs.TryGetValue(mesh, out var c) && c.Version == mesh.Version) return c.Bvh;
        var bvh = new TriangleBvh(mesh);
        Bvhs.AddOrUpdate(mesh, new Cached(mesh.Version, bvh));
        return bvh;
    }
}
