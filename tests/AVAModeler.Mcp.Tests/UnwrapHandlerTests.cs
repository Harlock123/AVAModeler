using System.Text.Json.Nodes;
using AVAModeler.Core;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>unwrap_uvs: charts cut along seams, flattened and packed, as one undo step.</summary>
public class UnwrapHandlerTests
{
    [Fact]
    public async Task A_box_unwraps_into_six_charts_and_undo_takes_them_back()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        var r = await Parse(b.Tools.UnwrapUVs(cancel: Cancel));
        var layer = (JsonObject)((JsonArray)r["layers"]!)[0]!;
        Assert.Equal(6, (int)layer["charts"]!);
        Assert.Equal(0, (int)layer["flipped_faces"]!);
        Assert.InRange((double)layer["stretch"]!, 1, 1.01);
        Assert.Equal("UV", (string)r["current_uv_map"]!);
        var mesh = b.Doc.Current.Mesh;
        Assert.All(mesh.Polygons, p => Assert.True(mesh.IsMapped("UV", p)));
        await b.Tools.Undo(cancel: Cancel);
        Assert.Null(b.Doc.Current.Mesh.FindUVMap("UV"));
    }

    [Fact]
    public async Task Seams_given_by_points_cut_a_tube_open_and_bad_ones_are_explained()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Disc", Bench.Args(new { Sides = 12, Segments = 3, Caps = false }), Cancel);
        var mesh = b.Doc.Current.Mesh;
        // Any edge shared by two side quads that runs up the tube is a fine seam start; find a column of them.
        var vertical = mesh.EdgeMap().Where(kv => kv.Value.Count == 2 && Math.Abs(mesh.Points[kv.Key.A].X - mesh.Points[kv.Key.B].X) < 1e-9
            && Math.Abs(mesh.Points[kv.Key.A].Z - mesh.Points[kv.Key.B].Z) < 1e-9).Select(kv => kv.Key).ToList();
        Assert.NotEmpty(vertical);
        var x0 = mesh.Points[vertical[0].A];
        var column = vertical.Where(e => Math.Abs(mesh.Points[e.A].X - x0.X) < 1e-9 && Math.Abs(mesh.Points[e.A].Z - x0.Z) < 1e-9).ToList();
        var r = await Parse(b.Tools.UnwrapUVs(seams: [.. column.Select(e => new[] { e.A, e.B })], seam_angle: 0, cancel: Cancel));
        var layer = (JsonObject)((JsonArray)r["layers"]!)[0]!;
        Assert.Equal(1, (int)layer["charts"]!);
        Assert.Equal(0, (int)layer["split_to_flatten"]!);
        Assert.Equal(SelectionMode.Edges, b.Doc.Selection.Mode);
        Assert.Contains("aren't joined by an edge", await Error(b.Tools.UnwrapUVs(seams: [[0, 0]], cancel: Cancel)));
        Assert.Contains("0 to 180", await Error(b.Tools.UnwrapUVs(seam_angle: 400, cancel: Cancel)));
    }
}
