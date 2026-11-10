namespace AVAModeler.Core.Tests;

/// <summary>Surfaces' node graphs: building and checking them, and what they give a surface at a point.</summary>
public class SurfaceNodeTests
{
    private static NodeContext At(Vec3 p, Vec3? normal = null, Vec3? toEye = null) => new(p, p, normal ?? Vec3.UnitY, toEye ?? Vec3.UnitY);

    private static void Near(Vec3 a, Vec3? b, double tol = 1e-9)
    {
        Assert.NotNull(b);
        Assert.True((a - b.Value).Length < tol, $"{a} against {b}");
    }

    [Fact]
    public void A_graph_gives_only_what_is_linked_into_its_output()
    {
        var g = SurfaceNodeGraph.New();
        Assert.Null(SurfaceNodes.Compile(g));
        var red = g.Add(NodeKind.Color);
        red.Colors["Color"] = new Rgb(255, 0, 0);
        g.Link(red.Id, "Color", g.Output!.Id, "Color");
        var look = SurfaceNodes.Compile(g.Checked())!.Evaluate(At(Vec3.Zero));
        Near(new Vec3(1, 0, 0), look.Color);
        Assert.Null(look.Diffuse);
        Assert.Null(look.Transparency);
        // A colour into a value input is its average.
        g.Link(red.Id, "Color", g.Output.Id, "Diffuse");
        Assert.Equal(1.0 / 3, SurfaceNodes.Compile(g)!.Evaluate(At(Vec3.Zero)).Diffuse!.Value, 9);
    }

    [Fact]
    public void Gradients_mixes_and_sums_work_out_as_they_say()
    {
        var g = SurfaceNodeGraph.New();
        var pos = g.Add(NodeKind.Position);
        var ramp = g.Add(NodeKind.Gradient);
        ramp.Stops = [new(0, new Rgb(0, 0, 255)), new(1, new Rgb(255, 255, 255))];
        g.Link(pos.Id, "Y", ramp.Id, "Value");
        var mix = g.Add(NodeKind.Mix, option: "Multiply");
        mix.Colors["B"] = new Rgb(255, 0, 0);
        mix.Numbers["Factor"] = 1;
        g.Link(ramp.Id, "Color", mix.Id, "A");
        g.Link(mix.Id, "Color", g.Output!.Id, "Color");
        var sum = g.Add(NodeKind.Math, option: "Invert");
        g.Link(pos.Id, "Y", sum.Id, "A");
        g.Link(sum.Id, "Value", g.Output.Id, "Specular");
        var p = SurfaceNodes.Compile(g.Checked())!;
        // Half way up the ramp: (0.5, 0.5, 1) times red.
        var look = p.Evaluate(At(new Vec3(0, 0.5, 0)));
        Near(new Vec3(0.5, 0, 0), look.Color);
        Assert.Equal(0.5, look.Specular!.Value, 9);
        // Past the ramp's ends: its end colours.
        Near(new Vec3(0, 0, 0), p.Evaluate(At(new Vec3(0, -3, 0))).Color);
        Near(new Vec3(1, 0, 0), p.Evaluate(At(new Vec3(0, 3, 0))).Color);
        // Facing and slope.
        var facing = SurfaceNodeGraph.New();
        var inc = facing.Add(NodeKind.Incidence);
        var slope = facing.Add(NodeKind.Slope);
        facing.Link(inc.Id, "Value", facing.Output!.Id, "Luminosity");
        facing.Link(slope.Id, "Slope", facing.Output.Id, "Reflection");
        var fp = SurfaceNodes.Compile(facing)!;
        var wall = fp.Evaluate(At(Vec3.Zero, Vec3.UnitX, new Vec3(1, 1, 0)));
        Assert.Equal(Math.Sqrt(0.5), wall.Luminosity!.Value, 9);
        Assert.Equal(1, wall.Reflection!.Value, 9);
        Assert.Equal(0, fp.Evaluate(At(Vec3.Zero)).Reflection!.Value, 9);
    }

