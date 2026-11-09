using System.Text.Json.Nodes;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>set_particles and set_hair: their settings, undo, the census, and their mistakes.</summary>
public class ParticleHandlerTests
{
    private static async Task<Bench> WithBox()
    {
        var b = new Bench();
        await b.Tools.RunTool("Box", Bench.Args(new { Size = new[] { 2.0, 2, 2 } }), Cancel);
        await b.Tools.AddObject(cancel: Cancel);
        return b;
    }

    [Fact]
    public async Task An_agent_puts_an_emitter_on_an_object_changes_it_and_takes_it_away()
    {
        using var b = await WithBox();
        string box = b.Scene.Objects.Single().Name;
        b.Layout.SetFrame(30);
        var r = await Parse(b.Tools.SetParticles(box, rate: 20, gravity: 0, color: [255, 120, 0], floor: 0, cancel: Cancel));
        Assert.Equal(20, (double)r["particles"]!["rate"]!);
        Assert.Equal(new JsonArray(255, 120, 0).ToJsonString(), r["particles"]!["color"]!.ToJsonString());
        Assert.Equal(0, (double)r["particles"]!["floor"]!);
        // A second in at 20 a second: 21 alive.
        Assert.Equal(21, (int)r["alive_now"]!);
        Assert.Equal("Agent: set particles", (string?)r["undo_scene"]!["next_undo"]);
        // Changing one setting keeps the rest; the census describes it.
        await b.Tools.SetParticles(box, emit_from: "origin", no_floor: true, cancel: Cancel);
        var p = b.Scene.Objects.Single().Particles!;
        Assert.Equal((20.0, Scene.EmitFrom.Origin, (double?)null), (p.Rate, p.From, p.Floor));
        var item = (await Parse(b.Tools.GetScene(cancel: Cancel)))["items"]!.AsArray().First(i => (string?)i!["name"] == box)!;
        Assert.Equal("origin", (string?)item["particles"]!["emit_from"]);
        // Mistakes change nothing.
        Assert.Contains("surface, points or origin", await Error(b.Tools.SetParticles(box, emit_from: "everywhere", cancel: Cancel)));
        Assert.Contains("life", await Error(b.Tools.SetParticles(box, life: 0, cancel: Cancel)));
        Assert.Equal(20, b.Scene.Objects.Single().Particles!.Rate);
        await b.Tools.SetParticles(box, off: true, cancel: Cancel);
        Assert.Null(b.Scene.Objects.Single().Particles);
        await b.Tools.UndoScene(Cancel);
        Assert.NotNull(b.Scene.Objects.Single().Particles);
    }

    [Fact]
    public async Task An_agent_grows_hair_on_an_object()
    {
        using var b = await WithBox();
        string box = b.Scene.Objects.Single().Name;
        // A 2 m cube has 24 m² of faces: at 100 a square metre, 2400 strands.
        var r = await Parse(b.Tools.SetHair(box, density: 100, length: 0.2, segments: 3, cancel: Cancel));
        Assert.Equal(2400, (int)r["strands"]!);
        Assert.Equal(0.2, (double)r["hair"]!["length"]!);
        Assert.Contains("no surface \"Fur\"", await Error(b.Tools.SetHair(box, on_surface: "Fur", cancel: Cancel)));
        Assert.Contains("segments", await Error(b.Tools.SetHair(box, segments: 0, cancel: Cancel)));
        var item = (await Parse(b.Tools.GetScene(cancel: Cancel)))["items"]!.AsArray().First(i => (string?)i!["name"] == box)!;
        Assert.Equal(3, (int)item["hair"]!["segments"]!);
        await b.Tools.SetHair(box, off: true, cancel: Cancel);
        Assert.Null(b.Scene.Objects.Single().Hair);
    }
}
