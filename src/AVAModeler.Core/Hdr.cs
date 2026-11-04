using System.Text;

namespace AVAModeler.Core;

/// <summary>
/// A high-dynamic-range picture: width × height pixels of red, green and blue as floats (1 is the brightness an 8-bit
/// image's 255 stands for, and brighter is allowed), top row first.
/// </summary>
public sealed class HdrImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>The pixels, 3 floats each (red, green, blue), top row first.</summary>
    public float[] Rgb { get; }

    public HdrImage(int width, int height, float[] rgb)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("An image needs at least one pixel.");
        if (rgb.Length != width * height * 3) throw new ArgumentException("The pixels must be width × height × 3 floats.", nameof(rgb));
        Width = width;
        Height = height;
        Rgb = rgb;
    }

    public Vec3 Pixel(int x, int y)
    {
        int i = (y * Width + x) * 3;
        return new Vec3(Rgb[i], Rgb[i + 1], Rgb[i + 2]);
    }

    /// <summary>An 8-bit picture as floats, 255 becoming 1 (alpha is dropped).</summary>
    public static HdrImage FromTexture(TextureImage image)
    {
        var rgb = new float[image.Width * image.Height * 3];
        for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4)
        {
            rgb[i] = image.Rgba[j] / 255f;
            rgb[i + 1] = image.Rgba[j + 1] / 255f;
            rgb[i + 2] = image.Rgba[j + 2] / 255f;
        }
        return new HdrImage(image.Width, image.Height, rgb);
    }
}

/// <summary>
/// Radiance RGBE pictures (.hdr, .pic), the usual format for high-dynamic-range environment maps. Reads the header
/// (<c>#?RADIANCE</c> or <c>#?RGBE</c>, <c>FORMAT=32-bit_rle_rgbe</c>, an optional <c>EXPOSURE</c>), the size line
/// (<c>-Y h +X w</c>, top row first, or <c>+Y h +X w</c>, bottom row first), and scanlines that are flat, run-length
/// encoded the newer way (each channel's runs in turn) or the older way (a 1, 1, 1, n pixel repeating the last). Writes
/// flat scanlines. Each pixel is a shared exponent e and three mantissas m, worth m · 2^(e − 136).
/// </summary>
public static class Hdr
{
    /// <summary>Whether bytes look like a Radiance picture.</summary>
    public static bool IsHdr(ReadOnlySpan<byte> data) => data.StartsWith("#?"u8);

    /// <summary>Reads a Radiance picture. Throws <see cref="InvalidDataException"/> if it isn't one or is cut short.</summary>
    public static HdrImage Read(byte[] data)
    {
        int pos = 0;
        string Line()
        {
            int start = pos;
            while (pos < data.Length && data[pos] != (byte)'\n') pos++;
            if (pos >= data.Length) throw new InvalidDataException("The HDR header ends before the picture's size.");
            return Encoding.ASCII.GetString(data, start, pos++ - start).TrimEnd('\r');
        }
        string magic = Line();
        if (!magic.StartsWith("#?", StringComparison.Ordinal)) throw new InvalidDataException("Not a Radiance HDR picture: it doesn't start with #?.");
        double exposure = 1;
        for (string line = Line(); line.Length > 0; line = Line())
        {
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal) && line != "FORMAT=32-bit_rle_rgbe")
                throw new InvalidDataException($"The HDR picture is {line[7..]}; only 32-bit_rle_rgbe is read.");
            if (line.StartsWith("EXPOSURE=", StringComparison.Ordinal)
                && double.TryParse(line[9..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double e) && e > 0)
                exposure *= e;
        }
        var size = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (size.Length != 4 || size[2] != "+X" || size[0] is not ("-Y" or "+Y")
            || !int.TryParse(size[1], out int height) || !int.TryParse(size[3], out int width) || width <= 0 || height <= 0 || (long)width * height > 1L << 28)
            throw new InvalidDataException("The HDR picture's size line isn't \"-Y height +X width\".");
        bool bottomUp = size[0] == "+Y";

