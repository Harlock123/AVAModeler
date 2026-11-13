using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// Voxel remeshing by surface nets: a closed solid is sampled on a grid of cubes a voxel across (inside or outside, by
/// rays swept along each axis), and a new skin of even quads is built round the inside cubes: one point in each cube
/// the surface passes through (the average of where it crosses the cube's edges), one quad across each crossed edge.
/// Smoothing passes then even it out, each pulled back onto the original surface. The result is closed, wound
/// outward, and all quads; the original's UVs aren't kept.
/// </summary>
public static class Remesh
{
    /// <summary>The most grid points along any side.</summary>
    public const int MaxCells = 400;

    /// <summary>What a remesh made.</summary>
    public readonly record struct Result(int FacesBefore, int FacesAfter, int Cells);

    /// <summary>
    /// Replaces the chosen faces (which must make closed solids) with an even all-quad skin, voxels
    /// <paramref name="voxel"/> metres across, smoothed <paramref name="smoothing"/> times. The new faces take the
    /// surface of the first chosen face.
    /// </summary>
    public static Result Apply(Mesh mesh, IReadOnlyCollection<int> faces, double voxel, int smoothing = 2)
    {
        if (!(voxel > 0) || !double.IsFinite(voxel)) throw new ArgumentException("Remesh's voxel size must be more than 0 metres.");
        var chosen = faces.Where(i => i >= 0 && i < mesh.Polygons.Count && mesh.Polygons[i].Count >= 3).Order().ToList();
        if (chosen.Count == 0) throw new InvalidOperationException("Remesh needs faces to work on.");
        var source = new Mesh { Points = [.. mesh.Points], Polygons = [.. chosen.Select(i => mesh.Polygons[i].Clone())] };
        source.Touch();
        foreach (var (e, l) in source.EdgeMap())
            if (l.Count != 2)
                throw new InvalidOperationException($"Remesh needs a closed solid, but {(l.Count == 1 ? "an edge has a face on one side only (a hole)" : "an edge is shared by more than two faces")}. Close holes with Fill Hole first (Mesh Info shows them).");
        var bounds = source.Bounds(source.Polygons.SelectMany(p => p.Points));
        var origin = bounds.Min - Vec3.One * (1.5 * voxel);
        int nx = (int)Math.Ceiling(bounds.Size.X / voxel) + 4, ny = (int)Math.Ceiling(bounds.Size.Y / voxel) + 4, nz = (int)Math.Ceiling(bounds.Size.Z / voxel) + 4;
        if (Math.Max(nx, Math.Max(ny, nz)) > MaxCells)
            throw new ArgumentException($"That voxel size makes a grid {Math.Max(nx, Math.Max(ny, nz))} cubes long; the most is {MaxCells}. Make the voxels at least {Math.Max(bounds.Size.X, Math.Max(bounds.Size.Y, bounds.Size.Z)) / (MaxCells - 4):0.###} m.");
        var bvh = new TriangleBvh(source);
        Vec3 G(int i, int j, int k) => origin + new Vec3(i * voxel, j * voxel, k * voxel);

        // Inside or outside at each grid point, by rays along X: odd crossings before a point, it's inside.
        var inside = new bool[nx, ny, nz];
        var crossX = Sweep(bvh, 0, nx, ny, nz, voxel, origin, out var hitsX);
        for (int j = 0; j < ny; j++)
            for (int k = 0; k < nz; k++)
            {
                var hits = hitsX[j, k];
                int h = 0;
                for (int i = 0; i < nx; i++)
                {
                    double x = origin.X + i * voxel;
                    while (h < hits.Count && hits[h] < x) h++;
                    inside[i, j, k] = (h & 1) == 1;
                }
            }
        var crossY = Sweep(bvh, 1, nx, ny, nz, voxel, origin, out _);
        var crossZ = Sweep(bvh, 2, nx, ny, nz, voxel, origin, out _);

        // Where the surface crosses a grid edge between an inside and an outside point.
        Vec3 Crossing(int axis, int i, int j, int k)
        {
            var dict = axis == 0 ? crossX : axis == 1 ? crossY : crossZ;
            if (dict.TryGetValue((i, j, k), out var p)) return p;
            var a = G(i, j, k);
            return a + Vec3.Unit((Axis)axis) * (voxel / 2);
        }

        // One point per cube the surface passes through.
        var pointOf = new Dictionary<(int, int, int), int>();
        var result = new Mesh();
        int[][] corner = [[0, 0, 0], [1, 0, 0], [0, 1, 0], [1, 1, 0], [0, 0, 1], [1, 0, 1], [0, 1, 1], [1, 1, 1]];
        (int A, int B, int Axis)[] cubeEdges = [(0, 1, 0), (2, 3, 0), (4, 5, 0), (6, 7, 0), (0, 2, 1), (1, 3, 1), (4, 6, 1), (5, 7, 1), (0, 4, 2), (1, 5, 2), (2, 6, 2), (3, 7, 2)];
        int CellPoint(int i, int j, int k)
        {
            if (pointOf.TryGetValue((i, j, k), out int at)) return at;
            var sum = Vec3.Zero;
            int n = 0;
            foreach (var (a, b, axis) in cubeEdges)
            {
                var ca = corner[a];
                var cb = corner[b];
                if (inside[i + ca[0], j + ca[1], k + ca[2]] == inside[i + cb[0], j + cb[1], k + cb[2]]) continue;
                sum += Crossing(axis, i + ca[0], j + ca[1], k + ca[2]);
                n++;
            }
            at = result.AddPoint(n > 0 ? sum / n : G(i, j, k) + Vec3.One * (voxel / 2));
            pointOf[(i, j, k)] = at;
            return at;
        }

        // One quad across each crossed grid edge, joining the four cubes round it, facing out.
        void Quad(Vec3 outward, (int, int, int) c0, (int, int, int) c1, (int, int, int) c2, (int, int, int) c3)
        {
            int[] q = [CellPoint(c0.Item1, c0.Item2, c0.Item3), CellPoint(c1.Item1, c1.Item2, c1.Item3), CellPoint(c2.Item1, c2.Item2, c2.Item3), CellPoint(c3.Item1, c3.Item2, c3.Item3)];
            var poly = new Polygon(q, 0);
            if (Vec3.Dot(result.Normal(poly), outward) < 0) poly.Points.Reverse();
            result.Polygons.Add(poly);
        }
        for (int i = 1; i < nx - 1; i++)
            for (int j = 1; j < ny - 1; j++)
                for (int k = 1; k < nz - 1; k++)
                {
                    bool here = inside[i, j, k];
                    if (here != inside[i + 1, j, k])
                        Quad(here ? Vec3.UnitX : -Vec3.UnitX, (i, j - 1, k - 1), (i, j, k - 1), (i, j, k), (i, j - 1, k));
                    if (here != inside[i, j + 1, k])
                        Quad(here ? Vec3.UnitY : -Vec3.UnitY, (i - 1, j, k - 1), (i, j, k - 1), (i, j, k), (i - 1, j, k));
                    if (here != inside[i, j, k + 1])
                        Quad(here ? Vec3.UnitZ : -Vec3.UnitZ, (i - 1, j - 1, k), (i, j - 1, k), (i, j, k), (i - 1, j, k));
                }
        if (result.Polygons.Count == 0) throw new InvalidOperationException("Remesh found nothing inside at that voxel size: make the voxels smaller than the object's thinnest part.");

        // Even it out, pulling each pass back onto the original surface.
        result.Touch();
        for (int pass = 0; pass < smoothing; pass++)
        {
            Subdivision.Smooth(result, null, 0.5, 1);
            for (int p = 0; p < result.Points.Count; p++)
                if (bvh.Nearest(result.Points[p]) is { } near) result.Points[p] = near.Point;
            result.Touch();
        }

        int surface = mesh.Polygons[chosen[0]].Surface;
        foreach (var p in result.Polygons) p.Surface = surface;
        mesh.RemovePolygons(chosen);
        mesh.Append(result);
        mesh.RemoveUnusedPoints();
        return new Result(chosen.Count, result.Polygons.Count, nx * ny * nz);
    }

