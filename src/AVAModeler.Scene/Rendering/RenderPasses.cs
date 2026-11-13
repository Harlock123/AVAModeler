using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>The passes a render can give beside its picture.</summary>
[Flags]
public enum RenderPass
{
    None = 0,
    /// <summary>Distance from the camera along its view, metres.</summary>
    Depth = 1,
    /// <summary>The surface's facing in the world.</summary>
    Normal = 2,
    /// <summary>Which object is seen (flat colours, one an object).</summary>
    ObjectId = 4,
    /// <summary>The surface's own colour, before light.</summary>
    Albedo = 8,
    /// <summary>The light it gives off (luminosity).</summary>
    Emission = 16,
    /// <summary>How much of its direct light is in shadow (white: all).</summary>
    Shadow = 32,
    /// <summary>What it reflects.</summary>
    Reflection = 64,
    All = 127,
}

/// <summary>
/// A render's passes (<see cref="RayTracer.Passes"/>): one ray through each pixel's centre, what it first hits. Each
/// array is width × height, row by row from the top, or null when not asked for. Where nothing is hit the depth is
/// NaN, the object −1 and the rest zero.
/// </summary>
public sealed class PassImages
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public float[]? Depth { get; init; }
    public Vec3[]? Normal { get; init; }
    public int[]? ObjectId { get; init; }
    public Vec3[]? Albedo { get; init; }
    public Vec3[]? Emission { get; init; }
    public float[]? Shadow { get; init; }
    public Vec3[]? Reflection { get; init; }

    /// <summary>The names the passes are saved under, as agents give them.</summary>
    public static readonly IReadOnlyDictionary<string, RenderPass> Names = new Dictionary<string, RenderPass>(StringComparer.OrdinalIgnoreCase)
    {
        ["depth"] = RenderPass.Depth, ["normal"] = RenderPass.Normal, ["object_id"] = RenderPass.ObjectId, ["albedo"] = RenderPass.Albedo,
        ["emission"] = RenderPass.Emission, ["shadow"] = RenderPass.Shadow, ["reflection"] = RenderPass.Reflection,
    };

    /// <summary>A pass as a picture: depth white near to black far over what's seen; normals as colour (½ + ½n); objects in flat colours; the rest as they are.</summary>
    public RenderImage ToImage(RenderPass pass)
    {
        var rgba = new byte[Width * Height * 4];
        static byte B(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        double near = double.MaxValue, far = double.MinValue;
        if (pass == RenderPass.Depth && Depth != null)
            foreach (var d in Depth) if (float.IsFinite(d)) { near = Math.Min(near, d); far = Math.Max(far, d); }
        for (int i = 0; i < Width * Height; i++)
        {
            Vec3 c = Vec3.Zero;
            double a = 1;
            switch (pass)
            {
                case RenderPass.Depth when Depth != null:
                    if (float.IsFinite(Depth[i])) { double v = far > near ? 1 - (Depth[i] - near) / (far - near) * 0.9 : 1; c = new Vec3(v, v, v); }
                    else a = 0;
                    break;
                case RenderPass.Normal when Normal != null:
                    c = Normal[i] == Vec3.Zero ? Vec3.Zero : Normal[i] * 0.5 + new Vec3(0.5, 0.5, 0.5);
                    break;
                case RenderPass.ObjectId when ObjectId != null:
                    c = ObjectId[i] < 0 ? Vec3.Zero : IdColour(ObjectId[i]);
                    break;
                case RenderPass.Albedo when Albedo != null: c = Albedo[i]; break;
                case RenderPass.Emission when Emission != null: c = Emission[i]; break;
                case RenderPass.Shadow when Shadow != null: c = new Vec3(Shadow[i], Shadow[i], Shadow[i]); break;
                case RenderPass.Reflection when Reflection != null: c = Reflection[i]; break;
                default: throw new ArgumentException($"The {pass} pass wasn't rendered.");
            }
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (B(c.X), B(c.Y), B(c.Z), B(a));
        }
        return new RenderImage(Width, Height, rgba);
    }

    /// <summary>A bright, well-spread colour for an object's number (the golden angle round the hue circle).</summary>
    public static Vec3 IdColour(int id)
    {
        double h = (id * 0.618033988749895) % 1 * 6, x = 1 - Math.Abs(h % 2 - 1);
        (double r, double g, double b) = (int)h switch { 0 => (1.0, x, 0.0), 1 => (x, 1.0, 0.0), 2 => (0.0, 1.0, x), 3 => (0.0, x, 1.0), 4 => (x, 0.0, 1.0), _ => (1.0, 0.0, x) };
        return new Vec3(0.2 + 0.8 * r, 0.2 + 0.8 * g, 0.2 + 0.8 * b);
    }
}