        var rgbe = new byte[width * 4];
        var rgb = new float[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            pos = Scanline(data, pos, rgbe, width);
            int row = bottomUp ? height - 1 - y : y;
            for (int x = 0; x < width; x++)
            {
                int e = rgbe[x * 4 + 3], o = (row * width + x) * 3;
                if (e == 0) continue;
                double f = Math.ScaleB(1.0, e - 136) / exposure;
                rgb[o] = (float)(rgbe[x * 4] * f);
                rgb[o + 1] = (float)(rgbe[x * 4 + 1] * f);
                rgb[o + 2] = (float)(rgbe[x * 4 + 2] * f);
            }
        }
        return new HdrImage(width, height, rgb);
    }

    private static InvalidDataException Short() => new("The HDR picture's pixels end early.");

    /// <summary>Reads one scanline of RGBE pixels from <paramref name="pos"/>; returns where the next begins.</summary>
    private static int Scanline(byte[] data, int pos, byte[] line, int width)
    {
        if (pos + 4 > data.Length) throw Short();
        bool newRle = width is >= 8 and < 32768 && data[pos] == 2 && data[pos + 1] == 2 && (data[pos + 2] & 0x80) == 0;
        if (newRle)
        {
            if ((data[pos + 2] << 8 | data[pos + 3]) != width) throw new InvalidDataException("An HDR scanline's length doesn't match the picture's width.");
            pos += 4;
            for (int c = 0; c < 4; c++)
                for (int x = 0; x < width;)
                {
                    if (pos >= data.Length) throw Short();
                    int count = data[pos++];
                    if (count > 128)
                    {
                        count -= 128;
                        if (x + count > width || pos >= data.Length) throw new InvalidDataException("An HDR scanline's run goes past its end.");
                        byte v = data[pos++];
                        for (int i = 0; i < count; i++) line[(x++) * 4 + c] = v;
                    }
                    else
                    {
                        if (count == 0 || x + count > width || pos + count > data.Length) throw new InvalidDataException("An HDR scanline's run goes past its end.");
                        for (int i = 0; i < count; i++) line[(x++) * 4 + c] = data[pos++];
                    }
                }
            return pos;
        }
        // Flat pixels, where a 1, 1, 1, n pixel repeats the one before n times (shifted up 8 bits per repeat marker in a row).
        int shift = 0;
        for (int x = 0; x < width;)
        {
            if (pos + 4 > data.Length) throw Short();
            byte r = data[pos], g = data[pos + 1], b = data[pos + 2], e = data[pos + 3];
            pos += 4;
            if (r == 1 && g == 1 && b == 1 && x > 0)
            {
                int count = e << shift;
                if (x + count > width) throw new InvalidDataException("An HDR scanline's run goes past its end.");
                for (int i = 0; i < count; i++, x++) Array.Copy(line, (x - 1) * 4, line, x * 4, 4);
                shift += 8;
                continue;
            }
            (line[x * 4], line[x * 4 + 1], line[x * 4 + 2], line[x * 4 + 3]) = (r, g, b, e);
            x++;
            shift = 0;
        }
        return pos;
    }

    /// <summary>A colour as an RGBE pixel: the brightest channel's exponent shared by all three.</summary>
    public static (byte R, byte G, byte B, byte E) ToRgbe(Vec3 c)
    {
        double v = Math.Max(c.X, Math.Max(c.Y, c.Z));
        if (!(v >= 1e-32)) return (0, 0, 0, 0);
        int e = Math.ILogB(v) + 1;
        double scale = Math.ScaleB(256.0, -e);
        static byte B(double x) => (byte)Math.Clamp((int)x, 0, 255);
        return (B(Math.Max(0, c.X) * scale), B(Math.Max(0, c.Y) * scale), B(Math.Max(0, c.Z) * scale), (byte)Math.Clamp(e + 128, 0, 255));
    }

    /// <summary>Writes a picture as a Radiance file with flat (uncompressed) scanlines, top row first.</summary>
    public static byte[] Write(HdrImage image)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {image.Height} +X {image.Width}\n"));
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var (r, g, b, e) = ToRgbe(image.Pixel(x, y));
                output.Write([r, g, b, e]);
            }
        return output.ToArray();
    }
}