    /// <summary>
    /// Rays along one axis through every grid line: the crossings of each grid edge they find (by its lower grid point),
    /// and for each line the sorted places along the axis where the ray crosses the surface.
    /// </summary>
    private static Dictionary<(int, int, int), Vec3> Sweep(TriangleBvh bvh, int axis, int nx, int ny, int nz, double voxel, Vec3 origin, out List<double>[,] lines)
    {
        int[] n = [nx, ny, nz];
        int u = (axis + 1) % 3, v = (axis + 2) % 3;
        var dir = Vec3.Unit((Axis)axis);
        var crossings = new Dictionary<(int, int, int), Vec3>();
        lines = axis == 0 ? new List<double>[ny, nz] : new List<double>[1, 1];
        // A hair off the grid, so rays don't run exactly along the model's edges.
        double jitter = voxel * 1.31e-6;
        for (int a = 0; a < n[u]; a++)
            for (int b = 0; b < n[v]; b++)
            {
                var start = origin - dir * voxel;
                start = start.With((Axis)u, origin[(Axis)u] + a * voxel + jitter).With((Axis)v, origin[(Axis)v] + b * voxel + jitter * 0.7);
                var found = new List<double>();
                double t = 0, length = (n[axis] + 1) * voxel;
                while (t < length && bvh.Raycast(new Ray(start + dir * t, dir), length - t) is { } hit)
                {
                    double at = start[(Axis)axis] + t + hit.Distance;
                    found.Add(at);
                    int cell = (int)Math.Floor((at - origin[(Axis)axis]) / voxel);
                    if (cell >= 0 && cell < n[axis] - 1)
                    {
                        var key = axis == 0 ? (cell, a, b) : axis == 1 ? (b, cell, a) : (a, b, cell);
                        crossings.TryAdd(key, hit.Point);
                    }
                    t += hit.Distance + voxel * 1e-7;
                }
                if (axis == 0) lines[a, b] = found;
            }
        return crossings;
    }
}
