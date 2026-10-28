using System.Buffers.Binary;
using System.IO.Compression;
using AVAModeler.Core;

namespace AVAModeler.Core.Tests;

public class TextureMappingTests
{
    private static void Near(Vec2 want, Vec2 got, double tol = 1e-9)
    {
        Assert.True(Math.Abs(want.X - got.X) <= tol && Math.Abs(want.Y - got.Y) <= tol, $"wanted {want}, got {got}");
    }

    [Fact]
    public void Planar_maps_show_the_image_as_the_view_down_the_axis_does()
    {
        var t = new TextureLayer { Projection = TextureProjection.Planar, Axis = Axis.Z, Center = new Vec3(1, 1, 0), Size = new Vec3(2, 4, 1) };
        Near(new Vec2(0.5, 0.5), TextureMapping.Project(t, new Vec3(1, 1, 7)));
        Near(new Vec2(1, 1), TextureMapping.Project(t, new Vec3(2, 3, 0)));
        Near(new Vec2(0, 0), TextureMapping.Project(t, new Vec3(0, -1, 0)));
        // Top: X across, Z up. Side: Z across, Y up.
        t.Axis = Axis.Y;
        t.Center = Vec3.Zero;
        t.Size = Vec3.One;
        Near(new Vec2(1, 0.75), TextureMapping.Project(t, new Vec3(0.5, 9, 0.25)));
        t.Axis = Axis.X;
        Near(new Vec2(0.75, 0), TextureMapping.Project(t, new Vec3(9, -0.5, 0.25)));
    }

    [Fact]
    public void Cylindrical_maps_wrap_round_the_axis_with_the_seam_behind()
    {
        var t = new TextureLayer { Projection = TextureProjection.Cylindrical, Axis = Axis.Y, Size = new Vec3(1, 2, 1) };
        // Straight toward the front (−Z) is the middle of the image; +X a quarter turn on; the back is the seam.
        Near(new Vec2(0.5, 0.5), TextureMapping.Project(t, new Vec3(0, 0, -1)));
        Near(new Vec2(0.75, 0.75), TextureMapping.Project(t, new Vec3(1, 0.5, 0)));
        Near(new Vec2(0.25, 0.5), TextureMapping.Project(t, new Vec3(-1, 0, 0)));
        Assert.True(TextureMapping.Project(t, new Vec3(0.001, 0, 1)).X > 0.99);
        Assert.True(TextureMapping.Project(t, new Vec3(-0.001, 0, 1)).X < 0.01);
        t.WidthWrap = 3;
        Near(new Vec2(2.25, 0.75), TextureMapping.Project(t, new Vec3(1, 0.5, 0)));
    }

    [Fact]
    public void Spherical_maps_run_pole_to_pole()
    {
        var t = new TextureLayer { Projection = TextureProjection.Spherical, Axis = Axis.Y, Center = new Vec3(0, 1, 0) };
        Near(new Vec2(0.5, 0.5), TextureMapping.Project(t, new Vec3(0, 1, -2)));
        Near(new Vec2(0.5, 1), TextureMapping.Project(t, new Vec3(0, 3, -1e-12)), 1e-6);
        Near(new Vec2(0.5, 0), TextureMapping.Project(t, new Vec3(0, -1, -1e-12)), 1e-6);
        Near(new Vec2(0.75, 0.75), TextureMapping.Project(t, new Vec3(1, 2, 0)));
    }

    [Fact]
    public void Cubic_maps_take_the_axis_each_face_looks_along()
    {
        var t = new TextureLayer { Projection = TextureProjection.Cubic };
        var p = new Vec3(0.25, 0.1, -0.4);
        Near(TextureMapping.Project(new TextureLayer { Axis = Axis.X }, p), TextureMapping.Project(t, p, new Vec3(-0.9, 0.1, 0.2)));
        Near(TextureMapping.Project(new TextureLayer { Axis = Axis.Y }, p), TextureMapping.Project(t, p, new Vec3(0, 1, 0)));
        Near(TextureMapping.Project(new TextureLayer { Axis = Axis.Z }, p), TextureMapping.Project(t, p, new Vec3(0.1, 0.2, -0.9)));
    }

