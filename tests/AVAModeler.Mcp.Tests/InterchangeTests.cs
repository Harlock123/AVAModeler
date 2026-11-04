using System.Text.Json.Nodes;
using AVAModeler.Formats;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>Agents saving and opening glTF and STL, from the handlers, a Lua macro and a real client; and export_scene.</summary>
public class InterchangeTests
{
    [Theory]
    [InlineData("box.glb", false, false)]
    [InlineData("box.gltf", false, false)]
    [InlineData("box.gltf", false, true)]
    [InlineData("box.stl", false, false)]
    [InlineData("box.stl", true, false)]
    public async Task Agents_save_and_open_glTF_and_STL(string name, bool ascii, bool separateBin)
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        var saved = await Parse(b.Tools.Save("out/" + name, ascii: ascii, separate_bin: separateBin, cancel: Cancel));
        string file = b.Folder.File(Path.Combine("out", name));
        Assert.Equal(file, (string?)saved["saved"]);
        // An export doesn't make the file the document's own, as saving as .lwo does.
        Assert.Null(b.Doc.FilePath);
        if (separateBin)
        {
            Assert.Equal(Path.ChangeExtension(file, ".bin"), (string?)saved["also"]![0]);
            Assert.True(File.Exists(Path.ChangeExtension(file, ".bin")));
        }
        else Assert.Null(saved["also"]);
        var head = File.ReadAllBytes(file).Take(5).ToArray();
        if (name.EndsWith(".glb")) Assert.Equal("glTF"u8.ToArray(), head[..4]);
        if (name.EndsWith(".stl")) Assert.Equal(ascii, head.SequenceEqual("solid"u8.ToArray()));

        var opened = await Parse(b.Tools.Open("out/" + name, discard_changes: true, cancel: Cancel));
        Assert.Equal(file, (string?)opened["file"]);
        Assert.Equal(12, b.Doc.Current.Mesh.Polygons.Count); // the six quads, as triangles
        Assert.Equal(8, b.Doc.Current.Mesh.Points.Count);    // their corners merged again
        Assert.Equal(1, b.Doc.Bounds().Size.X, 5);
    }

    [Fact]
    public async Task Saving_glTF_or_STL_keeps_to_the_working_folder_and_wont_replace_others_files()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        Assert.Contains("outside the working folder", await Error(b.Tools.Save("../escape.glb", cancel: Cancel)));
        File.WriteAllText(b.Folder.File("theirs.bin"), "keep me");
        Assert.Contains("theirs.bin already exists", await Error(b.Tools.Save("theirs.gltf", separate_bin: true, cancel: Cancel)));
        Assert.Equal("keep me", File.ReadAllText(b.Folder.File("theirs.bin")));
        Assert.False(File.Exists(b.Folder.File("theirs.gltf")));
        // Inside the .gltf instead, the .bin isn't touched.
        await b.Tools.Save("theirs.gltf", cancel: Cancel);
        Assert.Equal("keep me", File.ReadAllText(b.Folder.File("theirs.bin")));
        // Its own files it may write again.
        await b.Tools.Save("mine.stl", cancel: Cancel);
        await b.Tools.Save("mine.stl", ascii: true, cancel: Cancel);
        Assert.StartsWith("solid", File.ReadAllText(b.Folder.File("mine.stl")));
    }

    [Fact]
    public async Task A_macro_saves_and_loads_the_new_formats_by_extension()
    {
        using var b = new Bench();
        var r = await Parse(b.Tools.RunLua("tool('Box'); save('m.lwo'); save('m.glb'); save('m.stl'); new(); load('m.glb'); print(polycount()); new(); load('m.stl'); print(pointcount())", cancel: Cancel));
        Assert.Equal(["12", "8"], r["output"]!.AsArray().Select(l => (string)l!));
        Assert.True(File.Exists(b.Folder.File("m.glb")));
    }

    [Fact]
    public async Task Export_scene_writes_glTF_and_leaves_the_scene_alone()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        await b.Tools.AddObject(cancel: Cancel);
        int depth = b.Layout.History.Count;
        var result = await Parse(b.Tools.ExportScene("scene.glb", cancel: Cancel));
        string file = b.Folder.File("scene.glb");
        Assert.Equal(file, (string?)result["exported"]);
        Assert.Equal(60, (int)result["frames"]!);
        Assert.Equal("glTF"u8.ToArray(), File.ReadAllBytes(file)[..4]);
        Assert.Equal(depth, b.Layout.History.Count);
        Assert.Null(b.Layout.ScenePath);

        var gltf = await Parse(b.Tools.ExportScene("scene.gltf", separate_bin: true, cancel: Cancel));
        Assert.Equal(b.Folder.File("scene.bin"), (string?)gltf["also"]![0]);
        var json = JsonNode.Parse(File.ReadAllText(b.Folder.File("scene.gltf")))!;
        Assert.Contains(json["nodes"]!.AsArray(), n => (string?)n!["name"] == "Camera" && n["camera"] != null);

        Assert.Contains("save_scene", await Error(b.Tools.ExportScene("scene.lws", cancel: Cancel)));
        File.WriteAllText(b.Folder.File("theirs.glb"), "x");
        Assert.Contains("already exists", await Error(b.Tools.ExportScene("theirs.glb", cancel: Cancel)));
        Assert.Contains("outside the working folder", await Error(b.Tools.ExportScene("../out.glb", cancel: Cancel)));
    }

    [Fact]
    public async Task Through_a_client_save_takes_its_options_and_open_reads_them_back()
    {
        using var folder = new TempFolder();
        await using var session = await InProcess.Start(new HeadlessHost(folder.Path));
        var client = session.Client;
        var save = (await client.ListToolsAsync(cancellationToken: Cancel)).Single(t => t.Name == "save");
        var properties = save.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("ascii", out _));
        Assert.True(properties.TryGetProperty("separate_bin", out _));
        Assert.Contains(".glb", save.Description);

        await client.Json("run_tool", new { name = "Ball" });
        await client.Json("save", new { path = "ball.glb" });
        await client.Json("save", new { path = "ball.stl", ascii = true });
        var opened = await client.Json("open", new { path = "ball.glb", discard_changes = true });
        Assert.Equal(Path.Combine(folder.Path, "ball.glb"), (string?)opened["file"]);
        var stl = await client.Json("open", new { path = "ball.stl", discard_changes = true });
        Assert.Equal(Path.Combine(folder.Path, "ball.stl"), (string?)stl["file"]);
        Assert.Equal(ObjectFiles.Load(Path.Combine(folder.Path, "ball.glb")).Layers[0].Mesh.Polygons.Count,
            ObjectFiles.Load(Path.Combine(folder.Path, "ball.stl")).Layers[0].Mesh.Polygons.Count);
        var exported = await client.Json("export_scene", new { path = "empty.glb" });
        Assert.Equal(Path.Combine(folder.Path, "empty.glb"), (string?)exported["exported"]);
    }
}
