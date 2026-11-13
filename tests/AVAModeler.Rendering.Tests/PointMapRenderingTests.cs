using AVAModeler.Core;
using AVAModeler.Rendering;

namespace AVAModeler.Rendering.Tests;

/// <summary>The shaded views colour faces by a shown weight or colour map, in both renderers.</summary>
public class PointMapRenderingTests
{
    private static (int R, int G, int B) Rgb8(uint c) => TextureRenderingTests.Rgb8(c);

    private static Document Square(PointMapKind kind)
    {
        var d = new Document();
        var m = d.Layers[0].Mesh;
        m.Append(TextureRenderingTests.Square());
        d.Surfaces[0].Luminosity = 1;
        var map = m.AddPointMap("Map", kind);
        for (int i = 0; i < m.Points.Count; i++)
            if (kind == PointMapKind.Weight) map.SetWeight(i, 1);
            else map.SetColor(i, new Vec3(0, 0, 1));
        return d;
    }

    private static SoftwareRenderer Front(ViewScene scene)
    {
        var c = new ViewCamera(ViewKind.Perspective) { Heading = 0, Pitch = 0, Distance = 2.5 };
        c.Resize(100, 100);
        var r = new SoftwareRenderer();
        r.Render(scene, c);
        return r;
    }

    [Fact]
    public void A_shown_weight_map_draws_in_heat_colours_and_a_colour_map_as_it_is()
    {
        var d = Square(PointMapKind.Weight);
        var plain = ViewScene.FromDocument(d, DisplayStyle.Shaded, backfaceTint: false);
        Assert.Null(plain.Layers[0].PointColors);
        d.ShownPointMap = "Map";
        var shown = ViewScene.FromDocument(d, DisplayStyle.Shaded, backfaceTint: false);
        shown.ShowGrid = false;
        Assert.NotNull(shown.Layers[0].PointColors);
        var (r, g, b) = Rgb8(Front(shown).Pixels[50 * 100 + 50]);
        Assert.True(r > 200 && g < 80 && b < 80, $"weight 1 should be red, not {r},{g},{b}");

        var c = Square(PointMapKind.Rgb);
        c.ShownPointMap = "Map";
        var colors = ViewScene.FromDocument(c, DisplayStyle.Shaded, backfaceTint: false);
        colors.ShowGrid = false;
        Assert.Equal((0, 0, 255), Rgb8(Front(colors).Pixels[50 * 100 + 50]));

        // OpenGL builds its vertices with the same colours.
        var cam = new ViewCamera(ViewKind.Perspective) { Heading = 0, Pitch = 0, Distance = 2.5 };
        var v = new List<float>();
        GlRenderer.FaceTriangles(colors.Layers[0], colors, cam, v, []);
        Assert.Equal(0f, v[3]);
        Assert.Equal(1f, v[5]);
        // A map name the mesh doesn't have draws the surfaces.
        c.ShownPointMap = "Other";
        Assert.Null(ViewScene.FromDocument(c, DisplayStyle.Shaded).Layers[0].PointColors);
    }
}