    [Fact]
    public void A_face_meeting_a_pole_takes_its_neighbours_way_round()
    {
        // A triangle from the north pole down to two points either side of the seam at the back.
        Vec3[] tri = [new(0, 1, 0), new(0.1, 0.5, 0.8), new(-0.1, 0.5, 0.8)];
        Span<Vec2> uv = stackalloc Vec2[3];
        TextureMapping.ProjectFace(TextureProjection.Spherical, Axis.Y, Vec3.Zero, Vec3.One, 1, 1, tri, new Vec3(0, 1, 1), uv);
        // The two lower corners are joined across the seam, and the pole sits between them, not in the image's middle.
        Assert.True(uv[2].X > 1 && uv[1].X < 1, $"{uv[1]} {uv[2]}");
        Near(new Vec2((uv[1].X + uv[2].X) / 2, 1), uv[0], 1e-9);
        // Planar faces are only projected.
        TextureMapping.ProjectFace(TextureProjection.Planar, Axis.Z, Vec3.Zero, Vec3.One, 1, 1, tri, Vec3.UnitZ, uv);
        Near(new Vec2(0.5, 1.5), uv[0]);
    }

    [Fact]
    public void Automatic_sizing_fits_the_bounds_and_seams_join()
    {
        var (c, s) = TextureMapping.AutoSize(new Box3(new Vec3(-1, 0, 2), new Vec3(3, 2, 2)));
        Assert.Equal(new Vec3(1, 1, 2), c);
        // A flat side is given the largest side's size, not none.
        Assert.Equal(new Vec3(4, 2, 4), s);
        Span<Vec2> uvs = [new(0.95, 0), new(0.05, 0), new(0.9, 1)];
        TextureMapping.JoinSeam(uvs, new TextureLayer { Projection = TextureProjection.Cylindrical });
        Near(new Vec2(1.05, 0), uvs[1]);
        Near(new Vec2(0.95, 0), uvs[0]);
        // A face whose far corners lie on the seam itself keeps within the image, starting the next column at 0.
        Span<Vec2> onSeam = [new(1, 0), new(0.125, 0), new(1, 1)];
        TextureMapping.JoinSeam(onSeam, TextureProjection.Spherical, 1);
        Assert.Equal([0, 0.125, 0], onSeam.ToArray().Select(u => u.X));
        Span<Vec2> ending = [new(0.875, 0), new(0, 0), new(0, 1)];
        TextureMapping.JoinSeam(ending, TextureProjection.Spherical, 1);
        Assert.Equal([0.875, 1, 1], ending.ToArray().Select(u => u.X));
    }
}

