using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// An environment picture ready to look up by direction (<see cref="EnvironmentSettings"/>): a latitude-longitude
/// image of floats, its middle column looking along +Z and its top row straight up, turned by a heading. Lookups are
/// bilinear, wrapping round left to right. A small, blurred copy (64 × 32, each pixel the average of those it covers)
/// serves the lighting's rays, so a bright patch lights smoothly rather than in speckles, and from it a table of the
/// light falling on a surface facing each way (<see cref="Irradiance"/>, the cosine-weighted average over the
/// hemisphere, 32 × 16 directions) lets the lighting count only what's hidden from a point, so open surfaces come out
/// without noise.
/// <para>
/// Pictures are read once and kept until their file changes (<see cref="Load"/>). <c>builtin:sky</c> and
/// <c>builtin:studio</c> are made here: a blue sky with a warm sun over a grey ground, and a dim studio with two soft
/// boxes and a bright overhead panel.
/// </para>
/// </summary>
public sealed class EnvironmentMap
{
    public const string BuiltinPrefix = "builtin:";
    /// <summary>The made-up environments, by name after <see cref="BuiltinPrefix"/>.</summary>
    public static readonly IReadOnlyList<string> Builtins = ["sky", "studio"];

    private readonly HdrImage _image, _small, _irradiance;
    private readonly double _cos, _sin;

    public EnvironmentMap(HdrImage image, double heading = 0)
        : this(image, Shrink(image, 64, 32), null, heading)
    {
    }

    /// <summary>The picture, as read.</summary>
    public HdrImage Image => _image;

    /// <summary>The same picture turned to another heading.</summary>
    public EnvironmentMap Turned(double heading) => new(_image, _small, _irradiance, heading);

    private EnvironmentMap(HdrImage image, HdrImage small, HdrImage? irradiance, double heading)
    {
        _image = image;
        _small = small;
        _irradiance = irradiance ?? IrradianceOf(small, 32, 16);
        double h = heading * Math.PI / 180;
        (_cos, _sin) = (Math.Cos(h), Math.Sin(h));
    }

    /// <summary>
    /// The light falling on a surface facing a way (a unit normal in the world), from the blurred environment: its
    /// cosine-weighted average over the hemisphere the normal faces, which is what <see cref="Blurred"/> averages to
    /// over rays spread that way.
    /// </summary>
    public Vec3 Irradiance(Vec3 normal) => Lookup(_irradiance, normal);

    /// <summary>For each direction of a lat-long table, the cosine-weighted average of a picture over the hemisphere it faces.</summary>
    private static HdrImage IrradianceOf(HdrImage src, int width, int height)
    {
        // Each source pixel's direction and its share of the sphere (solid angle).
        int n = src.Width * src.Height;
        var dirs = new Vec3[n];
        var weights = new double[n];
        for (int y = 0; y < src.Height; y++)
        {
            double t0 = y * Math.PI / src.Height, t1 = (y + 1) * Math.PI / src.Height, theta = (t0 + t1) / 2;
            double area = (Math.Cos(t0) - Math.Cos(t1)) * 2 * Math.PI / src.Width;
            for (int x = 0; x < src.Width; x++)
            {
                double phi = ((x + 0.5) / src.Width - 0.5) * 2 * Math.PI;
                dirs[y * src.Width + x] = new Vec3(Math.Sin(theta) * Math.Sin(phi), Math.Cos(theta), Math.Sin(theta) * Math.Cos(phi));
                weights[y * src.Width + x] = area;
            }
        }
        var rgb = new float[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            double theta = (y + 0.5) / height * Math.PI;
            for (int x = 0; x < width; x++)
            {
                double phi = ((x + 0.5) / width - 0.5) * 2 * Math.PI;
                var normal = new Vec3(Math.Sin(theta) * Math.Sin(phi), Math.Cos(theta), Math.Sin(theta) * Math.Cos(phi));
                double r = 0, g = 0, b = 0;
                for (int i = 0; i < n; i++)
                {
                    double c = Vec3.Dot(normal, dirs[i]);
                    if (c <= 0) continue;
                    double w = c * weights[i];
                    int j = i * 3;
                    r += src.Rgb[j] * w;
                    g += src.Rgb[j + 1] * w;
                    b += src.Rgb[j + 2] * w;
                }
                int o = (y * width + x) * 3;
                (rgb[o], rgb[o + 1], rgb[o + 2]) = ((float)(r / Math.PI), (float)(g / Math.PI), (float)(b / Math.PI));
            }
        }
        return new HdrImage(width, height, rgb);
    }

