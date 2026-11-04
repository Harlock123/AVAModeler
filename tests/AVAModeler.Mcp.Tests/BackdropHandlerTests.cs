using System.Text.Json.Nodes;
using AVAModeler.Core;
using ModelContextProtocol.Protocol;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_backdrop: reference images behind the flat views for agents, seen in render_view.</summary>
public class BackdropHandlerTests
{
    /// <summary>A 40 × 20 picture: red on the left half, blue on the right.</summary>
    private static void Picture(string path)
    {
        var rgba = new byte[40 * 20 * 4];
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
            {
                int i = (y * 40 + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = x < 20 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255);
            }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Png.Encode(40, 20, rgba));
    }

    [Fact]
    public async Task Set_backdrop_places_changes_and_clears_a_views_image_without_touching_the_object()
    {
        using var b = new Bench();
        Picture(b.Folder.File("blueprints/side.png"));
        await b.Tools.RunTool("Box", null, Cancel);
        int steps = b.Doc.Undo.Count;
        b.Doc.Modified = false;
        var r = await Parse(b.Tools.SetBackdrop("side", "blueprints/side.png", center: [0.5, 1], width: 4, opacity: 0.7, cancel: Cancel));
        var side = r["backdrops"]!["side"]!;
        Assert.Equal(b.Folder.File("blueprints/side.png"), (string?)side["image"]);
        Assert.Equal("z, y", (string?)side["axes"]);
        Assert.Equal([0.5, 1.0], side["center"]!.AsArray().Select(v => (double)v!));
        // 4 m wide, so 2 m high from the picture's shape.
        Assert.Equal((4.0, 2.0, true), ((double)side["width"]!, (double)side["height"]!, (bool)side["height_from_image"]!));
        Assert.Equal([40, 20], side["pixels"]!.AsArray().Select(v => (int)v!));
        Assert.Equal(steps, b.Doc.Undo.Count);
        Assert.False(b.Doc.Modified);

        // Without an image it changes the settings; fit sizes it round the foreground; height stretches it.
        await b.Tools.SetBackdrop("side", invert: true, contrast: 1.5, height: 3, cancel: Cancel);
        Assert.Equal((true, 1.5, 3.0), (b.Doc.Backdrops.Side!.Invert, b.Doc.Backdrops.Side.Contrast, b.Doc.Backdrops.Side.Height!.Value));
        await b.Tools.SetBackdrop("side", height: 0, fit: true, cancel: Cancel);
        Assert.Null(b.Doc.Backdrops.Side!.Height);
        Assert.Equal(new Vec2(0, 0), b.Doc.Backdrops.Side.Center);
        Assert.Equal(2.2, b.Doc.Backdrops.Side.Width, 9); // the 1 m box is 1 m high: 2:1 picture 2.2 m wide
        await b.Tools.SetBackdrop("side", pixel_size: 0.01, cancel: Cancel);
        Assert.Equal(0.4, b.Doc.Backdrops.Side!.Width, 9);

        // get_document lists it; clear takes it away.
        var doc = await Parse(b.Tools.GetDocument(Cancel));
        Assert.Equal(0.4, (double)doc["backdrops"]!["side"]!["width"]!, 9);
        var cleared = await Parse(b.Tools.SetBackdrop("side", clear: true, cancel: Cancel));
        Assert.True((bool)cleared["cleared"]!);
        Assert.True(b.Doc.Backdrops.IsEmpty);
        Assert.Null((await Parse(b.Tools.GetDocument(Cancel)))["backdrops"]);
    }

    [Fact]
    public async Task Set_backdrops_mistakes_say_what_to_do_and_change_nothing()
    {
        using var b = new Bench();
        Picture(b.Folder.File("front.png"));
        File.WriteAllText(b.Folder.File("notes.txt"), "not a picture");
        await b.Tools.SetBackdrop("front", "front.png", cancel: Cancel);
        var before = b.Doc.Backdrops;
        Assert.Contains("view must be one of top, front, side", await Error(b.Tools.SetBackdrop("back", "front.png", cancel: Cancel)));
        Assert.Contains("outside the working folder", await Error(b.Tools.SetBackdrop("front", "../elsewhere.png", cancel: Cancel)));
        Assert.Contains("is a picture", await Error(b.Tools.SetBackdrop("front", "notes.txt", cancel: Cancel)));
        Assert.Contains("There's no image missing.png", await Error(b.Tools.SetBackdrop("front", "missing.png", cancel: Cancel)));
        Assert.Contains("opacity must be from 0 to 1", await Error(b.Tools.SetBackdrop("front", opacity: 1.5, cancel: Cancel)));
        Assert.Contains("width must be more than 0", await Error(b.Tools.SetBackdrop("front", "front.png", width: -2, cancel: Cancel)));
        Assert.Contains("[x, z]", await Error(b.Tools.SetBackdrop("top", "front.png", center: [1, 2, 3], cancel: Cancel)));
        Assert.Contains("top view has no backdrop yet", await Error(b.Tools.SetBackdrop("top", width: 2, cancel: Cancel)));
        Assert.Contains("nothing to fit", await Error(b.Tools.SetBackdrop("front", fit: true, cancel: Cancel)));
        Assert.Same(before, b.Doc.Backdrops);
    }

    [Fact]
    public async Task Render_view_shows_the_backdrop_under_the_wireframe_and_frames_it()
    {
        using var b = new Bench();
        Picture(b.Folder.File("front.png"));
        // A picture 8 m wide well off to the right of a small box: the framing takes both in.
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 0.5, 0.5, 0.5 } }), Cancel);
        await b.Tools.SetBackdrop("front", "front.png", center: [6, 0], width: 8, opacity: 1, cancel: Cancel);
        var result = await b.Tools.RenderView("front", 400, 200, cancel: Cancel);
        var about = result.Content.OfType<TextContentBlock>().Single().Text;
        Assert.Contains("over the backdrop front.png", about);
        var image = TextureImages.Decode(Pngs.Of(result))!;
        int red = 0, blue = 0;
        for (int i = 0; i < image.Rgba.Length; i += 4)
        {
            if (image.Rgba[i] > 240 && image.Rgba[i + 2] < 20) red++;
            if (image.Rgba[i + 2] > 240 && image.Rgba[i] < 20) blue++;
        }
        Assert.True(red > 4000 && blue > 4000, $"{red} red and {blue} blue pixels");
        // The perspective and the side views don't show it.
        var persp = await b.Tools.RenderView("side", 200, 200, cancel: Cancel);
        Assert.DoesNotContain("backdrop", persp.Content.OfType<TextContentBlock>().Single().Text);
    }

    [Fact]
    public async Task An_agent_finds_the_tool_and_models_over_a_backdrop_through_a_client()
    {
        using var b = new Bench();
        Picture(b.Folder.File("sketch.png"));
        await using var s = await InProcess.Start(b.Host);
        var tool = (await s.Client.ListToolsAsync(cancellationToken: Cancel)).Single(t => t.Name == "set_backdrop");
        Assert.Contains("top: [x, z]", tool.Description);
        var set = await s.Client.Json("set_backdrop", new { view = "top", image = "sketch.png", center = new[] { 0.0, 0.0 }, width = 2 });
        Assert.Equal("top", (string?)set["view"]);
        var doc = await s.Client.Json("get_document");
        Assert.Equal("x, z", (string?)doc["backdrops"]!["top"]!["axes"]);
        var view = await s.Client.Call("render_view", new { view = "top", width = 200, height = 200 });
        Assert.True(view.IsError != true);
        Assert.Contains("over the backdrop sketch.png", Text(view));
        Assert.Contains("set_backdrop", ModelerServer.Instructions);
    }
}
