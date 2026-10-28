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
/// <para>
/// Image textures change the colour and the other settings where a ray hits, layer over layer, each sampled with
/// bilinear filtering between the two mipmap levels nearest the size of the pixel's footprint on the image (trilinear),
/// so distant textures don't shimmer. Projections work from the object's own coordinates (moving with it), or the
/// world's for a layer that says so. A bump layer tilts the shading normal by the slope of its image's brightness.
/// </para>
/// Rendering is deterministic: the antialiasing jitter comes from a hash of the pixel and sample number.
/// </summary>
public static class RayTracer
{
    private sealed record Material(Vec3 Color, double Diffuse, double Specular, double Glossiness, double Luminosity,
        double Reflection, double Transparency, double Ior, bool CastShadows, bool ReceiveShadows)
    {
        /// <summary>The image texture layers, in order, with their images.</summary>
        public IReadOnlyList<(TextureLayer Layer, TextureImage Image)> Layers { get; init; } = [];
        public bool Textured => Layers.Count > 0;
        public bool TexturedTransparency => Layers.Any(l => l.Layer.Channel == TextureChannel.Transparency);
    }

    /// <summary>
    /// A textured triangle's texturing: its corners in object coordinates and its face normal there (for projections),
    /// and each layer's image coordinates at its corners (for UV layers, and for every layer's bump tangents), and
    /// whether each layer reaches it.
    /// </summary>
    private sealed class TriangleTexture
    {
        public required Vec3 A, B, C, Normal;
        public required Vec2[][] Corners;
        public required bool[] Mapped;
    }

    /// <summary>What a surface is like where a ray hit it, after its textures: the settings shading uses, and the normal.</summary>
    private readonly record struct Look(Vec3 Color, double Diffuse, double Specular, double Luminosity, double Reflection, double Transparency, Vec3 Normal);

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

        double tanV = 1 / camera.Zoom.Evaluate(frame);
        double tanH = tanV * width * camera.PixelAspect / height;
        int samples = Math.Max(1, settings.SamplesPerPixel);
        int cols = (int)Math.Ceiling(Math.Sqrt(samples));
        int rows = (samples + cols - 1) / cols;
        // How wide a sample's footprint is per unit of distance along its ray, for texture filtering.
        double spread = 2 * tanV / height / cols;
        var tracer = new Tracer(scene, scene.Evaluate(frame), frame, settings, spread);
        var toWorld = camera.CameraToWorld(frame);
        var eye = toWorld.TransformPoint(Vec3.Zero);

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
        private readonly double _bias, _spread;
        private readonly List<TriangleTexture> _textures = [];