    /// <summary>The environment seen along a direction (a unit vector in the world).</summary>
    public Vec3 Sample(Vec3 dir) => Lookup(_image, dir);

    /// <summary>The blurred environment along a direction, for lighting.</summary>
    public Vec3 Blurred(Vec3 dir) => Lookup(_small, dir);

    /// <summary>Where a world direction lands in the picture: u across from the left (0 to 1), v down from the top.</summary>
    public (double U, double V) Coordinates(Vec3 dir)
    {
        // Into the picture's own frame: turned back by its heading about Y.
        double x = dir.X * _cos - dir.Z * _sin, z = dir.X * _sin + dir.Z * _cos;
        double u = 0.5 + Math.Atan2(x, z) / (2 * Math.PI);
        double v = Math.Acos(Math.Clamp(dir.Y, -1, 1)) / Math.PI;
        return (u, v);
    }

    private Vec3 Lookup(HdrImage img, Vec3 dir)
    {
        var (u, v) = Coordinates(dir);
        double fx = u * img.Width - 0.5, fy = v * img.Height - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        int Wrap(int x) => ((x % img.Width) + img.Width) % img.Width;
        int Clamp(int y) => Math.Clamp(y, 0, img.Height - 1);
        int xa = Wrap(x0), xb = Wrap(x0 + 1), ya = Clamp(y0), yb = Clamp(y0 + 1);
        return (img.Pixel(xa, ya) * (1 - tx) + img.Pixel(xb, ya) * tx) * (1 - ty) + (img.Pixel(xa, yb) * (1 - tx) + img.Pixel(xb, yb) * tx) * ty;
    }

