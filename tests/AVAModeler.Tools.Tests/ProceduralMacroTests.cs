using AVAModeler.Core;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

public class ProceduralMacroTests
{
    private static string Samples(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", .. parts]);

    [Fact]
    public void Macros_lay_procedural_textures_with_proctex_or_texture()
    {
        var cmd = new Commands(new Document());
        Macros.Run("""
            tool("Box", { Size = {4, 0, 4}, Surface = "Floor" })
            proctex("Floor", "checkerboard", { Color = {20, 30, 40}, Size = {0.5, 0.5, 0.5} })
            texture("Floor", "fractal noise", { Channel = "bump", Amplitude = 0.2, Frequencies = 5, SmallPower = 0.6, Size = {0.1, 0.1, 0.1} })
            proctex("Floor", "Marble", { Channel = "specular", Value = 0.8, Axis = "x", VeinSpacing = 0.3, vein_sharpness = 7, Falloff = {0.5, 0, 0}, Negative = true })
            proctex("Floor", "checkerboard", { Color = {200, 0, 0} })
            proctex("Floor", "checkerboard", { NewLayer = true, Opacity = 0.5, Blend = "multiply", World = true })
            """, cmd);
        var floor = cmd.Document.Surfaces[cmd.Document.SurfaceIndex("Floor")];
        Assert.Equal(["Checkerboard", "Checkerboard", "Fractal Noise", "Marble"], floor.Textures.Select(t => t.Procedural));
        var checks = floor.Textures[0];
        // The second call changed the first layer of that kind: its colour, keeping its size.
        Assert.Equal((new Rgb(200, 0, 0), new Vec3(0.5, 0.5, 0.5)), (checks.TextureColor, checks.Size));
        var more = floor.Textures[1];
        Assert.Equal((0.5, TextureBlend.Multiply, true), (more.Opacity, more.Blend, more.WorldCoordinates));
        var bump = floor.Textures[2];
        Assert.Equal((TextureChannel.Bump, 0.2, 5.0, 0.6), (bump.Channel, bump.Amplitude, Procedurals.Get(bump, "Frequencies"), Procedurals.Get(bump, "Small Power")));
        var marble = floor.Textures[3];
        Assert.Equal((TextureChannel.Specular, 0.8, Axis.X, true), (marble.Channel, marble.Value, marble.Axis, marble.Negative));
        Assert.Equal((0.3, 7.0, new Vec3(0.5, 0, 0)), (Procedurals.Get(marble, "Vein Spacing"), Procedurals.Get(marble, "Vein Sharpness"), marble.Falloff));
        Assert.Equal("Procedural texture", cmd.Document.Undo.UndoLabel);

        // Mistakes say what's wrong and change nothing.
        string before = floor.Signature();
        Assert.Contains("the kinds are", Assert.Throws<MacroException>(() => Macros.Run("proctex('Floor', 'plaid')", cmd)).Message);
        Assert.Contains("VeinSpacing", Assert.Throws<MacroException>(() => Macros.Run("proctex('Floor', 'marble', { Wavelength = 1 })", cmd)).Message);
        Assert.Contains("from 1 to 8", Assert.Throws<MacroException>(() => Macros.Run("proctex('Floor', 'marble', { Frequencies = 40 })", cmd)).Message);
        Assert.Contains("image textures", Assert.Throws<MacroException>(() => Macros.Run("proctex('Floor', 'grid', { Projection = 'cubic' })", cmd)).Message);
        Assert.Contains("0 to 255", Assert.Throws<MacroException>(() => Macros.Run("proctex('Floor', 'grid', { Color = {300, 0, 0} })", cmd)).Message);
        Assert.Equal(before, cmd.Document.Surfaces[cmd.Document.SurfaceIndex("Floor")].Signature());
        // An image with a procedural's name but an extension is still an image.
        Macros.Run("texture('Floor', 'checkerboard.png')", cmd);
        Assert.Contains(cmd.Document.Surfaces[cmd.Document.SurfaceIndex("Floor")].Textures, t => t.Image == "checkerboard.png" && t.IsImage);

        // Each call was one undo step.
        cmd.Undo();
        cmd.Undo();
        Assert.Equal(["Checkerboard", "Fractal Noise", "Marble"], cmd.Document.Surfaces[cmd.Document.SurfaceIndex("Floor")].Textures.Select(t => t.Procedural));
        Assert.Contains(Macros.Reference, r => r.Signature.StartsWith("proctex(") && r.Does.Contains("\"fractal_noise\""));
    }

    [Fact]
    public void The_procedurals_sample_builds_its_scene()
    {
        var cmd = new Commands(new Document()) { BaseDirectory = Samples("scripts") };
        Macros.Run(File.ReadAllText(Samples("scripts", "procedurals.lua")), cmd);
        var doc = cmd.Document;
        Assert.Equal(["Default", "Floor", "Marble", "Wood", "Rock"], doc.Surfaces.Select(s => s.Name));
        string Kinds(string surface) => string.Join(",", doc.Surfaces[doc.SurfaceIndex(surface)].Textures.Select(t => $"{t.Channel}:{t.Procedural}"));
        Assert.Equal("Color:Checkerboard", Kinds("Floor"));
        Assert.Equal("Color:Marble", Kinds("Marble"));
        Assert.Equal("Color:Wood", Kinds("Wood"));
        Assert.Equal("Color:Fractal Noise,Bump:Fractal Noise", Kinds("Rock"));
        Assert.All(doc.Surfaces.SelectMany(s => s.Textures), t => Assert.True(t.IsProcedural));
        Assert.Equal(4, doc.Layers.Count(l => !l.Mesh.IsEmpty));
    }
}
