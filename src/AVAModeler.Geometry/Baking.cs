using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>What Bake Maps bakes.</summary>
public enum BakeKind
{
    /// <summary>Tangent-space normals: the detailed background's surface onto the foreground's, or its own smoothing.</summary>
    Normal,
    /// <summary>Ambient occlusion: white in the open, darker where nearby surfaces hide the sky.</summary>
    AmbientOcclusion,
    /// <summary>Curvature: lighter on edges and bumps, darker in creases, grey where flat.</summary>
    Curvature,
    /// <summary>The surface's colour, with its colour procedurals and UV images.</summary>
    Color,
}

/// <summary>How to bake.</summary>
public sealed record BakeSettings
{
    public BakeKind Kind { get; init; } = BakeKind.Normal;
    /// <summary>The picture's width and height in pixels.</summary>
    public int Size { get; init; } = 1024;
    /// <summary>How many pixels the baked areas are bled outward, so filtering at their edges doesn't pick up the background.</summary>
    public int Padding { get; init; } = 4;
    /// <summary>Ambient occlusion: rays a pixel.</summary>
    public int Samples { get; init; } = 32;
    /// <summary>
    /// Normal: how far from the low surface the detailed one is looked for, either way along its normal; ambient
    /// occlusion: how far away something still hides the sky. 0 works it out (a twentieth of the object's size).
    /// </summary>
    public double Distance { get; init; }
    /// <summary>The UV map baked through.</summary>
    public string UVMap { get; init; } = "UV";

    public BakeSettings Checked()
    {
        if (Size < 16 || Size > 8192) throw new ArgumentException("A baked map's size must be from 16 to 8192 pixels.");
        if (Padding < 0 || Padding > 64) throw new ArgumentException("Padding must be from 0 to 64 pixels.");
        if (Samples < 1 || Samples > 1024) throw new ArgumentException("Samples must be from 1 to 1024.");
        if (!(Distance >= 0) || !double.IsFinite(Distance)) throw new ArgumentException("Distance must be 0 (worked out) or more metres.");
        if (string.IsNullOrWhiteSpace(UVMap)) throw new ArgumentException("Baking needs a UV map: make one with Make UVs (atlas lays each face out on its own).");
        return this;
    }
}

/// <summary>A baked picture: RGBA rows from the top, and how many pixels the faces covered.</summary>
public sealed record BakedMap(int Width, int Height, byte[] Rgba, int Covered);