        public Tracer(Scene scene, SceneFrame sf, double frame, RenderSettings settings, double spread)
        {
            _scene = scene;
            _settings = settings;
            _spread = spread;
            _ambient = scene.AmbientColor * scene.AmbientIntensity;
            var tris = new List<Triangle>();
            var bounds = Box3.Empty;
            foreach (var of in sf.Objects)
            {
                if (!of.Item.Visible || of.Item.IsPlaceholder) continue;
                int firstMaterial = _materials.Count;
                string? images = TextureImages.BaseOf(of.Item.SourcePath ?? of.Item.Document?.FilePath);
                foreach (var s in of.Surfaces.DefaultIfEmpty(new Surface("Default"))) _materials.Add(MaterialOf(s, of.Item, settings, images));
                var mesh = of.WorldMesh;
                bounds = bounds.Include(mesh.Bounds());
                AddTriangles(mesh, of.Mesh, of.Surfaces, firstMaterial, of.World.Determinant() < 0, tris);
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

        private static Material MaterialOf(Surface s, ObjectItem o, RenderSettings settings, string? images) => new(
            s.Color.ToVec(), s.Diffuse, s.Specular, Math.Max(1, s.Glossiness), s.Luminosity, Math.Clamp(s.Reflection, 0, 1),
            Math.Clamp(s.Transparency, 0, 1), settings.RefractionIndices.GetValueOrDefault(s.Name, 1), o.CastShadows, o.ReceiveShadows)
        {
            Layers = s.Textures.Where(t => t.IsImage && t.Opacity > 0).Select(t => (t, TextureImages.Load(t.Image, images))).ToList(),
        };

        /// <summary>
        /// Triangulates a world-space mesh, with corner normals smoothed per surface where it asks, and the texturing of
        /// those with textured surfaces (from <paramref name="local"/>, the mesh in object coordinates, point for point).
        /// </summary>
        private void AddTriangles(Mesh mesh, Mesh local, IReadOnlyList<Surface> surfaces, int firstMaterial, bool mirrored, List<Triangle> tris)
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
                var material = _materials[firstMaterial + si];
                foreach (var (a, b, c) in Triangulator.Triangulate(mesh, poly))
                {
                    int texture = material.Textured ? AddTexture(material, mesh, local, pi, a, b, c) : -1;
                    tris.Add(new Triangle(mesh.Points[a], mesh.Points[b], mesh.Points[c], normals[pi], corner[a], corner[b], corner[c],
                        firstMaterial + si, doubleSided, texture));
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

        /// <summary>A textured triangle's texturing, kept for when rays hit it; returns its index.</summary>
        private int AddTexture(Material m, Mesh mesh, Mesh local, int polygon, int a, int b, int c)
        {
            var poly = mesh.Polygons[polygon];
            Vec3 P(int i) => i < local.Points.Count ? local.Points[i] : mesh.Points[i];
            var normal = local.Normal(poly);
            var corners = new Vec2[m.Layers.Count][];
            var mapped = new bool[m.Layers.Count];
            for (int k = 0; k < m.Layers.Count; k++)
            {
                var t = m.Layers[k].Layer;
                var uv = new Vec2[3];
                mapped[k] = true;
                int[] pts = [a, b, c];
                if (t.Projection != TextureProjection.UV)
                    TextureMapping.ProjectFace(t, pts.Select(p => t.WorldCoordinates ? mesh.Points[p] : P(p)).ToArray(), t.WorldCoordinates ? mesh.Normal(poly) : normal, uv);
                else
                    for (int j = 0; j < 3; j++)
                        if (mesh.TryGetUV(t.UVMap, poly, pts[j], out var u)) uv[j] = u;
                        else mapped[k] = false;
                corners[k] = uv;
            }
            _textures.Add(new TriangleTexture { A = P(a), B = P(b), C = P(c), Normal = normal, Corners = corners, Mapped = mapped });
            return _textures.Count - 1;
        }

        /// <summary>
        /// The surface where a ray hit it: the material's settings with its texture layers applied, and the shading
        /// normal tilted by any bump layers. <paramref name="distance"/> is how far the ray has come from the eye, which
        /// sets how big a patch of the image a sample covers.
        /// </summary>
        private Look LookAt(Material m, in Triangle tri, in Hit hit, Vec3 p, Vec3 normal, double distance)
        {
            var look = new Look(m.Color, m.Diffuse, m.Specular, m.Luminosity, m.Reflection, m.Transparency, normal);
            if (tri.Texture < 0) return look;
            var tex = _textures[tri.Texture];
            double w0 = 1 - hit.U - hit.V;
            var local = tex.A * w0 + tex.B * hit.U + tex.C * hit.V;
            double footprint = distance * _spread;
            var (color, diffuse, specular, luminosity, reflection, transparency, n) = look;
            for (int k = 0; k < m.Layers.Count; k++)
            {
                var (t, img) = m.Layers[k];
                if (!tex.Mapped[k]) continue;
                var c = tex.Corners[k];
                var uv = t.Projection == TextureProjection.UV
                    ? c[0] * w0 + c[1] * hit.U + c[2] * hit.V
                    : TextureMapping.Project(t, t.WorldCoordinates ? p : local, tex.Normal);
                // The image's directions across the triangle in the world, from its corners' coordinates.
                var (du, dv) = Tangents(tri, c);
                double texels = Math.Max(du.Length > 0 ? footprint * img.Width / du.Length : 0, dv.Length > 0 ? footprint * img.Height / dv.Length : 0);
                if (t.Channel == TextureChannel.Bump)
                {
                    n = Bump(t, img, uv, n, du, dv, texels);
                    continue;
                }
                var (col, alpha) = img.SampleFiltered(uv.X, uv.Y, t.WidthTiling, t.HeightTiling, t.PixelBlending, texels);
                if (t.Negative) col = Vec3.One - col;
                double weight = Math.Clamp(t.Opacity, 0, 1) * alpha;
                double value = TextureImage.Brightness(col);
                switch (t.Channel)
                {
                    case TextureChannel.Color: color = TextureMapping.Blend(color, col, weight, t.Blend); break;
                    case TextureChannel.Diffuse: diffuse = TextureMapping.Blend(diffuse, value, weight, t.Blend); break;
                    case TextureChannel.Specular: specular = TextureMapping.Blend(specular, value, weight, t.Blend); break;
                    case TextureChannel.Luminosity: luminosity = TextureMapping.Blend(luminosity, value, weight, t.Blend); break;
                    case TextureChannel.Reflection: reflection = TextureMapping.Blend(reflection, value, weight, t.Blend); break;
                    case TextureChannel.Transparency: transparency = TextureMapping.Blend(transparency, value, weight, t.Blend); break;
                }
            }
            return new Look(color, diffuse, specular, luminosity, Math.Clamp(reflection, 0, 1), Math.Clamp(transparency, 0, 1), n);
        }

        /// <summary>
        /// How the world moves for a step of 1 in u and in v across a triangle, given its corners' image coordinates (zero
        /// when they don't span the image).
        /// </summary>
        private static (Vec3 U, Vec3 V) Tangents(in Triangle tri, Vec2[] c)
        {
            var d1 = c[1] - c[0];
            var d2 = c[2] - c[0];
            double det = d1.X * d2.Y - d2.X * d1.Y;
            if (Math.Abs(det) < 1e-18) return (Vec3.Zero, Vec3.Zero);
            double r = 1 / det;
            return ((tri.E1 * d2.Y - tri.E2 * d1.Y) * r, (tri.E2 * d1.X - tri.E1 * d2.X) * r);
        }

        /// <summary>
        /// A normal tilted by a bump layer: the image's brightness is the height, and the normal leans away from the way
        /// it rises, by the rise over one pixel times the amplitude (one pixel from black to white at 1 leans it 45°).
        /// </summary>
        private static Vec3 Bump(TextureLayer t, TextureImage img, Vec2 uv, Vec3 n, Vec3 du, Vec3 dv, double texels)
        {
            if (du.LengthSquared == 0 || dv.LengthSquared == 0) return n;
            double step = Math.Max(1, texels);
            double ex = step / img.Width, ey = step / img.Height;
            double H(double u, double v)
            {
                var (c, a) = img.SampleFiltered(u, v, t.WidthTiling, t.HeightTiling, t.PixelBlending, texels);
                double h = TextureImage.Brightness(c) * a;
                return t.Negative ? 1 - h : h;
            }
            // The rise per pixel across and up the image.
            double gu = (H(uv.X + ex, uv.Y) - H(uv.X - ex, uv.Y)) / (2 * step);
            double gv = (H(uv.X, uv.Y + ey) - H(uv.X, uv.Y - ey)) / (2 * step);
            var tu = (du - n * Vec3.Dot(du, n)).Normalized();
            var tv = (dv - n * Vec3.Dot(dv, n)).Normalized();
            var tilted = (n - (tu * gu + tv * gv) * (t.Amplitude * Math.Clamp(t.Opacity, 0, 1))).Normalized();
            return tilted.LengthSquared > 0 ? tilted : n;
        }

        private Vec3 Background(Vec3 dir)
        {
            if (!_scene.GradientBackground) return _scene.BackgroundColor;
            double t = Math.Clamp((dir.Y + 1) / 2, 0, 1);
            return Vec3.Lerp(_scene.GradientBottom, _scene.GradientTop, t);
        }

        public Vec3 Trace(Vec3 origin, Vec3 dir, int depth, out bool hitSomething, double travelled = 0)
        {
            hitSomething = _bvh.Intersect(origin, dir, _bias, double.PositiveInfinity, out var hit);
            if (!hitSomething) return Background(dir);

            var tri = _bvh[hit.Triangle];
            var m = _materials[tri.Material];
            var p = origin + dir * hit.T;
            var nf = tri.Normal;
            var ns = (tri.NA * (1 - hit.U - hit.V) + tri.NB * hit.U + tri.NC * hit.V).Normalized();
            if (ns.LengthSquared == 0) ns = nf;
            var look = LookAt(m, tri, hit, p, ns, travelled + hit.T);
            ns = look.Normal;
            bool inside = Vec3.Dot(dir, nf) > 0;
            if (inside)
            {
                nf = -nf;
                ns = -ns;
            }
            var view = -dir;
            var lift = nf * _bias * 10;

            var diffuse = look.Color * look.Diffuse;
            var lit = Vec3.Scale(diffuse, _ambient) + look.Color * look.Luminosity;
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
                if (look.Specular > 0)
                {
                    var r = ns * (2 * ndl) - toLight;
                    double rv = Vec3.Dot(r, view);
                    if (rv > 0) highlight += c * (look.Specular * Math.Pow(rv, m.Glossiness));
                }
            }

            double reflection = _settings.Reflections ? look.Reflection : 0;
            double transparency = _settings.Transparency ? Math.Min(look.Transparency, 1 - reflection) : 0;
            var color = lit * (1 - reflection - transparency) + highlight;
            double far = travelled + hit.T;
            if (reflection > 0)
            {
                var r = dir - ns * (2 * Vec3.Dot(dir, ns));
                if (Vec3.Dot(r, nf) <= 0) r = dir - nf * (2 * Vec3.Dot(dir, nf));
                color += (depth < _settings.MaxDepth ? Trace(p + lift, r.Normalized(), depth + 1, out _, far) : Background(r)) * reflection;
            }
            if (transparency > 0 && depth < _settings.MaxDepth)
                color += Transmitted(p, dir, nf, ns, lift, m.Ior, inside, depth, far) * transparency;
            return Fog(color, hit.T);
        }

        /// <summary>What's seen through a transparent surface: straight through, or bent by its refraction index.</summary>
        private Vec3 Transmitted(Vec3 p, Vec3 dir, Vec3 nf, Vec3 ns, Vec3 lift, double ior, bool inside, int depth, double travelled)
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
            return Trace(start, through, depth + 1, out _, travelled);
        }

        /// <summary>How much light gets from a point to a light: 1 unblocked, less through transparent surfaces, 0 blocked.</summary>
        private double Transmission(Vec3 from, Vec3 dir, double distance)
        {
            double share = 1;
            for (int i = 0; i < 64 && share > 1e-4; i++)
            {
                if (!_bvh.Intersect(from, dir, _bias, distance, out var hit)) return share;
                var tri = _bvh[hit.Triangle];
                var m = _materials[tri.Material];
                double clear = m.TexturedTransparency ? LookAt(m, tri, hit, from + dir * hit.T, tri.Normal, 0).Transparency : m.Transparency;
                if (m.CastShadows) share *= _settings.Transparency ? clear : 0;
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
