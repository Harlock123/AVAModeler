using AVAModeler.Core;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>bake_maps: a normal map from a detailed background onto a low foreground, inside the working folder.</summary>
public class BakeHandlerTests
{
    [Fact]
    public async Task A_low_ball_gets_the_detailed_balls_normals_and_files_stay_in_the_working_folder()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Ball", Bench.Args(new { Sides = 12, Segments = 6 }), Cancel);
        await Parse(b.Tools.MakeUVs("UV", projection: "atlas", cancel: Cancel));
        await b.Tools.SetLayers([2], cancel: Cancel);
        await b.Tools.RunTool("Ball", Bench.Args(new { Sides = 48, Segments = 24 }), Cancel);
        await b.Tools.SetLayers([1], [2], cancel: Cancel);
        var r = await Parse(b.Tools.BakeMaps("normal", "maps/ball_normal.png", size: 128, cancel: Cancel));
        string written = (string)r["written"]!;
        Assert.StartsWith(b.Folder.Path, written);
        Assert.True(File.Exists(written));
        Assert.True((int)r["pixels_covered"]! > 1000);
        Assert.Contains(b.Doc.Surfaces.SelectMany(s => s.Textures), t => t.Channel == TextureChannel.Normal);
        Assert.Contains("outside the working folder", await Error(b.Tools.BakeMaps("normal", "../escape.png", size: 32, cancel: Cancel)));
        Assert.Contains("outside the working folder", await Error(b.Tools.RunTool("Bake Maps", Bench.Args(new { Size = 32, File = "/tmp/escape.png" }), Cancel)));
        Assert.Contains("kind must be", await Error(b.Tools.BakeMaps("shiny", cancel: Cancel)));
    }
}
