using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>Backdrops from macros and the command API: backdrop() and nobackdrop(), and the rocket sample.</summary>
public class BackdropMacroTests
{
    private static string Samples(params string[] parts) =>
        Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", .. parts]));

    [Fact]
    public void Backdrop_places_an_image_behind_a_view_by_its_options_without_an_undo_step()
    {
        var cmd = new Commands(new Document()) { BaseDirectory = Samples("scripts") };
        Macros.Run("""
            backdrop("side", "../images/rocket-side.png", { Center = {0, 1.05}, Size = 1.2, Opacity = 0.4, Contrast = 1.5, Brightness = -0.1, Invert = true })
            backdrop("TOP", "../images/rocket-top.png", { center = {0.1, -0.2}, Height = 2 })
            """, cmd);
        var doc = cmd.Document;
        var side = doc.Backdrops.Side!;
        // The relative path was found from the macro's folder and kept whole.
        Assert.Equal(Samples("images", "rocket-side.png"), side.Image);
        Assert.Equal((new Vec2(0, 1.05), 1.2, (double?)null), (side.Center, side.Width, side.Height));
        Assert.Equal((0.4, 1.5, -0.1, true, true), (side.Opacity, side.Contrast, side.Brightness, side.Invert, side.Visible));
        Assert.Equal((new Vec2(0.1, -0.2), 2.0), (doc.Backdrops.Top!.Center, doc.Backdrops.Top.Height!.Value));
        Assert.Null(doc.Backdrops.Front);
        Assert.Equal(0, doc.Undo.Count);
        Assert.False(doc.Modified);

        // nil as the image changes the settings only; nobackdrop takes them away.
        Macros.Run("""backdrop("side", nil, { Visible = false, Opacity = 0.9 })""", cmd);
        Assert.Equal((false, 0.9, Samples("images", "rocket-side.png")), (doc.Backdrops.Side!.Visible, doc.Backdrops.Side.Opacity, doc.Backdrops.Side.Image));
        Macros.Run("""print(nobackdrop("side")); print(nobackdrop())""", cmd);
        Assert.Equal(["1", "1"], cmd.Output);
        Assert.True(doc.Backdrops.IsEmpty);
    }

    [Fact]
    public void Fit_and_pixel_size_place_it_and_mistakes_change_nothing()
    {
        var cmd = new Commands(new Document()) { BaseDirectory = Samples("scripts") };
        cmd.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 1, 1 }, ["Center"] = new[] { 1.0, 0.5, 0 } });
        Macros.Run("""backdrop("front", "../images/rocket-top.png", { Fit = true })""", cmd);
        var front = cmd.Document.Backdrops.Front!;
        // The square picture round the 2 × 1 box's front, 5% spare each side.
        Assert.Equal(new Vec2(1, 0.5), front.Center);
        Assert.Equal(2.2, front.Width, 9);
        // 360 pixels at 5 mm.
        Macros.Run("""backdrop("front", nil, { PixelSize = 0.005 })""", cmd);
        Assert.Equal(1.8, cmd.Document.Backdrops.Front!.Width, 9);

        var before = cmd.Document.Backdrops;
        Assert.Contains("no \"back\" view", Assert.Throws<MacroException>(() => Macros.Run("""backdrop("back", "x.png")""", cmd)).Message);
        Assert.Contains("Opacity", Assert.Throws<MacroException>(() => Macros.Run("""backdrop("front", nil, { Opacity = 3 })""", cmd)).Message);
        Assert.Contains("no option \"Zoom\"", Assert.Throws<MacroException>(() => Macros.Run("""backdrop("front", "x.png", { Zoom = 2 })""", cmd)).Message);
        Assert.Contains("Center is {a, b}", Assert.Throws<MacroException>(() => Macros.Run("""backdrop("front", "x.png", { Center = {1, 2, 3} })""", cmd)).Message);
        Assert.Contains("has no backdrop", Assert.Throws<MacroException>(() => Macros.Run("""backdrop("side", nil, { Size = 2 })""", cmd)).Message);
        Assert.Same(before, cmd.Document.Backdrops);
    }

    [Fact]
    public void A_missing_image_is_said_and_the_backdrops_stay_through_new_and_load()
    {
        var cmd = new Commands(new Document());
        Macros.Run("""local note = backdrop("front", "no-such-blueprint.png", { Size = 3 }); print(note ~= nil)""", cmd);
        Assert.StartsWith("Can't find the image no-such-blueprint.png", cmd.Output[0]);
        Assert.Equal("true", cmd.Output[1]);
        var set = cmd.Document.Backdrops;
        cmd.New();
        Assert.Same(set, cmd.Document.Backdrops);
        string file = Path.Combine(Path.GetTempPath(), $"avamodeler-backdrop-{Guid.NewGuid():N}.lwo");
        try
        {
            cmd.Run("Box");
            cmd.Save(file);
            cmd.Load(file);
            Assert.Same(set, cmd.Document.Backdrops);
        }
        finally { File.Delete(file); }
        Assert.Contains(Macros.Reference, r => r.Signature.StartsWith("backdrop(view, image", StringComparison.Ordinal) && r.Does.Contains("PixelSize"));
        Assert.Contains(Macros.Reference, r => r.Signature == "nobackdrop(view)");
    }

    [Fact]
    public void The_rocket_sample_sets_up_three_blueprints_and_lathes_a_closed_body_over_them()
    {
        var cmd = new Commands(new Document()) { BaseDirectory = Samples("scripts") };
        Macros.Run(File.ReadAllText(Samples("scripts", "rocket-blueprints.lua")), cmd);
        var b = cmd.Document.Backdrops;
        Assert.Equal([BackdropView.Top, BackdropView.Front, BackdropView.Side], b.All().Select(x => x.View));
        Assert.Empty(BackdropMapping.Problems(b, null));
        foreach (var (view, backdrop) in b.All())
        {
            var image = TextureImages.Load(backdrop.Image);
            Assert.False(image.IsPlaceholder);
            // 300 pixels a metre, so the drawings are 1.2 m wide.
            Assert.Equal(360, image.Width);
            Assert.Equal(backdrop.Width * 300, image.Width, 6);
        }
        // The elevations run from 0.1 m under the ground to 2.2 m up: the body's 2.1 m fits inside them.
        var side = BackdropMapping.Bounds(BackdropView.Side, b.Side!, 360, 690);
        Assert.Equal((-0.1, 2.2), (Math.Round(side.Min.Y, 6), Math.Round(side.Max.Y, 6)));
        var body = cmd.Document.Current.Mesh;
        var box = body.Bounds();
        Assert.Equal((0.0, 2.1, 0.15), (Math.Round(box.Min.Y, 6), Math.Round(box.Max.Y, 6), Math.Round(box.Max.X, 3)));
        Assert.True(body.Polygons.Count > 200);
        Assert.StartsWith("Rocket blueprints:", cmd.Output[^1]);
        // Lathed the LightWave way: it faces outward (its open mouth at the nozzle is the only hole).
        var r = MeshCheck.Document(cmd.Document);
        Assert.Equal(1, r.Holes);
        Assert.Equal(0, r.InconsistentEdges);
    }
}
