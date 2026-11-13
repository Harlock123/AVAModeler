using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_weights, get_weights and auto_weights, and bones that follow the maps in Layout.</summary>
public class WeightHandlerTests
{
    [Fact]
    public async Task Auto_weights_rig_a_limb_whose_bones_follow_their_maps()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 0.4, 2, 0.4 }, Center = new[] { 0.0, 1, 0 }, SegmentsY = 4 }), Cancel);
        int n = b.Doc.Current.Mesh.Points.Count;
        await b.Tools.AddPoints([[0, 0, 0], [0, 1, 0], [0, 2, 0]], Cancel);
        await b.Tools.AddPolygons([[n, n + 1]], "Skelegon_Arm", Cancel);
        await b.Tools.AddPolygons([[n + 1, n + 2]], "Skelegon_Hand", Cancel);
        var auto = await Parse(b.Tools.AutoWeights(cancel: Cancel));
        var maps = auto["layers"]![0]!["maps"]!.AsArray();
        Assert.Equal(["Arm", "Hand"], maps.Select(m => (string?)m!["name"]));
        Assert.Equal(["Arm", "Hand"], (await Parse(b.Tools.GetDocument(cancel: Cancel)))["weight_maps"]!.AsArray().Select(x => (string?)x));

        // The top ring of the box belongs to the hand alone: make it so, and read it back.
        var m = b.Doc.Current.Mesh;
        var top = Enumerable.Range(0, n).Where(i => m.Points[i].Y > 1.99).ToArray();
        await Parse(b.Tools.SetWeights("Hand", top, [1], Cancel));
        await Parse(b.Tools.SetWeights("Arm", top, [0], Cancel));
        var read = await Parse(b.Tools.GetWeights("Hand", Cancel));
        Assert.Contains(read["weights"]!.AsArray(), w => (int)w![0]! == top[0] && (double)w[1]! == 1);
        Assert.Contains("0 to 1", await Error(b.Tools.SetWeights("Hand", [0], [3], Cancel)));
        Assert.Contains("no weight map \"Leg\"", await Error(b.Tools.GetWeights("Leg", Cancel)));

        // In Layout the bones take the maps of their names.
        await b.Tools.AddObject(name: "Limb", cancel: Cancel);
        var r = await Parse(b.Tools.SkelegonsToBones("Limb", Cancel));
        Assert.Equal(["Arm", "Hand"], r["bones"]!.AsArray().Select(x => (string?)x!["weight_map"]));
        var limb = (ObjectItem)b.Scene.Find("Limb")!;
        await b.Tools.SetTransform("Hand", 1, rotation: [0, 90, 0], key: false, cancel: Cancel);
        var bent = limb.DeformedMesh(1).Points;
        // The bottom ring (all Arm) doesn't move; the top ring swings round the hand's base.
        foreach (int i in Enumerable.Range(0, n).Where(i => m.Points[i].Y < 0.01)) Assert.True((bent[i] - m.Points[i]).Length < 1e-6);
        foreach (int i in top) Assert.True(bent[i].Y < 1.5, $"point {i} at {bent[i]} didn't swing");
        // set_item can take a bone off its map.
        await Parse(b.Tools.SetItem("Hand", weight_map: "", cancel: Cancel));
        Assert.Null(b.Scene.Bones.Single(x => x.Name == "Hand").WeightMap);
    }

    [Fact]
    public async Task Set_weights_with_symmetry_weights_the_mirror_too()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 2.0, 2, 2 } }), Cancel);
        await b.Tools.SetSymmetry("x", Cancel);
        var m = b.Doc.Current.Mesh;
        int right = Enumerable.Range(0, m.Points.Count).First(i => m.Points[i].X > 0);
        int left = Symmetry.Pairs(m, Axis.X)[right];
        await Parse(b.Tools.SetWeights("Arm.L", [0], [0], Cancel));
        await Parse(b.Tools.SetWeights("Arm.R", [right], [0.5], Cancel));
        Assert.Equal(0.5, b.Doc.Current.Mesh.FindPointMap("Arm.L")!.Weight(left), 6);
        Assert.Equal("Arm.R", b.Doc.CurrentWeightMap);
        await Parse(b.Tools.SetWeights("Spine", [right], [0.25], Cancel));
        Assert.Equal(0.25, b.Doc.Current.Mesh.FindPointMap("Spine")!.Weight(left), 6);
    }
}
