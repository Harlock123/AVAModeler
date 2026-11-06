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
/// <para>
/// Procedural texture layers (<see cref="Procedurals"/>) are worked out in 3D at the hit point, in the same coordinates:
/// the colour channel takes the layer's texture colour and the others its texture value, by the procedural's amount
/// there. A procedural bump tilts the normal by the amount's slope, found by finite differences along the triangle's
/// sides: amplitude 1 tilts it 45° where the amount rises by 1 over one texture size.
/// </para>
/// <para>
/// The render effects (<see cref="RenderEffects"/>), each off until switched on, and leaving the picture exactly as it
/// was when off: ambient occlusion dims the ambient term; an environment picture replaces the backdrop and can light
/// the scene (the irradiance a surface facing that way gets, less the light rays over its hemisphere find hidden);
/// area lights and point or spot lights with a radius are sampled across their area, each sample with its own shadow
/// ray; depth of field moves each camera ray's origin across a thin lens and aims it at the plane in focus; motion blur
/// gives each camera ray a moment across the shutter, with the scene worked out at up to <see cref="MaxMoments"/>.
/// </para>
/// Rendering is deterministic: the antialiasing jitter and every effect's samples come from hashes of the pixel, the
/// sample number and what they're for.
/// </summary>
public static class RayTracer
{
    private sealed record Material(Vec3 Color, double Diffuse, double Specular, double Glossiness, double Luminosity,
        double Reflection, double Transparency, double Ior, bool CastShadows, bool ReceiveShadows)
    {
        /// <summary>The texture layers, in order, with their images (a blank one for a procedural).</summary>
        public IReadOnlyList<(TextureLayer Layer, TextureImage Image)> Layers { get; init; } = [];
        /// <summary>Each layer's procedural, ready to evaluate, or null for an image layer.</summary>
        public IReadOnlyList<ProceduralTexture?> Procedurals { get; init; } = [];
        /// <summary>The object's dissolve at the frame, 0 (solid) to under 1: the share of what's behind it seen through it.</summary>
        public double Dissolve { get; init; }
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

    /// <summary>
    /// A light at the frame: where it is and the way it shines in the world, its colour times intensity, and for an area
    /// light its sides (<paramref name="U"/> across its width, <paramref name="V"/> up its height, scaled with the item).
    /// </summary>
    private sealed record LightInfo(LightType Type, Vec3 Position, Vec3 Direction, Vec3 Color, LightItem Item, Vec3 U = default, Vec3 V = default);

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

        double zoom = camera.Zoom.Evaluate(frame);
        double tanV = 1 / zoom;
        double tanH = tanV * width * camera.PixelAspect / height;
        var dof = camera.DepthOfField;
        var blur = camera.MotionBlur;
        bool lens = dof.On && dof.FStop > 0 && dof.FocalDistance > 0;
        bool moving = blur.On && blur.Shutter > 0;
        int samples = RaysPerPixel(settings, camera);
        int cols = (int)Math.Ceiling(Math.Sqrt(samples));
        int rows = (samples + cols - 1) / cols;
        // How wide a sample's footprint is per unit of distance along its ray, for texture filtering.
        double spread = 2 * tanV / height / cols;
        var environment = EnvironmentOf(scene);
        // With motion blur, the scene is worked out at moments spread evenly across the shutter, centred on the frame.
        int moments = moving ? Math.Min(Math.Min(samples, MaxMoments), Math.Max(1, blur.Samples)) : 1;
        var tracers = new Tracer[moments];
        var toWorld = new Mat4[moments];
        var eyes = new Vec3[moments];
        for (int k = 0; k < moments; k++)
        {
            double t = moving ? frame + blur.Shutter * ((k + 0.5) / moments - 0.5) : frame;
            tracers[k] = new Tracer(scene, scene.Evaluate(t), t, settings, spread, environment);
            toWorld[k] = camera.CameraToWorld(t);
            eyes[k] = toWorld[k].TransformPoint(Vec3.Zero);
        }
        double aperture = lens ? dof.ApertureRadius(zoom) : 0;
        // Compositing (camera rays only): the background where they meet nothing, the foreground laid over afterwards.
        var background = SceneImages.Picture(scene, scene.Compositing.Background, frame);
        var foreground = SceneImages.Picture(scene, scene.Compositing.Foreground, frame);
        var foregroundAlpha = foreground != null && scene.Compositing.Key == ForegroundKey.Alpha ? SceneImages.Picture(scene, scene.Compositing.ForegroundAlpha, frame) : null;

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
                    int k = 0;
                    if (moments > 1)
                    {
                        // The moment: stratified over the pixel's rays, in an order shuffled per pixel.
                        double when = (Permute(s, samples, Seed(x, y, DimensionTime)) + Random01(x, y, s, DimensionTime)) / samples;
                        k = Math.Min(moments - 1, (int)(when * moments));
                    }
                    var pixel = new PixelSamples(x, y, s, samples);
                    Vec3 origin, dir;
                    if (lens)
                    {
                        // A thin lens: rays from across the aperture meet at the plane in focus.
                        var (lu, lv) = Hammersley(x, y, DimensionLens * 64, Permute(s, samples, Seed(x, y, DimensionLens)), samples);
                        var (dx, dy) = Disc(lu, lv);
                        var onLens = new Vec3(dx * aperture, dy * aperture, 0);
                        origin = toWorld[k].TransformPoint(onLens);
                        dir = toWorld[k].TransformVector(local * dof.FocalDistance - onLens).Normalized();
                    }
                    else
                    {
                        origin = eyes[k];
                        dir = toWorld[k].TransformVector(local).Normalized();
                    }
                    if (background == null)
                    {
                        sum += tracers[k].Trace(origin, dir, 0, out double coverage, pixel);
                        alpha += coverage;
                    }
                    else
                    {
                        // The background picture stretched to the frame, at this ray's place on it, over the backdrop.
                        var (bc, ba) = background.Sample((x + sx) / width, 1 - (y + sy) / height, TextureTiling.Edge, TextureTiling.Edge);
                        sum += tracers[k].Trace(origin, dir, 0, out double coverage, pixel, backdropAlpha: ba, backdropColor: bc);
                        alpha += coverage;
                    }
                }
                var c = sum / samples;
                if (foreground != null)
                {
                    double w = ForegroundWeight(scene.Compositing, foreground, foregroundAlpha, (x + 0.5) / width, 1 - (y + 0.5) / height, out var fc);
                    c = c * (1 - w) + fc * w;
                    alpha = (w + alpha / samples * (1 - w)) * samples;
                }
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

