using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Core.Tests;

/// <summary>The Radiance RGBE reader and writer, on files built by hand byte by byte.</summary>
public class HdrTests
{
    private static byte[] File(string header, params byte[][] scanlines) =>
        [.. Encoding.ASCII.GetBytes(header), .. scanlines.SelectMany(s => s)];

    [Fact]
    public void Reads_flat_pixels_with_their_shared_exponent()
    {
        // Exponent 128 is 2^-8 per step: 128 → 0.5; 129 doubles it; exponent 0 is black.
        var data = File("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 3\n", [128, 64, 32, 128, 128, 64, 32, 129, 200, 100, 50, 0]);
        var img = Hdr.Read(data);
        Assert.Equal((3, 1), (img.Width, img.Height));
        Assert.Equal(new Vec3(0.5, 0.25, 0.125), img.Pixel(0, 0));
        Assert.Equal(new Vec3(1, 0.5, 0.25), img.Pixel(1, 0));
        Assert.Equal(Vec3.Zero, img.Pixel(2, 0));
    }

    [Fact]
    public void Reads_run_length_encoded_scanlines()
    {
        // Width 8, the newer encoding: 2, 2, then the width; then each channel as runs (a count over 128 repeats the
        // next byte count − 128 times) and literals (a count up to 128 copies that many bytes).
        byte[] line =
        [
            2, 2, 0, 8,
            128 + 8, 128,                       // red: 128 eight times
            4, 0, 64, 128, 255, 128 + 4, 32,    // green: 0, 64, 128, 255 literally, then 32 four times
            128 + 5, 0, 3, 1, 2, 3,             // blue: 0 five times, then 1, 2, 3
            128 + 8, 129,                       // exponent: 129 throughout (2^-7 a step)
        ];
        var img = Hdr.Read(File("#?RGBE\n\n-Y 2 +X 8\n", line, line));
        Assert.Equal((8, 2), (img.Width, img.Height));
        double step = 1.0 / 128;
        Assert.Equal(new Vec3(128 * step, 0, 0), img.Pixel(0, 1));
        Assert.Equal(new Vec3(128 * step, 255 * step, 0), img.Pixel(3, 0));
        Assert.Equal(new Vec3(128 * step, 32 * step, 0), img.Pixel(4, 0));
        Assert.Equal(new Vec3(128 * step, 32 * step, 3 * step), img.Pixel(7, 1));
    }

    [Fact]
    public void Reads_the_older_run_encoding_bottom_up_pictures_and_exposure()
    {
        // 1, 1, 1, n repeats the pixel before n times.
        var data = File("#?RADIANCE\nEXPOSURE=2\n\n+Y 2 +X 4\n", [128, 0, 0, 128, 1, 1, 1, 3], [0, 128, 0, 128, 0, 128, 0, 128, 0, 128, 0, 128, 0, 128, 0, 128]);
        var img = Hdr.Read(data);
        // +Y: the first scanline in the file is the bottom row. Exposure 2 means the values were doubled, so they're halved.
        Assert.Equal(new Vec3(0.25, 0, 0), img.Pixel(3, 1));
        Assert.Equal(new Vec3(0, 0.25, 0), img.Pixel(0, 0));
    }

    [Fact]
    public void Writes_what_it_reads()
    {
        var img = new HdrImage(3, 2, [0.5f, 0.25f, 0.125f, 4, 2, 1, 0, 0, 0, 100, 50, 25, 1e-3f, 0, 0, 1, 1, 1]);
        var back = Hdr.Read(Hdr.Write(img));
        for (int i = 0; i < img.Rgb.Length; i++) Assert.Equal(img.Rgb[i], back.Rgb[i], img.Rgb[i] * 0.01 + 1e-9);
        Assert.Equal(((byte)128, (byte)64, (byte)32, (byte)128), Hdr.ToRgbe(new Vec3(0.5, 0.25, 0.125)));
        Assert.True(Hdr.IsHdr(Hdr.Write(img)));
    }

    [Theory]
    [InlineData("P6\n1 1\n255\n")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_xyze\n\n-Y 1 +X 1\n")]
    [InlineData("#?RADIANCE\n\n-X 1 +Y 1\n")]
    [InlineData("#?RADIANCE\n\n-Y 2 +X 2\n\u0080\u0080\u0080\u0080")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n")]
    public void Refuses_what_isnt_a_whole_rgbe_picture(string text) =>
        Assert.Throws<InvalidDataException>(() => Hdr.Read(Encoding.Latin1.GetBytes(text)));

    [Fact]
    public void Refuses_a_run_past_the_end_of_its_scanline()
    {
        byte[] line = [2, 2, 0, 8, 128 + 9, 1];
        Assert.Throws<InvalidDataException>(() => Hdr.Read(File("#?RADIANCE\n\n-Y 1 +X 8\n", line)));
    }

    [Fact]
    public void An_8_bit_picture_becomes_floats_with_255_as_1()
    {
        var img = HdrImage.FromTexture(new TextureImage(1, 1, [255, 51, 0, 255]));
        Assert.Equal(new Vec3(1, 0.2, 0), img.Pixel(0, 0), new Vec3Tolerance(1e-6));
    }

    private sealed class Vec3Tolerance(double t) : IEqualityComparer<Vec3>
    {
        public bool Equals(Vec3 a, Vec3 b) => Vec3.Near(a, b, t);
        public int GetHashCode(Vec3 v) => 0;
    }
}
