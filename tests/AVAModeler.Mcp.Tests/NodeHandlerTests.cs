using System.Text.Json;
using AVAModeler.Core;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_surface_nodes: a surface's node graph for agents.</summary>
public class NodeHandlerTests
{
    private static JsonElement[] Nodes(params object[] nodes) => [.. nodes.Select(n => JsonSerializer.SerializeToElement(n))];

    [Fact]
    public async Task An_agent_wires_a_node_graph_into_a_surface_as_one_undo_step()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Surface = "Rock" }), Cancel);
        int steps = b.Doc.Undo.Count;
        var r = await Parse(b.Tools.SetSurfaceNodes("Rock", Nodes(
            new { id = 1, type = "output", inputs = new { Color = "3.Color", Bump = "2.Value" }, settings = new { bump_height = 0.02 } },
            new { id = 2, type = "noise", settings = new { scale = 0.25, octaves = 5 } },
            new { id = 3, type = "gradient", inputs = new { Value = "2.Value" }, stops = new object[] { new object[] { 0, new[] { 60, 50, 40 } }, new object[] { 1, new[] { 200, 190, 170 } } } },
            new { id = 4, type = "texture", option = "marble", settings = new { size = 0.5, vein_spacing = 0.3 } },
            new { id = 5, type = "mix", option = "multiply", inputs = new { A = new[] { 255, 0, 0 }, Factor = 0.25 } }), cancel: Cancel));
        Assert.Equal(steps + 1, b.Doc.Undo.Count);
        var g = b.Doc.Surfaces.Single(s => s.Name == "Rock").Nodes!;
        Assert.Equal(5, g.Nodes.Count);
        Assert.Equal(new NodeLink(3, "Color"), g.Output!.Links["Color"]);
        Assert.Equal(0.02, g.Output.Number("Bump Height"));
        Assert.Equal(5, g.Find(2)!.Number("Octaves"));
        Assert.Equal(("Marble", 0.3), (g.Find(4)!.Option, g.Find(4)!.Number("Vein Spacing")));
        Assert.Equal(("Multiply", new Rgb(255, 0, 0), 0.25), (g.Find(5)!.Option, g.Find(5)!.ColorOf("A"), g.Find(5)!.Number("Factor")));
        // Read back as it was written.
        var nodes = r["surface"]!["nodes"]!.AsArray();
        Assert.Equal("3.Color", (string?)nodes[0]!["inputs"]!["Color"]);
        Assert.Equal("gradient", (string?)nodes[2]!["type"]);
        Assert.Equal(2, nodes[2]!["stops"]!.AsArray().Count);
        // Mistakes change nothing.
        Assert.Contains("no surface \"Moss\"", await Error(b.Tools.SetSurfaceNodes("Moss", Nodes(new { id = 1, type = "output" }), cancel: Cancel)));
        Assert.Contains("type output", await Error(b.Tools.SetSurfaceNodes("Rock", Nodes(new { id = 2, type = "noise" }), cancel: Cancel)));
        Assert.Contains("teapot", await Error(b.Tools.SetSurfaceNodes("Rock", Nodes(new { id = 1, type = "teapot" }), cancel: Cancel)));
        Assert.Contains("loop", await Error(b.Tools.SetSurfaceNodes("Rock", Nodes(
            new { id = 1, type = "output", inputs = new { Diffuse = "2.Value" } },
            new { id = 2, type = "math", inputs = new { A = "3.Value" } },
            new { id = 3, type = "math", inputs = new { A = "2.Value" } }), cancel: Cancel)));
        Assert.Contains("no setting \"wobble\"", await Error(b.Tools.SetSurfaceNodes("Rock", Nodes(
            new { id = 1, type = "output" }, new { id = 2, type = "noise", settings = new { wobble = 1 } }), cancel: Cancel)));
        Assert.Contains("<id>.<output>", await Error(b.Tools.SetSurfaceNodes("Rock", Nodes(new { id = 1, type = "output", inputs = new { Color = "red" } }), cancel: Cancel)));
        Assert.Equal(5, b.Doc.Surfaces.Single(s => s.Name == "Rock").Nodes!.Nodes.Count);
        Assert.Equal(steps + 1, b.Doc.Undo.Count);
        // Taken away, and undone.
        await b.Tools.SetSurfaceNodes("Rock", off: true, cancel: Cancel);
        Assert.Null(b.Doc.Surfaces.Single(s => s.Name == "Rock").Nodes);
        await b.Tools.Undo(cancel: Cancel);
        Assert.NotNull(b.Doc.Surfaces.Single(s => s.Name == "Rock").Nodes);
    }
}
