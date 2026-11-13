using System.Text.Json.Nodes;
using AVAModeler.Core;
using ModelContextProtocol.Protocol;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>compare_to_reference: outlines against a reference picture, placed by a backdrop or fitted.</summary>
public class CompareHandlerTests
{
    /// <summary>A white picture with a black rectangle: a subject <paramref name="w"/> × <paramref name="h"/> pixels in the middle.</summary>
    internal static string Silhouette(Bench b, string name, int w, int h, int size = 100)
    {
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool inside = Math.Abs(x + 0.5 - size / 2.0) < w / 2.0 && Math.Abs(y + 0.5 - size / 2.0) < h / 2.0;
                byte v = inside ? (byte)0 : (byte)255;
                int i = (y * size + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = v;
                rgba[i + 3] = 255;
            }
        string path = b.Folder.File(name);
        Png.Save(path, size, size, rgba);
        return name;
    }

    private static JsonObject Json(CallToolResult r) => JsonNode.Parse(r.Content.OfType<TextContentBlock>().Single().Text)!.AsObject();

    [Fact]
    public async Task A_box_matching_its_backdrop_overlaps_it_and_a_short_one_is_told_where()
    {
        using var b = new Bench();
        // The picture covers 2 m across in the front view: a 1 m × 0.6 m subject is 50 × 30 pixels.
        string pic = Silhouette(b, "front.png", 50, 30);
        await Parse(b.Tools.SetBackdrop("front", pic, center: [0, 0], width: 2, cancel: Cancel));
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 1.0, 0.6, 0.5 } }), Cancel);
        var match = await b.Tools.CompareToReference("front", cancel: Cancel);
        var r = Json(match);
        Assert.True((double)r["overlap"]! > 0.95, r.ToJsonString());
        Assert.False((bool)r["fitted"]!);
        Assert.NotNull(match.Content.OfType<ImageContentBlock>().Single());

        // Half as tall, standing on its base: the reference's top half is missing.
        await b.Tools.Undo(Cancel);
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 1.0, 0.3, 0.5 }, Center = new[] { 0.0, -0.15, 0 } }), Cancel);
        r = Json(await b.Tools.CompareToReference("front", cancel: Cancel));
        Assert.InRange((double)r["overlap"]!, 0.4, 0.6);
        Assert.Equal("top", (string?)r["missing_where"]);
        Assert.Contains("short", (string?)r["advice"]);
    }

    [Fact]
    public async Task Without_a_backdrop_the_outline_is_fitted_and_its_shape_compared()
    {
        using var b = new Bench();
        string pic = Silhouette(b, "side.png", 60, 30);
        // Twice as wide as tall from the side (Z across, Y up), at another size and place: fitted, it matches.
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 0.2, 1, 2 }, Center = new[] { 5.0, 5, 5 } }), Cancel);
        var r = Json(await b.Tools.CompareToReference("side", "side.png", cancel: Cancel));
        Assert.True((bool)r["fitted"]!);
        Assert.True((double)r["overlap"]! > 0.9, r.ToJsonString());
        Assert.Equal(2, (double)r["width_to_height"]!["model"]!, 1);
        Assert.Contains("set a front backdrop", (await Assert.ThrowsAnyAsync<Exception>(() => b.Tools.CompareToReference("front", cancel: Cancel))).Message);
    }
}