    /// <summary>A picture box-filtered down to at most a size.</summary>
    private static HdrImage Shrink(HdrImage img, int width, int height)
    {
        int w = Math.Min(width, img.Width), h = Math.Min(height, img.Height);
        var rgb = new float[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int y0 = y * img.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * img.Height / h);
            for (int x = 0; x < w; x++)
            {
                int x0 = x * img.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * img.Width / w);
                double r = 0, g = 0, b = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = (sy * img.Width + sx) * 3;
                        r += img.Rgb[i];
                        g += img.Rgb[i + 1];
                        b += img.Rgb[i + 2];
                    }
                double n = (y1 - y0) * (x1 - x0);
                int o = (y * w + x) * 3;
                (rgb[o], rgb[o + 1], rgb[o + 2]) = ((float)(r / n), (float)(g / n), (float)(b / n));
            }
        }
        return new HdrImage(w, h, rgb);
    }

    // ───────────────────────────── loading ─────────────────────────────

    private sealed record Entry(DateTime Written, HdrImage Image);
    private static readonly Dictionary<string, Entry> Cache = [];
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// An environment's picture: a <c>builtin:</c> one, or a file (a relative path from <paramref name="baseDirectory"/>,
    /// else the working directory), read once and kept until it changes. Returns null with the reason if it can't be had.
    /// </summary>
    public static HdrImage? Load(string image, string? baseDirectory, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(image))
        {
            problem = "No environment image is set.";
            return null;
        }
        string key = image.Trim();
        if (key.StartsWith(BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string name = key[BuiltinPrefix.Length..].ToLowerInvariant();
            lock (CacheLock)
            {
                if (Cache.TryGetValue(BuiltinPrefix + name, out var hit)) return hit.Image;
            }
            HdrImage? made = name switch { "sky" => Sky(), "studio" => Studio(), _ => null };
            if (made == null)
            {
                problem = $"There's no built-in environment \"{name}\": the built-in ones are {string.Join(" and ", Builtins.Select(b => BuiltinPrefix + b))}.";
                return null;
            }
            lock (CacheLock) Cache[BuiltinPrefix + name] = new Entry(default, made);
            return made;
        }
        string? file = Resolve(key, baseDirectory);
        if (file == null)
        {
            problem = $"Can't find the environment image {key}.";
            return null;
        }
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { written = default; }
        lock (CacheLock)
        {
            if (Cache.TryGetValue(file, out var hit) && hit.Written == written) return hit.Image;
        }
        try
        {
            var bytes = File.ReadAllBytes(file);
            HdrImage img;
            if (Hdr.IsHdr(bytes)) img = Hdr.Read(bytes);
            else
            {
                var tex = TextureImages.Load(file);
                if (tex.IsPlaceholder)
                {
                    problem = $"Can't read the environment image {Path.GetFileName(file)}: it isn't a Radiance .hdr, PNG, JPEG or BMP picture.";
                    return null;
                }
                img = HdrImage.FromTexture(tex);
            }
            lock (CacheLock) Cache[file] = new Entry(written, img);
            return img;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            problem = $"Can't read the environment image {Path.GetFileName(file)}: {e.Message}";
            return null;
        }
    }

    /// <summary>Where an environment image's file is, or null: as given if rooted, else from the base directory, else the working directory.</summary>
    public static string? Resolve(string image, string? baseDirectory)
    {
        try
        {
            if (Path.IsPathRooted(image)) return File.Exists(image) ? Path.GetFullPath(image) : null;
            if (baseDirectory != null && File.Exists(Path.Combine(baseDirectory, image))) return Path.GetFullPath(Path.Combine(baseDirectory, image));
            return File.Exists(image) ? Path.GetFullPath(image) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>What's wrong with a scene's environment image, or null if it's off or can be had.</summary>
    public static string? Problem(EnvironmentSettings settings)
    {
        if (!settings.On) return null;
        Load(settings.Image, null, out var problem);
        return problem;
    }

    // ───────────────────────────── the built-in environments ─────────────────────────────

    private const int BuiltinWidth = 256, BuiltinHeight = 128;

    /// <summary>Makes a lat-long picture from a function of direction.</summary>
    private static HdrImage Make(Func<Vec3, Vec3> colour)
    {
        var rgb = new float[BuiltinWidth * BuiltinHeight * 3];
        for (int y = 0; y < BuiltinHeight; y++)
        {
            double theta = (y + 0.5) / BuiltinHeight * Math.PI;
            for (int x = 0; x < BuiltinWidth; x++)
            {
                double phi = ((x + 0.5) / BuiltinWidth - 0.5) * 2 * Math.PI;
                var dir = new Vec3(Math.Sin(theta) * Math.Sin(phi), Math.Cos(theta), Math.Sin(theta) * Math.Cos(phi));
                var c = colour(dir);
                int o = (y * BuiltinWidth + x) * 3;
                (rgb[o], rgb[o + 1], rgb[o + 2]) = ((float)c.X, (float)c.Y, (float)c.Z);
            }
        }
        return new HdrImage(BuiltinWidth, BuiltinHeight, rgb);
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>A clear sky, deep blue overhead and pale at the horizon, a warm sun up and to the left, grey-brown ground.</summary>
    private static HdrImage Sky()
    {
        var sun = new Vec3(-0.5, 0.6, 0.62).Normalized();
        return Make(d =>
        {
            if (d.Y < 0)
            {
                double fade = Smooth(0, -0.25, d.Y);
                return Vec3.Lerp(new Vec3(0.55, 0.55, 0.55), new Vec3(0.28, 0.25, 0.22), fade);
            }
            var sky = Vec3.Lerp(new Vec3(0.85, 0.9, 1.0), new Vec3(0.25, 0.45, 0.9), Math.Pow(d.Y, 0.6));
            double cos = Vec3.Dot(d, sun);
            double glow = Math.Pow(Math.Max(0, cos), 64) * 2;
            double disc = Smooth(0.9990, 0.9996, cos) * 40;
            return sky + new Vec3(1, 0.9, 0.7) * (glow + disc);
        });
    }

    /// <summary>A dim grey studio: two big soft boxes to the front left and right, a panel overhead, a dark floor.</summary>
    private static HdrImage Studio()
    {
        static double Box(Vec3 d, Vec3 centre, double size)
        {
            double cos = Vec3.Dot(d, centre.Normalized());
            return Smooth(Math.Cos(size * 1.25), Math.Cos(size), cos);
        }
        return Make(d =>
        {
            double wall = 0.18 + 0.1 * Math.Max(0, d.Y);
            var c = d.Y < -0.02 ? new Vec3(0.12, 0.12, 0.13) : new Vec3(wall, wall, wall * 1.05);
            c += new Vec3(4, 3.9, 3.7) * Box(d, new Vec3(-1, 0.6, 0.6), 0.32);
            c += new Vec3(2.2, 2.3, 2.6) * Box(d, new Vec3(1, 0.4, 0.3), 0.3);
            c += new Vec3(3, 3, 3) * Box(d, new Vec3(0, 1, 0.15), 0.35);
            return c;
        });
    }
}