public class TextureImageTests
{
    /// <summary>A 2 × 2 image: red, green on top; blue, white below.</summary>
    private static TextureImage Four() => new(2, 2, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]);

    [Fact]
    public void Sampling_reads_pixels_with_v_up_and_blends_between_them()
    {
        var img = Four();
        Assert.Equal(new Vec3(1, 0, 0), img.Sample(0.25, 0.75, TextureTiling.Repeat, TextureTiling.Repeat).Color);
        Assert.Equal(new Vec3(0, 0, 1), img.Sample(0.25, 0.25, TextureTiling.Repeat, TextureTiling.Repeat).Color);
        var mid = img.Sample(0.5, 0.75, TextureTiling.Edge, TextureTiling.Edge).Color;
        Assert.Equal(0.5, mid.X, 9);
        Assert.Equal(0.5, mid.Y, 9);
        // Without pixel blending, a pixel is a square.
        Assert.Equal(new Vec3(0, 1, 0), img.Sample(0.51, 0.75, TextureTiling.Edge, TextureTiling.Edge, blend: false).Color);
    }

    [Fact]
    public void Tilings_repeat_mirror_stretch_or_leave_nothing()
    {
        var img = Four();
        Assert.Equal(new Vec3(1, 0, 0), img.Sample(1.25, 0.75, TextureTiling.Repeat, TextureTiling.Repeat).Color);
        Assert.Equal(new Vec3(0, 1, 0), img.Sample(1.25, 0.75, TextureTiling.Mirror, TextureTiling.Repeat).Color);
        Assert.Equal(new Vec3(0, 1, 0), img.Sample(7.25, 0.75, TextureTiling.Edge, TextureTiling.Repeat).Color);
        Assert.Equal(0, img.Sample(1.25, 0.75, TextureTiling.Reset, TextureTiling.Repeat).Alpha);
        Assert.Equal(1, img.Sample(0.25, 0.75, TextureTiling.Reset, TextureTiling.Reset).Alpha);
        // At a reset edge the image fades out, keeping its colour rather than darkening.
        var edge = img.Sample(1.0, 0.75, TextureTiling.Reset, TextureTiling.Edge);
        Assert.Equal(0.5, edge.Alpha, 9);
        Assert.Equal(new Vec3(0, 1, 0), edge.Color);
        Assert.Equal(-1, TextureImage.Wrap(-1, 4, TextureTiling.Reset));
        Assert.Equal(3, TextureImage.Wrap(-1, 4, TextureTiling.Repeat));
        Assert.Equal(0, TextureImage.Wrap(-1, 4, TextureTiling.Mirror));
        Assert.Equal(3, TextureImage.Wrap(4, 4, TextureTiling.Mirror));
        Assert.Equal(0, TextureImage.Wrap(-9, 4, TextureTiling.Edge));
        Assert.Equal((Vec3.Zero, 0.0), img.Sample(double.NaN, 0, TextureTiling.Repeat, TextureTiling.Repeat));
    }
}

