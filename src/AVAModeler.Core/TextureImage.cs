using System.Buffers.Binary;
using System.IO.Compression;

namespace AVAModeler.Core;

/// <summary>
/// An image for texturing: width × height pixels of 8-bit red, green, blue and alpha, top row first. A placeholder
/// (a checker) stands in for an image that couldn't be found or read, and says why.
/// </summary>
public sealed class TextureImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>The pixels, 4 bytes each (red, green, blue, alpha), top row first.</summary>
    public byte[] Rgba { get; }
    /// <summary>Whether this is the checker that stands in for a missing image.</summary>
    public bool IsPlaceholder { get; init; }
    /// <summary>Why the image couldn't be had (for a placeholder), such as "Can't find brick.png".</summary>
    public string? Problem { get; init; }
    /// <summary>The file it was read from, if any.</summary>
    public string? Path { get; init; }

    public TextureImage(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("An image needs at least one pixel.");
        if (rgba.Length != width * height * 4) throw new ArgumentException("The pixels must be width × height × 4 bytes.", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    /// <summary>A grey and magenta checker, 8 squares across, for an image that can't be had.</summary>
    public static TextureImage Placeholder(string problem)
    {
        const int size = 64, square = 8;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool odd = (x / square + y / square) % 2 == 1;
                int i = (y * size + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = odd ? ((byte)230, (byte)40, (byte)200, (byte)255) : ((byte)70, (byte)70, (byte)76, (byte)255);
            }
        return new TextureImage(size, size, rgba) { IsPlaceholder = true, Problem = problem };
    }

    /// <summary>A pixel as colour (0 to 1 per channel) and alpha.</summary>
    public (Vec3 Color, double Alpha) Pixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (new Vec3(Rgba[i] / 255.0, Rgba[i + 1] / 255.0, Rgba[i + 2] / 255.0), Rgba[i + 3] / 255.0);
    }

    /// <summary>
    /// The image at image coordinates (u across from the left edge, v up from the bottom; 0 to 1 is the image once),
    /// past its edges as the tilings say. With <paramref name="blend"/> the four nearest pixels are blended
    /// (bilinear, weighted by their alpha, so a <see cref="TextureTiling.Reset"/> edge fades out rather than going
    /// dark); without, the pixel the point falls in. Outside a reset edge, alpha is 0.
    /// </summary>
    public (Vec3 Color, double Alpha) Sample(double u, double v, TextureTiling across, TextureTiling up, bool blend = true)
    {
        if (!double.IsFinite(u) || !double.IsFinite(v)) return (Vec3.Zero, 0);
        double x = u * Width, y = (1 - v) * Height;
        if (!blend) return Texel(Floor(x), Floor(y), across, up);
        x -= 0.5;
        y -= 0.5;
        long x0 = Floor(x), y0 = Floor(y);
        double fx = x - x0, fy = y - y0;
        double r = 0, g = 0, b = 0, a = 0;
        void Add(long tx, long ty, double w)
        {
            if (w == 0) return;
            var (c, alpha) = Texel(tx, ty, across, up);
            double k = w * alpha;
            r += c.X * k;
            g += c.Y * k;
            b += c.Z * k;
            a += k;
        }
        Add(x0, y0, (1 - fx) * (1 - fy));
        Add(x0 + 1, y0, fx * (1 - fy));
        Add(x0, y0 + 1, (1 - fx) * fy);
        Add(x0 + 1, y0 + 1, fx * fy);
        return a > 1e-12 ? (new Vec3(r / a, g / a, b / a), Math.Min(1, a)) : (Vec3.Zero, 0);
    }

    private static long Floor(double v) => (long)Math.Floor(Math.Clamp(v, -1e15, 1e15));

    private (Vec3 Color, double Alpha) Texel(long x, long y, TextureTiling across, TextureTiling up)
    {
        int tx = Wrap(x, Width, across), ty = Wrap(y, Height, up);
        return tx < 0 || ty < 0 ? (Vec3.Zero, 0) : Pixel(tx, ty);
    }

    /// <summary>A pixel index brought into the image by a tiling; -1 when it falls outside a reset edge.</summary>
    public static int Wrap(long i, int size, TextureTiling tiling)
    {
        switch (tiling)
        {
            case TextureTiling.Repeat:
                return (int)(((i % size) + size) % size);
            case TextureTiling.Mirror:
            {
                long m = ((i % (2L * size)) + 2L * size) % (2L * size);
                return (int)(m < size ? m : 2L * size - 1 - m);
            }
            case TextureTiling.Edge:
                return (int)Math.Clamp(i, 0, size - 1);
            default:
                return i < 0 || i >= size ? -1 : (int)i;
        }
    }

    private TextureImage[]? _mips;
    private readonly Lock _mipLock = new();

    /// <summary>
    /// The image at a mipmap level: halved (each pixel the alpha-weighted average of the four it covers) once per level,
    /// down to a single pixel; level 0 is the image itself. Made on first use and kept.
    /// </summary>
    public TextureImage Level(int level)
    {
        if (level <= 0) return this;
        lock (_mipLock)
        {
            if (_mips == null)
            {
                var chain = new List<TextureImage> { this };
                while (chain[^1].Width > 1 || chain[^1].Height > 1) chain.Add(chain[^1].Halved());
                _mips = [.. chain];
            }
            return _mips[Math.Min(level, _mips.Length - 1)];
        }
    }

    private TextureImage Halved()
    {
        int w = Math.Max(1, (Width + 1) / 2), h = Math.Max(1, (Height + 1) / 2);
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(2 * x + dx, Width - 1), sy = Math.Min(2 * y + dy, Height - 1);
                        int i = (sy * Width + sx) * 4;
                        double al = Rgba[i + 3];
                        r += Rgba[i] * al;
                        g += Rgba[i + 1] * al;
                        b += Rgba[i + 2] * al;
                        a += al;
                        n++;
                    }
                int o = (y * w + x) * 4;
                if (a > 0) (rgba[o], rgba[o + 1], rgba[o + 2]) = ((byte)Math.Round(r / a), (byte)Math.Round(g / a), (byte)Math.Round(b / a));
                rgba[o + 3] = (byte)Math.Round(a / n);
            }
        return new TextureImage(w, h, rgba);
    }

    /// <summary>
    /// <see cref="Sample"/> for a sample covering about <paramref name="texels"/> pixels of the image across: blended
    /// between the two mipmap levels nearest that size (trilinear filtering), so a distant texture doesn't shimmer.
    /// </summary>
    public (Vec3 Color, double Alpha) SampleFiltered(double u, double v, TextureTiling across, TextureTiling up, bool blend, double texels)
    {
        if (!blend || !(texels > 1)) return Sample(u, v, across, up, blend);
        double lod = Math.Min(Math.Log2(texels), 24);
        int l0 = (int)Math.Floor(lod);
        double f = lod - l0;
        var (c0, a0) = Level(l0).Sample(u, v, across, up);
        if (f < 1e-6) return (c0, a0);
        var (c1, a1) = Level(l0 + 1).Sample(u, v, across, up);
        double a = a0 * (1 - f) + a1 * f;
        return a > 1e-12 ? ((c0 * (a0 * (1 - f)) + c1 * (a1 * f)) / a, a) : (Vec3.Zero, 0);
    }

    /// <summary>The brightness of a colour (for value and bump layers): the average of its channels.</summary>
    public static double Brightness(Vec3 c) => (c.X + c.Y + c.Z) / 3;
}

