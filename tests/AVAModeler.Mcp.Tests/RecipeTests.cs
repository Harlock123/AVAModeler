using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Scene;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>
/// The saved recipes (MCP prompts): each one is fetched through a client and followed call by call through the server,
/// as an agent would, so a wrong tool name, argument or value in a recipe fails here; then the result is checked.
/// </summary>
public class RecipeTests
{
    private static readonly string[] Names = ["lathed_vase", "table", "chess_pawn", "three_point_lighting", "turntable_render", "bouncing_ball", "carbon_atom", "morph_smile", "wave_flag"];

    /// <summary>Each recipe with arguments that keep it quick (small pictures, few frames).</summary>
    public static TheoryData<string, string> Cases => new()
    {
        { "lathed_vase", """{"color": "200, 80, 60"}""" },
        { "table", """{"width": "1.5", "color": "wood"}""" },
        { "chess_pawn", "{}" },
        { "three_point_lighting", """{"width": "64"}""" },
        { "turntable_render", """{"frames": "4", "width": "48", "movie": "none"}""" },
        { "bouncing_ball", """{"frames": "8", "width": "48"}""" },
        { "carbon_atom", """{"width": "96"}""" },
        { "morph_smile", """{"frames": "10", "width": "96"}""" },
        { "wave_flag", """{"frames": "6", "width": "48"}""" },
    };

    /// <summary>A recipe's tool calls, read from its text as an agent reads them.</summary>
    internal static List<(string Tool, Dictionary<string, object?> Args)> Calls(string text) =>
        text.Split('\n').Select(l => Regex.Replace(l.Trim(), @"^\d+\.\s+", "")).Where(l => l.StartsWith(Recipes.CallMarker, StringComparison.Ordinal)).Select(l =>
        {
            string call = l[Recipes.CallMarker.Length..];
            int space = call.IndexOf(' ');
            var args = JsonDocument.Parse(call[(space + 1)..]).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            return (call[..space], args);
        }).ToList();

    [Fact]
    public async Task The_server_lists_the_recipes_with_their_arguments()
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        var prompts = await s.Client.ListPromptsAsync(cancellationToken: Cancel);
        Assert.Equal(Names.Order(), prompts.Select(p => p.Name).Order());
        Assert.All(prompts, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        var vase = prompts.Single(p => p.Name == "lathed_vase").ProtocolPrompt;
        Assert.Equal(["height", "radius", "color", "sides"], vase.Arguments!.Select(a => a.Name));
        Assert.All(vase.Arguments!, a => Assert.NotEqual(true, a.Required));
        // Arguments are text; a bad one is a clear error.
        var e = await Assert.ThrowsAsync<McpProtocolException>(async () => await s.Client.GetPromptAsync("lathed_vase", new Dictionary<string, object?> { ["height"] = "tall" }, cancellationToken: Cancel));
        Assert.Contains("height must be a number", e.Message);
        e = await Assert.ThrowsAsync<McpProtocolException>(async () => await s.Client.GetPromptAsync("table", new Dictionary<string, object?> { ["color"] = "plaid" }, cancellationToken: Cancel));
        Assert.Contains("A colour is r, g, b", e.Message);
        // Every call names a real tool.
        var tools = (await s.Client.ListToolsAsync(cancellationToken: Cancel)).Select(t => t.Name).ToHashSet();
        foreach (var name in Names)
        {
            var text = PromptText(await s.Client.GetPromptAsync(name, cancellationToken: Cancel));
            var calls = Calls(text);
            Assert.True(calls.Count >= 3, name);
            Assert.All(calls, c => Assert.Contains(c.Tool, tools));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_recipe_runs_through_the_server(string name, string arguments)
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        // The scene recipes stage what's been modeled: a box.
        if (name is "three_point_lighting" or "turntable_render")
            await s.Client.Json("run_tool", new { name = "Box", @params = new Dictionary<string, object> { ["Size"] = new[] { 1.0, 1, 1 } } });
        if (name == "three_point_lighting") await s.Client.Json("add_object");

        var args = JsonDocument.Parse(arguments).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetString());
        var text = PromptText(await s.Client.GetPromptAsync(name, args, cancellationToken: Cancel));
        byte[]? lastPicture = null;
        int step = 0;
        foreach (var (tool, callArgs) in Calls(text))
        {
            step++;
            var result = await s.Client.CallToolAsync(tool, callArgs, cancellationToken: Cancel);
            Assert.True(result.IsError != true, $"{name} step {step}, {tool}, failed: {Text(result)}");
            if (result.Content.OfType<ImageContentBlock>().FirstOrDefault() is { } image) lastPicture = image.DecodedData.ToArray();
        }
        Assert.NotNull(lastPicture);
        Assert.True(Pngs.Inspect(lastPicture).Colours > 8, $"{name}'s last picture is blank.");
        Check(name, b);
    }

