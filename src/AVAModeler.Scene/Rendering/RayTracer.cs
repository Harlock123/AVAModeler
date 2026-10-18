using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>How to render.</summary>
public sealed class RenderSettings
{
    /// <summary>The image size; null takes the camera's.</summary>
    public int? Width, Height;
    /// <summary>The camera to render through; null takes the scene's current camera.</summary>
    public CameraItem? Camera;
    /// <summary>Rays per pixel for antialiasing, spread over a jittered grid (1 shoots through each pixel's centre).</summary>
    public int SamplesPerPixel = 1;
    /// <summary>How many times a ray may bounce (reflection and transparency), like LightWave's ray recursion limit.</summary>
    public int MaxDepth = 8;
    public bool Shadows = true, Reflections = true, Transparency = true;
    /// <summary>Refraction indices by surface name (Core's surfaces don't carry one); a surface not listed doesn't bend light.</summary>
    public Dictionary<string, double> RefractionIndices = [];
    /// <summary>The most threads to use; 0 or less for as many as the machine has.</summary>
    public int MaxThreads = -1;
}

/// <summary>A rendered image: width × height pixels, 4 bytes each (red, green, blue, alpha), top row first.</summary>
public sealed class RenderImage(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgba { get; } = rgba;

    public (byte R, byte G, byte B, byte A) Pixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
    }

    /// <summary>
    /// The picture as a PNG. With <paramref name="alpha"/> its alpha channel is the coverage (the backdrop
    /// transparent, for compositing); without, every pixel is opaque, backdrop and all.
    /// </summary>
    public byte[] ToPng(bool alpha = true) => Png.Encode(Width, Height, alpha ? Rgba : Opaque());
    public void SavePng(string path, bool alpha = true) => File.WriteAllBytes(path, ToPng(alpha));

    /// <summary>The pixels with full alpha everywhere.</summary>
    public byte[] Opaque()
    {
        var copy = (byte[])Rgba.Clone();
        for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
        return copy;
    }
}

/// <summary>
/// An offline CPU ray tracer in the spirit of LightWave's: Phong shading with each surface's colour, diffuse,
/// specular, glossiness, luminosity, reflection and transparency; smoothing within the surface's smoothing angle;
/// ray-traced shadows from distant, point and spot lights; ambient light; a solid or gradient backdrop; linear fog.
/// <para>
/// Shading per light is colour · diffuse · (N·L) plus a white highlight of specular · (R·V)^g, where g is the LWOB
/// glossiness used directly as the Phong exponent (16 low, 64 medium, 256 high, 1024 very high). Ambient adds
/// colour · diffuse · ambient, and luminosity adds colour · luminosity. Reflection and transparency each replace their
/// fraction of that diffuse result with what the reflected or transmitted ray sees; the highlight is added on top.
/// Single-sided faces can't be hit from behind, by any ray. There's no gamma: values map linearly to 0–255, as in
/// LightWave 5. Alpha is the share of a pixel's samples that hit something.
/// </para>
/// Rendering is deterministic: the antialiasing jitter comes from a hash of the pixel and sample number.
/// </summary>
public static class RayTracer
{
    private sealed record Material(Vec3 Color, double Diffuse, double Specular, double Glossiness, double Luminosity,
        double Reflection, double Transparency, double Ior, bool CastShadows, bool ReceiveShadows);

    private sealed record LightInfo(LightType Type, Vec3 Position, Vec3 Direction, Vec3 Color, LightItem Item);