    [Fact]
    public void Textures_and_noise_vary_over_the_surface_the_same_every_time()
    {
        var g = SurfaceNodeGraph.New();
        var check = g.Add(NodeKind.Texture, option: "Checkerboard");
        check.Numbers["Size"] = 0.5;
        g.Link(check.Id, "Value", g.Output!.Id, "Diffuse");
        var noise = g.Add(NodeKind.Noise);
        noise.Numbers["Scale"] = 0.3;
        g.Link(noise.Id, "Value", g.Output.Id, "Transparency");
        var p = SurfaceNodes.Compile(g.Checked())!;
        Assert.NotEqual(p.Evaluate(At(new Vec3(0.25, 0.1, 0.25))).Diffuse, p.Evaluate(At(new Vec3(0.75, 0.1, 0.25))).Diffuse);
        var values = Enumerable.Range(0, 50).Select(i => p.Evaluate(At(new Vec3(i * 0.07, 0.2, 0.1))).Transparency!.Value).ToList();
        Assert.All(values, v => Assert.InRange(v, 0, 1));
        Assert.True(values.Max() - values.Min() > 0.1);
        Assert.Equal(values, Enumerable.Range(0, 50).Select(i => SurfaceNodes.Compile(g)!.Evaluate(At(new Vec3(i * 0.07, 0.2, 0.1))).Transparency!.Value));
        // A bump's height.
        g.Link(noise.Id, "Value", g.Output.Id, "Bump");
        var b = SurfaceNodes.Compile(g)!;
        Assert.True(b.HasBump);
        Assert.Equal(0.01, b.BumpHeight);
        Assert.Equal(values[3], b.Height(At(new Vec3(3 * 0.07, 0.2, 0.1))), 12);
    }

    [Fact]
    public void Graphs_are_checked_and_kept_as_json()
    {
        var g = SurfaceNodeGraph.New();
        var a = g.Add(NodeKind.Math);
        var b = g.Add(NodeKind.Math);
        g.Link(a.Id, "Value", b.Id, "A");
        Assert.Contains("loop", Assert.Throws<ArgumentException>(() => g.Link(b.Id, "Value", a.Id, "A")).Message);
        Assert.Contains("loop", Assert.Throws<ArgumentException>(() => g.Link(a.Id, "Value", a.Id, "B")).Message);
        Assert.Contains("no output \"Colour\"", Assert.Throws<ArgumentException>(() => g.Link(a.Id, "Colour", b.Id, "B")).Message);
        Assert.Contains("no input", Assert.Throws<ArgumentException>(() => g.Link(a.Id, "Value", b.Id, "C")).Message);
        Assert.Contains("one output", Assert.Throws<ArgumentException>(() => g.Add(NodeKind.Output)).Message);
        var tex = g.Add(NodeKind.Texture, option: "Wood");
        tex.Numbers["Ring Spacing"] = 0.2;
        g.Link(tex.Id, "Value", b.Id, "B");
        g.Link(b.Id, "Value", g.Output!.Id, "Diffuse");
        var back = SurfaceNodeGraph.FromJson(g.ToJson());
        Assert.Equal(g.ToJson(), back.ToJson());
        Assert.Equal(0.2, back.Find(tex.Id)!.Number("Ring Spacing"));
        tex.Numbers["Ring Spacing"] = -5;
        Assert.Contains("Ring Spacing", Assert.Throws<ArgumentException>(() => g.Checked()).Message);
        tex.Numbers.Clear();
        tex.Option = "Teapot";
        Assert.Contains("isn't one of", Assert.Throws<ArgumentException>(() => g.Checked()).Message);
        tex.Option = "wood";
        Assert.Equal("Wood", g.Checked().Find(tex.Id)!.Option);
        // Taking a node away takes its links.
        g.Remove(tex.Id);
        Assert.False(b.Links.ContainsKey("B"));
        // A surface copies its graph, and its signature changes with it.
        var s = new Surface("Wood") { Nodes = g };
        var sig = s.Signature();
        var copy = s.Clone();
        copy.Nodes!.Find(a.Id)!.Option = "Multiply";
        Assert.Equal(sig, s.Signature());
        Assert.NotEqual(sig, copy.Signature());
    }
}