/// <summary>
/// Finding, reading and caching texture images. A relative image path is looked for beside the object file, then in
/// LightWave's usual places (an Images folder beside it or one level up, the object's own folder by file name alone),
/// then from the working directory. PNG and BMP are read here; other formats (JPEG among them) through
/// <see cref="Decoders"/>, which the app fills from SkiaSharp. An image that can't be found or read comes back as a
/// placeholder checker that says so, and nothing throws.
/// </summary>
public static class TextureImages
{
    private sealed record Entry(string? File, DateTime Written, TextureImage Image, DateTime Checked);

    private static readonly Dictionary<(string Path, string Base), Entry> Cache = [];
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// Readers for other image formats, tried after PNG and BMP: each takes the file's bytes and returns the image, or
    /// null if it isn't a format it reads.
    /// </summary>
    public static List<Func<byte[], TextureImage?>> Decoders { get; } = [];

    /// <summary>How long a missing image is remembered as missing before the disk is looked at again.</summary>
    public static TimeSpan RetryMissingAfter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The image at a path (relative ones from <paramref name="baseDirectory"/>, usually the object file's folder),
    /// read once and kept until the file changes. Never null: a placeholder stands in for a missing or unreadable one.
    /// </summary>
    public static TextureImage Load(string path, string? baseDirectory = null)
    {
        var key = (path ?? "", baseDirectory ?? "");
        var now = DateTime.UtcNow;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var e))
            {
                if (e.File == null && now - e.Checked < RetryMissingAfter) return e.Image;
                if (e.File != null && SafeWriteTime(e.File) == e.Written) return e.Image;
            }
        }
        string? file = Resolve(path ?? "", baseDirectory);
        TextureImage image;
        DateTime written = default;
        if (file == null) image = TextureImage.Placeholder($"Can't find the image {Name(path)}; a checker stands in for it.");
        else
        {
            written = SafeWriteTime(file);
            try
            {
                image = Decode(File.ReadAllBytes(file), file) ?? TextureImage.Placeholder($"Can't read the image {Name(path)} (not a format this reads); a checker stands in for it.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
            {
                image = TextureImage.Placeholder($"Can't read the image {Name(path)} ({ex.Message}); a checker stands in for it.");
            }
        }
        lock (CacheLock) Cache[key] = new Entry(image.IsPlaceholder ? null : file, written, image, now);
        return image;
    }

    /// <summary>Puts an image in the cache under a path, as if read from there (for images made in memory, and tests).</summary>
    public static void Put(string path, TextureImage image, string? baseDirectory = null)
    {
        lock (CacheLock) Cache[(path, baseDirectory ?? "")] = new Entry(null, default, image, DateTime.MaxValue);
    }

    /// <summary>Forgets every cached image, so the next use reads the file again.</summary>
    public static void Clear()
    {
        lock (CacheLock) Cache.Clear();
    }

    private static string Name(string? path) => string.IsNullOrEmpty(path) ? "(none given)" : System.IO.Path.GetFileName(path.Replace('\\', '/')) is { Length: > 0 } n ? n : path;

    private static DateTime SafeWriteTime(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return default; }
    }

    /// <summary>
    /// Where an image path leads, or null if nowhere: as given (if rooted), from the base directory, LightWave's
    /// Images folders beside it and one up, the base directory by file name alone, then the working directory.
    /// </summary>
    public static string? Resolve(string path, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var candidates = new List<string>();
        string clean = path.Replace('\\', '/');
        // A path from another machine (an Amiga volume, a Windows drive) may only be any use by its file name.
        string name = clean[(clean.LastIndexOfAny(['/', ':']) + 1)..];
        try
        {
            if (System.IO.Path.IsPathRooted(clean)) candidates.Add(clean);
            if (baseDirectory != null)
            {
                if (!System.IO.Path.IsPathRooted(clean)) candidates.Add(System.IO.Path.Combine(baseDirectory, clean));
                if (!System.IO.Path.IsPathRooted(clean)) candidates.Add(System.IO.Path.Combine(baseDirectory, "..", clean));
                if (name.Length > 0)
                {
                    candidates.Add(System.IO.Path.Combine(baseDirectory, name));
                    candidates.Add(System.IO.Path.Combine(baseDirectory, "Images", name));
                    candidates.Add(System.IO.Path.Combine(baseDirectory, "..", "Images", name));
                }
            }
            if (!System.IO.Path.IsPathRooted(clean)) candidates.Add(System.IO.Path.GetFullPath(clean));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return System.IO.Path.GetFullPath(c);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException) { }
        }
        return null;
    }

    /// <summary>
    /// An image path to store with an object saved at <paramref name="objectPath"/>: relative to the object's folder
    /// (with forward slashes) when the image is in it, below it, or beside it in the folder above (LightWave's
    /// Objects and Images folders); otherwise as it was.
    /// </summary>
    public static string RelativePath(string imagePath, string? objectPath)
    {
        if (string.IsNullOrEmpty(imagePath) || objectPath == null || !System.IO.Path.IsPathRooted(imagePath)) return imagePath;
        try
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(objectPath)) ?? "";
            string rel = System.IO.Path.GetRelativePath(dir, imagePath).Replace('\\', '/');
            if (System.IO.Path.IsPathRooted(rel) || rel.StartsWith("../../", StringComparison.Ordinal) || rel == "..") return imagePath;
            return rel;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return imagePath; }
    }

    /// <summary>The folder an object's relative image paths start from: the folder its file is in, or none.</summary>
    public static string? BaseOf(string? objectPath)
    {
        if (string.IsNullOrEmpty(objectPath)) return null;
        try { return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(objectPath)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>A note for each image the surfaces' texture layers use that can't be had, once each.</summary>
    public static List<string> Problems(IEnumerable<Surface> surfaces, string? baseDirectory)
    {
        var seen = new HashSet<string>();
        var problems = new List<string>();
        foreach (var s in surfaces)
            foreach (var t in s.Textures)
                if (t.IsImage && seen.Add(t.Image) && Load(t.Image, baseDirectory) is { IsPlaceholder: true, Problem: { } p })
                    problems.Add($"{s.Name}: {p}");
        return problems;
    }

    /// <summary>Reads an image file's bytes: PNG or BMP here, anything else through <see cref="Decoders"/>; null if none can.</summary>
    public static TextureImage? Decode(byte[] data, string? file = null)
    {
        TextureImage? image = null;
        if (PngReader.IsPng(data)) image = PngReader.Read(data);
        else if (data.Length > 2 && data[0] == 'B' && data[1] == 'M') image = BmpReader.Read(data);
        else
            foreach (var d in Decoders)
                if ((image = d(data)) != null) break;
        return image == null ? null : new TextureImage(image.Width, image.Height, image.Rgba) { Path = file };
    }
}

/// <summary>Reads PNG files (every colour type and bit depth, interlaced or not), with no outside library.</summary>
internal static class PngReader
{
    public static bool IsPng(byte[] d) => d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G';

    public static TextureImage Read(byte[] data)
    {
        int pos = 8, width = 0, height = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null, transparency = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            string id = System.Text.Encoding.Latin1.GetString(data, pos + 4, 4);
            if (length < 0 || pos + 12 + length > data.Length) throw new InvalidDataException("a PNG chunk runs past the end of the file");
            var body = data.AsSpan(pos + 8, length);
            switch (id)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8];
                    type = body[9];
                    interlace = body[12];
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": transparency = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
            }
            pos += 12 + length;
            if (id == "IEND") break;
        }
        if (width <= 0 || height <= 0 || (long)width * height > 1 << 28) throw new InvalidDataException("the PNG's size isn't sensible");
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {type} isn't known") };
        if (depth is not (1 or 2 or 4 or 8 or 16)) throw new InvalidDataException($"PNG bit depth {depth} isn't known");
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();
        var rgba = new byte[width * height * 4];
        int bitsPerPixel = channels * depth;
        int at = 0;
        if (interlace == 0) Pass(0, 0, 1, 1);
        else
        {
            (int X, int Y, int Dx, int Dy)[] passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            foreach (var p in passes) Pass(p.X, p.Y, p.Dx, p.Dy);
        }
        return new TextureImage(width, height, rgba);

        void Pass(int x0, int y0, int dx, int dy)
        {
            int w = (width - x0 + dx - 1) / dx, h = (height - y0 + dy - 1) / dy;
            if (w <= 0 || h <= 0) return;
            int stride = (w * bitsPerPixel + 7) / 8, bpp = Math.Max(1, bitsPerPixel / 8);
            var prev = new byte[stride];
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                if (at + 1 + stride > bytes.Length) throw new InvalidDataException("the PNG's image data is too short");
                int filter = bytes[at];
                Array.Copy(bytes, at + 1, row, 0, stride);
                at += 1 + stride;
                Unfilter(filter, row, prev, bpp);
                for (int x = 0; x < w; x++) Put(row, x, (x0 + x * dx) + (y0 + y * dy) * width);
                (prev, row) = (row, prev);
            }
        }

        int Sample(byte[] row, int index)
        {
            if (depth == 8) return row[index];
            if (depth == 16) return row[index * 2];
            int bit = index * depth;
            return (row[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
        }

        int Full(int v) => depth switch { 1 => v * 255, 2 => v * 85, 4 => v * 17, _ => v };

        void Put(byte[] row, int x, int pixel)
        {
            int o = pixel * 4;
            int c = x * channels;
            switch (type)
            {
                case 0:
                {
                    int g = Sample(row, c);
                    byte v = (byte)Full(g);
                    bool clear = transparency is { Length: >= 2 } && RawSample(row, c) == (transparency[0] << 8 | transparency[1]);
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (v, v, v, clear ? (byte)0 : (byte)255);
                    break;
                }
                case 2:
                {
                    bool clear = transparency is { Length: >= 6 }
                        && RawSample(row, c) == (transparency[0] << 8 | transparency[1])
                        && RawSample(row, c + 1) == (transparency[2] << 8 | transparency[3])
                        && RawSample(row, c + 2) == (transparency[4] << 8 | transparency[5]);
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = ((byte)Sample(row, c), (byte)Sample(row, c + 1), (byte)Sample(row, c + 2), clear ? (byte)0 : (byte)255);
                    break;
                }
                case 3:
                {
                    int i = Sample(row, c);
                    if (palette != null && i * 3 + 2 < palette.Length)
                        (rgba[o], rgba[o + 1], rgba[o + 2]) = (palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]);
                    rgba[o + 3] = transparency != null && i < transparency.Length ? transparency[i] : (byte)255;
                    break;
                }
                case 4:
                {
                    byte v = (byte)Sample(row, c);
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (v, v, v, (byte)Sample(row, c + 1));
                    break;
                }
                default:
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = ((byte)Sample(row, c), (byte)Sample(row, c + 1), (byte)Sample(row, c + 2), (byte)Sample(row, c + 3));
                    break;
            }
        }

        // A sample at its file precision, for comparing with tRNS.
        int RawSample(byte[] row, int index) => depth == 16 ? row[index * 2] << 8 | row[index * 2 + 1] : Sample(row, index);
    }

    private static void Unfilter(int filter, byte[] row, byte[] prev, int bpp)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + a),
                2 => (byte)(row[i] + b),
                3 => (byte)(row[i] + (a + b) / 2),
                4 => (byte)(row[i] + Paeth(a, b, c)),
                _ => throw new InvalidDataException($"PNG filter {filter} isn't known"),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

