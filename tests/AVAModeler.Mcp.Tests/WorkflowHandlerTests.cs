using AVAModeler.Core;
using AVAModeler.Scene;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>get_history and undo steps, instances seen in get_document and Layout, and photo match.</summary>
public class WorkflowHandlerTests
{
    [Fact]
    public async Task The_history_is_listed_and_undone_several_steps_at_once()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        await b.Tools.RunTool("Move", Bench.Args(new { Offset = new[] { 1.0, 0, 0 } }), Cancel);
        await b.Tools.RunTool("Size", Bench.Args(new { Factor = 50.0 }), Cancel);
        var h = await Parse(b.Tools.GetHistory(Cancel));
        Assert.Equal(["ran Size", "ran Move", "ran Box"], h["undo"]!.AsArray().Select(x => (string?)x).Take(3).Select(l => "ran " + l));
        var u = await Parse(b.Tools.Undo(Cancel, steps: 2));
        Assert.Equal(2, u["undone"]!.AsArray().Count);
        Assert.Equal(2, (await Parse(b.Tools.GetHistory(Cancel)))["redo"]!.AsArray().Count);
        await Parse(b.Tools.Redo(Cancel, steps: 2));
        Assert.Empty((await Parse(b.Tools.GetHistory(Cancel)))["redo"]!.AsArray());
        Assert.Contains("1 to 100", await Error(b.Tools.Undo(Cancel, steps: 0)));
    }

    [Fact]
    public async Task Instances_show_in_the_document_and_in_Layout()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 0.5, 0.5, 0.5 } }), Cancel);
        await b.Tools.RunTool("Instance", Bench.Args(new { Copies = 2, Offset = new[] { 1.0, 0, 0 } }), Cancel);
        var doc = await Parse(b.Tools.GetDocument(cancel: Cancel));
        var layer2 = doc["layers"]!.AsArray().First(l => (int)l!["layer"]! == 2)!;
        Assert.Equal(1, (int)layer2["instances"]!["of_layer"]!);
        Assert.Equal(2, (int)layer2["instances"]!["copies"]!);
        await b.Tools.AddObject(name: "Row", cancel: Cancel);
        var row = (ObjectItem)b.Scene.Find("Row")!;
        Assert.Equal(24, row.BaseMesh().Points.Count);
    }

    [Fact]
    public async Task A_photo_is_matched_and_two_give_a_point()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 2.0, 1, 1.5 } }), Cancel);
        var m = b.Doc.Current.Mesh;
        // Two pinhole cameras' pictures of the box's corners.
        Func<Vec3, Vec2> Camera(Vec3 eye, double f)
        {
            var fw = (-eye).Normalized();
            var rt = Vec3.Cross(Vec3.UnitY, fw).Normalized();
            var up = Vec3.Cross(fw, rt);
            return p => { var d = p - eye; double z = Vec3.Dot(d, fw); return new Vec2(512 + f * Vec3.Dot(d, rt) / z, 384 - f * Vec3.Dot(d, up) / z); };
        }
        var seeA = Camera(new Vec3(3, 2, -5), 800);
        var seeB = Camera(new Vec3(-4, 1, -3), 700);
        double[][] Marks(Func<Vec3, Vec2> see) => [.. Enumerable.Range(0, 8).Select(i => new[] { i, see(m.Points[i]).X, see(m.Points[i]).Y })];
        var a = await Parse(b.Tools.MatchPhoto("a", Marks(seeA), width: 1024, height: 768, cancel: Cancel));
        Assert.True((double)a["error_pixels"]! < 1e-3);
        Assert.Equal([3.0, 2, -5], a["camera_position"]!.AsArray().Select(x => Math.Round((double)x!, 4)));
        await Parse(b.Tools.MatchPhoto("b", Marks(seeB), width: 1024, height: 768, cancel: Cancel));
        var target = new Vec3(0.2, 0.9, 0.1);
        var p = await Parse(b.Tools.PointFromPhotos("a", [seeA(target).X, seeA(target).Y], "b", [seeB(target).X, seeB(target).Y], cancel: Cancel));
        Assert.Equal([0.2, 0.9, 0.1], p["point"]!.AsArray().Select(x => Math.Round((double)x!, 5)));
        Assert.Equal(9, b.Doc.Current.Mesh.Points.Count);
        Assert.Contains("at least 6 marks", await Error(b.Tools.MatchPhoto("c", Marks(seeA)[..4], width: 1024, height: 768, cancel: Cancel)));
        Assert.Contains("no matched photo \"z\"", await Error(b.Tools.PointFromPhotos("a", [1, 1], "z", [1, 1], cancel: Cancel)));
    }
}
