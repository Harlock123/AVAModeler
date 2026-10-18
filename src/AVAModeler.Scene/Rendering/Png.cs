using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AVAModeler.Scene;

/// <summary>Writes 8-bit RGBA images as PNG files, with no outside library.</summary>
public static class Png
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>Encodes width × height RGBA pixels (top row first) as a PNG file's bytes.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("A PNG needs at least one pixel.");
        if (rgba.Length != width * height * 4) throw new ArgumentException("The pixel buffer must hold width × height × 4 bytes.", nameof(rgba));
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 6; // colour type: RGBA
        WriteChunk(output, "IHDR", header);

        using var data = new MemoryStream();
        using (var z = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(rgba.Slice(y * stride, stride));
            }
        }
        WriteChunk(output, "IDAT", data.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static void Save(string path, int width, int height, ReadOnlySpan<byte> rgba) => File.WriteAllBytes(path, Encode(width, height, rgba));

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        s.Write(four);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        s.Write(four);
    }

    /// <summary>The CRC-32 (as PNG and zip use it) of some bytes.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes) => Crc32(bytes, 0xFFFFFFFF) ^ 0xFFFFFFFF;

    private static uint Crc32(ReadOnlySpan<byte> bytes, uint crc)
    {
        foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
