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
        // Slicing is no undo step.
        Assert.Equal(steps, b.Doc.Undo.Count);
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
