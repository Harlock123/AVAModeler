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

    [Fact]
    public async Task An_agent_chooses_how_particles_look_and_what_they_do_on_a_hit()
    {
        using var b = await WithBox();
        string box = b.Scene.Objects.Single().Name;
        var r = await Parse(b.Tools.SetParticles(box, look: "puff", opacity: 0.4, collide: true, on_hit: "bounce", bounce: 0.8, cancel: Cancel));
        Assert.Equal("puff", (string?)r["particles"]!["look"]);
        Assert.Equal(0.4, (double)r["particles"]!["opacity"]!);
        Assert.Equal(0.8, (double)r["particles"]!["bounce"]!);
        Assert.True((bool)r["particles"]!["collide"]!);
        // Copies of an object need one that's there.
        Assert.Contains("no other object called \"Leaf\"", await Error(b.Tools.SetParticles(box, look: "object", instance_of: "Leaf", cancel: Cancel)));
        Assert.Contains("gem, blob, puff, sprite or object", await Error(b.Tools.SetParticles(box, look: "smoke", cancel: Cancel)));
        Assert.Contains("stick, bounce or die", await Error(b.Tools.SetParticles(box, on_hit: "explode", cancel: Cancel)));
        Assert.Equal(Scene.ParticleLook.Puff, b.Scene.Objects.Single().Particles!.Look);
    }

    [Fact]
    public async Task An_agent_combs_hair_makes_it_swing_and_makes_an_object_cloth()
    {
        using var b = await WithBox();
        string box = b.Scene.Objects.Single().Name;
        var r = await Parse(b.Tools.SetHair(box, density: 10, dynamics: true, stiffness: 4, comb: 0.5, comb_direction: [1, 0, 0], cancel: Cancel));
        Assert.True((bool)r["hair"]!["dynamics"]!);
        Assert.Equal(4, (double)r["hair"]!["stiffness"]!);
        Assert.Equal(new JsonArray(1, 0, 0).ToJsonString(), r["hair"]!["comb_direction"]!.ToJsonString());
        Assert.True((int)r["strands"]! > 0);
        Assert.Contains("three numbers", await Error(b.Tools.SetHair(box, comb_direction: [1, 0], cancel: Cancel)));
        // Cloth: settings, the census, mistakes, and solid again.
        r = await Parse(b.Tools.SetSoftBody(box, goal: 0.5, floor: 0, collide: true, cancel: Cancel));
        Assert.Equal(0.5, (double)r["soft_body"]!["goal"]!);
        Assert.Equal(0, (double)r["soft_body"]!["floor"]!);
        Assert.Equal("Agent: set soft body", (string?)r["undo_scene"]!["next_undo"]);
        var item = (await Parse(b.Tools.GetScene(cancel: Cancel)))["items"]!.AsArray().First(i => (string?)i!["name"] == box)!;
        Assert.True((bool)item["soft_body"]!["collide"]!);
        Assert.Contains("no surface \"Pole\"", await Error(b.Tools.SetSoftBody(box, pin: "Pole", cancel: Cancel)));
        Assert.Contains("stiffness", await Error(b.Tools.SetSoftBody(box, stiffness: 3, cancel: Cancel)));
        Assert.Equal(0.5, b.Scene.Objects.Single().SoftBody!.Goal);
        await b.Tools.SetSoftBody(box, off: true, cancel: Cancel);
        Assert.Null(b.Scene.Objects.Single().SoftBody);
    }

    [Fact]
    public async Task An_agent_combs_hair_by_hand_pours_a_fluid_and_keeps_cloth_off_itself()
    {
        using var b = await WithBox();
        string box = b.Scene.Objects.Single().Name;
        Assert.Contains("no hair", await Error(b.Tools.CombHair(box, direction: [1, 0, 0], cancel: Cancel)));
        await b.Tools.SetHair(box, density: 10, cancel: Cancel);
        // A brush at one corner of the cube combs that corner's point only.
        var r = await Parse(b.Tools.CombHair(box, direction: [0, 0, -1], at: [1, 1, 1], radius: 0.5, cancel: Cancel));
        Assert.Equal(1, (int)r["hair"]!["combed_points"]!);
        Assert.Equal("Agent: combed hair", (string?)r["undo_scene"]!["next_undo"]);
        // Every point, then uncombed half way, then cleared.
        r = await Parse(b.Tools.CombHair(box, direction: [1, 0, 0], cancel: Cancel));
        Assert.Equal(8, (int)r["hair"]!["combed_points"]!);
        await b.Tools.CombHair(box, uncomb: true, strength: 0.5, cancel: Cancel);
        Assert.Equal(0.5, b.Scene.Objects.Single().Hair!.Combing!.Directions[0].Length, 6);
        r = await Parse(b.Tools.CombHair(box, clear: true, cancel: Cancel));
        Assert.Equal(0, (int)r["hair"]!["combed_points"]!);
        Assert.Contains("direction", await Error(b.Tools.CombHair(box, cancel: Cancel)));
        Assert.Contains("strength", await Error(b.Tools.CombHair(box, direction: [1, 0, 0], strength: 2, cancel: Cancel)));
        // A fluid: its settings in the census, and no count (it's stepped).
        r = await Parse(b.Tools.SetParticles(box, fluid: true, viscosity: 0.3, look: "blob", cancel: Cancel));
        Assert.True((bool)r["particles"]!["fluid"]!);
        Assert.Equal(0.3, (double)r["particles"]!["viscosity"]!);
        Assert.Null(r["alive_now"]);
        Assert.Contains("viscosity", await Error(b.Tools.SetParticles(box, viscosity: 5, cancel: Cancel)));
        // Cloth that keeps off itself.
        r = await Parse(b.Tools.SetSoftBody(box, self_collide: true, self_distance: 0.05, cancel: Cancel));
        Assert.True((bool)r["soft_body"]!["self_collide"]!);
        Assert.Equal(0.05, (double)r["soft_body"]!["self_distance"]!);
    }
}
