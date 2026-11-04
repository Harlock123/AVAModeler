using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using ModelContextProtocol.Protocol;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_procedural: LightWave 5's procedural textures for agents, with no image files.</summary>
public class ProceduralHandlerTests
{
    private static Dictionary<string, System.Text.Json.JsonElement> P(object o) => Bench.Args(o);

    [Fact]
    public async Task Set_procedural_lays_a_procedural_with_its_settings_as_one_undo_step()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", P(new { Surface = "Top" }), Cancel);
        int steps = b.Doc.Undo.Count;
        var r = await Parse(b.Tools.SetProcedural("Top", "marble", color: [40, 60, 50], size: [0.2, 0.2, 0.2], axis: "x",
            settings: P(new { vein_spacing = 0.3, frequencies = 6, VeinSharpness = 8 }), cancel: Cancel));
        Assert.Equal(steps + 1, b.Doc.Undo.Count);
        var t = r["texture"]!;
        Assert.Equal(("marble", "color", "x"), ((string?)t["procedural"], (string?)t["channel"], (string?)t["axis"]));
        Assert.Equal([40, 60, 50], t["color"]!.AsArray().Select(v => (int)v!));
        Assert.Equal((0.3, 6.0, 8.0, 0.5), ((double)t["settings"]!["vein_spacing"]!, (double)t["settings"]!["frequencies"]!,
            (double)t["settings"]!["vein_sharpness"]!, (double)t["settings"]!["turbulence"]!));
        var layer = Assert.Single(b.Doc.Surfaces.Single(s => s.Name == "Top").Textures);
        Assert.Equal(("Marble", new Vec3(0.2, 0.2, 0.2), Axis.X), (layer.Procedural, layer.Size, layer.Axis));

        // Again: the same layer, changed; new_layer adds one; other channels take a value, bump an amplitude.
        await b.Tools.SetProcedural("Top", "marble", settings: P(new { turbulence = 1 }), cancel: Cancel);
        Assert.Equal((1.0, 0.3), (Procedurals.Get(layer, "Turbulence"), Procedurals.Get(layer, "Vein Spacing")));
        await b.Tools.SetProcedural("Top", "marble", new_layer: true, opacity: 0.5, blend: "multiply", cancel: Cancel);
        var spec = await Parse(b.Tools.SetProcedural("Top", "fractal_noise", channel: "specular", value: 0.8, falloff: [0.5, 0, 0], negative: true, cancel: Cancel));
        Assert.Equal(0.8, (double)spec["texture"]!["value"]!);
        Assert.Equal(true, (bool?)spec["texture"]!["negative"]);
        var bump = await Parse(b.Tools.SetProcedural("Top", "ripples", channel: "bump", amplitude: 0.05, world_coordinates: true, cancel: Cancel));
        Assert.Equal(0.05, (double)bump["texture"]!["amplitude"]!);
        var textures = b.Doc.Surfaces.Single(s => s.Name == "Top").Textures;
        Assert.Equal(["Marble", "Marble", "Fractal Noise", "Ripples"], textures.Select(x => x.Procedural));
        Assert.True(textures[3].WorldCoordinates);
        // get_document describes them.
        var doc = await Parse(b.Tools.GetDocument(Cancel));
        var listed = doc["surfaces"]!.AsArray().Single(s => (string?)s!["name"] == "Top")!["textures"]!.AsArray();
        Assert.Equal(["marble", "marble", "fractal_noise", "ripples"], listed.Select(x => (string)x!["procedural"]!));
        Assert.Null(listed[0]!["image"]);