    /// <summary>
    /// Renders a frame. <paramref name="progress"/> gets the fraction of rows done, from worker threads. Cancelling
    /// throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static RenderImage Render(Scene scene, double frame, RenderSettings? settings = null, Action<double>? progress = null,
        CancellationToken cancel = default)
    {
        settings ??= new RenderSettings();
        var camera = settings.Camera ?? scene.CurrentCamera ?? throw new InvalidOperationException("The scene has no camera to render through.");
        int width = Math.Max(1, settings.Width ?? camera.Width);
        int height = Math.Max(1, settings.Height ?? camera.Height);
        cancel.ThrowIfCancellationRequested();

        var tracer = new Tracer(scene, scene.Evaluate(frame), frame, settings);
        var toWorld = camera.CameraToWorld(frame);
        var eye = toWorld.TransformPoint(Vec3.Zero);
        double tanV = 1 / camera.Zoom.Evaluate(frame);
        double tanH = tanV * width * camera.PixelAspect / height;
        int samples = Math.Max(1, settings.SamplesPerPixel);
        int cols = (int)Math.Ceiling(Math.Sqrt(samples));
        int rows = (samples + cols - 1) / cols;

        var rgba = new byte[width * height * 4];
        int done = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = settings.MaxThreads > 0 ? settings.MaxThreads : -1 };
        Parallel.For(0, height, options, y =>
        {
            for (int x = 0; x < width; x++)
            {
                if (cancel.IsCancellationRequested) return;
                var sum = Vec3.Zero;
                double alpha = 0;
                for (int s = 0; s < samples; s++)
                {
                    double sx = 0.5, sy = 0.5;
                    if (samples > 1)
                    {
                        sx = (s % cols + Jitter(x, y, s, 0)) / cols;
                        sy = (s / cols + Jitter(x, y, s, 1)) / rows;
                    }
                    var local = new Vec3((2 * (x + sx) / width - 1) * tanH, (1 - 2 * (y + sy) / height) * tanV, 1);
                    var dir = toWorld.TransformVector(local).Normalized();
                    sum += tracer.Trace(eye, dir, 0, out bool hit);
                    if (hit) alpha++;
                }
                var c = sum / samples;
                int i = (y * width + x) * 4;
                rgba[i] = ToByte(c.X);
                rgba[i + 1] = ToByte(c.Y);
                rgba[i + 2] = ToByte(c.Z);
                rgba[i + 3] = ToByte(alpha / samples);
            }
            int n = Interlocked.Increment(ref done);
            progress?.Invoke((double)n / height);
        });
        cancel.ThrowIfCancellationRequested();
        return new RenderImage(width, height, rgba);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    /// <summary>A repeatable pseudo-random number in [0, 1) for a pixel, sample and axis.</summary>
    private static double Jitter(int x, int y, int s, int axis)
    {
        ulong h = (ulong)(uint)x * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(uint)(s * 2 + axis) * 0x165667B19E3779F9UL;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>The scene at a frame turned into triangles, materials and lights, ready to trace rays through.</summary>
    private sealed class Tracer
    {
        private readonly Scene _scene;
        private readonly RenderSettings _settings;
        private readonly Bvh _bvh;
        private readonly List<Material> _materials = [];
        private readonly List<LightInfo> _lights = [];
        private readonly Vec3 _ambient;
        private readonly double _bias;

        public Tracer(Scene scene, SceneFrame sf, double frame, RenderSettings settings)
        {
            _scene = scene;
            _settings = settings;
            _ambient = scene.AmbientColor * scene.AmbientIntensity;
            var tris = new List<Triangle>();
            var bounds = Box3.Empty;
            foreach (var of in sf.Objects)
            {
                if (!of.Item.Visible || of.Item.IsPlaceholder) continue;
                int firstMaterial = _materials.Count;
                foreach (var s in of.Surfaces.DefaultIfEmpty(new Surface("Default"))) _materials.Add(MaterialOf(s, of.Item, settings));
                var mesh = of.WorldMesh;
                bounds = bounds.Include(mesh.Bounds());
                AddTriangles(mesh, of.Surfaces, firstMaterial, of.World.Determinant() < 0, tris);
            }
            _bvh = new Bvh([.. tris]);
            // Offsets that keep secondary rays off the surface they leave, scaled to the scene's size.
            _bias = 1e-7 * (bounds.IsEmpty ? 1 : Math.Max(1, Math.Max(bounds.Extent, Math.Max(bounds.Max.Length, bounds.Min.Length))));
            foreach (var l in scene.Lights)
            {
                if (!l.Visible) continue;
                var w = l.WorldMatrix(frame);
                var color = l.Color * l.Intensity.Evaluate(frame);
                if (color == Vec3.Zero) continue;
                _lights.Add(new LightInfo(l.Type, w.TransformPoint(Vec3.Zero), w.TransformVector(Vec3.UnitZ).Normalized(), color, l));
            }
        }

        private static Material MaterialOf(Surface s, ObjectItem o, RenderSettings settings) => new(
            s.Color.ToVec(), s.Diffuse, s.Specular, Math.Max(1, s.Glossiness), s.Luminosity, Math.Clamp(s.Reflection, 0, 1),
            Math.Clamp(s.Transparency, 0, 1), settings.RefractionIndices.GetValueOrDefault(s.Name, 1), o.CastShadows, o.ReceiveShadows);

        /// <summary>Triangulates a world-space mesh, with corner normals smoothed per surface where it asks.</summary>
        private static void AddTriangles(Mesh mesh, IReadOnlyList<Surface> surfaces, int firstMaterial, bool mirrored, List<Triangle> tris)
        {
            int count = mesh.Polygons.Count;
            var normals = new Vec3[count];
            for (int i = 0; i < count; i++)
            {
                var n = mesh.Normal(mesh.Polygons[i]);
                normals[i] = mirrored ? -n : n;
            }
            for (int pi = 0; pi < count; pi++)
            {
                var poly = mesh.Polygons[pi];
                if (poly.Count < 3 || normals[pi].LengthSquared == 0) continue;
                int si = poly.Surface >= 0 && poly.Surface < surfaces.Count ? poly.Surface : 0;
                var surface = si < surfaces.Count ? surfaces[si] : null;
                bool doubleSided = surface?.DoubleSided ?? false;
                var corner = new Dictionary<int, Vec3>();
                foreach (int p in poly.Points)
                    corner[p] = surface is { Smoothing: true } ? SmoothNormal(mesh, normals, pi, p, surface.SmoothingAngle) : normals[pi];
                foreach (var (a, b, c) in Triangulator.Triangulate(mesh, poly))
                {
                    tris.Add(new Triangle(mesh.Points[a], mesh.Points[b], mesh.Points[c], normals[pi], corner[a], corner[b], corner[c],
                        firstMaterial + si, doubleSided));
                }
            }
        }

        /// <summary>The average normal of the faces of the same surface at a point that meet this face within the smoothing angle.</summary>
        private static Vec3 SmoothNormal(Mesh mesh, Vec3[] normals, int polygon, int point, double angle)
        {
            double cos = Math.Cos(angle * Math.PI / 180);
            var n = normals[polygon];
            var sum = Vec3.Zero;
            foreach (int q in mesh.PolygonsOf(point))
                if (mesh.Polygons[q].Surface == mesh.Polygons[polygon].Surface && Vec3.Dot(normals[q], n) >= cos - 1e-12)
                    sum += normals[q];
            var s = sum.Normalized();
            return s.LengthSquared == 0 ? n : s;
        }

        private Vec3 Background(Vec3 dir)
        {
            if (!_scene.GradientBackground) return _scene.BackgroundColor;
            double t = Math.Clamp((dir.Y + 1) / 2, 0, 1);
            return Vec3.Lerp(_scene.GradientBottom, _scene.GradientTop, t);
        }

        public Vec3 Trace(Vec3 origin, Vec3 dir, int depth, out bool hitSomething)
        {
            hitSomething = _bvh.Intersect(origin, dir, _bias, double.PositiveInfinity, out var hit);
            if (!hitSomething) return Background(dir);

            var tri = _bvh[hit.Triangle];
            var m = _materials[tri.Material];
            var p = origin + dir * hit.T;
            var nf = tri.Normal;
            var ns = (tri.NA * (1 - hit.U - hit.V) + tri.NB * hit.U + tri.NC * hit.V).Normalized();
            bool inside = Vec3.Dot(dir, nf) > 0;
            if (inside)
            {
                nf = -nf;
                ns = -ns;
            }
            if (ns.LengthSquared == 0) ns = nf;
            var view = -dir;
            var lift = nf * _bias * 10;

            var diffuse = m.Color * m.Diffuse;
            var lit = Vec3.Scale(diffuse, _ambient) + m.Color * m.Luminosity;
            var highlight = Vec3.Zero;
            foreach (var light in _lights)
            {
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
                {
                    double cosAngle = Math.Clamp(Vec3.Dot(light.Direction, -toLight), -1, 1);
                    strength *= light.Item.SpotFactor(Math.Acos(cosAngle) * 180 / Math.PI);
                }
                if (strength <= 0) continue;
                if (_settings.Shadows && light.Item.Shadows && m.ReceiveShadows)
                    strength *= Transmission(p + lift, toLight, distance);
                if (strength <= 0) continue;
                var c = light.Color * strength;
                lit += Vec3.Scale(diffuse, c) * ndl;
                if (m.Specular > 0)
                {
                    var r = ns * (2 * ndl) - toLight;
                    double rv = Vec3.Dot(r, view);
                    if (rv > 0) highlight += c * (m.Specular * Math.Pow(rv, m.Glossiness));
                }
            }

            double reflection = _settings.Reflections ? m.Reflection : 0;
            double transparency = _settings.Transparency ? Math.Min(m.Transparency, 1 - reflection) : 0;
            var color = lit * (1 - reflection - transparency) + highlight;
            if (reflection > 0)
            {
                var r = dir - ns * (2 * Vec3.Dot(dir, ns));
                if (Vec3.Dot(r, nf) <= 0) r = dir - nf * (2 * Vec3.Dot(dir, nf));
                color += (depth < _settings.MaxDepth ? Trace(p + lift, r.Normalized(), depth + 1, out _) : Background(r)) * reflection;
            }
            if (transparency > 0 && depth < _settings.MaxDepth)
                color += Transmitted(p, dir, nf, ns, lift, m.Ior, inside, depth) * transparency;
            return Fog(color, hit.T);
        }

        /// <summary>What's seen through a transparent surface: straight through, or bent by its refraction index.</summary>
        private Vec3 Transmitted(Vec3 p, Vec3 dir, Vec3 nf, Vec3 ns, Vec3 lift, double ior, bool inside, int depth)
        {
            var through = dir;
            if (Math.Abs(ior - 1) > 1e-12)
            {
                double eta = inside ? ior : 1 / ior;
                double cosI = -Vec3.Dot(dir, ns);
                double k = 1 - eta * eta * (1 - cosI * cosI);
                through = k < 0
                    ? dir + ns * (2 * cosI) // total internal reflection
                    : (dir * eta + ns * (eta * cosI - Math.Sqrt(k))).Normalized();
            }
            var start = Vec3.Dot(through, nf) < 0 ? p - lift : p + lift;
            return Trace(start, through, depth + 1, out _);
        }

        /// <summary>How much light gets from a point to a light: 1 unblocked, less through transparent surfaces, 0 blocked.</summary>
        private double Transmission(Vec3 from, Vec3 dir, double distance)
        {
            double share = 1;
            for (int i = 0; i < 64 && share > 1e-4; i++)
            {
                if (!_bvh.Intersect(from, dir, _bias, distance, out var hit)) return share;
                var m = _materials[_bvh[hit.Triangle].Material];
                if (m.CastShadows) share *= _settings.Transparency ? m.Transparency : 0;
                from += dir * hit.T;
                distance -= hit.T;
            }
            return 0;
        }

        private Vec3 Fog(Vec3 color, double distance)
        {
            if (_scene.Fog != FogType.Linear) return color;
            double span = _scene.FogMaxDistance - _scene.FogMinDistance;
            double f = span <= 0 ? (distance >= _scene.FogMaxDistance ? 1 : 0) : Math.Clamp((distance - _scene.FogMinDistance) / span, 0, 1);
            return Vec3.Lerp(color, _scene.FogColor, f);
        }
    }
}