public class ImageFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-core-" + Guid.NewGuid().ToString("N"));

    public ImageFileTests()
    {
        Directory.CreateDirectory(_dir);
        TextureImages.Clear();
    }

    public void Dispose()
    {
        TextureImages.Clear();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>A PNG written by hand: the given colour type and depth, rows given as raw bytes (filter 0 each).</summary>
    internal static byte[] Png(int width, int height, int type, int depth, byte[][] rows, byte[]? palette = null, byte[]? trns = null, int filter = 0)
    {
        using var s = new MemoryStream();
        s.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string id, byte[] body)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
            s.Write(len);
            s.Write(System.Text.Encoding.ASCII.GetBytes(id));
            s.Write(body);
            s.Write(new byte[4]);
        }
        var head = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(head, width);
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(4), height);
        head[8] = (byte)depth;
        head[9] = (byte)type;
        Chunk("IHDR", head);
        if (palette != null) Chunk("PLTE", palette);
        if (trns != null) Chunk("tRNS", trns);
        using var data = new MemoryStream();
        using (var z = new ZLibStream(data, CompressionLevel.Fastest, true))
            foreach (var r in rows)
            {
                z.WriteByte((byte)filter);
                z.Write(r);
            }
        Chunk("IDAT", data.ToArray());
        Chunk("IEND", []);
        return s.ToArray();
    }

    [Fact]
    public void Pngs_of_every_common_kind_are_read()
    {
        var rgba = TextureImages.Decode(Png(2, 1, 6, 8, [[255, 0, 0, 255, 0, 0, 255, 128]]))!;
        Assert.Equal((2, 1), (rgba.Width, rgba.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 128 }, rgba.Rgba);
        var rgb = TextureImages.Decode(Png(1, 2, 2, 8, [[1, 2, 3], [4, 5, 6]]))!;
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, rgb.Rgba);
        // A 1-bit palette image, the second colour see-through.
        var pal = TextureImages.Decode(Png(3, 1, 3, 1, [[0b0100_0000]], [10, 20, 30, 40, 50, 60], [255, 0]))!;
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 0, 10, 20, 30, 255 }, pal.Rgba);
        var grey = TextureImages.Decode(Png(2, 1, 0, 16, [[0x80, 0, 0xFF, 0xFF]]))!;
        Assert.Equal(new byte[] { 128, 128, 128, 255, 255, 255, 255, 255 }, grey.Rgba);
        // Sub filtering: each byte adds the one a pixel before.
        var sub = TextureImages.Decode(Png(2, 1, 4, 8, [[100, 255, 10, 0]], filter: 1))!;
        Assert.Equal(new byte[] { 100, 100, 100, 255, 110, 110, 110, 255 }, sub.Rgba);
    }

    [Fact]
    public void Bmps_are_read_bottom_row_first()
    {
        // 2 × 2, 24-bit: rows padded to 4 bytes, stored bottom up, blue-green-red.
        var b = new List<byte>();
        void U2(int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        void U4(int v) { U2(v & 0xFFFF); U2(v >> 16); }
        b.AddRange("BM"u8.ToArray()); U4(70); U4(0); U4(54);
        U4(40); U4(2); U4(2); U2(1); U2(24); U4(0); U4(16); U4(0); U4(0); U4(0); U4(0);
        b.AddRange([255, 0, 0, 0, 255, 0, 0, 0]);     // bottom: blue, green
        b.AddRange([0, 0, 255, 255, 255, 255, 0, 0]); // top: red, white
        var img = TextureImages.Decode([.. b])!;
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 255, 0, 255 }, img.Rgba);
    }

    [Fact]
    public void Images_are_found_beside_the_object_and_missing_ones_become_a_checker()
    {
        string objects = Path.Combine(_dir, "Objects"), images = Path.Combine(_dir, "Images");
        Directory.CreateDirectory(objects);
        Directory.CreateDirectory(images);
        File.WriteAllBytes(Path.Combine(images, "red.png"), Png(1, 1, 2, 8, [[255, 0, 0]]));
        // As LightWave stored it (from the content folder), by file name, and from an Amiga volume.
        foreach (var path in new[] { "Images/red.png", "red.png", "Work:Images/red.png", Path.Combine(images, "red.png") })
        {
            var img = TextureImages.Load(path, objects);
            Assert.False(img.IsPlaceholder, path);
            Assert.Equal(new Vec3(1, 0, 0), img.Pixel(0, 0).Color);
        }
        var missing = TextureImages.Load("nowhere/gone.png", objects);
        Assert.True(missing.IsPlaceholder);
        Assert.Contains("gone.png", missing.Problem);
        File.WriteAllBytes(Path.Combine(objects, "junk.png"), [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2]);
        var junk = TextureImages.Load("junk.png", objects);
        Assert.True(junk.IsPlaceholder);
        Assert.Contains("Can't read", junk.Problem);
        var s = new Surface("Brick") { Textures = [new TextureLayer { Image = "nowhere/gone.png" }, new TextureLayer { Image = "Images/red.png" }] };
        Assert.Single(TextureImages.Problems([s], objects));
    }

    [Fact]
    public void Image_paths_are_kept_relative_to_the_object_where_they_can_be()
    {
        string obj = Path.Combine(_dir, "Objects", "ship.lwo");
        Assert.Equal("hull.png", TextureImages.RelativePath(Path.Combine(_dir, "Objects", "hull.png"), obj));
        Assert.Equal("../Images/hull.png", TextureImages.RelativePath(Path.Combine(_dir, "Images", "hull.png"), obj));
        string far = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "x", "hull.png");
        Assert.Equal(far, TextureImages.RelativePath(far, obj));
        Assert.Equal("already/relative.png", TextureImages.RelativePath("already/relative.png", obj));
        Assert.Equal(Path.Combine(_dir, "a.png"), TextureImages.RelativePath(Path.Combine(_dir, "a.png"), null));
    }

    [Fact]
    public void Surfaces_carry_textures_through_clones_and_signatures()
    {
        var s = new Surface("Wall") { Textures = [new TextureLayer { Image = "brick.png", Size = new Vec3(2, 1, 1) }] };
        var c = s.Clone();
        c.Textures[0].Image = "stone.png";
        Assert.Equal("brick.png", s.Textures[0].Image);
        Assert.NotEqual(s.Signature(), c.Signature());
        c.Textures[0].Image = "brick.png";
        Assert.Equal(s.Signature(), c.Signature());
        c.Textures[0].Size = new Vec3(2, 1, 1.5);
        Assert.NotEqual(s.Signature(), c.Signature());
    }
}