        // Each call is one step; undo takes the last off.
        await b.Tools.Undo(Cancel);
        Assert.Equal(3, b.Doc.Surfaces.Single(s => s.Name == "Top").Textures.Count);
    }

    [Fact]
    public async Task Set_procedurals_mistakes_say_what_to_do_and_change_nothing()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", P(new { Surface = "Top" }), Cancel);
        int steps = b.Doc.Undo.Count;
        Assert.Contains("checkerboard, grid, dots", await Error(b.Tools.SetProcedural("Top", "plaid", cancel: Cancel)));
        Assert.Contains("its settings are frequencies, turbulence, vein_spacing, vein_sharpness",
            await Error(b.Tools.SetProcedural("Top", "marble", settings: P(new { wavelength = 1 }), cancel: Cancel)));
        Assert.Contains("checkerboard has no settings", await Error(b.Tools.SetProcedural("Top", "checkerboard", settings: P(new { size = 1 }), cancel: Cancel)));
        Assert.Contains("from 1 to 8", await Error(b.Tools.SetProcedural("Top", "wood", settings: P(new { frequencies = 12 }), cancel: Cancel)));
        Assert.Contains("must be a number", await Error(b.Tools.SetProcedural("Top", "wood", settings: P(new { frequencies = "many" }), cancel: Cancel)));
        Assert.Contains("for other channels give value", await Error(b.Tools.SetProcedural("Top", "grid", channel: "diffuse", color: [1, 2, 3], cancel: Cancel)));
        Assert.Contains("on color give color", await Error(b.Tools.SetProcedural("Top", "grid", value: 0.5, cancel: Cancel)));
        Assert.Contains("not 0 to 1", await Error(b.Tools.SetProcedural("Top", "grid", color: [0.5, 0.5, 0.5], cancel: Cancel)));
        Assert.Contains("positive", await Error(b.Tools.SetProcedural("Top", "grid", size: [1, 0, 1], cancel: Cancel)));
        Assert.Contains("must be one of", await Error(b.Tools.SetProcedural("Top", "grid", channel: "gloss", cancel: Cancel)));
        Assert.Empty(b.Doc.Surfaces.Single(s => s.Name == "Top").Textures);
        Assert.Equal(steps, b.Doc.Undo.Count);
    }

    [Fact]
    public void The_tools_description_lists_every_kind_and_setting_with_its_default()
    {
        foreach (var kind in Procedurals.All)
        {
            Assert.Contains(kind.Key + " (", ModelerTools.ProceduralKinds);
            int at = ModelerTools.ProceduralKinds.IndexOf(kind.Key + " (", StringComparison.Ordinal);
            string entry = ModelerTools.ProceduralKinds[at..ModelerTools.ProceduralKinds.IndexOf(')', at)];
            foreach (var p in kind.Parameters) Assert.Contains($"{p.Key} {p.Default.ToString(CultureInfo.InvariantCulture)}", entry);
            if (kind.Parameters.Count == 0) Assert.Contains("no settings", entry);
        }
        Assert.Contains("procedural", ModelerServer.Instructions);
        Assert.Contains("set_procedural", ModelerServer.Instructions);
    }

    [Fact]
    public async Task An_agent_textures_with_procedurals_and_sees_them_through_a_client()
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        await s.Client.Json("run_tool", new { name = "Box", @params = new Dictionary<string, object> { ["Size"] = new[] { 2.0, 0, 2 }, ["Surface"] = "Floor" } });
        await s.Client.Json("set_surface", new { name = "Floor", color = new[] { 255, 255, 255 }, luminosity = 1, diffuse = 0, assign = false });
        var plain = await s.Client.CallToolAsync("render_view", new Dictionary<string, object?> { ["view"] = "perspective", ["mode"] = "textured", ["width"] = 160, ["height"] = 120 }, cancellationToken: Cancel);
        await s.Client.Json("set_procedural", new { surface = "Floor", kind = "checkerboard", color = new[] { 255, 0, 0 }, size = new[] { 0.5, 0.5, 0.5 } });
        var view = await s.Client.CallToolAsync("render_view", new Dictionary<string, object?> { ["view"] = "perspective", ["mode"] = "textured", ["width"] = 160, ["height"] = 120 }, cancellationToken: Cancel);
        var render = await s.Client.CallToolAsync("render", new Dictionary<string, object?> { ["width"] = 96, ["height"] = 72, ["samples"] = 1 }, cancellationToken: Cancel);
        Assert.True(view.IsError != true && render.IsError != true);
        byte[] Picture(CallToolResult r) => r.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray();
        // The view shows the red checks, which the plain white floor didn't have.
        int Red(CallToolResult r)
        {
            var img = TextureImages.Decode(Picture(r))!;
            int n = 0;
            for (int i = 0; i < img.Rgba.Length; i += 4)
                if (img.Rgba[i] > 200 && img.Rgba[i + 1] < 60 && img.Rgba[i + 2] < 60) n++;
            return n;
        }
        Assert.Equal(0, Red(plain));
        Assert.True(Red(view) > 500, $"{Red(view)} red pixels");
        Assert.True(Pngs.Inspect(Picture(render)).Colours > 8);
        // The schema takes settings as an object of numbers.
        var bad = await s.Client.CallToolAsync("set_procedural", new Dictionary<string, object?> { ["surface"] = "Floor", ["kind"] = "grid", ["settings"] = new Dictionary<string, object> { ["line_thickness"] = 0.2 } }, cancellationToken: Cancel);
        Assert.True(bad.IsError != true, Text(bad));
        Assert.Equal(0.2, Procedurals.Get(b.Doc.Surfaces.Single(x => x.Name == "Floor").Textures.Single(t => t.Procedural == "Grid"), "Line Thickness"), 9);
    }
}
