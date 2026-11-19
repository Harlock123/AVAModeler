using System.Text.Json.Nodes;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>add_deformer, set_deformer and remove_deformer, the amount channel, and their mistakes.</summary>
public class DeformerHandlerTests
{
    [Fact]
    public async Task A_tall_box_bends_on_a_keyed_amount_and_a_lattice_lifts_its_top()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 0.2, 2, 0.2 }, Center = new[] { 0, 1.0, 0 }, SegmentsY = 8 }), Cancel);
        await b.Tools.AddObject(name: "Reed", cancel: Cancel);
        var added = await Parse(b.Tools.AddDeformer("Reed", "bend", 90, cancel: Cancel));
        Assert.Equal("deformer.1.amount", (string)added["added"]!);
        var deformer = (JsonObject)((JsonArray)added["deformers"]!)[0]!;
        Assert.Equal("bend", (string)deformer["kind"]!);
        Assert.Equal("x", (string)deformer["toward"]!);
        var frame = b.Scene.Evaluate(1).Objects[0];
        Assert.True(frame.Mesh.Bounds().Max.X > 0.5, "the top curls over toward +X");

        await Parse(b.Tools.SetKey("Reed", "deformer.1.amount", 1, 0, cancel: Cancel));
        await Parse(b.Tools.SetKey("Reed", "deformer.1.amount", 20, 90, cancel: Cancel));
        Assert.True(b.Scene.Evaluate(1).Objects[0].Mesh.Bounds().Max.X < 0.11);
        Assert.Contains("is keyed", await Error(b.Tools.SetDeformerTool("Reed", 1, amount: 10, cancel: Cancel)));

        var lattice = await Parse(b.Tools.AddDeformer("Reed", "lattice", divisions: [2, 2, 2], offsets: [[0, 1, 0, 0, 0.5, 0], [1, 1, 0, 0, 0.5, 0], [0, 1, 1, 0, 0.5, 0], [1, 1, 1, 0, 0.5, 0]], cancel: Cancel));
        Assert.Equal(4, (int)((JsonArray)lattice["deformers"]!)[1]!["moved_points"]!);
        await Parse(b.Tools.SetDeformerTool("Reed", 1, enabled: false, cancel: Cancel));
        Assert.True(b.Scene.Evaluate(1).Objects[0].Mesh.Bounds().Max.Y > 2.3);

        Assert.Contains("isn't in a 2 × 2 × 2", await Error(b.Tools.SetDeformerTool("Reed", 2, offsets: [[2, 0, 0, 1, 0, 0]], cancel: Cancel)));
        Assert.Contains("toward axis must differ", await Error(b.Tools.AddDeformer("Reed", "shear", axis: "x", toward: "x", cancel: Cancel)));
        Assert.Contains("no deformer 5", await Error(b.Tools.RemoveDeformer("Reed", 5, Cancel)));
        Assert.Contains("kind", await Error(b.Tools.AddDeformer("Reed", "melt", cancel: Cancel)));

        await Parse(b.Tools.RemoveDeformer("Reed", 1, Cancel));
        Assert.Single(b.Scene.Objects.First().Deformers);
        await b.Tools.UndoScene(Cancel);
        Assert.Equal(2, b.Scene.Objects.First().Deformers.Count);
    }
}
