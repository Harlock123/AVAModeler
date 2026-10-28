using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>
/// UVs for geometry cut out of other geometry (booleans and drills): each new corner takes the UVs its source face
/// had at the same place, interpolated across the triangle of the source face it lies in. Correct for any face whose
/// UVs vary evenly across it, which is every flat-mapped face; it doesn't try to be clever about the rest.
/// </summary>
internal static class UVTransfer
{
    /// <summary>
    /// Gives the result's polygons the UVs of their sources (a source mesh and one of its polygons, for each result
    /// polygon that came from one), in every UV map the sources have. Corners whose source face isn't wholly mapped
    /// are left unmapped.
    /// </summary>
    public static void FromSources(Mesh result, IReadOnlyDictionary<Polygon, (Mesh Mesh, Polygon Polygon)> sources, params Mesh[] sourceMeshes)
    {
        var names = sourceMeshes.SelectMany(m => m.UVMaps).Select(m => m.Name).Distinct().ToList();
        if (names.Count == 0) return;
        foreach (var name in names) result.AddUVMap(name);
        var cache = new Dictionary<(Polygon, string), Interpolant?>();
        foreach (var poly in result.Polygons)
        {
            if (!sources.TryGetValue(poly, out var src)) continue;
            poly.CornerUVs = null;
            foreach (var name in names)
            {
                if (!cache.TryGetValue((src.Polygon, name), out var f)) cache[(src.Polygon, name)] = f = Interpolant.Of(src.Mesh, src.Polygon, name);
                if (f == null) continue;
                foreach (int q in poly.Points.Distinct())
                    if (q >= 0 && q < result.Points.Count) result.SetCornerUV(name, poly, q, f.At(result.Points[q]));
            }
        }
    }

    /// <summary>A face's UVs as a function of position: its triangles, each with its corners' UVs.</summary>
    private sealed class Interpolant
    {
        private readonly List<(Vec3 A, Vec3 B, Vec3 C, Vec2 Ua, Vec2 Ub, Vec2 Uc)> _tris = [];

        public static Interpolant? Of(Mesh mesh, Polygon poly, string map)
        {
            if (poly.Count < 3) return null;
            var uvs = mesh.CornerUVsOf(map, poly);
            if (uvs.Any(u => u == null)) return null;
            var f = new Interpolant();
            var tris = Triangulator.TriangulatePolygon(mesh, poly);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                f._tris.Add((mesh.Points[poly.Points[a]], mesh.Points[poly.Points[b]], mesh.Points[poly.Points[c]], uvs[a]!.Value, uvs[b]!.Value, uvs[c]!.Value));
            }
            return f._tris.Count > 0 ? f : null;
        }

        /// <summary>The UVs at a point: from the triangle it lies in (or, failing that, the nearest), by barycentric weights.</summary>
        public Vec2 At(Vec3 p)
        {
            Vec2 best = _tris[0].Ua;
            double bestScore = double.NegativeInfinity;
            foreach (var t in _tris)
            {
                var (w0, w1, w2) = Barycentric(p, t.A, t.B, t.C);
                double score = Math.Min(w0, Math.Min(w1, w2));
                if (score <= bestScore) continue;
                bestScore = score;
                // Outside every triangle (a point a hair off the face), the weights are clamped onto the nearest.
                if (score < 0)
                {
                    w0 = Math.Max(w0, 0);
                    w1 = Math.Max(w1, 0);
                    w2 = Math.Max(w2, 0);
                    double s = w0 + w1 + w2;
                    if (s > 0) (w0, w1, w2) = (w0 / s, w1 / s, w2 / s);
                    else (w0, w1, w2) = (1, 0, 0);
                }
                best = t.Ua * w0 + t.Ub * w1 + t.Uc * w2;
            }
            return best;
        }

        private static (double, double, double) Barycentric(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
        {
            Vec3 v0 = b - a, v1 = c - a, v2 = p - a;
            double d00 = Vec3.Dot(v0, v0), d01 = Vec3.Dot(v0, v1), d11 = Vec3.Dot(v1, v1);
            double d20 = Vec3.Dot(v2, v0), d21 = Vec3.Dot(v2, v1);
            double den = d00 * d11 - d01 * d01;
            if (Math.Abs(den) < 1e-300) return (1, 0, 0);
            double v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
            return (1 - v - w, v, w);
        }
    }
}
