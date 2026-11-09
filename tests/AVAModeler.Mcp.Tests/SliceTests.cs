using System.Text.Json.Nodes;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>Agents slicing the object to G-code.</summary>
public class SliceTests
{
    [Fact]
    public async Task An_agent_slices_a_box_to_gcode_and_the_object_is_unchanged()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        int steps = b.Doc.Undo.Count;
        // The 1 m box at 2% is 20 mm across.
        var r = await Parse(b.Tools.SliceToGCode("prints/box.gcode", scale_percent: 2, infill_percent: 15, material: "petg", cancel: Cancel));
        string file = b.Folder.File(Path.Combine("prints", "box.gcode"));
        Assert.Equal(file, (string?)r["saved"]);
        Assert.StartsWith(";FLAVOR:Marlin", File.ReadAllText(file));
        Assert.Contains("M109 S240", File.ReadAllText(file));
        Assert.Equal("PETG", (string?)r["material"]);
        Assert.Equal(20, (double)r["size_mm"]![0]!, 6);
        Assert.Equal(100, (int)r["layers"]!);
        Assert.True((double)r["grams"]! > 1);
        Assert.True((bool)r["printable"]!);
        Assert.Empty((JsonArray)r["warnings"]!);
        // Laid on a side, turned and four of them: two rows of two 20 mm cubes 5 mm apart.
        var four = await Parse(b.Tools.SliceToGCode("prints/four.gcode", scale_percent: 2, lay_flat: "largest_face", rotate: 30, copies: 4, cancel: Cancel));
        Assert.InRange((double)four["size_mm"]![0]!, 45, 60);
        Assert.Contains("selected_face", await Error(b.Tools.SliceToGCode("x.gcode", lay_flat: "upside_down", cancel: Cancel)));
        Assert.Contains("Select a polygon", await Error(b.Tools.SliceToGCode("x.gcode", scale_percent: 2, lay_flat: "selected_face", cancel: Cancel)));
        await b.Tools.SliceToGCode("prints/held.gcode", scale_percent: 2, supports: true, supports_bed_only: true, cancel: Cancel);
        Assert.Contains(", supports", File.ReadAllText(b.Folder.File(Path.Combine("prints", "held.gcode"))));
        // A printer on the market by name, its notes coming back.
        var fcp = await Parse(b.Tools.SliceToGCode("prints/fcp.gcode", printer: "flashforge creator pro", scale_percent: 2, cancel: Cancel));
        Assert.Equal("Flashforge Creator Pro", (string?)fcp["printer"]);
        Assert.Contains(((JsonArray)fcp["warnings"]!).Select(w => (string)w!), w => w.Contains("GPX"));
        // .x3g for the Creator Pro, which its Sailfish firmware reads; not for a G-code printer.
        await b.Tools.SliceToGCode("prints/fcp.x3g", printer: "Flashforge Creator Pro", scale_percent: 2, cancel: Cancel);
        Assert.Equal(153, File.ReadAllBytes(b.Folder.File(Path.Combine("prints", "fcp.x3g")))[0]);
        Assert.Contains("doesn't read .x3g", await Error(b.Tools.SliceToGCode("prints/no.x3g", scale_percent: 2, cancel: Cancel)));
        await b.Tools.SliceToGCode("prints/gyroid.gcode", scale_percent: 2, infill_pattern: "gyroid", cancel: Cancel);
        Assert.Contains("gyroid infill", File.ReadAllText(b.Folder.File(Path.Combine("prints", "gyroid.gcode"))));
        Assert.Contains("lines, grid or gyroid", await Error(b.Tools.SliceToGCode("prints/x.gcode", infill_pattern: "honeycomb", cancel: Cancel)));
        // Slicing is no undo step.
        Assert.Equal(steps, b.Doc.Undo.Count);
    }

    [Fact]
    public async Task A_macro_run_by_an_agent_slices_inside_the_working_folder()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        var r = await Parse(b.Tools.RunLua("print(slice('lua/box.gcode', {scale = 2}).layers)", cancel: Cancel));
        Assert.StartsWith(";FLAVOR:Marlin", File.ReadAllText(b.Folder.File(Path.Combine("lua", "box.gcode"))));
        Assert.Contains("100", r.ToJsonString());
        Assert.Contains("outside the working folder", await Error(b.Tools.RunLua("slice('../out.gcode', {scale = 2})", cancel: Cancel)));
        File.WriteAllText(b.Folder.File("theirs.gcode"), "keep me");
        Assert.Contains("already exists", await Error(b.Tools.RunLua("slice('theirs.gcode', {scale = 2})", cancel: Cancel)));
        Assert.Equal("keep me", File.ReadAllText(b.Folder.File("theirs.gcode")));
    }

    [Fact]
    public async Task Mistakes_are_explained_and_nothing_is_written()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        Assert.Contains("Generic 220 × 220", await Error(b.Tools.SliceToGCode("a.gcode", printer: "Prusa", cancel: Cancel)));
        Assert.Contains("PLA, PETG, ABS, TPU", await Error(b.Tools.SliceToGCode("a.gcode", material: "wood", cancel: Cancel)));
        Assert.Contains("layer height", await Error(b.Tools.SliceToGCode("a.gcode", layer_height: 2, cancel: Cancel)));
        Assert.Contains(".gcode", await Error(b.Tools.SliceToGCode("a.txt", cancel: Cancel)));
        Assert.Contains("outside the working folder", await Error(b.Tools.SliceToGCode("../a.gcode", cancel: Cancel)));
        File.WriteAllText(b.Folder.File("theirs.gcode"), "keep me");
        Assert.Contains("already exists", await Error(b.Tools.SliceToGCode("theirs.gcode", scale_percent: 2, cancel: Cancel)));
        Assert.Equal("keep me", File.ReadAllText(b.Folder.File("theirs.gcode")));
        Assert.False(File.Exists(b.Folder.File("a.gcode")));
        // Too big for the bed still slices, with the warning.
        var big = await Parse(b.Tools.SliceToGCode("big.gcode", scale_percent: 30, cancel: Cancel));
        Assert.Contains(((JsonArray)big["warnings"]!).Select(w => (string)w!), w => w.Contains("bigger than"));
        // Its own file it may write again.
        await b.Tools.SliceToGCode("big.gcode", scale_percent: 2, cancel: Cancel);
        await b.Tools.New(discard_changes: true, cancel: Cancel);
        Assert.Contains("nothing to print", await Error(b.Tools.SliceToGCode("empty.gcode", cancel: Cancel)));
    }
}
