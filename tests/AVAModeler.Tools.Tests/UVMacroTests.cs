using AVAModeler.Core;
using AVAModeler.Tools;
using SkiaSharp;

namespace AVAModeler.Tools.Tests;

public class UVMacroTests
{
    private static string Samples(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", .. parts]);

    [Fact]
    public void Macros_make_choose_and_set_uvs()
    {
        var cmd = new Commands(new Document());
        Macros.Run("""
            tool("Box", { Size = {2, 1, 1} })
            makeuvs("Front", "planar", "z")
            selmode("polygons")
            selbox(-2, 0.49, -2, 2, 2, 2)
            makeuvs("Top", "planar", "y")
            print(table.concat(uvmaps(), ","))
            print(uvmap())
            uvmap("Front")
            local u, v = getuv(1)
            print(u .. " " .. v)
            setuv(1, 0.25, 0.75)
            u, v = getuv(1)
            print(u .. " " .. v)
            print(tostring(getuv(1) ~= nil))
            """, cmd);
        Assert.Equal(["Front,Top", "Top", "0 0", "0.25 0.75", "true"], cmd.Output);
        var mesh = cmd.Document.Current.Mesh;
        Assert.Equal(4, mesh.FindUVMap("Top")!.Points.Count);
        Assert.Equal("Set UV", cmd.Document.Undo.UndoLabel);
        cmd.Undo();
        Assert.Equal(new Vec2(0, 0), cmd.GetUV(0));
    }

    [Fact]
    public void Macros_texture_surfaces_and_take_textures_off()
    {
        var cmd = new Commands(new Document());
        Macros.Run("""
            tool("Box", { Size = {2, 4, 1}, Center = {1, 2, 0}, Surface = "Wall" })
            texture("Wall", "brick.png", { Projection = "cylindrical", Axis = "y", Fit = true, WidthWrap = 3, WidthTiling = "mirror", Opacity = 0.5, Blend = "multiply" })
            texture("Wall", "dents.png", { Channel = "bump", Amplitude = 2, Center = {1, 2, 3}, Size = {4, 5, 6} })
            texture("Wall", "stone.png")
            print(notexture("Wall", "bump"))
            """, cmd);
        var wall = cmd.Document.Surfaces[cmd.Document.SurfaceIndex("Wall")];
        var colour = Assert.Single(wall.Textures);
        Assert.Equal("stone.png", colour.Image);
        Assert.Equal((TextureProjection.Cylindrical, Axis.Y, 3.0, TextureTiling.Mirror, 0.5, TextureBlend.Multiply),
            (colour.Projection, colour.Axis, colour.WidthWrap, colour.WidthTiling, colour.Opacity, colour.Blend));
        // Fitted to the wall's bounds.
        Assert.Equal(new Vec3(1, 2, 0), colour.Center);
        Assert.Equal(new Vec3(2, 4, 1), colour.Size);
        Assert.Equal("1", cmd.Output.Single());
        Assert.Contains("no option", Assert.Throws<MacroException>(() => Macros.Run("texture('Wall', 'x.png', { Wobble = 1 })", cmd)).Message);
        Assert.Contains("surface", Assert.Throws<MacroException>(() => Macros.Run("notexture('Nothing')", cmd)).Message);
        cmd.Undo();
        Assert.Equal(2, cmd.Document.Surfaces[cmd.Document.SurfaceIndex("Wall")].Textures.Count);
    }

    [Fact]
    public void The_still_life_sample_textures_three_ways()
    {
        var cmd = new Commands(new Document()) { BaseDirectory = Samples("scripts") };
        Macros.Run(File.ReadAllText(Samples("scripts", "still-life.lua")), cmd);
        var doc = cmd.Document;
        Assert.StartsWith("Still life", cmd.Output.Single());
        Assert.Equal("Planet", doc.ActiveUVMap);
        var crate = doc.Surfaces[doc.SurfaceIndex("Crate")];
        Assert.Equal([TextureChannel.Color, TextureChannel.Bump], crate.Textures.Select(t => t.Channel));
        foreach (var s in doc.Surfaces.Where(s => s.Textures.Count > 0))
            foreach (var t in s.Textures)
            {
                Assert.True(Path.IsPathRooted(t.Image), t.Image);
                Assert.False(TextureImages.Load(t.Image).IsPlaceholder, t.Image);
            }
        // Every face of the planet is mapped, and the map wraps round it once.
        var mesh = doc.Current.Mesh;
        int planet = doc.SurfaceIndex("Planet");
        var globe = mesh.Polygons.Where(p => p.Surface == planet).ToList();
        Assert.Equal(32 * 16, globe.Count);
        Assert.All(globe, p => Assert.True(mesh.IsMapped("Planet", p)));
        Assert.InRange(mesh.UVBounds("Planet").Size.X, 1, 1.05);
    }

    [Fact]
    public void Jpegs_and_other_formats_come_in_through_skia()
    {
        SkiaImages.Register();
        using var bitmap = new SKBitmap(8, 4);
        bitmap.Erase(new SKColor(200, 40, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var decoded = TextureImages.Decode(jpeg.ToArray());
        Assert.NotNull(decoded);
        Assert.Equal((8, 4), (decoded.Width, decoded.Height));
        var (c, a) = decoded.Pixel(3, 2);
        Assert.InRange(c.X * 255, 190, 210);
        Assert.InRange(c.Y * 255, 30, 50);
        Assert.Equal(1, a);
        Assert.Null(SkiaImages.Decode([1, 2, 3, 4]));
        Assert.Null(TextureImages.Decode([1, 2, 3, 4]));
    }
}
