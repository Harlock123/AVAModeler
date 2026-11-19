using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>What <see cref="RayTracer.BakeLighting"/> bakes, and how.</summary>
public sealed record LightBakeSettings
{
    /// <summary>The picture's width and height in pixels, 16 to 4096.</summary>
    public int Size { get; init; } = 512;
    /// <summary>The UV map to bake through (null: the object's first).</summary>
    public string? UVMap { get; init; }
    /// <summary>Whether the surfaces' colour (and glow) is baked in, or only the light falling on them.</summary>
    public bool WithColor { get; init; } = true;
    /// <summary>Rays a pixel for soft lights, ambient occlusion, the environment and bounced light, 1 to 1024.</summary>
    public int Samples { get; init; } = 32;
    /// <summary>Pixels the baked areas are spread out by past their edges, so filtering doesn't pull in the empty ones.</summary>
    public int Padding { get; init; } = 4;
    /// <summary>A scale on the light before it's written (1: as rendered).</summary>
    public double Exposure { get; init; } = 1;

    public LightBakeSettings Checked()
    {
        if (Size is < 16 or > 4096) throw new ArgumentException("The bake's size must be from 16 to 4096 pixels.");
        if (Samples is < 1 or > 1024) throw new ArgumentException("The bake's samples must be from 1 to 1024 rays a pixel.");
        if (Padding is < 0 or > 64) throw new ArgumentException("The bake's padding must be from 0 to 64 pixels.");
        if (!(Exposure > 0) || !double.IsFinite(Exposure)) throw new ArgumentException("The bake's exposure must be more than 0.");
        return this;
    }
}

/// <summary>A baked light map: its pixels (RGBA, rows from the top) and as a PNG, its size, the pixels covered, and the brightest light found (before clamping).</summary>
public sealed record LightBakeResult(byte[] Rgba, byte[] Png, int Width, int Height, int Covered, double Brightest, string UVMap);

public static partial class RayTracer
{
    private const int DimensionBounce = 5;