    private static string PromptText(GetPromptResult r) => Assert.IsType<TextContentBlock>(Assert.Single(r.Messages).Content).Text;

    /// <summary>What each recipe should have made.</summary>
    private static void Check(string name, Bench b)
    {
        var doc = b.Doc;
        Box3 Bounds(int layer) => doc.Layers[layer].Mesh.Bounds();
        ObjectItem Obj(string n) => Assert.IsType<ObjectItem>(b.Scene.Items.Single(i => i.Name == n));
        switch (name)
        {
            case "lathed_vase":
                Near(new Vec3(0.24, 0.4, 0.24), Bounds(0).Size, 0.01);
                Assert.Equal(new Rgb(200, 80, 60), doc.Surfaces.Single(x => x.Name == "Vase").Color);
                Assert.True(doc.Layers[0].Mesh.Polygons.Count > 32 * 10);
                break;
            case "table":
                Near(new Vec3(1.5, 0.75, 0.8), Bounds(0).Size, 0.01);
                // Four legs and a top.
                Assert.InRange(doc.Layers[0].Mesh.Polygons.Count, 4 * 16 * 5, 4 * 16 * 7 + 6);
                break;
            case "chess_pawn":
                Assert.Equal(0.06, Bounds(0).Size.Y, 3);
                Assert.Equal(2 * 0.3 * 0.06 / 0.88, Bounds(0).Size.X, 3);
                break;
            case "three_point_lighting":
                Assert.Equal(["Camera", "Modeler object", "Key", "Fill", "Rim"], b.Scene.Items.Select(i => i.Name));
                break;
            case "turntable_render":
                Assert.Equal(270, b.Scene.Items.Single(i => i.Name == "Turntable").RotationAt(4).X, 6);
                Assert.Same(b.Scene.Items.Single(i => i.Name == "Turntable"), Obj("Subject").Parent);
                Assert.All(new[] { 1, 2, 3, 4 }, f => Assert.True(File.Exists(b.Folder.File($"turntable/turntable{f:0000}.png"))));
                break;
            case "bouncing_ball":
                var ball = Obj("Ball");
                Assert.Equal(0, ball.PositionAt(4).Y, 6);
                Assert.Equal(2, ball.PositionAt(8).Y, 6);
                Assert.Equal(0.65, ball.ScaleAt(4).Y, 6);
                Assert.True(File.Exists(b.Folder.File("bounce/bounce0008.png")));
                break;
            case "carbon_atom":
                Assert.InRange(doc.Layers[0].Mesh.Polygons.Count, 12 * 32 * 14, 12 * 32 * 16);
                foreach (var orbit in new[] { "Orbit 1", "Orbit 2", "Orbit 3" }) Assert.Equal(new[] { "Spin", "Tilt" }, Ancestors(Obj(orbit)));
                Assert.Equal(120, b.Scene.LastFrame);
                break;
            case "morph_smile":
                var smiley = Obj("Smiley");
                var smile = Assert.Single(smiley.Morphs);
                Assert.Equal("Smile", smile.Name);
                Assert.Empty(smiley.MorphProblems());
                Assert.Equal(1, smile.Weight.Evaluate(10), 6);
                // The mouth's ends rise 0.09 at the full smile, and nothing else moves.
                var straight = smiley.DeformedMesh(1).Points;
                var smiling = smiley.DeformedMesh(10).Points;
                var lift = straight.Zip(smiling, (a, c) => c.Y - a.Y).ToList();
                Assert.Equal(0.09, lift.Max(), 6);
                Assert.Equal(0, lift.Min(), 6);
                Assert.True(lift.Count(x => x > 1e-9) >= 12);
                break;
            case "wave_flag":
                var flag = Obj("Flag");
                var wave = Assert.Single(flag.Displacements);
                Assert.Equal(Axis.Z, wave.Direction);
                Assert.Equal(Axis.X, wave.Along);
                Assert.True(flag.DeformedMesh(6).Bounds().Size.Z > 0.1);
                Assert.True(File.Exists(b.Folder.File("flag/flag0005.png")));
                break;
        }
    }

    private static IEnumerable<string> Ancestors(SceneItem item)
    {
        for (var p = item.Parent; p != null; p = p.Parent) yield return p.Name.Split(' ')[0];
    }

    private static void Near(Vec3 expected, Vec3 actual, double tolerance) =>
        Assert.True((expected - actual).Length <= tolerance, $"Expected {expected}, got {actual}.");
}
