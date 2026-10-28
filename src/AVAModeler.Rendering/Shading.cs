using AVAModeler.Core;

namespace AVAModeler.Rendering;

/// <summary>How a face is lit in the shaded view, shared by both renderers so they draw the same picture.</summary>
public static class Shading
{
    /// <summary>A headlight a little above and to the left of the eye.</summary>
    public static Vec3 Light(ViewCamera cam)
    {
        var (r, u, f) = cam.Basis();
        return (-f + u * 0.5 - r * 0.35).Normalized();
    }

    /// <summary>
    /// A face's colour (0..1 per channel): its surface's colour, diffuse and luminosity, a specular highlight
    /// by glossiness, a blue tint when seen from behind (unless double-sided), and yellow when selected.
    /// </summary>
    public static Vec3 FaceColor(Vec3 normal, Vec3 at, Surface? surface, ViewCamera cam, Vec3 light, bool backfaceTint, bool selected, Vec3? smoothNormal = null)
    {
        var toEye = cam.IsPerspective ? (cam.Eye - at).Normalized() : -cam.Basis().Forward;
        // Which way the face looks decides the tint; a smoothed normal (when given) does the lighting.
        bool facingAway = Vec3.Dot(normal, toEye) < 0;
        var lit = smoothNormal ?? normal;
        var n = facingAway ? -lit : lit;
        double k = 0.22 + 0.78 * (surface?.Diffuse ?? 1) * Math.Max(0, Vec3.Dot(n, light));
        double spec = (surface?.Specular ?? 0) * Math.Pow(Math.Max(0, Vec3.Dot((light + toEye).Normalized(), n)), Math.Max(4, surface?.Glossiness ?? 64));
        var col = (surface?.Color.ToVec() ?? new Vec3(0.78, 0.78, 0.78)) * Math.Min(1, k + (surface?.Luminosity ?? 0)) + Vec3.One * spec;
        if (facingAway && !(surface?.DoubleSided ?? false) && backfaceTint) col = Vec3.Lerp(col, new Vec3(0.42, 0.47, 0.72), 0.65);
        if (selected) col = Vec3.Lerp(col, new Vec3(1, 0.89, 0.29), 0.55);
        return new Vec3(Math.Clamp(col.X, 0, 1), Math.Clamp(col.Y, 0, 1), Math.Clamp(col.Z, 0, 1));
    }

    /// <summary>
    /// <see cref="FaceColor"/> taken apart for a textured face, whose colour comes per pixel: the colour is
    /// clamp(colour · Mul + Add), with the same lighting, tint and selection.
    /// </summary>
    public static (double Mul, Vec3 Add) FaceLight(Vec3 normal, Vec3 at, Surface? surface, ViewCamera cam, Vec3 light, bool backfaceTint, bool selected, Vec3? smoothNormal = null)
    {
        var toEye = cam.IsPerspective ? (cam.Eye - at).Normalized() : -cam.Basis().Forward;
        bool facingAway = Vec3.Dot(normal, toEye) < 0;
        var lit = smoothNormal ?? normal;
        var n = facingAway ? -lit : lit;
        double k = 0.22 + 0.78 * (surface?.Diffuse ?? 1) * Math.Max(0, Vec3.Dot(n, light));
        double spec = (surface?.Specular ?? 0) * Math.Pow(Math.Max(0, Vec3.Dot((light + toEye).Normalized(), n)), Math.Max(4, surface?.Glossiness ?? 64));
        double mul = Math.Min(1, k + (surface?.Luminosity ?? 0));
        var add = Vec3.One * spec;
        if (facingAway && !(surface?.DoubleSided ?? false) && backfaceTint)
        {
            mul *= 0.35;
            add = add * 0.35 + new Vec3(0.42, 0.47, 0.72) * 0.65;
        }
        if (selected)
        {
            mul *= 0.45;
            add = add * 0.45 + new Vec3(1, 0.89, 0.29) * 0.55;
        }
        return (mul, add);
    }

    private sealed record NormalsEntry(int Version, int SurfacesHash, Vec3[][] Normals);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, NormalsEntry> NormalsCache = new();

    /// <summary>
    /// Each polygon's corner normals for smooth shading: for a surface with smoothing on, the average of the faces that
    /// share the point and the surface and meet this face within the surface's smoothing angle; otherwise the face's
    /// own normal. Cached until the mesh or the surfaces' smoothing changes.
    /// </summary>
    public static Vec3[][] CornerNormals(Mesh mesh, IReadOnlyList<Surface> surfaces)
    {
        int hash = 17;
        foreach (var s in surfaces) hash = HashCode.Combine(hash, s.Smoothing, s.SmoothingAngle);
        if (NormalsCache.TryGetValue(mesh, out var e) && e.Version == mesh.Version && e.SurfacesHash == hash) return e.Normals;
        var face = new Vec3[mesh.Polygons.Count];
        for (int i = 0; i < face.Length; i++) face[i] = mesh.Normal(i);
        var result = new Vec3[mesh.Polygons.Count][];
        for (int pi = 0; pi < mesh.Polygons.Count; pi++)
        {
            var poly = mesh.Polygons[pi];
            var surface = poly.Surface >= 0 && poly.Surface < surfaces.Count ? surfaces[poly.Surface] : null;
            var corners = new Vec3[poly.Count];
            for (int c = 0; c < poly.Count; c++)
            {
                if (surface is not { Smoothing: true } || poly.Count < 3) { corners[c] = face[pi]; continue; }
                double limit = Math.Cos(Mat4.Deg(Math.Clamp(surface.SmoothingAngle, 0, 180)));
                var sum = Vec3.Zero;
                foreach (int other in mesh.PolygonsOf(poly.Points[c]))
                    if (mesh.Polygons[other].Surface == poly.Surface && mesh.Polygons[other].Count >= 3 && Vec3.Dot(face[other], face[pi]) >= limit - 1e-9)
                        sum += face[other];
                corners[c] = sum.LengthSquared > 0 ? sum.Normalized() : face[pi];
            }
            result[pi] = corners;
        }
        NormalsCache.AddOrUpdate(mesh, new NormalsEntry(mesh.Version, hash, result));
        return result;
    }

    /// <summary>A corner's smooth normal, by the point it uses in its polygon.</summary>
    public static Vec3 Corner(Vec3[][] normals, Mesh mesh, int polygon, int point)
    {
        int c = mesh.Polygons[polygon].Points.IndexOf(point);
        return c >= 0 ? normals[polygon][c] : mesh.Normal(polygon);
    }

    public static Surface? SurfaceOf(ViewScene scene, Polygon p) =>
        p.Surface >= 0 && p.Surface < scene.Surfaces.Count ? scene.Surfaces[p.Surface] : null;
}