    /// <summary>
    /// How much of the foreground covers a place on the frame (u across, v up, 0 to 1), and its colour there: its alpha
    /// (or the alpha image's brightness, for the alpha key), nothing where the colour key leaves it out, less its dissolve.
    /// </summary>
    private static double ForegroundWeight(CompositingSettings c, TextureImage foreground, TextureImage? alphaImage, double u, double v, out Vec3 color)
    {
        var (fc, fa) = foreground.Sample(u, v, TextureTiling.Edge, TextureTiling.Edge);
        color = fc;
        if (c.Key == ForegroundKey.Alpha && alphaImage != null)
        {
            var (ac, aa) = alphaImage.Sample(u, v, TextureTiling.Edge, TextureTiling.Edge);
            fa = TextureImage.Brightness(ac) * aa;
        }
        if (c.KeyedOut(fc)) fa = 0;
        return Math.Clamp(fa * (1 - c.ForegroundDissolve), 0, 1);
    }

    /// <summary>The most moments motion blur works the scene out at.</summary>
    public const int MaxMoments = 32;

    /// <summary>
    /// Rays shot through each pixel: the antialiasing's, raised to the camera's depth of field or motion blur samples
    /// when those are on.
    /// </summary>
    public static int RaysPerPixel(RenderSettings settings, CameraItem camera)
    {
        int samples = Math.Max(1, settings.SamplesPerPixel);
        if (camera.DepthOfField.On) samples = Math.Max(samples, camera.DepthOfField.Samples);
        if (camera.MotionBlur.On) samples = Math.Max(samples, camera.MotionBlur.Samples);
        return samples;
    }