    /// <summary>
    /// Bakes an object's lighting at a frame into a picture through its UV map: at every pixel a face covers, the light
    /// falling there as the ray tracer works it out (the ambient, dimmed by ambient occlusion when that's on; the
    /// environment's light; every light with its shadows, soft ones sampled; and bounced light when global
    /// illumination is on), times the surface's colour with its glow added (<see cref="LightBakeSettings.WithColor"/>),
    /// or the light alone. Textures aren't baked in: with colour, it's the surfaces' own colours.
    /// </summary>
    public static LightBakeResult BakeLighting(Scene scene, ObjectItem item, double frame, LightBakeSettings? settings = null, CancellationToken cancel = default)
    {
        settings = (settings ?? new LightBakeSettings()).Checked();
        if (item.IsPlaceholder) throw new InvalidOperationException($"{item.Name} has no geometry to bake.");
        var sf = scene.Evaluate(frame, render: true);
        var of = sf.Objects.FirstOrDefault(o => o.Item == item) ?? throw new InvalidOperationException($"{item.Name} isn't in the scene.");
        var mesh = of.Mesh;
        string map = settings.UVMap ?? mesh.UVMaps.FirstOrDefault(m => m.Points.Count > 0)?.Name
            ?? throw new InvalidOperationException($"{item.Name} has no UV map to bake through: give it one in the Modeler (Unwrap UVs or Make UVs) first.");
        if (mesh.FindUVMap(map) == null) throw new InvalidOperationException($"{item.Name} has no UV map \"{map}\". Its maps: {string.Join(", ", mesh.UVMaps.Select(m => m.Name))}.");
        int size = settings.Size;
        var tracer = new Tracer(scene, sf, frame, new RenderSettings(), 1.0 / size, EnvironmentOf(scene));

        // Each face's point normals, smoothed where its surface is smooth.
        var faceNormal = mesh.Polygons.Select(p => p.Count >= 3 ? mesh.Normal(p) : Vec3.Zero).ToArray();
        var pointNormal = new Dictionary<(int Surface, int Point), Vec3>();
        for (int f = 0; f < mesh.Polygons.Count; f++)
            foreach (int p in mesh.Polygons[f].Points)
            {
                var key = (mesh.Polygons[f].Surface, p);
                pointNormal[key] = pointNormal.GetValueOrDefault(key) + faceNormal[f] * mesh.Area(mesh.Polygons[f]);
            }
        var world = of.World;
        var normalMatrix = world.Inverted().Transposed();
        bool mirrored = world.Determinant() < 0;

        // Every covered pixel's place, normal and surface; the first face to cover a pixel keeps it.
        var texels = new (Vec3 P, Vec3 N, int Surface)?[size * size];
        for (int f = 0; f < mesh.Polygons.Count; f++)
        {
            var poly = mesh.Polygons[f];
            if (poly.Count < 3 || !mesh.IsMapped(map, poly)) continue;
            var uv = mesh.CornerUVsOf(map, poly);
            var corners = poly.Points.Select(i => mesh.Points[i]).ToArray();
            var surface = poly.Surface < of.Surfaces.Count ? of.Surfaces[poly.Surface] : null;
            bool smooth = surface?.Smoothing == true;
            var normals = poly.Points.Select(i => smooth ? pointNormal[(poly.Surface, i)].Normalized() : faceNormal[f]).ToArray();
            foreach (var (a, b, c) in Triangulator.Triangulate(corners, faceNormal[f]))
            {
                var (ua, ub, uc) = (Pixel(uv[a]!.Value, size), Pixel(uv[b]!.Value, size), Pixel(uv[c]!.Value, size));
                double area = Vec2.Cross(ub - ua, uc - ua);
                if (Math.Abs(area) < 1e-12) continue;
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ua.X, Math.Min(ub.X, uc.X)))), x1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(ua.X, Math.Max(ub.X, uc.X))));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ua.Y, Math.Min(ub.Y, uc.Y)))), y1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(ua.Y, Math.Max(ub.Y, uc.Y))));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var q = new Vec2(x + 0.5, y + 0.5);
                        double wa = Vec2.Cross(ub - q, uc - q) / area, wb = Vec2.Cross(uc - q, ua - q) / area, wc = 1 - wa - wb;
                        if (wa < -1e-9 || wb < -1e-9 || wc < -1e-9) continue;
                        int k = y * size + x;
                        if (texels[k] != null) continue;
                        var p = corners[a] * wa + corners[b] * wb + corners[c] * wc;
                        var n = normals[a] * wa + normals[b] * wb + normals[c] * wc;
                        var wn = normalMatrix.TransformVector(n).Normalized();
                        texels[k] = (world.TransformPoint(p), mirrored ? -wn : wn, poly.Surface);
                    }
            }
        }
        cancel.ThrowIfCancellationRequested();

        var light = new Vec3[size * size];
        double brightest = 0;
        var gate = new object();
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            double rowMax = 0;
            for (int x = 0; x < size; x++)
            {
                int k = y * size + x;
                if (texels[k] is not { } t) continue;
                var irradiance = tracer.BakeIrradiance(t.P, t.N, item, new PixelSamples(x, y, 0, 1), settings.Samples);
                Vec3 value = irradiance;
                if (settings.WithColor)
                {
                    var s = t.Surface < of.Surfaces.Count ? of.Surfaces[t.Surface] : new Surface("Default");
                    value = Vec3.Scale(s.Color.ToVec() * s.Diffuse, irradiance) + s.Color.ToVec() * s.Luminosity;
                }
                value *= settings.Exposure;
                light[k] = value;
                rowMax = Math.Max(rowMax, Math.Max(value.X, Math.Max(value.Y, value.Z)));
            }
            lock (gate) brightest = Math.Max(brightest, rowMax);
        });

        var rgba = new byte[size * size * 4];
        var covered = new bool[size * size];
        int count = 0;
        for (int k = 0; k < texels.Length; k++)
        {
            if (texels[k] == null) continue;
            covered[k] = true;
            count++;
            rgba[k * 4] = ToByte(light[k].X);
            rgba[k * 4 + 1] = ToByte(light[k].Y);
            rgba[k * 4 + 2] = ToByte(light[k].Z);
            rgba[k * 4 + 3] = 255;
        }
        Bleed(rgba, covered, size, settings.Padding);
        return new LightBakeResult(rgba, Png.Encode(size, size, rgba), size, size, count, brightest, map);
    }

    /// <summary>A UV in pixels: u across from the left, v up from the bottom (so rows run down from v = 1).</summary>
    private static Vec2 Pixel(Vec2 uv, int size) => new(uv.X * size, (1 - uv.Y) * size);

    /// <summary>Spreads covered pixels outward into empty ones, a ring a pass.</summary>
    private static void Bleed(byte[] rgba, bool[] covered, int size, int passes)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            var add = new List<(int K, byte R, byte G, byte B)>();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int k = y * size + x;
                    if (covered[k]) continue;
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                            int j = yy * size + xx;
                            if (!covered[j]) continue;
                            r += rgba[j * 4]; g += rgba[j * 4 + 1]; b += rgba[j * 4 + 2]; n++;
                        }
                    if (n > 0) add.Add((k, (byte)(r / n), (byte)(g / n), (byte)(b / n)));
                }
            if (add.Count == 0) break;
            foreach (var (k, r, g, b) in add)
            {
                (rgba[k * 4], rgba[k * 4 + 1], rgba[k * 4 + 2], rgba[k * 4 + 3]) = (r, g, b, 255);
                covered[k] = true;
            }
        }
    }

    private sealed partial class Tracer
    {
        private readonly GlobalIllumination? _gi;

        /// <summary>
        /// The bounced light reaching a point from the hemisphere above it: cosine-weighted rays, each bringing back
        /// what the surface it meets reflects diffusely. The first bounce of a camera ray takes its share of the
        /// pixel's samples; deeper bounces and other rays take one.
        /// </summary>
        private Vec3 Indirect(Vec3 from, Vec3 ns, Vec3 nf, in PixelSamples pixel, int depth, int bounce, int? samples = null)
        {
            var gi = _gi!;
            int k = samples ?? (bounce == 0 && depth == 0 ? pixel.PerHit(gi.Samples) : 1);
            var sum = Vec3.Zero;
            for (int i = 0; i < k; i++)
            {
                var (u, v) = pixel.Point(DimensionBounce + bounce, depth, i, k);
                var d = Hemisphere(ns, u, v);
                if (Vec3.Dot(d, nf) <= 0) continue;
                sum += Radiance(from, d, bounce, pixel, depth);
            }
            return sum / k * gi.Strength;
        }

        /// <summary>The light a ray brings back for bounced light: what the surface it meets reflects diffusely, or the backdrop's.</summary>
        private Vec3 Radiance(Vec3 origin, Vec3 dir, int bounce, in PixelSamples pixel, int depth)
        {
            var gi = _gi!;
            if (!_bvh.Intersect(origin, dir, _bias, double.PositiveInfinity, out var hit))
                return gi.Backdrop && !(_environment != null && _scene.Environment.Lighting) ? Background(dir) : Vec3.Zero;
            var tri = _bvh[hit.Triangle];
            var m = _materials[tri.Material];
            var p = origin + dir * hit.T;
            var nf = tri.Normal;
            var ns = (tri.NA * (1 - hit.U - hit.V) + tri.NB * hit.U + tri.NC * hit.V).Normalized();
            if (ns.LengthSquared == 0) ns = nf;
            var look = LookAt(m, tri, hit, p, ns, hit.T, -dir);
            ns = look.Normal;
            if (Vec3.Dot(dir, nf) > 0) { nf = -nf; ns = -ns; }
            var lift = nf * _bias * 10;
            var diffuse = look.Color * look.Diffuse * (1 - m.Dissolve);
            var irradiance = _ambient + DirectDiffuse(p, lift, ns, nf, m.Owner, m.ReceiveShadows, pixel, depth, sampled: false);
            var result = Vec3.Scale(diffuse, irradiance) + look.Color * look.Luminosity;
            if (bounce + 1 < gi.Bounces) result += Vec3.Scale(diffuse, Indirect(p + lift, ns, nf, pixel, depth, bounce + 1));
            return result;
        }

        /// <summary>
        /// The light from the lights falling on a point (each one's colour × strength × cos, shadowed and linked as in
        /// a render). <paramref name="sampled"/> samples soft lights across their area; otherwise each is taken at its middle.
        /// </summary>
        private Vec3 DirectDiffuse(Vec3 p, Vec3 lift, Vec3 ns, Vec3 nf, ObjectItem? owner, bool receiveShadows, in PixelSamples pixel, int depth, bool sampled)
        {
            var lit = Vec3.Zero;
            for (int li = 0; li < _lights.Count; li++)
            {
                var light = _lights[li];
                if (light.Item.LinkMode != LightLinkMode.All && !light.Item.Lights(owner)) continue;
                var map = _shadowMaps?[li];
                if (sampled && light.Item.IsSoft && map == null)
                {
                    var highlight = Vec3.Zero;
                    var white = new Look(Vec3.One, 1, 0, 0, 0, 0, ns);
                    var plain = new Material(Vec3.One, 1, 0, 1, 0, 0, 0, 1, true, receiveShadows) { Owner = owner };
                    SoftLight(light, li, p, lift, ns, ns, white, plain, Vec3.One, pixel, depth, ref lit, ref highlight);
                    continue;
                }
                Vec3 toLight;
                double distance;
                if (light.Type == LightType.Distant)
                {
                    toLight = -light.Direction;
                    distance = double.PositiveInfinity;
                }
                else
                {
                    var d = light.Position - p;
                    distance = d.Length;
                    if (distance == 0) continue;
                    toLight = d / distance;
                }
                double ndl = Vec3.Dot(ns, toLight);
                if (ndl <= 0) continue;
                double strength = light.Item.Falloff(distance);
                if (light.Type == LightType.Spot)
                    strength *= light.Item.SpotFactor(Math.Acos(Math.Clamp(Vec3.Dot(light.Direction, -toLight), -1, 1)) * 180 / Math.PI);
                else if (light.Type == LightType.Area)
                    strength *= Math.Max(0, Vec3.Dot(light.Direction, -toLight));
                if (strength <= 0) continue;
                if (_settings.Shadows && light.Item.Shadows && receiveShadows)
                    strength *= map != null ? map.Lit(p + lift, nf, toLight, _settings.Counters) : Transmission(p + lift, toLight, distance);
                if (strength <= 0) continue;
                lit += light.Color * (strength * ndl);
            }
            return lit;
        }

        /// <summary>
        /// The light falling on a point of a surface for a light map, as a render works it out for a white, fully diffuse
        /// surface there: ambient (with occlusion), the environment, the lights (soft ones sampled) and bounced light.
        /// </summary>
        public Vec3 BakeIrradiance(Vec3 p, Vec3 n, ObjectItem owner, in PixelSamples pixel, int samples)
        {
            var lift = n * _bias * 10;
            var spread = new PixelSamples(pixel.X, pixel.Y, 0, 1);
            var ambient = _ambient;
            if (_occlusion != null) ambient *= OcclusionWith(p + lift, n, spread, samples);
            var lit = ambient;
            if (_environment != null && _scene.Environment.Lighting) lit += EnvironmentLightWith(p + lift, n, spread, samples);
            lit += DirectDiffuse(p, lift, n, n, owner, owner.ReceiveShadows, spread, 0, sampled: true);
            if (_gi != null) lit += Indirect(p + lift, n, n, spread, 0, 0, samples);
            return lit;
        }

        private double OcclusionWith(Vec3 from, Vec3 n, in PixelSamples pixel, int samples)
        {
            var o = _occlusion!;
            double hidden = 0;
            for (int i = 0; i < samples; i++)
            {
                var (u, v) = pixel.Point(DimensionOcclusion, 0, i, samples);
                var d = Hemisphere(n, u, v);
                if (_bvh.Intersect(from, d, _bias, o.Distance, out var hit))
                {
                    double f = hit.T / o.Distance;
                    hidden += (1 - f * f) * (1 - _materials[_bvh[hit.Triangle].Material].Dissolve);
                }
            }
            return Math.Clamp(1 - o.Strength * hidden / samples, 0, 1);
        }

        private Vec3 EnvironmentLightWith(Vec3 from, Vec3 n, in PixelSamples pixel, int samples)
        {
            var env = _environment!;
            var hidden = Vec3.Zero;
            for (int i = 0; i < samples; i++)
            {
                var (u, v) = pixel.Point(DimensionEnvironment, 0, i, samples);
                var d = Hemisphere(n, u, v);
                double share = Transmission(from, d, double.PositiveInfinity);
                if (share < 1) hidden += env.Blurred(d) * (1 - share);
            }
            return Vec3.Max(env.Irradiance(n) - hidden / samples, Vec3.Zero) * _environmentIntensity;
        }
    }
}
