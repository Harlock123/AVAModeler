using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>A baked normal map makes a flat face shade like the surface it was baked from.</summary>
public class NormalMapRenderTests
{
    private static Mesh Square()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(-1, -1, 0), new Vec3(-1, 1, 0), new Vec3(1, 1, 0), new Vec3(1, -1, 0));
        var poly = m.Polygons[0];
        for (int i = 0; i < 4; i++) m.SetCornerUV("UV", poly, poly.Points[i], new Vec2((m.Points[i].X + 1) / 2, (m.Points[i].Y + 1) / 2));
        return m;
    }

    private static int Middle(Mesh mesh, Action<Surface>? surface = null)
    {
        var scene = new AVAModeler.Scene.Scene { AmbientIntensity = 0 };
        var doc = new Document(); doc.Layers[0].Mesh = mesh; doc.Surfaces[0].Diffuse = 1; doc.Surfaces[0].Color = new Rgb(255, 255, 255); surface?.Invoke(doc.Surfaces[0]); scene.AddObject("Square", doc);
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Width = 20;
        cam.Height = 20;
        var light = scene.AddLight("Light");
        light.Type = LightType.Distant;
        light.SetRotation(0, new Vec3(0, 0, 0)); // shining along +Z, into the square's front
        return RayTracer.Render(scene, 0, cancel: TestContext.Current.CancellationToken).Pixel(10, 10).R;
    }

    [Fact]
    public void The_normal_map_shades_the_flat_square_as_the_tilted_one()
    {
        var tilted = Square();
        tilted.Transform(Mat4.Rotation(Vec3.UnitY, Mat4.Deg(40)));
        int expected = Middle(tilted);
        int flat = Middle(Square());
        Assert.True(flat > expected + 20, $"flat {flat}, tilted {expected}");

        var low = Square();
        var detail = Square();
        detail.Transform(Mat4.Rotation(Vec3.UnitY, Mat4.Deg(40)));
        var baked = Baking.Bake(low, [0], [new Surface("Default")], new BakeSettings { Size = 64, Distance = 2 }, detail);
        string path = Path.Combine(Path.GetTempPath(), $"ava-normal-{Guid.NewGuid():N}.png");
        TextureImages.Put(path, new TextureImage(baked.Width, baked.Height, baked.Rgba));
        int mapped = Middle(Square(), s => s.Textures.Add(new TextureLayer { Channel = TextureChannel.Normal, Image = path, Projection = TextureProjection.UV, UVMap = "UV" }));
        Assert.InRange(mapped, expected - 6, expected + 6);
    }
}
