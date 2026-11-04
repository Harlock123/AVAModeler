using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>Whether Inset works face by face or on each connected region of faces as one.</summary>
public enum InsetMode { Individual, Group }

/// <summary>
/// Inset: shrinks faces inward, leaving a border band of quads round them, and optionally moves them along their
/// normals: with a depth, a region inset is a group bevel (the region bevelled as one, where Bevel works face by face).
/// </summary>
public static class Insets
{
    /// <summary>
    /// Insets the faces given. <see cref="InsetMode.Individual"/> treats each face on its own (a quad round each);
    /// <see cref="InsetMode.Group"/> treats each connected region of them as one, with quads only round the region's
    /// outline. The outline's edges move in by <paramref name="thickness"/> (in parallel, so the band is an even width;
    /// negative goes out), and the faces move <paramref name="depth"/> along their normals (in a region, the averaged
    /// normal at each point; negative goes in). The original outline stays for the faces round about. The faces keep
    /// their indices; the band's quads are added at the end with their faces' surfaces. Moved corners take the UVs of
    /// the place on the face they moved to, so the band's UVs run between the old outline and the new one. Points and
    /// lines are ignored.
    /// </summary>
    public static void Apply(Mesh mesh, IEnumerable<int> polygons, double thickness, double depth, InsetMode mode = InsetMode.Individual)
    {
        var faces = polygons.Where(i => i >= 0 && i < mesh.Polygons.Count && mesh.Polygons[i].Count >= 3 && mesh.Normal(i).LengthSquared > 0)
            .Distinct().ToList();
        if (faces.Count == 0) return;
        var regions = mode == InsetMode.Group ? Topology.Components(mesh, faces) : faces.Select(f => new List<int> { f }).ToList();
        var original = mesh.Clone();
        var walls = new List<Polygon>();
        var moves = new Dictionary<int, Vec3>();
        foreach (var region in regions) InsetRegion(mesh, original, region, thickness, depth, walls, moves);
        foreach (var (p, to) in moves) mesh.Points[p] = to;
        mesh.Polygons.AddRange(walls);
        mesh.Touch();
    }

