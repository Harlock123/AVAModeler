using System.Text.Json.Nodes;
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
}
