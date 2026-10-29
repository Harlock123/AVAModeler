using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Tools;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>The MCP tool handlers driven directly against a headless host: each group of tools, its errors, and undo.</summary>
public class HandlerTests
{
    private static Dictionary<string, System.Text.Json.JsonElement> P(object o) => Bench.Args(o);

    // ───────────────────────────── discovery ─────────────────────────────

    [Fact]
    public async Task List_tools_comes_from_the_registry_with_tabs_and_summaries()
    {
        using var b = new Bench();
        var list = await Parse(b.Tools.ListTools(Cancel));
        var tools = list["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        Assert.Equal(ToolRegistry.Default.Names.Count, tools.Count);
        Assert.Equal((int)list["count"]!, tools.Count);
        var box = tools.Single(t => (string?)t["name"] == "Box");
        Assert.Equal("Objects", (string?)box["tab"]);
        Assert.Contains("centred on Center", (string?)box["summary"]);
        // Every tool says what it does; the mouse-only ones say so.
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace((string?)t["summary"])));
        Assert.Equal(["Drag", "Points"], tools.Where(t => (bool?)t["scriptable"] == false).Select(t => (string)t["name"]!).Order());
        // The Modeler's tab order.
        Assert.Equal("Objects", (string?)tools[0]["tab"]);
    }

    [Fact]
    public async Task Describe_tool_gives_each_parameter_with_its_unit_limits_and_choices()
    {
        using var b = new Bench();
        var box = await Parse(b.Tools.DescribeTool("box", Cancel));
        Assert.Equal("Box", (string?)box["name"]);
        var size = box["parameters"]!.AsArray().Single(p => (string?)p!["name"] == "Size")!;
        Assert.Equal("vector [x, y, z]", (string?)size["type"]);
        Assert.Equal("metres", (string?)size["unit"]);
        Assert.Equal(0, (double)size["min"]!);
        Assert.Equal([1.0, 1, 1], size["default"]!.AsArray().Select(v => (double)v!));
        Assert.Contains("Edge lengths", (string?)size["description"]);
        var segments = box["parameters"]!.AsArray().Single(p => (string?)p!["name"] == "SegmentsX")!;
        Assert.Equal("integer", (string?)segments["type"]);
        Assert.Equal(1000, (double)segments["max"]!);
        Assert.Equal("Box", (string?)box["example"]!["name"]);

        var ball = await Parse(b.Tools.DescribeTool("Ball", Cancel));
        var kind = ball["parameters"]!.AsArray().Single(p => (string?)p!["name"] == "Style")!;
        Assert.Equal(["Globe", "Tessellated"], kind["choices"]!.AsArray().Select(v => (string)v!));
        Assert.Equal("Kind", (string?)kind["label"]);
        var rotate = await Parse(b.Tools.DescribeTool("Rotate", Cancel));
        Assert.Equal("degrees", (string?)rotate["parameters"]!.AsArray().Single(p => (string?)p!["name"] == "Angle")!["unit"]);

        var unknown = await Error(b.Tools.DescribeTool("Bx", Cancel));
        Assert.Contains("no tool called \"Bx\"", unknown);
        Assert.Contains("\"Box\"", unknown);
    }

    [Fact]
    public async Task A_plugins_tools_are_listed_and_described()
    {
        var built = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "StarPlugin", "bin");
        var dll = Directory.GetFiles(built, "StarPlugin.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        using var plugins = new TempFolder();
        File.Copy(dll, plugins.File("StarPlugin.dll"));
        var registry = ToolRegistry.Create();
        Assert.Empty(registry.LoadPlugins(plugins.Path));
        using var b = new Bench(registry);
        var list = await Parse(b.Tools.ListTools(Cancel));
        Assert.Contains(list["tools"]!.AsArray(), t => (string?)t!["name"] == "Star");
        var star = await Parse(b.Tools.DescribeTool("Star", Cancel));
        Assert.Contains(star["parameters"]!.AsArray(), p => (string?)p!["name"] == "Points");
        var run = await Parse(b.Tools.RunTool("Star", P(new { Points = 6 }), Cancel));
        Assert.Equal(12, (int)run["points"]!["after"]!);
        // Its file format can be saved to.
        var saved = await Parse(b.Tools.Save("star.xyz", cancel: Cancel));
        Assert.True(File.Exists((string)saved["saved"]!));
    }

    // ───────────────────────────── acting ─────────────────────────────

    [Fact]
    public async Task Run_tool_reports_the_change_and_is_one_undo_step()
    {
        using var b = new Bench();
        var r = await Parse(b.Tools.RunTool("Box", P(new { Size = new[] { 2.0, 1, 1 }, SegmentsX = 2 }), Cancel));
        Assert.Equal("Box", (string?)r["done"]);
        Assert.Equal(0, (int)r["points"]!["before"]!);
        Assert.Equal(12, (int)r["points"]!["after"]!);
        Assert.Equal(10, (int)r["polygons"]!["after"]!);
        Assert.Equal(1, (int)r["changed_layers"]![0]!["layer"]!);
        Assert.Equal(1, (int)r["undo"]!["depth"]!);
        Assert.Equal("Box", (string?)r["undo"]!["next_undo"]);
        Assert.Equal(2, b.Doc.Bounds().Size.X, 9);
        // A vector can be an object too, and names are any case.
        await b.Tools.RunTool("move", P(new { offset = new { x = 1, y = 0, z = 0 } }), Cancel);
        Assert.Equal(2, b.Doc.Bounds().Max.X, 9);
        var undo = await Parse(b.Tools.Undo(Cancel));
        Assert.Equal("Move", (string?)undo["undone"]);
        Assert.Equal(1, b.Doc.Bounds().Max.X, 9);
        await b.Tools.Undo(Cancel);
        Assert.Empty(b.Doc.Current.Mesh.Points);
        var redo = await Parse(b.Tools.Redo(Cancel));
        Assert.Equal("Box", (string?)redo["redone"]);
        Assert.Equal(12, b.Doc.Current.Mesh.Points.Count);
    }

    [Fact]
    public async Task Bad_tools_and_parameters_are_clear_errors_that_change_nothing()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        int depth = b.Doc.Undo.Count;
        var mesh = b.Doc.Current.Mesh.Clone();

        Assert.Contains("no tool called \"Boxx\"", await Error(b.Tools.RunTool("Boxx", null, Cancel)));
        Assert.Contains("has no parameter \"Sise\"", await Error(b.Tools.RunTool("Box", P(new { Sise = 2 }), Cancel)));
        Assert.Contains("Size must be a vector of three numbers", await Error(b.Tools.RunTool("Box", P(new { Size = 2 }), Cancel)));
        Assert.Contains("Size must be a vector of three numbers [x, y, z], not 2", await Error(b.Tools.RunTool("Box", P(new { Size = new[] { 1.0, 2 } }), Cancel)));
        Assert.Contains("SegmentsX must be a whole number", await Error(b.Tools.RunTool("Box", P(new { SegmentsX = 2.5 }), Cancel)));
        Assert.Contains("SegmentsX must be from 1 to 1000, not 0", await Error(b.Tools.RunTool("Box", P(new { SegmentsX = 0 }), Cancel)));
        Assert.Contains("Must be at least 0 on each axis", (await Parse(b.Tools.DescribeTool("Box", Cancel)))["parameters"]![1]!["description"]!.ToString());
        Assert.Contains("must be one of Globe, Tessellated, not \"cube\"", await Error(b.Tools.RunTool("Ball", P(new { Kind = "cube" }), Cancel)));
        Assert.Contains("must be true or false", await Error(b.Tools.RunTool("Disc", P(new { Caps = "yes" }), Cancel)));
        Assert.Contains("needs a value", await Error(b.Tools.RunTool("Box", P(new { Size = (double[]?)null }), Cancel)));
        Assert.Contains("only works with the mouse", await Error(b.Tools.RunTool("Points", null, Cancel)));
        // A tool that fails while it works (Boolean with nothing in the background) is undone, not left half done.
        Assert.Contains("background", await Error(b.Tools.RunTool("Boolean", null, Cancel)));

        Assert.Equal(depth, b.Doc.Undo.Count);
        Assert.Equal(mesh.Points, b.Doc.Current.Mesh.Points);
        Assert.Equal(mesh.Polygons.Count, b.Doc.Current.Mesh.Polygons.Count);
        Assert.False(b.Doc.InGroup);
    }

    [Fact]
    public async Task Points_and_polygons_are_added_in_one_step_each_and_checked()
    {
        using var b = new Bench();
        var pts = await Parse(b.Tools.AddPoints([[0, 0, 0], [0, 1, 0], [1, 1, 0], [1, 0, 0]], Cancel));
        Assert.Equal(0, (int)pts["indices"]!["first"]!);
        Assert.Equal(3, (int)pts["indices"]!["last"]!);
        Assert.Equal(1, b.Doc.Undo.Count);
        var polys = await Parse(b.Tools.AddPolygons([[0, 1, 2, 3], [0, 2]], "Panel", Cancel));
        Assert.Equal(2, (int)polys["indices"]!["count"]!);
        Assert.Equal(2, b.Doc.Undo.Count);
        // Clockwise seen from the front (looking along +Z), so it faces the viewer: its normal is -Z.
        Assert.True(Vec3.Near(-Vec3.UnitZ, b.Doc.Current.Mesh.Normal(0), 1e-9));
        Assert.Equal("Panel", b.Doc.Surfaces[b.Doc.Current.Mesh.Polygons[0].Surface].Name);

        Assert.Contains("uses point 9, but the current layer's points are numbered 0 to 3", await Error(b.Tools.AddPolygons([[0, 1, 9]], null, Cancel)));
        Assert.Contains("same point twice", await Error(b.Tools.AddPolygons([[0, 1, 1]], null, Cancel)));
        Assert.Contains("needs three numbers", await Error(b.Tools.AddPoints([[0, 0]], Cancel)));
        Assert.Contains("finite", await Error(b.Tools.AddPoints([[0, double.NaN, 0]], Cancel)));
        Assert.Equal(2, b.Doc.Undo.Count);
        Assert.Equal(2, b.Doc.Current.Mesh.Polygons.Count);
    }

    [Fact]
    public async Task Selection_by_mode_index_box_all_none_invert_and_connected()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        await b.Tools.RunTool("Box", P(new { Center = new[] { 3.0, 0, 0 } }), Cancel);
        var sel = await Parse(b.Tools.SelectPoints([0, 1], cancel: Cancel));
        Assert.Equal("points", (string?)sel["mode"]);
        Assert.Equal([0, 1], sel["current_layer"]!.AsArray().Select(v => (int)v!));
        sel = await Parse(b.Tools.SelectPoints([2], add: true, cancel: Cancel));
        Assert.Equal(3, (int)sel["selected"]!);
        sel = await Parse(b.Tools.SelectPoints([5], cancel: Cancel));
        Assert.Equal(1, (int)sel["selected"]!);
        sel = await Parse(b.Tools.SelectConnected(Cancel));
        Assert.Equal(8, (int)sel["selected"]!);
        sel = await Parse(b.Tools.SelectInvert(Cancel));
        Assert.Equal(8, (int)sel["selected"]!);
        Assert.All(sel["current_layer"]!.AsArray(), i => Assert.True(b.Doc.Current.Mesh.Points[(int)i!].X > 2));

        await b.Tools.SetSelectionMode("polygons", Cancel);
        sel = await Parse(b.Tools.SelectBox([2, -1, -1], [4, 1, 1], cancel: Cancel));
        Assert.Equal(6, (int)sel["selected"]!);
        sel = await Parse(b.Tools.SelectPolygons([0, 1], cancel: Cancel));
        Assert.Equal([0, 1], sel["current_layer"]!.AsArray().Select(v => (int)v!));
        sel = await Parse(b.Tools.SelectAll(Cancel));
        Assert.Equal(12, (int)sel["selected"]!);
        sel = await Parse(b.Tools.SelectNone(Cancel));
        Assert.Equal(0, (int)sel["selected"]!);
        Assert.Contains("everything", (string?)sel["note"]);
        await b.Tools.SetSelectionMode("volume", Cancel);
        sel = await Parse(b.Tools.SelectBox([-1, -1, -1], [0, 1, 1], cancel: Cancel));
        Assert.Equal(-1, (double)sel["volume"]!["min"]![0]!);

        Assert.Contains("There's no point 99", await Error(b.Tools.SelectPoints([99], cancel: Cancel)));
        Assert.Contains("There's no polygon -1", await Error(b.Tools.SelectPolygons([-1], cancel: Cancel)));
        Assert.Contains("must be one of points, polygons, edges, volume", await Error(b.Tools.SetSelectionMode("faces", Cancel)));
        Assert.Contains("three numbers", await Error(b.Tools.SelectBox([0, 0], [1, 1, 1], cancel: Cancel)));
        // Selecting isn't an undo step.
        Assert.Equal(2, b.Doc.Undo.Count);
    }

    [Fact]
    public async Task Layers_are_set_and_checked()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        // Layers are numbered from 1, as the app's buttons show them.
        var doc = await Parse(b.Tools.SetLayers([3], [1], Cancel));
        Assert.Equal(3, (int)doc["current_layer"]!);
        Assert.Equal("background", (string?)doc["layers"]!.AsArray().Single(l => (int)l!["layer"]! == 1)!["state"]);
        await b.Tools.RunTool("Ball", P(new { Radius = new[] { 0.2, 0.2, 0.2 } }), Cancel);
        Assert.NotEmpty(b.Doc.Layers[2].Mesh.Points);
        Assert.Equal(8, b.Doc.Layers[0].Mesh.Points.Count);
        Assert.Contains("both the foreground and the background", await Error(b.Tools.SetLayers([1], [1], Cancel)));
        Assert.Contains("at least one foreground", await Error(b.Tools.SetLayers([], null, Cancel)));
        Assert.Contains("no layer 101", await Error(b.Tools.SetLayers([101], null, Cancel)));
        Assert.Contains("no layer 0: layers are numbered from 1", await Error(b.Tools.SetLayers([0], null, Cancel)));
    }

    [Fact]
    public async Task Files_open_and_save_inside_the_working_folder_only()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        var saved = await Parse(b.Tools.Save("models/box.lwo", cancel: Cancel));
        string file = b.Folder.File(Path.Combine("models", "box.lwo"));
        Assert.Equal(file, (string?)saved["saved"]);
        Assert.Equal(file, b.Doc.FilePath);
        Assert.False(b.Doc.Modified);
        Assert.Equal(8, ObjectFiles.Load(file).Current.Mesh.Points.Count);
        // Saving again over its own file is fine; someone else's file needs overwrite.
        await b.Tools.Save("models/box.lwo", cancel: Cancel);
        File.WriteAllText(b.Folder.File("theirs.obj"), "v 0 0 0\n");
        Assert.Contains("already exists", await Error(b.Tools.Save("theirs.obj", cancel: Cancel)));
        Assert.Equal("v 0 0 0\n", File.ReadAllText(b.Folder.File("theirs.obj")));
        await b.Tools.Save("theirs.obj", overwrite: true, cancel: Cancel);
        Assert.True(File.Exists(b.Folder.File("theirs.mtl")));

        Assert.Contains("outside the working folder", await Error(b.Tools.Save("../escape.lwo", cancel: Cancel)));
        Assert.Contains("outside the working folder", await Error(b.Tools.Save("/tmp/escape.lwo", cancel: Cancel)));
        Assert.Contains("can't save .txt files", await Error(b.Tools.Save("notes.txt", cancel: Cancel)));
        Assert.Contains("can't open .txt files", await Error(b.Tools.Open("notes.txt", true, Cancel)));
        Assert.Contains("There's no file", await Error(b.Tools.Open("missing.lwo", true, Cancel)));

        // New and open won't throw away unsaved work unless told to.
        await b.Tools.RunTool("Move", P(new { Offset = new[] { 1.0, 0, 0 } }), Cancel);
        Assert.Contains("haven't been saved", await Error(b.Tools.New(cancel: Cancel)));
        Assert.Contains("haven't been saved", await Error(b.Tools.Open("models/box.lwo", cancel: Cancel)));
        Assert.Equal(1.5, b.Doc.Bounds().Max.X, 9);
        var opened = await Parse(b.Tools.Open("models/box.lwo", true, Cancel));
        Assert.Equal(file, (string?)opened["file"]);
        Assert.Equal(0.5, b.Doc.Bounds().Max.X, 9);
        var fresh = await Parse(b.Tools.New(cancel: Cancel));
        Assert.Null(fresh["visible_bounds"]);
        Assert.Empty(b.Doc.Current.Mesh.Points);
    }

    [Fact]
    public async Task Surfaces_take_their_look_in_one_undo_step()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        await b.Tools.RunTool("Box", P(new { Center = new[] { 0, 1.0, 0 } }), Cancel);
        await b.Tools.SelectBox([-1, 0.4, -1], [1, 2, 1], cancel: Cancel);
        await b.Tools.SetSelectionMode("polygons", Cancel);
        await b.Tools.SelectBox([-1, 0.4, -1], [1, 2, 1], cancel: Cancel);
        int depth = b.Doc.Undo.Count;
        var r = await Parse(b.Tools.SetSurface("Red", color: [200, 30, 20], specular: 0.5, glossiness: 256, smoothing: true, cancel: Cancel));
        Assert.Equal(depth + 1, b.Doc.Undo.Count);
        var red = b.Doc.Surfaces.Single(s => s.Name == "Red");
        Assert.Equal(new Rgb(200, 30, 20), red.Color);
        Assert.Equal(0.5, red.Specular);
        Assert.Equal(256, red.Glossiness);
        Assert.True(red.Smoothing);
        // The upper box and the lower box's top lie in the box chosen.
        Assert.Equal(7, (int)r["surface"]!["polygons"]!);
        // Only the look: no polygons change surface.
        await b.Tools.SelectNone(Cancel);
        await b.Tools.SetSurface("Red", diffuse: 0.7, assign: false, cancel: Cancel);
        Assert.Equal(7, b.Doc.Current.Mesh.Polygons.Count(p => p.Surface == b.Doc.SurfaceIndex("Red")));
        Assert.Equal(0.7, red.Diffuse);
        await b.Tools.Undo(Cancel);
        await b.Tools.Undo(Cancel);
        Assert.DoesNotContain(b.Doc.Surfaces, s => s.Name == "Red");
        Assert.All(b.Doc.Current.Mesh.Polygons, p => Assert.Equal(0, p.Surface));

        Assert.Contains("0 to 255", await Error(b.Tools.SetSurface("X", color: [300, 0, 0], cancel: Cancel)));
        Assert.Contains("not 0 to 1: [1, 0.5, 0] would be [255, 128, 0]", await Error(b.Tools.SetSurface("X", color: [1, 0.5, 0], cancel: Cancel)));
        Assert.Contains("from 0 to 1", await Error(b.Tools.SetSurface("X", transparency: 2, cancel: Cancel)));
        Assert.Contains("glossiness", await Error(b.Tools.SetSurface("X", glossiness: 0, cancel: Cancel)));
        Assert.DoesNotContain(b.Doc.Surfaces, s => s.Name == "X");
    }

    [Fact]
    public async Task UVs_and_textures_through_the_texture_commands()
    {
        using var b = new Bench();
        File.Copy(Path.Combine(Samples, "images", "crate.png"), b.Folder.File("crate.png"));
        await b.Tools.RunTool("Box", P(new { Surface = "Crate" }), Cancel);
        var uv = await Parse(b.Tools.MakeUVs("Box", "atlas", cancel: Cancel));
        Assert.Equal("Box", (string?)uv["current_uv_map"]);
        Assert.Equal(["Box"], uv["uv_maps"]!.AsArray().Select(v => (string)v!));
        var tex = await Parse(b.Tools.SetTexture("Crate", "crate.png", projection: "uv", cancel: Cancel));
        Assert.Equal("uv", (string?)tex["texture"]!["projection"]);
        Assert.Equal("Box", (string?)tex["texture"]!["uv_map"]);
        Assert.Null(tex["warning"]);
        var layer = Assert.Single(b.Doc.Surfaces.Single(s => s.Name == "Crate").Textures);
        Assert.Equal(TextureProjection.UV, layer.Projection);
        Assert.True(File.Exists(Path.IsPathRooted(layer.Image) ? layer.Image : b.Folder.File(layer.Image)));
        var planar = await Parse(b.Tools.SetTexture("Crate", "missing.png", channel: "bump", projection: "planar", axis: "y", amplitude: 0.5, cancel: Cancel));
        Assert.Contains("magenta checker", (string?)planar["warning"]);
        Assert.Equal(1, (double)planar["texture"]!["size"]![0]!, 9); // fitted to the unit box
        await b.Tools.SetUVMap("Box", Cancel);
        Assert.Contains("no UV map \"Nope\"", await Error(b.Tools.SetUVMap("Nope", Cancel)));
        Assert.Contains("must be one of", await Error(b.Tools.SetTexture("Crate", "crate.png", channel: "shine", cancel: Cancel)));
        var removed = await Parse(b.Tools.RemoveTextures("Crate", "bump", Cancel));
        Assert.Equal(1, (int)removed["removed"]!);
        var doc = await Parse(b.Tools.GetDocument(Cancel));
        Assert.Single(doc["surfaces"]!.AsArray().Single(s => (string?)s!["name"] == "Crate")!["textures"]!.AsArray());
    }

    [Fact]
    public async Task Lua_macros_print_run_as_one_step_and_undo_entirely_when_they_fail()
    {
        using var b = new Bench();
        var r = await Parse(b.Tools.RunLua("tool('Box'); selmode('polygons'); selbox(-1, 0.4, -1, 1, 1, 1); tool('Extrude', {Offset = {0, 1, 0}}); print(polycount())", cancel: Cancel));
        Assert.Equal(["10"], r["output"]!.AsArray().Select(v => (string)v!));
        Assert.Equal(1, b.Doc.Undo.Count);
        Assert.Equal("Macro", b.Doc.Undo.UndoLabel);

        var failed = await Error(b.Tools.RunLua("tool('Ball'); print('half way'); tool('Nope')", cancel: Cancel));
        Assert.Contains("There's no tool called \"Nope\"", failed);
        Assert.Contains("half way", failed);
        Assert.Contains("Nothing it did was kept", failed);
        Assert.Equal(1, b.Doc.Undo.Count);
        Assert.Equal(10, b.Doc.Current.Mesh.Polygons.Count);

        Assert.Contains("can't be used in the middle of a grouped edit", await Error(b.Tools.RunLua("tool('Ball'); undo()", cancel: Cancel)));
        Assert.Contains("stopped after running", await Error(b.Tools.RunLua("while true do end", 0.3, Cancel)));
        Assert.Equal(10, b.Doc.Current.Mesh.Polygons.Count);
        // Its files keep to the working folder too.
        Assert.Contains("outside the working folder", await Error(b.Tools.RunLua("save('/tmp/elsewhere.lwo')", cancel: Cancel)));
        await b.Tools.RunLua("save('macro.lwo')", cancel: Cancel);
        Assert.True(File.Exists(b.Folder.File("macro.lwo")));
        Assert.Contains("time_limit_seconds", await Error(b.Tools.RunLua("print(1)", 0, Cancel)));
    }

    // ───────────────────────────── seeing ─────────────────────────────

    [Fact]
    public async Task The_document_points_and_polygons_read_back_in_pages()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", P(new { SegmentsX = 4, Surface = "Grey" }), Cancel);
        await b.Tools.SetLayers([1], [2], Cancel);
        var doc = await Parse(b.Tools.GetDocument(Cancel));
        Assert.Equal(1, (int)doc["current_layer"]!);
        var layer0 = doc["layers"]![0]!;
        Assert.Equal("foreground", (string?)layer0["state"]);
        Assert.Equal(20, (int)layer0["points"]!);
        Assert.Equal(-0.5, (double)layer0["bounds"]!["min"]![0]!);
        Assert.Equal("3-10", (string?)doc["empty_layers_off"]);
        Assert.Equal(1, (double)doc["visible_bounds"]!["size"]![1]!);
        Assert.Contains(doc["surfaces"]!.AsArray(), s => (string?)s!["name"] == "Grey" && (int)s["polygons"]! == 18);
        Assert.Equal(1, (int)doc["undo"]!["depth"]!);

        var page = await Parse(b.Tools.GetPoints(offset: 10, limit: 5, cancel: Cancel));
        Assert.Equal(20, (int)page["total"]!);
        Assert.Equal(5, (int)page["count"]!);
        Assert.Equal(10, (int)page["points"]![0]![0]!);
        Assert.True((bool)page["more"]!);
        var p10 = b.Doc.Current.Mesh.Points[10];
        Assert.Equal(p10.X, (double)page["points"]![0]![1]!, 6);

        var polys = await Parse(b.Tools.GetPolygons(limit: 100, cancel: Cancel));
        Assert.Equal(18, (int)polys["count"]!);
        Assert.False((bool)polys["more"]!);
        var first = polys["polygons"]![0]!;
        Assert.Equal("Grey", (string?)first["surface"]);
        Assert.Equal(4, first["points"]!.AsArray().Count);
        Assert.Equal(1, Math.Abs((double)first["normal"]![0]!) + Math.Abs((double)first["normal"]![1]!) + Math.Abs((double)first["normal"]![2]!), 6);

        await b.Tools.SetSelectionMode("polygons", Cancel);
        var none = await Parse(b.Tools.GetPolygons(selected_only: true, cancel: Cancel));
        Assert.Equal(0, (int)none["total"]!);
        await b.Tools.SelectPolygons([3, 4], cancel: Cancel);
        var some = await Parse(b.Tools.GetPolygons(selected_only: true, cancel: Cancel));
        Assert.Equal([3, 4], some["polygons"]!.AsArray().Select(p => (int)p!["index"]!));
        var pts = await Parse(b.Tools.GetPoints(selected_only: true, cancel: Cancel));
        Assert.True((int)pts["total"]! is > 4 and < 20);

        Assert.Contains("no layer 12", await Error(b.Tools.GetPoints(layer: 12, cancel: Cancel)));
        Assert.Contains("limit must be from 1 to 5000", await Error(b.Tools.GetPolygons(limit: 0, cancel: Cancel)));
        Assert.Contains("offset can't be negative", await Error(b.Tools.GetPoints(offset: -1, cancel: Cancel)));
    }

    [Fact]
    public async Task Views_and_renders_come_back_as_pictures_with_something_in_them()
    {
        using var b = new Bench();
        var empty = Pngs.Of(await b.Tools.RenderView("front", 64, 48, "wireframe", cancel: Cancel));
        await b.Tools.RunTool("Ball", null, Cancel);
        await b.Tools.SetSurface("Default", color: [200, 60, 40], cancel: Cancel);
        foreach (var view in new[] { "perspective", "top", "front", "side" })
        {
            var result = await b.Tools.RenderView(view, 160, 120, "shaded", cancel: Cancel);
            var (w, h, colours) = Pngs.Inspect(Pngs.Of(result));
            Assert.Equal((160, 120), (w, h));
            Assert.True(colours > 8, $"{view} looks blank");
            Assert.Contains("framing x -0.5 to 0.5", Text(result));
        }
        // The flat views are wireframe whatever the mode; the perspective view shades the red ball.
        var shaded = TextureImages.Decode(Pngs.Of(await b.Tools.RenderView("perspective", 96, 96, "shaded", cancel: Cancel)))!;
        int red = Enumerable.Range(0, 96 * 96).Count(i => shaded.Rgba[i * 4] > shaded.Rgba[i * 4 + 1] + 60);
        Assert.True(red > 150, $"only {red} red pixels");
        Assert.NotEqual(empty, Pngs.Of(await b.Tools.RenderView("front", 64, 48, "wireframe", cancel: Cancel)));

        var traced = await b.Tools.Render(96, 72, 1, cancel: Cancel);
        var (tw, th, tc) = Pngs.Inspect(Pngs.Of(traced));
        Assert.Equal((96, 72), (tw, th));
        Assert.True(tc > 20, "the render looks blank");
        var image = TextureImages.Decode(Pngs.Of(traced))!;
        // The ball fills the middle of the frame.
        int mid = (36 * 96 + 48) * 4;
        Assert.True(image.Rgba[mid] > image.Rgba[mid + 2], "the middle should be the red ball");

        Assert.Contains("view must be one of top, front, side, perspective, not \"uv\"", await Error(b.Tools.RenderView("uv", cancel: Cancel)));
        Assert.Contains("16 to 2048", await Error(b.Tools.RenderView(width: 5000, cancel: Cancel)));
        Assert.Contains("samples", await Error(b.Tools.Render(samples: 99, cancel: Cancel)));
    }

    private static string Samples => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples");
}
