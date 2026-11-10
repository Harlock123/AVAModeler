using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Surfaces' node graphs in renders.</summary>
public class SurfaceNodeRenderTests
{
    private static readonly CancellationToken Cancel = TestContext.Current.CancellationToken;

    /// <summary>A grey wall filling a camera's view, lit head on.</summary>
    private static (Scene Scene, Surface Surface) Wall()
    {
        var scene = new Scene { AmbientIntensity = 0.2 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -2));
        cam.Width = cam.Height = 32;
        var doc = Doc(Square(Vec3.Zero, 4, -Vec3.UnitZ), s => { s.Color = new Rgb(128, 128, 128); s.Diffuse = 1; });
        scene.AddObject("Wall", doc);
        scene.AddLight("Sun").SetRotation(0, Vec3.Zero);
        return (scene, doc.Surfaces[0]);
    }

    [Fact]
    public void A_graph_colours_and_bumps_the_render_and_none_leaves_it_as_it_was()
    {
        var settings = new RenderSettings { SamplesPerPixel = 1 };
        var (scene, surface) = Wall();
        var plain = RayTracer.Render(scene, 0, settings, null, Cancel);
        // Red below, green above (a gradient up the object's Y): the picture's top and bottom.
        var g = SurfaceNodeGraph.New();
        var pos = g.Add(NodeKind.Position);
        var ramp = g.Add(NodeKind.Gradient);
        ramp.Stops = [new(-0.5, new Rgb(255, 0, 0)), new(0.5, new Rgb(0, 255, 0))];
        g.Link(pos.Id, "Y", ramp.Id, "Value");
        g.Link(ramp.Id, "Color", g.Output!.Id, "Color");
        surface.Nodes = g.Checked();
        var coloured = RayTracer.Render(scene, 0, settings, null, Cancel);
        var top = coloured.Pixel(16, 2);
        var bottom = coloured.Pixel(16, 29);
        Assert.True(top.G > 2 * top.R, $"{top}");
        Assert.True(bottom.R > 2 * bottom.G, $"{bottom}");
        // A bump of noise shades the flat wall unevenly; with nothing linked it's as before.
        var bumpy = SurfaceNodeGraph.New();
        var noise = bumpy.Add(NodeKind.Noise);
        noise.Numbers["Scale"] = 0.2;
        bumpy.Link(noise.Id, "Value", bumpy.Output!.Id, "Bump");
        bumpy.Output.Numbers["Bump Height"] = 0.05;
        surface.Nodes = bumpy.Checked();
        var rough = RayTracer.Render(scene, 0, settings, null, Cancel);
        var greens = Enumerable.Range(0, 32).Select(x => (int)rough.Pixel(x, 16).G).ToList();
        Assert.True(greens.Max() - greens.Min() > 10, string.Join(", ", greens));
        surface.Nodes = SurfaceNodeGraph.New();
        var again = RayTracer.Render(scene, 0, settings, null, Cancel);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++) Assert.Equal(plain.Pixel(x, y), again.Pixel(x, y));
    }
}