public static partial class RayTracer
{
    /// <summary>
    /// The passes asked for, through the camera at a frame (one ray through each pixel's centre, at the frame itself:
    /// no depth of field or motion blur), at the settings' size.
    /// </summary>
    public static PassImages Passes(Scene scene, double frame, RenderSettings? settings, RenderPass wanted, CancellationToken cancel = default)
    {
        settings ??= new RenderSettings();
        var camera = settings.Camera ?? scene.CurrentCamera ?? throw new InvalidOperationException("The scene has no camera to render through.");
        int width = Math.Max(1, settings.Width ?? camera.Width), height = Math.Max(1, settings.Height ?? camera.Height);
        double zoom = camera.Zoom.Evaluate(frame), tanV = 1 / zoom, tanH = tanV * width * camera.PixelAspect / height;
        var toWorld = camera.CameraToWorld(frame);
        var eye = toWorld.TransformPoint(Vec3.Zero);
        var forward = toWorld.TransformVector(Vec3.UnitZ).Normalized();
        var tracer = new Tracer(scene, scene.Evaluate(frame), frame, settings, 2 * tanV / height, EnvironmentOf(scene));
        int n = width * height;
        var r = new PassImages
        {
            Width = width, Height = height,
            Depth = wanted.HasFlag(RenderPass.Depth) ? new float[n] : null,
            Normal = wanted.HasFlag(RenderPass.Normal) ? new Vec3[n] : null,
            ObjectId = wanted.HasFlag(RenderPass.ObjectId) ? new int[n] : null,
            Albedo = wanted.HasFlag(RenderPass.Albedo) ? new Vec3[n] : null,
            Emission = wanted.HasFlag(RenderPass.Emission) ? new Vec3[n] : null,
            Shadow = wanted.HasFlag(RenderPass.Shadow) ? new float[n] : null,
            Reflection = wanted.HasFlag(RenderPass.Reflection) ? new Vec3[n] : null,
        };
        var objects = scene.Objects.ToList();
        var options = new ParallelOptions { MaxDegreeOfParallelism = settings.MaxThreads > 0 ? settings.MaxThreads : -1, CancellationToken = cancel };
        Parallel.For(0, height, options, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                var local = new Vec3((2 * (x + 0.5) / width - 1) * tanH, (1 - 2 * (y + 0.5) / height) * tanV, 1);
                var dir = toWorld.TransformVector(local).Normalized();
                var probe = tracer.Probe(eye, dir, new PixelSamples(x, y, 0, 1), wanted);
                if (r.Depth != null) r.Depth[i] = probe.Hit ? (float)(probe.Distance * Vec3.Dot(dir, forward)) : float.NaN;
                if (r.Normal != null) r.Normal[i] = probe.Normal;
                if (r.ObjectId != null) r.ObjectId[i] = probe.Owner is { } o ? objects.IndexOf(o) : -1;
                if (r.Albedo != null) r.Albedo[i] = probe.Albedo;
                if (r.Emission != null) r.Emission[i] = probe.Emission;
                if (r.Shadow != null) r.Shadow[i] = (float)probe.Shadow;
                if (r.Reflection != null) r.Reflection[i] = probe.Reflection;
            }
        });
        return r;
    }

    /// <summary>What a ray first hits, for the passes.</summary>
    internal readonly record struct ProbeHit(bool Hit, double Distance, Vec3 Normal, ObjectItem? Owner, Vec3 Albedo, Vec3 Emission, double Shadow, Vec3 Reflection);

    private sealed partial class Tracer
    {
        internal ProbeHit Probe(Vec3 origin, Vec3 dir, in PixelSamples pixel, RenderPass wanted)
        {
            if (!_bvh.Intersect(origin, dir, _bias, double.PositiveInfinity, out var hit)) return default;
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
            double shadow = 0;
            if (wanted.HasFlag(RenderPass.Shadow) && _settings.Shadows && m.ReceiveShadows)
            {
                // The share of its direct light that's blocked, each light weighted by how much it would bring.
                double all = 0, blocked = 0;
                foreach (var light in _lights)
                {
                    if (!light.Item.Shadows || light.Item.LinkMode != LightLinkMode.All && !light.Item.Lights(m.Owner)) continue;
                    Vec3 toLight;
                    double distance;
                    if (light.Type == LightType.Distant) { toLight = -light.Direction; distance = double.PositiveInfinity; }
                    else { var d = light.Position - p; distance = d.Length; if (distance == 0) continue; toLight = d / distance; }
                    double w = Math.Max(0, Vec3.Dot(ns, toLight)) * (light.Color.X + light.Color.Y + light.Color.Z);
                    if (w <= 0) continue;
                    all += w;
                    blocked += w * (1 - Transmission(p + lift, toLight, distance));
                }
                shadow = all > 0 ? blocked / all : 0;
            }
            var reflection = Vec3.Zero;
            if (wanted.HasFlag(RenderPass.Reflection) && look.Reflection > 0 && _settings.Reflections)
            {
                var r = dir - ns * (2 * Vec3.Dot(dir, ns));
                reflection = Trace(p + lift, r.Normalized(), 1, out _, pixel, hit.T) * look.Reflection;
            }
            return new ProbeHit(true, hit.T, ns, m.Owner, look.Color, look.Color * look.Luminosity, shadow, reflection);
        }
    }

    /// <summary>
    /// A picture with its noise smoothed away, guided by its passes (needs depth, normal, object ID and albedo): an
    /// edge-stopping à-trous filter over the light (the picture divided by the surface colour, so textures stay sharp),
    /// never across objects, sharp turns of the surface or jumps in depth. <paramref name="strength"/> from 0 (none) to
    /// 2 (strong); 1 suits a few rays a pixel.
    /// </summary>
    public static RenderImage Denoise(RenderImage picture, PassImages guide, double strength = 1)
    {
        if (guide.Depth == null || guide.Normal == null || guide.ObjectId == null || guide.Albedo == null)
            throw new ArgumentException("Denoising needs the depth, normal, object ID and albedo passes.");
        if (guide.Width != picture.Width || guide.Height != picture.Height) throw new ArgumentException("The passes must be the picture's size.");
        int w = picture.Width, h = picture.Height, n = w * h;
        if (strength <= 0) return picture;
        var light = new Vec3[n];
        var alpha = new byte[n];
        const double eps = 0.02;
        for (int i = 0; i < n; i++)
        {
            var (r, g, b, a) = picture.Pixel(i % w, i / w);
            var c = new Vec3(r / 255.0, g / 255.0, b / 255.0);
            var al = guide.Albedo[i];
            light[i] = new Vec3(c.X / (al.X + eps), c.Y / (al.Y + eps), c.Z / (al.Z + eps));
            alpha[i] = a;
        }
        double[] kernel = [1 / 16.0, 1 / 4.0, 3 / 8.0, 1 / 4.0, 1 / 16.0];
        // How noisy each pixel's light is: the spread of its neighbours' (on the same object) over 5 × 5. The light may
        // differ from a neighbour's by about this much and still be blended, so flat noise is smoothed and real edges
        // (a shadow's, a highlight's) that stand out from it are kept; it halves each pass, as the filter takes noise away.
        var noise = new double[n];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (guide.ObjectId[i] < 0) continue;
                double sum = 0, sq = 0;
                int count = 0;
                for (int qy = Math.Max(0, y - 2); qy <= Math.Min(h - 1, y + 2); qy++)
                    for (int qx = Math.Max(0, x - 2); qx <= Math.Min(w - 1, x + 2); qx++)
                    {
                        int q = qy * w + qx;
                        if (guide.ObjectId[q] != guide.ObjectId[i]) continue;
                        double l = Luma(light[q]);
                        sum += l;
                        sq += l * l;
                        count++;
                    }
                double mean = sum / count;
                noise[i] = Math.Sqrt(Math.Max(0, sq / count - mean * mean));
            }
        });
        var next = new Vec3[n];
        for (int pass = 0, step = 1; pass < 5; pass++, step *= 2)
        {
            double fade = Math.Pow(0.5, pass);
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (guide.ObjectId[i] < 0) { next[i] = light[i]; continue; }
                    var sum = Vec3.Zero;
                    double total = 0;
                    double lp = Luma(light[i]), dp = guide.Depth[i];
                    var np = guide.Normal[i];
                    for (int ky = -2; ky <= 2; ky++)
                    {
                        int qy = y + ky * step;
                        if (qy < 0 || qy >= h) continue;
                        for (int kx = -2; kx <= 2; kx++)
                        {
                            int qx = x + kx * step;
                            if (qx < 0 || qx >= w) continue;
                            int q = qy * w + qx;
                            if (guide.ObjectId[q] != guide.ObjectId[i]) continue;
                            double wn = Math.Pow(Math.Max(0, Vec3.Dot(np, guide.Normal[q])), 32);
                            double wd = Math.Exp(-Math.Abs(dp - guide.Depth[q]) / (0.03 * Math.Max(0.1, dp) * step));
                            double wl = Math.Exp(-Math.Abs(lp - Luma(light[q])) / (2 * strength * noise[i] * fade + 1e-3));
                            double weight = kernel[kx + 2] * kernel[ky + 2] * wn * wd * wl;
                            sum += light[q] * weight;
                            total += weight;
                        }
                    }
                    next[i] = total > 0 ? sum / total : light[i];
                }
            });
            (light, next) = (next, light);
        }
        var rgba = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            var c = guide.ObjectId[i] < 0 ? light[i] : new Vec3(light[i].X * (guide.Albedo[i].X + eps), light[i].Y * (guide.Albedo[i].Y + eps), light[i].Z * (guide.Albedo[i].Z + eps));
            if (guide.ObjectId[i] < 0)
            {
                var (r, g, b, _) = picture.Pixel(i % w, i / w);
                c = new Vec3(r / 255.0, g / 255.0, b / 255.0);
            }
            rgba[i * 4] = ToByte(c.X);
            rgba[i * 4 + 1] = ToByte(c.Y);
            rgba[i * 4 + 2] = ToByte(c.Z);
            rgba[i * 4 + 3] = alpha[i];
        }
        return new RenderImage(w, h, rgba);
    }

    private static double Luma(Vec3 c) => 0.2126 * c.X + 0.7152 * c.Y + 0.0722 * c.Z;
}