    private static void InsetRegion(Mesh mesh, Mesh original, List<int> region, double thickness, double depth, List<Polygon> walls, Dictionary<int, Vec3> moves)
    {
        var inRegion = region.ToHashSet();
        var normals = region.ToDictionary(f => f, f => original.Normal(f));
        var counts = Topology.FaceEdgeCounts(original, region);
        // Each point's normal: the average of the region's faces round it.
        var pointNormal = new Dictionary<int, Vec3>();
        foreach (int f in region)
            foreach (int p in original.Polygons[f].Points.Distinct())
                pointNormal[p] = pointNormal.GetValueOrDefault(p) + normals[f];
        foreach (int p in pointNormal.Keys.ToList()) pointNormal[p] = pointNormal[p].Normalized();

        // The outline, as directed edges (the way their face runs along them), with the way in and out of each point.
        var rimIn = new Dictionary<int, List<int>>();
        var rimOut = new Dictionary<int, List<int>>();
        var rim = new List<(int A, int B, int Face)>();
        foreach (int f in region)
            foreach (var (a, b) in original.Polygons[f].Edges())
                if (a != b && counts[Edge.Of(a, b)] == 1)
                {
                    rim.Add((a, b, f));
                    (rimOut.TryGetValue(a, out var o) ? o : rimOut[a] = []).Add(b);
                    (rimIn.TryGetValue(b, out var i) ? i : rimIn[b] = []).Add(a);
                }

        // Where each point goes: outline points are copied, moved in along the surface (and out by the depth); points
        // inside the region that nothing else uses just move by the depth; the rest stay.
        var copies = new Dictionary<int, int>();
        var flat = new Dictionary<int, Vec3>();
        foreach (var (p, n) in pointNormal)
        {
            var at = original.Points[p];
            bool onRim = rimOut.ContainsKey(p) || rimIn.ContainsKey(p);
            if (onRim)
            {
                var d = Vec3.Zero;
                if (rimIn.TryGetValue(p, out var ins) && rimOut.TryGetValue(p, out var outs) && ins.Count == 1 && outs.Count == 1)
                {
                    var m1 = Vec3.Cross(n, (at - original.Points[ins[0]]).Normalized()).Normalized();
                    var m2 = Vec3.Cross(n, (original.Points[outs[0]] - at).Normalized()).Normalized();
                    // The point that is `thickness` in from both edges; a hairpin turn falls back to one edge's normal.
                    double denom = 1 + Vec3.Dot(m1, m2);
                    d = denom > 1e-6 ? (m1 + m2) * (thickness / denom) : m1 * thickness;
                }
                flat[p] = at + d;
                copies[p] = mesh.AddPoint(at + d + n * depth);
            }
            else if (original.PolygonsOf(p).All(inRegion.Contains))
                moves[p] = at + n * depth;
        }

        // The UVs the copies take: where they landed on the face that holds them (the region's best fit for a point
        // shared across faces whose UVs agree there, so no seam appears).
        var uvs = region.ToDictionary(f => f, f => FaceUVs.Of(original, original.Polygons[f]));
        var newUVs = region.ToDictionary(f => f, f => new Dictionary<string, Dictionary<int, Vec2>>());
        foreach (var map in original.UVMaps)
        {
            var fits = new Dictionary<int, UVTransfer.Interpolant?>();
            UVTransfer.Interpolant? Fit(int f) => fits.TryGetValue(f, out var x) ? x : fits[f] = UVTransfer.Interpolant.Of(original, original.Polygons[f], map.Name);
            foreach (var (p, copy) in copies)
            {
                var around = region.Where(f => original.Polygons[f].Points.Contains(p) && uvs[f].ContainsKey(map.Name)).ToList();
                if (around.Count == 0) continue;
                int bestFace = -1;
                Vec2 bestUV = default;
                double bestInside = double.NegativeInfinity;
                foreach (int f in around)
                {
                    if (Fit(f) is not { } fit) continue;
                    var uv = fit.At(flat[p], out double inside);
                    if (inside > bestInside) (bestFace, bestUV, bestInside) = (f, uv, inside);
                }
                if (bestFace < 0) continue;
                var shared = uvs[bestFace][map.Name][p];
                foreach (int f in around)
                {
                    var own = uvs[f][map.Name][p];
                    var uv = (own - shared).LengthSquared <= 1e-18 || Fit(f) is not { } fit ? bestUV : fit.At(flat[p]);
                    if (!newUVs[f].TryGetValue(map.Name, out var at)) newUVs[f][map.Name] = at = [];
                    at[copy] = uv;
                }
            }
        }

        foreach (var (a, b, f) in rim)
        {
            var wall = new Polygon([a, b, copies[b], copies[a]], original.Polygons[f].Surface);
            walls.Add(wall);
            foreach (var (name, at) in uvs[f])
            {
                var have = newUVs[f].GetValueOrDefault(name);
                if (have == null || !have.TryGetValue(copies[a], out var ua) || !have.TryGetValue(copies[b], out var ub)) continue;
                mesh.SetCornerUV(name, wall, a, at[a]);
                mesh.SetCornerUV(name, wall, b, at[b]);
                mesh.SetCornerUV(name, wall, copies[b], ub);
                mesh.SetCornerUV(name, wall, copies[a], ua);
            }
        }
        foreach (int f in region)
        {
            var poly = mesh.Polygons[f];
            if (!poly.Points.Any(copies.ContainsKey)) continue;
            var keep = uvs[f];
            poly.Points = poly.Points.Select(p => copies.GetValueOrDefault(p, p)).ToList();
            poly.CornerUVs = null;
            poly.Details = null;
            foreach (var (name, at) in keep)
                foreach (int p in original.Polygons[f].Points.Distinct())
                {
                    int q = copies.GetValueOrDefault(p, p);
                    var uv = at[p];
                    if (q != p && newUVs[f].TryGetValue(name, out var landed) && landed.TryGetValue(q, out var moved)) uv = moved;
                    mesh.SetCornerUV(name, poly, q, uv);
                }
        }
    }
}
