using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_subpatch, set_creases and the objects' SubPatch levels in Layout.</summary>
public class SubPatchHandlerTests
{
    [Fact]
    public async Task A_box_cage_becomes_a_creased_SubPatch_that_Layout_subdivides_and_freeze_bakes()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        var on = await Parse(b.Tools.SetSubPatch(cancel: Cancel));
        Assert.Equal(1, (int)((JsonArray)on["subpatch_layers"]!)[0]!);
        Assert.True(b.Doc.Current.Mesh.SubPatch);
        var doc = await Parse(b.Tools.GetDocument(cancel: Cancel));
        Assert.True((bool)((JsonArray)doc["layers"]!)[0]!["subpatch"]!);

        var edge = b.Doc.Current.Mesh.EdgeMap().Keys.First();
        var creased = await Parse(b.Tools.SetCreases([[edge.A, edge.B]], 1, Cancel));
        Assert.Equal(2, (int)creased["creased_points"]!);
        Assert.Contains("crease", await Error(b.Tools.SetCreases([[edge.A, edge.B]], 3, Cancel)));
        Assert.Contains("mode must be", await Error(b.Tools.SetSubPatch("sideways", cancel: Cancel)));

        // The pictures show the surface; Layout subdivides at the object's levels.
        Assert.NotNull(await b.Tools.RenderView("front", 64, 48, "shaded", cancel: Cancel));
        await b.Tools.AddObject(name: "Blob", cancel: Cancel);
        await Parse(b.Tools.SetItem("Blob", subpatch_level: 1, subpatch_render_level: 2, cancel: Cancel));
        var obj = b.Layout.Scene.Objects.First();
        Assert.Equal((1, 2), (obj.SubPatchLevel, obj.SubPatchRenderLevel));
        Assert.Equal(6 * 4, b.Layout.Scene.Evaluate(1).Objects[0].Mesh.Polygons.Count);
        Assert.Contains("0 to 6", await Error(b.Tools.SetItem("Blob", subpatch_level: 9, cancel: Cancel)));

        var frozen = await Parse(b.Tools.SetSubPatch("freeze", 1, Cancel));
        Assert.Empty((JsonArray)frozen["subpatch_layers"]!);
        Assert.Equal(24, b.Doc.Current.Mesh.Polygons.Count);
        await b.Tools.Undo(cancel: Cancel);
        Assert.True(b.Doc.Current.Mesh.SubPatch);
        Assert.Equal(6, b.Doc.Current.Mesh.Polygons.Count);
        Assert.NotNull(b.Doc.Current.Mesh.FindPointMap(SubPatches.CreaseMap));
    }
}
