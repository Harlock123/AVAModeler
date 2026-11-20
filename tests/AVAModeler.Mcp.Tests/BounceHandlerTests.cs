using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_scene's global illumination and bake_lighting's light maps, inside the working folder.</summary>
public class BounceHandlerTests
{
    [Fact]
    public async Task Bounced_light_is_set_reported_and_undone_and_a_light_map_is_baked()
    {
        using var b = new Bench();
        var on = await Parse(b.Tools.SetScene(gi: true, gi_bounces: 2, gi_samples: 16, cancel: Cancel));
        var gi = (JsonObject)on["global_illumination"]!;
        Assert.True((bool)gi["on"]!);
        Assert.Equal(2, (int)gi["bounces"]!);
        Assert.Contains("1 to 4", await Error(b.Tools.SetScene(gi_bounces: 7, cancel: Cancel)));
        await b.Tools.UndoScene(Cancel);
        Assert.False(b.Scene.GlobalIllumination.On);

        await b.Tools.RunTool("Box", null, Cancel);
        await b.Tools.AddObject(name: "Crate", cancel: Cancel);
        Assert.Contains("no UV map", await Error(b.Tools.BakeLighting("Crate", size: 32, cancel: Cancel)));
        await Parse(b.Tools.UnwrapUVs(cancel: Cancel));
        await b.Tools.AddLight("point", "Lamp", position: [0, 2, -1], cancel: Cancel);
        var baked = await Parse(b.Tools.BakeLighting("Crate", size: 64, samples: 4, cancel: Cancel));
        string written = (string)baked["written"]!;
        Assert.StartsWith(b.Folder.Path, written);
        Assert.True(File.Exists(written));
        Assert.True((int)baked["pixels_covered"]! > 64 * 64 / 4);
        File.WriteAllBytes(Path.Combine(b.Folder.Path, "mine.png"), [1]);
        Assert.Contains("already exists", await Error(b.Tools.BakeLighting("Crate", file: "mine.png", size: 32, cancel: Cancel)));
        // One it wrote itself it may write again.
        await Parse(b.Tools.BakeLighting("Crate", size: 32, samples: 1, cancel: Cancel));
        Assert.Contains("outside the working folder", await Error(b.Tools.BakeLighting("Crate", file: "../outside.png", size: 32, cancel: Cancel)));
        Assert.Contains(".png", await Error(b.Tools.BakeLighting("Crate", file: "light.jpg", size: 32, cancel: Cancel)));
        Assert.Contains("16 to 4096", await Error(b.Tools.BakeLighting("Crate", size: 8, cancel: Cancel)));
    }

    [Fact]
    public async Task A_baked_light_map_is_laid_on_the_surface_as_one_undo_step_and_baking_again_ignores_it()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        await Parse(b.Tools.UnwrapUVs(cancel: Cancel));
        await b.Tools.Save("crate.lwo", cancel: Cancel);
        await b.Tools.AddObject(file: "crate.lwo", name: "Crate", cancel: Cancel);
        await b.Tools.AddLight("point", "Lamp", position: [0, 2, -1], cancel: Cancel);
        var crate = (ObjectItem)b.Scene.Find("Crate")!;
        var before = crate.Document!;
        static IEnumerable<TextureLayer> Maps(Surface s) => s.Textures.Where(t => t.Projection == TextureProjection.UV && t.IsImage);

        var first = await Parse(b.Tools.BakeLighting("Crate", size: 32, samples: 2, cancel: Cancel));
        var hooked = (JsonArray)first["hooked_up"]!;
        string name = (string)Assert.Single(hooked)!["name"]!;
        Assert.Equal(1.0, (double)hooked[0]!["diffuse_was"]!);
        Assert.NotSame(before, crate.Document);
        var s = crate.Document!.Surfaces.Single(x => x.Name == name);
        Assert.Equal((0.0, 1.0), (s.Diffuse, s.Luminosity));
        var layer = Assert.Single(Maps(s));
        Assert.Equal(("Crate_light.png", TextureChannel.Color, TextureBlend.Normal), (layer.Image, layer.Channel, layer.Blend));
        Assert.Equal((string)first["uv_map"]!, layer.UVMap);
        // The object had a changed copy: the one before is as it was, and undo_scene puts it back.
        Assert.Empty(Maps(before.Surfaces.Single(x => x.Name == name)));
        await b.Tools.UndoScene(Cancel);
        Assert.Same(before, crate.Document);
        await b.Tools.RedoScene(Cancel);
        Assert.NotSame(before, crate.Document);

        // Baking again to the same file: the old map comes off for the bake, so the light is the same, and it's on once.
        var again = await Parse(b.Tools.BakeLighting("Crate", size: 32, samples: 2, cancel: Cancel));
        Assert.Equal((double)first["brightest"]!, (double)again["brightest"]!, 3);
        s = crate.Document!.Surfaces.Single(x => x.Name == name);
        Assert.Single(Maps(s));
        Assert.Equal(1.0, (double)again["hooked_up"]![0]!["diffuse_was"]!);

        // Light only: multiplied into the colour. Without hook_up, nothing in the scene changes.
        await Parse(b.Tools.BakeLighting("Crate", file: "light_only.png", size: 32, samples: 1, with_color: false, cancel: Cancel));
        Assert.Contains(Maps(crate.Document!.Surfaces.Single(x => x.Name == name)), t => t.Image == "light_only.png" && t.Blend == TextureBlend.Multiply);
        var doc = crate.Document;
        int steps = b.Layout.History.Count;
        var plain = await Parse(b.Tools.BakeLighting("Crate", file: "plain.png", size: 32, samples: 1, hook_up: false, cancel: Cancel));
        Assert.Null(plain["hooked_up"]);
        Assert.Same(doc, crate.Document);
        Assert.Equal(steps, b.Layout.History.Count);

        // The Modeler's own object in the scene: laid on as a Modeler undo step.
        await b.Tools.AddObject(name: "Live", cancel: Cancel);
        var live = await Parse(b.Tools.BakeLighting("Live", file: "live.png", size: 32, samples: 1, cancel: Cancel));
        Assert.Contains("Modeler", (string)live["note_hook"]!);
        var ls = b.Doc.Surfaces.Single(x => x.Name == (string)live["hooked_up"]![0]!["name"]!);
        Assert.Equal((0.0, 1.0), (ls.Diffuse, ls.Luminosity));
        await b.Tools.Undo(Cancel);
        ls = b.Doc.Surfaces.Single(x => x.Name == ls.Name);
        Assert.Equal((1.0, 0.0), (ls.Diffuse, ls.Luminosity));
        Assert.Empty(Maps(ls));
    }
}
