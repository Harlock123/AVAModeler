using System.Text.Json.Nodes;
using AVAModeler.Core;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>check_mesh: the mesh check as JSON for agents, through the handler and through a client.</summary>
public class CheckMeshTests
{
    [Fact]
    public async Task A_closed_box_is_printable_with_its_volume_in_cubic_metres()
    {
        using var b = new Bench();
        b.Commands.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 0.02, 0.03, 0.04 }, ["Center"] = new[] { 1.0, 0, 0 } });
        int steps = b.Doc.Undo.Count;
        var r = await Parse(b.Tools.CheckMesh(cancel: Cancel));
        Assert.Equal("Closed and printable (1 shell)", (string?)r["verdict"]);
        Assert.True((bool)r["printable"]!);
        Assert.True((bool)r["watertight"]!);
        Assert.Equal([1], r["layers_checked"]!.AsArray().Select(x => (int)x!));
        Assert.Equal(0.02 * 0.03 * 0.04, (double)r["volume_m3"]!, 12);
        Assert.Equal(2 * (0.02 * 0.03 + 0.02 * 0.04 + 0.03 * 0.04), (double)r["area_m2"]!, 9);
        Assert.Equal([1.0, 0, 0], r["center_of_mass"]!.AsArray().Select(x => (double)x!));
        Assert.Equal([0.02, 0.03, 0.04], r["bounds"]!["size"]!.AsArray().Select(x => (double)x!));
        Assert.Equal(6, (int)r["polygons"]!["quads"]!);
        Assert.Equal(1, (int)r["shells"]!);
        Assert.Empty(r["issues"]!.AsArray());
        Assert.Empty(r["how_to_fix"]!.AsArray());
        Assert.Equal(1, (int)r["per_layer"]![0]!["layer"]!);
        Assert.Equal("outward", (string?)r["per_layer"]![0]!["shells"]![0]!["facing"]);
        // It changed nothing.
        Assert.Equal(steps, b.Doc.Undo.Count);
    }

    [Fact]
    public async Task An_open_inside_out_mesh_says_what_is_wrong_and_how_to_fix_it()
    {
        using var b = new Bench();
        b.Commands.Run("Box");
        var mesh = b.Doc.Current.Mesh;
        for (int i = 0; i < 6; i++) mesh.Flip(i);
        mesh.AddPoint(new Vec3(5, 5, 5));
        var r = await Parse(b.Tools.CheckMesh(cancel: Cancel));
        Assert.Equal("Inside out: 1 closed shell facing inward", (string?)r["verdict"]);
        Assert.Equal(-1, (double)r["volume_m3"]!, 9);
        Assert.Equal(6, (int)r["flipped_polygons"]!);
        Assert.Equal(1, (int)r["unused_points"]!);
        var fixes = string.Join("\n", r["how_to_fix"]!.AsArray().Select(x => (string)x!));
        Assert.Contains("\"Align\"", fixes);
        Assert.Contains("\"Remove Degenerate\"", fixes);
        Assert.Equal([8], r["per_layer"]![0]!["examples"]!["unused_points"]!.AsArray().Select(x => (int)x!));

        // Open it: a hole, no volume, and the rim's edges as examples.
        mesh.RemovePolygons([0]);
        r = await Parse(b.Tools.CheckMesh(examples: 2, cancel: Cancel));
        Assert.StartsWith("Open: 1 hole along 4 edges", (string?)r["verdict"]);
        Assert.Null(r["volume_m3"]);
        Assert.Contains("closed", (string?)r["volume_note"]);
        Assert.Equal(2, r["per_layer"]![0]!["examples"]!["boundary_edges"]!.AsArray().Count);
        Assert.Contains("\"Fill Hole\"", string.Join("\n", r["how_to_fix"]!.AsArray().Select(x => (string)x!)));
    }

    [Fact]
    public async Task Layers_are_numbered_from_one_and_checked_each_on_its_own()
    {
        using var b = new Bench();
        b.Commands.Run("Box");
        b.Commands.SetLayers([2]);
        b.Commands.Run("Ball", new Dictionary<string, object?> { ["Center"] = new[] { 3.0, 0, 0 } });
        b.Commands.SetLayers([0]);
        var r = await Parse(b.Tools.CheckMesh([1, 3], cancel: Cancel));
        Assert.Equal([1, 3], r["layers_checked"]!.AsArray().Select(x => (int)x!));
        Assert.Equal(2, (int)r["shells"]!);
        Assert.True((bool)r["printable"]!);
        Assert.Equal(1 + 4.0 / 3 * Math.PI * 0.125, (double)r["volume_m3"]!, 1);
        Assert.Contains("no layer 11", await Error(b.Tools.CheckMesh([11], cancel: Cancel)));
        Assert.Contains("examples", await Error(b.Tools.CheckMesh(examples: 500, cancel: Cancel)));
        Assert.Contains("tolerance", await Error(b.Tools.CheckMesh(tolerance: -1, cancel: Cancel)));
        // The foreground by default.
        r = await Parse(b.Tools.CheckMesh(cancel: Cancel));
        Assert.Equal([1], r["layers_checked"]!.AsArray().Select(x => (int)x!));
    }

    [Fact]
    public async Task An_agent_checks_selects_fixes_and_checks_again_through_a_client()
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        var tool = (await s.Client.ListToolsAsync(cancellationToken: Cancel)).Single(t => t.Name == "check_mesh");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        await s.Client.Json("run_tool", new { name = "Box" });
        // Two faces turned the wrong way, as a hand-built mesh might have.
        await s.Client.Json("run_lua", new { script = "selmode('polygons'); selpolygons({1, 2}); tool('Flip'); deselect()" });
        var r = await s.Client.Json("check_mesh");
        Assert.False((bool)r["printable"]!);
        Assert.Equal(2, (int)r["flipped_polygons"]!);
        var picked = await s.Client.Json("run_tool", new { name = "Select Problems", @params = new { Problem = "FlippedPolygons" } });
        Assert.Equal(2, (int)picked["selection"]!["selected"]!);
        await s.Client.Json("select_none");
        await s.Client.Json("run_tool", new { name = "Align" });
        r = await s.Client.Json("check_mesh", new { examples = 0 });
        Assert.True((bool)r["printable"]!);
        Assert.Equal(1, (double)r["volume_m3"]!, 9);
        Assert.Null(r["per_layer"]![0]!["examples"]);
        Assert.Contains("no argument", await s.Client.Fails("check_mesh", new { layer = 1 }));
    }

    [Fact]
    public void The_instructions_tell_agents_to_check_their_models()
    {
        Assert.Contains("check_mesh", ModelerServer.Instructions);
        Assert.Contains("before saving for 3D printing", ModelerServer.Instructions);
    }
}