/// <summary>
/// Baking into a UV map: each face's triangles are laid onto the picture by their corners' UVs, and every pixel they
/// cover is worked out at the place on the face it stands for, then the covered areas are bled outward.
/// </summary>
public static class Baking
{
    public static BakedMap Bake(Mesh mesh, IReadOnlyCollection<int> faces, IReadOnlyList<Surface> surfaces, BakeSettings settings, Mesh? detail = null, string? imageBase = null)
    {
        settings = settings.Checked();
        int w = settings.Size, h = settings.Size;
        var chosen = faces.Where(i => i >= 0 && i < mesh.Polygons.Count && mesh.Polygons[i].Count >= 3).ToList();
        if (!chosen.Any(i => mesh.IsMapped(settings.UVMap, mesh.Polygons[i])))
            throw new InvalidOperationException($"None of the faces are laid out in the UV map \"{settings.UVMap}\": make UVs first (Make UVs, atlas or cubic, gives every face its own place).");
        var box = mesh.Bounds(chosen.SelectMany(i => mesh.Polygons[i].Points));
        double size = Math.Max(box.Extent, 1e-6);
        double reach = settings.Distance > 0 ? settings.Distance : size / 20;

        var normals = PointNormals(mesh);
        var curvature = settings.Kind == BakeKind.Curvature ? Curvature(mesh, normals) : null;
        var occluders = settings.Kind == BakeKind.AmbientOcclusion ? Brushes.BvhOf(detail == null ? mesh : Combined(mesh, detail)) : null;
        var target = settings.Kind == BakeKind.Normal && detail is { Polygons.Count: > 0 } ? detail : null;
        var targetBvh = target != null ? Brushes.BvhOf(target) : null;
        var targetNormals = target != null ? PointNormals(target) : null;
        var colors = new Vec3[w * h];
        var covered = new bool[w * h];
        int count = 0;

        foreach (int pi in chosen)
        {
            var poly = mesh.Polygons[pi];
            if (!mesh.IsMapped(settings.UVMap, poly)) continue;
            var surface = poly.Surface >= 0 && poly.Surface < surfaces.Count ? surfaces[poly.Surface] : null;
            bool smooth = surface?.Smoothing ?? false;
            var face = mesh.Normal(poly);
            var corners = Triangulator.TriangulatePolygon(mesh, poly);
            var look = settings.Kind == BakeKind.Color && surface != null ? ColorLook.Of(surface, imageBase, settings.UVMap) : null;
            for (int k = 0; k + 2 < corners.Length; k += 3)
            {
                int[] pts = [poly.Points[corners[k]], poly.Points[corners[k + 1]], poly.Points[corners[k + 2]]];
                var uv = pts.Select(p => mesh.TryGetUV(settings.UVMap, poly, p, out var u) ? u : Vec2.Zero).ToArray();
                var pos = pts.Select(p => mesh.Points[p]).ToArray();
                var nrm = pts.Select(p => smooth && normals[p].LengthSquared > 0 ? normals[p] : face).ToArray();
                // The triangle's U and V directions on the face, for tangent space.
                var (du, dv) = Tangents(pos, uv);
                double x0 = Math.Min(uv[0].X, Math.Min(uv[1].X, uv[2].X)) * w, x1 = Math.Max(uv[0].X, Math.Max(uv[1].X, uv[2].X)) * w;
                double y0 = (1 - Math.Max(uv[0].Y, Math.Max(uv[1].Y, uv[2].Y))) * h, y1 = (1 - Math.Min(uv[0].Y, Math.Min(uv[1].Y, uv[2].Y))) * h;
                double area = (uv[1].X - uv[0].X) * (uv[2].Y - uv[0].Y) - (uv[2].X - uv[0].X) * (uv[1].Y - uv[0].Y);
                if (Math.Abs(area) < 1e-14) continue;
                for (int y = Math.Max(0, (int)Math.Floor(y0)); y <= Math.Min(h - 1, (int)Math.Ceiling(y1)); y++)
                    for (int x = Math.Max(0, (int)Math.Floor(x0)); x <= Math.Min(w - 1, (int)Math.Ceiling(x1)); x++)
                    {
                        var at = new Vec2((x + 0.5) / w, 1 - (y + 0.5) / h);
                        double b1 = ((at.X - uv[0].X) * (uv[2].Y - uv[0].Y) - (uv[2].X - uv[0].X) * (at.Y - uv[0].Y)) / area;
                        double b2 = ((uv[1].X - uv[0].X) * (at.Y - uv[0].Y) - (at.X - uv[0].X) * (uv[1].Y - uv[0].Y)) / area;
                        double b0 = 1 - b1 - b2;
                        if (b0 < -1e-9 || b1 < -1e-9 || b2 < -1e-9) continue;
                        int i = y * w + x;
                        if (covered[i]) continue;
                        var p = pos[0] * b0 + pos[1] * b1 + pos[2] * b2;
                        var n = (nrm[0] * b0 + nrm[1] * b1 + nrm[2] * b2).Normalized();
                        if (n.LengthSquared == 0) n = face;
                        colors[i] = settings.Kind switch
                        {
                            BakeKind.Normal => target != null
                                ? ToTangent(FindNormal(target, targetBvh!, targetNormals!, p, n, reach) ?? n, n, du, dv)
                                : ToTangent(n, face, du, dv),
                            BakeKind.AmbientOcclusion => Vec3.One * Occlusion(occluders!, p, n, reach, settings.Samples, i, size),
                            BakeKind.Curvature => Vec3.One * (curvature![pts[0]] * b0 + curvature[pts[1]] * b1 + curvature[pts[2]] * b2),
                            _ => look?.At(p, uv[0] * b0 + uv[1] * b1 + uv[2] * b2) ?? Vec3.One,
                        };
                        covered[i] = true;
                        count++;
                    }
            }
        }

        // Bleed the covered areas outward.
        var filled = (bool[])covered.Clone();
        for (int pass = 0; pass < settings.Padding; pass++)
        {
            var add = new List<(int, Vec3)>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (filled[i]) continue;
                    var sum = Vec3.Zero;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h || !filled[yy * w + xx]) continue;
                            sum += colors[yy * w + xx];
                            n++;
                        }
                    if (n > 0) add.Add((i, sum / n));
                }
            if (add.Count == 0) break;
            foreach (var (i, c) in add) { colors[i] = c; filled[i] = true; }
        }
        var background = settings.Kind switch
        {
            BakeKind.Normal => new Vec3(0.5, 0.5, 1),
            BakeKind.Curvature => new Vec3(0.5, 0.5, 0.5),
            _ => Vec3.One,
        };
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            var c = filled[i] ? colors[i] : background;
            rgba[i * 4] = Byte(c.X);
            rgba[i * 4 + 1] = Byte(c.Y);
            rgba[i * 4 + 2] = Byte(c.Z);
            rgba[i * 4 + 3] = 255;
        }
        return new BakedMap(w, h, rgba, count);
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static Mesh Combined(Mesh a, Mesh b)
    {
        var m = a.Clone();
        m.Append(b);
        return m;
    }

    /// <summary>Each point's normal: the area-weighted average of its faces'.</summary>
    public static Vec3[] PointNormals(Mesh mesh)
    {
        var n = new Vec3[mesh.Points.Count];
        foreach (var poly in mesh.Polygons)
        {
            if (poly.Count < 3) continue;
            var f = mesh.Normal(poly) * mesh.Area(poly);
            foreach (int p in poly.Points.Distinct()) n[p] += f;
        }
        for (int i = 0; i < n.Length; i++) n[i] = n[i].Normalized();
        return n;
    }

    /// <summary>The way U and V run across a triangle in space, from its corners and their UVs.</summary>
    private static (Vec3 U, Vec3 V) Tangents(Vec3[] p, Vec2[] uv)
    {
        var e1 = p[1] - p[0];
        var e2 = p[2] - p[0];
        var d1 = uv[1] - uv[0];
        var d2 = uv[2] - uv[0];
        double det = d1.X * d2.Y - d2.X * d1.Y;
        if (Math.Abs(det) < 1e-18) return (Vec3.Zero, Vec3.Zero);
        double r = 1 / det;
        return ((e1 * d2.Y - e2 * d1.Y) * r, (e2 * d1.X - e1 * d2.X) * r);
    }

    /// <summary>A normal in a face's tangent space (U, V, out), as a normal map's colour.</summary>
    public static Vec3 ToTangent(Vec3 normal, Vec3 n, Vec3 du, Vec3 dv)
    {
        var t = (du - n * Vec3.Dot(du, n)).Normalized();
        var b = dv - n * Vec3.Dot(dv, n) - t * Vec3.Dot(dv, t);
        if (t.LengthSquared == 0 || b.LengthSquared == 0) return new Vec3(0.5, 0.5, 1);
        b = b.Normalized();
        var m = new Vec3(Vec3.Dot(normal, t), Vec3.Dot(normal, b), Vec3.Dot(normal, n));
        return new Vec3(m.X * 0.5 + 0.5, m.Y * 0.5 + 0.5, m.Z * 0.5 + 0.5);
    }

    /// <summary>
    /// The detailed surface's normal over a place on the low one: where a ray along the low normal (from outside in,
    /// within <paramref name="reach"/> either way) meets it, its smoothed normal there. Null if it's not found.
    /// </summary>
    private static Vec3? FindNormal(Mesh target, TriangleBvh bvh, Vec3[] normals, Vec3 p, Vec3 n, double reach)
    {
        var hit = bvh.Raycast(new Ray(p + n * reach, -n), reach * 2);
        if (hit is not { } h) return null;
        var poly = target.Polygons[h.Polygon];
        // Blend the face's point normals by nearness to the hit (a smooth surface), or its own normal.
        var sum = Vec3.Zero;
        double total = 0;
        foreach (int q in poly.Points)
        {
            double wgt = 1 / Math.Max((target.Points[q] - h.Point).Length, 1e-9);
            sum += normals[q] * wgt;
            total += wgt;
        }
        var blended = total > 0 ? (sum / total).Normalized() : target.Normal(poly);
        return blended.LengthSquared > 0 ? blended : target.Normal(poly);
    }

    /// <summary>The share of rays over the hemisphere (cosine-weighted, repeatable by pixel) that reach the sky.</summary>
    private static double Occlusion(TriangleBvh bvh, Vec3 p, Vec3 n, double reach, int samples, int pixel, double size)
    {
        var t = Math.Abs(n.X) < 0.9 ? Vec3.Cross(n, Vec3.UnitX).Normalized() : Vec3.Cross(n, Vec3.UnitY).Normalized();
        var b = Vec3.Cross(n, t);
        var from = p + n * (size * 1e-5);
        int open = 0;
        for (int s = 0; s < samples; s++)
        {
            double u1 = Hash(pixel, s, 1), u2 = Hash(pixel, s, 2);
            double r = Math.Sqrt(u1), phi = 2 * Math.PI * u2;
            var dir = (t * (r * Math.Cos(phi)) + b * (r * Math.Sin(phi)) + n * Math.Sqrt(Math.Max(0, 1 - u1))).Normalized();
            if (bvh.Raycast(new Ray(from, dir), reach) == null) open++;
        }
        return (double)open / samples;
    }

    private static double Hash(int pixel, int sample, int dim)
    {
        uint h = (uint)pixel * 0x9E3779B1u ^ (uint)sample * 0x85EBCA77u ^ (uint)dim * 0xC2B2AE3Du;
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return h / 4294967296.0;
    }

    /// <summary>Each point's curvature as a grey: 0.5 flat, toward 1 on edges and bumps, toward 0 in creases.</summary>
    private static double[] Curvature(Mesh mesh, Vec3[] normals)
    {
        var k = new double[mesh.Points.Count];
        double most = 0;
        for (int i = 0; i < k.Length; i++)
        {
            var nb = mesh.Neighbours(i).ToList();
            if (nb.Count == 0 || normals[i].LengthSquared == 0) continue;
            var avg = nb.Aggregate(Vec3.Zero, (s, j) => s + mesh.Points[j]) / nb.Count;
            double edge = nb.Average(j => (mesh.Points[j] - mesh.Points[i]).Length);
            if (edge <= 0) continue;
            // Neighbours below the point (along its normal) make a bump: the Laplacian points inward.
            k[i] = -Vec3.Dot(avg - mesh.Points[i], normals[i]) / edge;
            most = Math.Max(most, Math.Abs(k[i]));
        }
        for (int i = 0; i < k.Length; i++) k[i] = most > 0 ? 0.5 + 0.5 * k[i] / most : 0.5;
        return k;
    }

    /// <summary>A surface's colour at a place: its own, under its colour procedurals and its UV images in the map.</summary>
    private sealed class ColorLook
    {
        private Vec3 _base;
        private readonly List<(TextureLayer Layer, ProceduralTexture? Proc, TextureImage? Image)> _layers = [];

        public static ColorLook Of(Surface s, string? imageBase, string uvMap)
        {
            var look = new ColorLook { _base = s.Color.ToVec() };
            foreach (var t in s.TexturesOf(TextureChannel.Color))
            {
                if (!t.IsDrawn || t.Opacity <= 0) continue;
                if (!t.IsImage) look._layers.Add((t, Procedurals.Prepare(t), null));
                else if (t.Projection == TextureProjection.UV && (t.UVMap == uvMap || string.IsNullOrEmpty(t.UVMap))) look._layers.Add((t, null, TextureImages.Load(t.Image, imageBase)));
            }
            return look;
        }

        public Vec3 At(Vec3 p, Vec2 uv)
        {
            var c = _base;
            foreach (var (t, proc, img) in _layers)
            {
                if (proc != null) c = TextureMapping.Blend(c, proc.Color, Math.Clamp(t.Opacity, 0, 1) * proc.Amount(p), t.Blend);
                else if (img != null)
                {
                    var (col, alpha) = img.Sample(uv.X, uv.Y, t.WidthTiling, t.HeightTiling, t.PixelBlending);
                    if (t.Negative) col = Vec3.One - col;
                    c = TextureMapping.Blend(c, col, Math.Clamp(t.Opacity, 0, 1) * alpha, t.Blend);
                }
            }
            return c;
        }
    }
}