/// <summary>Reads uncompressed BMP files: 24- and 32-bit colour, and 1-, 4- and 8-bit with a palette.</summary>
internal static class BmpReader
{
    public static TextureImage Read(byte[] d)
    {
        if (d.Length < 54) throw new InvalidDataException("the BMP is too short");
        int offset = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(10));
        int header = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(14));
        int width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(30));
        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        if (width <= 0 || height <= 0 || (long)width * height > 1 << 28) throw new InvalidDataException("the BMP's size isn't sensible");
        if (compression is not (0 or 3)) throw new InvalidDataException("compressed BMPs aren't read");
        if (bits is not (1 or 4 or 8 or 24 or 32)) throw new InvalidDataException($"{bits}-bit BMPs aren't read");
        int colours = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(46));
        if (colours == 0 && bits <= 8) colours = 1 << bits;
        int paletteAt = 14 + header;
        int stride = (width * bits + 31) / 32 * 4;
        if (offset < 0 || offset + (long)stride * height > d.Length) throw new InvalidDataException("the BMP's pixels run past the end of the file");
        // A 32-bit BMP uses its fourth byte as alpha only if any pixel sets it.
        bool alpha = false;
        if (bits == 32)
            for (int y = 0; y < height && !alpha; y++)
                for (int x = 0; x < width; x++)
                    if (d[offset + y * stride + x * 4 + 3] != 0) { alpha = true; break; }
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int src = offset + (topDown ? y : height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                if (bits >= 24)
                {
                    int p = src + x * (bits / 8);
                    (rgba[o], rgba[o + 1], rgba[o + 2]) = (d[p + 2], d[p + 1], d[p]);
                    rgba[o + 3] = bits == 32 && alpha ? d[p + 3] : (byte)255;
                }
                else
                {
                    int bit = x * bits;
                    int index = (d[src + (bit >> 3)] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
                    int q = paletteAt + Math.Min(index, colours - 1) * 4;
                    if (q + 2 < d.Length) (rgba[o], rgba[o + 1], rgba[o + 2]) = (d[q + 2], d[q + 1], d[q]);
                    rgba[o + 3] = 255;
                }
            }
        }
        return new TextureImage(width, height, rgba);
    }
}