    /// <summary>The scene's environment ready to look up, or null when it has none (or it can't be read).</summary>
    private static EnvironmentMap? EnvironmentOf(Scene scene)
    {
        var e = scene.Environment;
        if (!e.On) return null;
        var image = EnvironmentMap.Load(e.Image, null, out _);
        return image == null ? null : new EnvironmentMap(image, e.Heading);
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

    // ───────────────────────────── repeatable sampling for the effects ─────────────────────────────
    // Every effect's random numbers are hashes of the pixel, the ray and a "dimension" naming what they're for, so the
    // same scene renders the same picture on any number of threads. Within a pixel the samples are a Hammersley set
    // (stratified), shifted by a per-pixel amount (a Cranley–Patterson rotation) so neighbouring pixels differ.

    private const int DimensionTime = 1, DimensionLens = 2, DimensionOcclusion = 3, DimensionEnvironment = 4, DimensionLight = 16;

    private static ulong Mix(ulong h)
    {
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }

    private static uint Seed(int x, int y, int dimension) =>
        (uint)Mix((ulong)(uint)x * 0xD6E8FEB86659FD93UL ^ (ulong)(uint)y * 0xA0761D6478BD642FUL ^ (ulong)(uint)dimension * 0xE7037ED1A0B428DBUL);

    /// <summary>A repeatable number in [0, 1) for a pixel, ray, dimension and index.</summary>
    private static double Random01(int x, int y, int s, int dimension, int index = 0) =>
        (Mix((ulong)Seed(x, y, dimension) << 32 ^ (ulong)(uint)s * 0x9FB21C651E98DF25UL ^ (ulong)(uint)index * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));

    /// <summary>Kensler's hashed permutation: i (0 to n − 1) to another index in 0 to n − 1, a different order per seed.</summary>
    private static int Permute(int i, int n, uint seed)
    {
        if (n <= 1) return 0;
        uint l = (uint)n - 1, w = l;
        w |= w >> 1; w |= w >> 2; w |= w >> 4; w |= w >> 8; w |= w >> 16;
        uint v = (uint)i;
        do
        {
            v ^= seed; v *= 0xe170893d;
            v ^= seed >> 16;
            v ^= (v & w) >> 4;
            v ^= seed >> 8; v *= 0x0929eb3f;
            v ^= seed >> 23;
            v ^= (v & w) >> 1; v *= 1 | seed >> 27;
            v *= 0x6935fa69;
            v ^= (v & w) >> 11; v *= 0x74dcb303;
            v ^= (v & w) >> 2; v *= 0x9e501cc3;
            v ^= (v & w) >> 2; v *= 0xc860a3df;
            v &= w;
            v ^= v >> 5;
        } while (v > l);
        return (int)((v + seed) % (uint)n);
    }

    private static double RadicalInverse(uint i)
    {
        i = (i << 16) | (i >> 16);
        i = ((i & 0x55555555u) << 1) | ((i & 0xAAAAAAAAu) >> 1);
        i = ((i & 0x33333333u) << 2) | ((i & 0xCCCCCCCCu) >> 2);
        i = ((i & 0x0F0F0F0Fu) << 4) | ((i & 0xF0F0F0F0u) >> 4);
        i = ((i & 0x00FF00FFu) << 8) | ((i & 0xFF00FF00u) >> 8);
        return i * (1.0 / 4294967296.0);
    }

    /// <summary>A point in the unit square mapped onto the unit disc, keeping strata apart (Shirley and Chiu's concentric map).</summary>
    private static (double X, double Y) Disc(double u, double v)
    {
        double a = 2 * u - 1, b = 2 * v - 1;
        if (a == 0 && b == 0) return (0, 0);
        double r, phi;
        if (Math.Abs(a) > Math.Abs(b)) { r = a; phi = Math.PI / 4 * (b / a); }
        else { r = b; phi = Math.PI / 2 - Math.PI / 4 * (a / b); }
        return (r * Math.Cos(phi), r * Math.Sin(phi));
    }

    /// <summary>A direction over the hemisphere about a unit normal, weighted toward it (cosine-weighted), from a point in the unit square.</summary>
    private static Vec3 Hemisphere(Vec3 n, double u, double v)
    {
        double r = Math.Sqrt(u), phi = 2 * Math.PI * v;
        var t = n.AnyPerpendicular();
        var b = Vec3.Cross(n, t);
        return (t * (r * Math.Cos(phi)) + b * (r * Math.Sin(phi)) + n * Math.Sqrt(Math.Max(0, 1 - u))).Normalized();
    }

    /// <summary>One ray of a pixel: where an effect's samples come from.</summary>
    private readonly record struct PixelSamples(int X, int Y, int Ray, int Rays)
    {
        /// <summary>How many samples a hit takes of an effect that wants <paramref name="perPixel"/> a pixel.</summary>
        public int PerHit(int perPixel) => Math.Max(1, (perPixel + Rays - 1) / Rays);

        /// <summary>
        /// The i-th of k samples (of a dimension, at a bounce depth) this ray takes: the pixel's rays together take
        /// k × rays points of a shifted Hammersley set, each ray its own share.
        /// </summary>
        public (double U, double V) Point(int dimension, int depth, int i, int k) => Hammersley(X, Y, dimension * 64 + depth, Ray * k + i, Rays * k);
    }

    /// <summary>The j-th of n points of a Hammersley set in the unit square, shifted per pixel and dimension.</summary>
    private static (double U, double V) Hammersley(int x, int y, int dimension, int j, int n)
    {
        double u = (j + 0.5) / n + Random01(x, y, 0, dimension, 1);
        double v = RadicalInverse((uint)j) + Random01(x, y, 0, dimension, 2);
        return (u - Math.Floor(u), v - Math.Floor(v));
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
        private readonly EnvironmentMap? _environment;
        private readonly double _environmentIntensity;
        private readonly AmbientOcclusion? _occlusion;

        public Tracer(Scene scene, SceneFrame sf, double frame, RenderSettings settings, double spread, EnvironmentMap? environment = null)
        {
            _scene = scene;
            _settings = settings;
            _spread = spread;
            _environment = environment;
            _environmentIntensity = scene.Environment.Intensity;
            _occlusion = scene.Occlusion.On && scene.Occlusion.Strength > 0 ? scene.Occlusion : null;
            _ambient = scene.AmbientColor * scene.AmbientIntensity;
            var tris = new List<Triangle>();
            var bounds = Box3.Empty;
            foreach (var of in sf.Objects)
            {
                if (!of.Item.Visible || of.Item.IsPlaceholder) continue;
                // A wholly dissolved object is left out: not seen, and casting no shadow.
                double dissolve = of.Item.DissolveAt(frame);
                if (dissolve >= 1) continue;
                int firstMaterial = _materials.Count;
                string? images = TextureImages.BaseOf(of.Item.SourcePath ?? of.Item.Document?.FilePath);
                foreach (var s in of.Surfaces.DefaultIfEmpty(new Surface("Default")))
                {
                    var material = MaterialOf(s, of.Item, settings, images, scene, frame);
                    _materials.Add(dissolve > 0 ? material with { Dissolve = dissolve } : material);
                }
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
                _lights.Add(new LightInfo(l.Type, w.TransformPoint(Vec3.Zero), w.TransformVector(Vec3.UnitZ).Normalized(), color, l,
                    w.TransformVector(Vec3.UnitX) * l.AreaWidth, w.TransformVector(Vec3.UnitY) * l.AreaHeight));
            }
        }

        private static Material MaterialOf(Surface s, ObjectItem o, RenderSettings settings, string? images, Scene scene, double frame)
        {
            // An image that's a sequence in the scene's image list shows its file for the frame.
            var layers = s.Textures.Where(t => t.IsDrawn && t.Opacity > 0)
                .Select(t => (t, !t.IsImage ? Blank
                    : SceneImages.TextureFile(scene, t.Image, images, frame) is { } file ? TextureImages.Load(file) : TextureImages.Load(t.Image, images))).ToList();
            return new(s.Color.ToVec(), s.Diffuse, s.Specular, Math.Max(1, s.Glossiness), s.Luminosity, Math.Clamp(s.Reflection, 0, 1),
                Math.Clamp(s.Transparency, 0, 1), settings.RefractionIndices.GetValueOrDefault(s.Name, 1), o.CastShadows, o.ReceiveShadows)
            {
                Layers = layers,
                Procedurals = layers.Select(l => l.t.IsImage ? null : Core.Procedurals.Prepare(l.t)).ToList(),
            };
        }

        /// <summary>The image a procedural layer is paired with: it is never sampled.</summary>
        private static readonly TextureImage Blank = new(1, 1, [255, 255, 255, 255]);

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
                if (!t.IsImage) { }
                else if (t.Projection != TextureProjection.UV)
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
                if (m.Procedurals[k] is { } proc)
                {
                    var at = t.WorldCoordinates ? p : local;
                    if (t.Channel == TextureChannel.Bump)
                    {
                        n = ProceduralBump(t, proc, at, tri, tex, n);
                        continue;
                    }
                    double w = Math.Clamp(t.Opacity, 0, 1) * proc.Amount(at);
                    switch (t.Channel)
                    {
                        case TextureChannel.Color: color = TextureMapping.Blend(color, proc.Color, w, t.Blend); break;
                        case TextureChannel.Diffuse: diffuse = TextureMapping.Blend(diffuse, proc.Value, w, t.Blend); break;
                        case TextureChannel.Specular: specular = TextureMapping.Blend(specular, proc.Value, w, t.Blend); break;
                        case TextureChannel.Luminosity: luminosity = TextureMapping.Blend(luminosity, proc.Value, w, t.Blend); break;
                        case TextureChannel.Reflection: reflection = TextureMapping.Blend(reflection, proc.Value, w, t.Blend); break;
                        case TextureChannel.Transparency: transparency = TextureMapping.Blend(transparency, proc.Value, w, t.Blend); break;
                    }
                    continue;
                }
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

        /// <summary>
        /// A normal tilted by a procedural bump: the amount is the height, its slope found by central differences along
        /// the triangle's two sides (in the layer's coordinates) and turned into a direction across the triangle in the
        /// world. The normal leans away from the way the height rises, by the rise per texture size times the amplitude.
        /// </summary>
        private static Vec3 ProceduralBump(TextureLayer t, ProceduralTexture proc, Vec3 at, in Triangle tri, TriangleTexture tex, Vec3 n)
        {
            var e1 = t.WorldCoordinates ? tri.E1 : tex.B - tex.A;
            var e2 = t.WorldCoordinates ? tri.E2 : tex.C - tex.A;
            double l1 = e1.Length, l2 = e2.Length;
            if (l1 == 0 || l2 == 0) return n;
            var size = proc.Size;
            double unit = (Math.Abs(size.X) + Math.Abs(size.Y) + Math.Abs(size.Z)) / 3;
            double eps = unit * 1e-3;
            // The height's change per step of 1 along each side (in barycentric terms, so it carries to the world).
            double Along(Vec3 dir, double length) => (proc.Amount(at + dir * (eps / length)) - proc.Amount(at - dir * (eps / length))) / (2 * eps) * length;
            double d1 = Along(e1, l1), d2 = Along(e2, l2);
            if (d1 == 0 && d2 == 0) return n;
            // The gradient in the world, in the triangle's plane: g·E1 = d1 and g·E2 = d2.
            double a11 = Vec3.Dot(tri.E1, tri.E1), a12 = Vec3.Dot(tri.E1, tri.E2), a22 = Vec3.Dot(tri.E2, tri.E2);
            double det = a11 * a22 - a12 * a12;
            if (Math.Abs(det) < 1e-30) return n;
            double ca = (d1 * a22 - d2 * a12) / det, cb = (d2 * a11 - d1 * a12) / det;
            var g = (tri.E1 * ca + tri.E2 * cb) * unit;
            g -= n * Vec3.Dot(g, n);
            var tilted = (n - g * (t.Amplitude * Math.Clamp(t.Opacity, 0, 1))).Normalized();
            return tilted.LengthSquared > 0 ? tilted : n;
        }

        /// <summary>
        /// What a ray sees where it meets nothing: the compositing background picture for a camera ray when it has one
        /// (over the backdrop by its alpha), else the backdrop.
        /// </summary>
        private Vec3 Backdrop(Vec3 dir, double backdropAlpha, Vec3 backdropColor) =>
            backdropAlpha >= 1 ? backdropColor : backdropColor * backdropAlpha + Background(dir) * (1 - backdropAlpha);

        private Vec3 Background(Vec3 dir)
        {
            if (_environment != null) return _environment.Sample(dir) * _environmentIntensity;
            if (!_scene.GradientBackground) return _scene.BackgroundColor;
            double t = Math.Clamp((dir.Y + 1) / 2, 0, 1);
            return Vec3.Lerp(_scene.GradientBottom, _scene.GradientTop, t);
        }

        /// <summary>
        /// What a ray sees, and how much of it is covered by something (1 a solid hit, 0 nothing; a dissolved surface
        /// covers less). <paramref name="backdropAlpha"/> and <paramref name="backdropColor"/>
        /// are a camera ray's compositing background: what it sees if it meets nothing (alpha 0: the backdrop). A ray carried
        /// on past a dissolved surface starts again from the same origin, <paramref name="from"/> along it.
        /// </summary>
        public Vec3 Trace(Vec3 origin, Vec3 dir, int depth, out double coverage, in PixelSamples pixel, double travelled = 0,
            double backdropAlpha = 0, Vec3 backdropColor = default, double from = 0)
        {
            bool hitSomething = _bvh.Intersect(origin, dir, from > 0 ? from : _bias, double.PositiveInfinity, out var hit);
            coverage = hitSomething ? 1 : 0;
            if (!hitSomething) return backdropAlpha > 0 ? Backdrop(dir, backdropAlpha, backdropColor) : Background(dir);

            var tri = _bvh[hit.Triangle];
            var m = _materials[tri.Material];
            if (m.Dissolve > 0)
            {
                // A dissolved surface: its share seen, the rest of what's behind it (the same ray, on past it) through it.
                double d = m.Dissolve;
                var behind = Trace(origin, dir, depth, out double through, pixel, travelled, backdropAlpha, backdropColor, hit.T + _bias * 10);
                coverage = (1 - d) + d * through;
                return Shade(origin, dir, depth, hit, pixel, travelled) * (1 - d) + behind * d;
            }
            return Shade(origin, dir, depth, hit, pixel, travelled);
        }

        /// <summary>The colour where a ray hit a surface: its lighting, reflection and transparency, and fog.</summary>
        private Vec3 Shade(Vec3 origin, Vec3 dir, int depth, in Hit hit, in PixelSamples pixel, double travelled)
        {
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
            var ambient = Vec3.Scale(diffuse, _ambient);
            if (_occlusion != null) ambient *= Occlusion(p + lift, ns, nf, pixel, depth);
            var lit = ambient + look.Color * look.Luminosity;
            if (_environment != null && _scene.Environment.Lighting) lit += Vec3.Scale(diffuse, EnvironmentLight(p + lift, ns, nf, pixel, depth));
            var highlight = Vec3.Zero;
            for (int li = 0; li < _lights.Count; li++)
            {
                var light = _lights[li];
                if (light.Item.IsSoft)
                {
                    SoftLight(light, li, p, lift, ns, view, look, m, diffuse, pixel, depth, ref lit, ref highlight);
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
                color += (depth < _settings.MaxDepth ? Trace(p + lift, r.Normalized(), depth + 1, out _, pixel, far) : Background(r)) * reflection;
            }
            if (transparency > 0 && depth < _settings.MaxDepth)
                color += Transmitted(p, dir, nf, ns, lift, m.Ior, inside, depth, far, pixel) * transparency;
            return Fog(color, hit.T);
        }

        /// <summary>What's seen through a transparent surface: straight through, or bent by its refraction index.</summary>
        private Vec3 Transmitted(Vec3 p, Vec3 dir, Vec3 nf, Vec3 ns, Vec3 lift, double ior, bool inside, int depth, double travelled, in PixelSamples pixel)
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
            return Trace(start, through, depth + 1, out _, pixel, travelled);
        }

        /// <summary>
        /// A soft light (an area light, or a point or spot light with a radius): its light and highlight averaged over
        /// points across it, each with its own shadow ray, so shadows fade out over a penumbra.
        /// </summary>
        private void SoftLight(LightInfo light, int index, Vec3 p, Vec3 lift, Vec3 ns, Vec3 view, in Look look, Material m, Vec3 diffuse,
            in PixelSamples pixel, int depth, ref Vec3 lit, ref Vec3 highlight)
        {
            var item = light.Item;
            int k = pixel.PerHit(Math.Max(1, item.ShadowSamples));
            bool area = light.Type == LightType.Area;
            // A ball of light looks like a disc facing the point lit.
            Vec3 du = Vec3.Zero, dv = Vec3.Zero;
            if (!area)
            {
                var axis = (p - light.Position).Normalized();
                if (axis.LengthSquared == 0) return;
                du = axis.AnyPerpendicular() * item.Radius;
                dv = Vec3.Cross(axis, du.Normalized()) * item.Radius;
            }
            var diffuseSum = Vec3.Zero;
            var highlightSum = Vec3.Zero;
            bool shadows = _settings.Shadows && item.Shadows && m.ReceiveShadows;
            for (int i = 0; i < k; i++)
            {
                var (u, v) = pixel.Point(DimensionLight + index, depth, i, k);
                Vec3 at;
                if (area) at = light.Position + light.U * (u - 0.5) + light.V * (v - 0.5);
                else
                {
                    var (x, y) = Disc(u, v);
                    at = light.Position + du * x + dv * y;
                }
                var d = at - p;
                double distance = d.Length;
                if (distance == 0) continue;
                var toLight = d / distance;
                double ndl = Vec3.Dot(ns, toLight);
                if (ndl <= 0) continue;
                double strength = item.Falloff(distance);
                if (area) strength *= Math.Max(0, Vec3.Dot(light.Direction, -toLight));
                else if (light.Type == LightType.Spot)
                    strength *= item.SpotFactor(Math.Acos(Math.Clamp(Vec3.Dot(light.Direction, -toLight), -1, 1)) * 180 / Math.PI);
                if (strength <= 0) continue;
                if (shadows) strength *= Transmission(p + lift, toLight, distance);
                if (strength <= 0) continue;
                var c = light.Color * strength;
                diffuseSum += c * ndl;
                if (look.Specular > 0)
                {
                    var r = ns * (2 * ndl) - toLight;
                    double rv = Vec3.Dot(r, view);
                    if (rv > 0) highlightSum += c * (look.Specular * Math.Pow(rv, m.Glossiness));
                }
            }
            lit += Vec3.Scale(diffuse, diffuseSum) / k;
            highlight += highlightSum / k;
        }

        /// <summary>
        /// How much ambient light reaches a point, 0 to 1: rays over the hemisphere above it that meet a surface within the
        /// occlusion distance hide some of it, the nearer the surface the more.
        /// </summary>
        private double Occlusion(Vec3 from, Vec3 ns, Vec3 nf, in PixelSamples pixel, int depth)
        {
            var o = _occlusion!;
            int k = pixel.PerHit(o.Samples);
            double hidden = 0;
            for (int i = 0; i < k; i++)
            {
                var (u, v) = pixel.Point(DimensionOcclusion, depth, i, k);
                var d = Hemisphere(ns, u, v);
                if (Vec3.Dot(d, nf) <= 0) continue;
                if (_bvh.Intersect(from, d, _bias, o.Distance, out var hit))
                {
                    double f = hit.T / o.Distance;
                    // A dissolved surface hides only its solid share.
                    if (_materials[_bvh[hit.Triangle].Material].Dissolve is var dissolve and > 0) hidden += (1 - f * f) * (1 - dissolve);
                    else hidden += 1 - f * f;
                }
            }
            return Math.Clamp(1 - o.Strength * hidden / k, 0, 1);
        }

        /// <summary>
        /// The environment's light falling on a point: the light a surface facing that way gets from the whole
        /// hemisphere (<see cref="EnvironmentMap.Irradiance"/>), less what rays spread over it (cosine-weighted) find
        /// hidden by whatever is in their way. Counting only what's hidden keeps open surfaces free of noise, and on
        /// average it's the same as gathering every ray's light.
        /// </summary>
        private Vec3 EnvironmentLight(Vec3 from, Vec3 ns, Vec3 nf, in PixelSamples pixel, int depth)
        {
            var env = _environment!;
            int k = pixel.PerHit(_scene.Environment.Samples);
            var hidden = Vec3.Zero;
            for (int i = 0; i < k; i++)
            {
                var (u, v) = pixel.Point(DimensionEnvironment, depth, i, k);
                var d = Hemisphere(ns, u, v);
                double share = Vec3.Dot(d, nf) <= 0 ? 0 : _settings.Shadows ? Transmission(from, d, double.PositiveInfinity) : 1;
                if (share < 1) hidden += env.Blurred(d) * (1 - share);
            }
            var light = env.Irradiance(ns) - hidden / k;
            return Vec3.Max(light, Vec3.Zero) * _environmentIntensity;
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
                if (m.CastShadows)
                {
                    // A dissolved surface lets its dissolved share of the light by, and the rest as its transparency does.
                    if (m.Dissolve > 0) share *= m.Dissolve + (1 - m.Dissolve) * (_settings.Transparency ? clear : 0);
                    else share *= _settings.Transparency ? clear : 0;
                }
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
