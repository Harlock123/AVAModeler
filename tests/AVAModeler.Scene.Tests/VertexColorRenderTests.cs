using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>A surface's vertex colour map multiplies its colour in renders.</summary>
public class VertexColorRenderTests
{
    private static (byte R, byte G, byte B, byte A) Middle(double intensity, string? map)
    {
        var mesh = new Mesh();
        // A square facing the camera at z = 0 (wound clockwise as seen from -Z).
        mesh.AddFace(0, new Vec3(-1, -1, 0), new Vec3(-1, 1, 0), new Vec3(1, 1, 0), new Vec3(1, -1, 0));
        var colors = mesh.AddPointMap("Paint", PointMapKind.Rgb);
        for (int i = 0; i < 4; i++) colors.SetColor(i, new Vec3(0, 1, 0));
        var scene = new Scene { AmbientIntensity = 0 };
        scene.AddObject("Square", Doc(mesh, s =>
        {
            s.Color = new Rgb(255, 255, 255);
            s.Luminosity = 1;
            s.Diffuse = 0;
            s.VertexColorMap = map;
            s.VertexColorIntensity = intensity;
        }));
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Width = 20;
        cam.Height = 20;
        return RayTracer.Render(scene, 0, cancel: TestContext.Current.CancellationToken).Pixel(10, 10);
    }

    [Fact]
    public void The_map_tints_the_surface_by_its_intensity()
    {
        Assert.Equal(((byte)255, (byte)255, (byte)255), Rgb3(Middle(1, null)));
        Assert.Equal(((byte)0, (byte)255, (byte)0), Rgb3(Middle(1, "Paint")));
        var half = Middle(0.5, "Paint");
        Assert.InRange(half.R, 120, 135);
        Assert.Equal(255, half.G);
        // A map the mesh hasn't got leaves the surface as it is.
        Assert.Equal(((byte)255, (byte)255, (byte)255), Rgb3(Middle(1, "Missing")));
    }

    private static (byte, byte, byte) Rgb3((byte R, byte G, byte B, byte A) p) => (p.R, p.G, p.B);
}
