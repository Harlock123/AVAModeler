using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>The scene's images, compositing and objects' dissolve through load_image, remove_image, set_compositing, set_dissolve and set_key.</summary>
public class CompositingHandlerTests
{
    private static async Task<Bench> WithBox()
    {
        var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 2.0, 2, 2 } }), Cancel);
        await b.Tools.AddObject(cancel: Cancel);
        return b;
    }

    private static string Png(Bench b, string name, byte r = 10, byte g = 20, byte bl = 30)
    {
        string path = b.Folder.File(name);
        if (Path.GetDirectoryName(path) is { } dir) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Core.Png.Encode(2, 2, [r, g, bl, 255, r, g, bl, 255, r, g, bl, 255, r, g, bl, 255]));
        return path;
    }

    [Fact]
    public async Task Load_image_adds_stills_and_sequences_to_the_list_as_undo_steps_and_get_scene_shows_them()
    {
        using var b = await WithBox();
        string bg = Png(b, "bg.png");
        Png(b, "frames/sky0001.png");
        Png(b, "frames/sky0002.png");
        var list = await Parse(b.Tools.LoadImage("bg.png", cancel: Cancel));
        Assert.Equal(bg, (string?)list["images"]![0]!["file"]);
        list = await Parse(b.Tools.LoadImage("frames/sky0001.png", sequence: true, offset: 1, loop: true, cancel: Cancel));
        var seq = list["images"]![1]!;
        Assert.True((bool)seq["sequence"]!);
        Assert.Equal([1, 2], seq["frames_on_disk"]!.AsArray().Select(x => (int)x!));
        Assert.Equal(new SceneImage { Path = b.Folder.File("frames/sky0001.png"), Sequence = true, Offset = 1, Loop = true }, b.Scene.Images[1]);
        Assert.Equal("Agent: loaded an image", (string?)list["undo_scene"]!["next_undo"]);

        // Loading it again changes its settings, not the list's length.
        await b.Tools.LoadImage("frames/sky0001.png", offset: 0, cancel: Cancel);
        Assert.Equal(2, b.Scene.Images.Count);
        Assert.Equal(0, b.Scene.Images[1].Offset);
        Assert.True(b.Scene.Images[1].Loop);

        var scene = await Parse(b.Tools.GetScene(cancel: Cancel));
        Assert.Equal(2, scene["images"]!.AsArray().Count);
        Assert.Null(scene["compositing"]);
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        Assert.Single(b.Scene.Images);
    }

    [Fact]
    public async Task Bad_images_are_refused_and_change_nothing()
    {
        using var b = await WithBox();
        Png(b, "flat.png");
        Assert.Contains("outside the working folder", await Error(b.Tools.LoadImage("../elsewhere.png", cancel: Cancel)));
        Assert.Contains("There's no image", await Error(b.Tools.LoadImage("missing.png", cancel: Cancel)));
        Assert.Contains("no number before its extension", await Error(b.Tools.LoadImage("flat.png", sequence: true, cancel: Cancel)));
        Assert.Contains("no numbered files", await Error(b.Tools.LoadImage("none0001.png", sequence: true, cancel: Cancel)));
        Assert.Contains("is a picture", await Error(b.Tools.LoadImage("notes.txt", cancel: Cancel)));
        Assert.Contains("load_image first", await Error(b.Tools.SetCompositing(background: "flat.png", cancel: Cancel)));
        Assert.Contains("load_image first", await Error(b.Tools.RemoveImage("flat.png", cancel: Cancel)));
        Assert.Empty(b.Scene.Images);
        Assert.False(b.Scene.Compositing.On);
        Assert.Equal(1, b.Layout.History.Count);
    }

    [Fact]
    public async Task Set_compositing_sets_the_background_and_a_keyed_foreground_from_the_list()
    {
        using var b = await WithBox();
        string bg = Png(b, "bg.png", 0, 255, 0);
        string fg = Png(b, "fg.png");
        await b.Tools.LoadImage("bg.png", cancel: Cancel);
        await b.Tools.LoadImage("fg.png", cancel: Cancel);
        var r = await Parse(b.Tools.SetCompositing(background: "bg.png", foreground: "2", key: "color", key_low: [0, 200, 0], key_high: [51, 255, 51], foreground_dissolve: 0.25, cancel: Cancel));
        var c = b.Scene.Compositing;
        Assert.Equal((bg, fg, ForegroundKey.Color, 0.25), (c.Background, c.Foreground, c.Key, c.ForegroundDissolve));
        Assert.Equal(new Vec3(0.2, 1, 0.2), c.KeyHigh);
        Assert.Equal("color", (string?)r["compositing"]!["key"]);
        Assert.Equal([51, 255, 51], r["compositing"]!["key_high"]!.AsArray().Select(x => (int)x!));
        var scene = await Parse(b.Tools.GetScene(cancel: Cancel));
        Assert.Equal(bg, (string?)scene["compositing"]!["background"]);

        // The green background shows behind the box in the render, with the foreground (not keyed out) three quarters over it.
        var picture = Pngs.Of(await b.Tools.RenderScene(width: 32, height: 24, samples: 1, cancel: Cancel));
        var img = TextureImages.Decode(picture)!;
        Assert.Equal((8, 79, 22), ((int)img.Rgba[0], (int)img.Rgba[1], (int)img.Rgba[2]));

        // An image compositing uses can't be removed; turning it off first lets it go.
        Assert.Contains("set_compositing it to \"\" first", await Error(b.Tools.RemoveImage("bg.png", cancel: Cancel)));
        Assert.Contains("must not be above", await Error(b.Tools.SetCompositing(key_low: [255, 255, 255], cancel: Cancel)));
        Assert.Contains("key must be one of", await Error(b.Tools.SetCompositing(key: "luma", cancel: Cancel)));
        await b.Tools.SetCompositing(background: "", cancel: Cancel);
        var list = await Parse(b.Tools.RemoveImage("bg.png", cancel: Cancel));
        Assert.Single(list["images"]!.AsArray());
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        Assert.Equal(bg, b.Scene.Compositing.Background);
        Assert.Equal(2, b.Scene.Images.Count);
    }

    [Fact]
    public async Task Set_dissolve_and_set_key_animate_an_objects_dissolve()
    {
        using var b = await WithBox();
        string box = b.Scene.Objects.Single().Name;
        var r = await Parse(b.Tools.SetDissolve(box, 0.5, key: false, cancel: Cancel));
        Assert.Equal(0.5, (double)r["dissolve"]!);
        Assert.Equal(0.5, b.Scene.Objects.Single().DissolveAt(10));
        Assert.Equal("Agent: set a dissolve", (string?)r["undo_scene"]!["next_undo"]);

        await b.Tools.SetKey(box, "dissolve", 1, 0, cancel: Cancel);
        await b.Tools.SetKey(box, "dissolve", 30, 1, cancel: Cancel);
        var item = (await Parse(b.Tools.GetScene(frame: 30, cancel: Cancel)))["items"]!.AsArray().First(i => (string?)i!["name"] == box)!;
        Assert.Equal(1, (double)item["dissolve"]!);
        Assert.Contains("dissolve", item["keyed"]!.AsArray().Select(x => (string?)x));
        var keys = await Parse(b.Tools.GetKeys(box, "dissolve", cancel: Cancel));
        Assert.Equal(2, keys["channels"]![0]!["keys"]!.AsArray().Count);

        Assert.Contains("0 (solid) to 1", await Error(b.Tools.SetDissolve(box, 2, cancel: Cancel)));
        Assert.Contains("not an object", await Error(b.Tools.SetDissolve("Light", 0.5, cancel: Cancel)));
        // A solid object doesn't mention it.
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        await b.Tools.UndoScene(Cancel);
        var solid = (await Parse(b.Tools.GetScene(cancel: Cancel)))["items"]!.AsArray().First(i => (string?)i!["name"] == box)!;
        Assert.Null(solid["dissolve"]);
    }
}
