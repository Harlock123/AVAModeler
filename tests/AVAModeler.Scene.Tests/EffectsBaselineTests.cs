using System.Security.Cryptography;
using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>
/// The render effects (ambient occlusion, soft and area lights, environment, depth of field, motion blur) are all off
/// by default, and then a render is byte for byte what it was before they existed: these hashes were taken from the
/// ray tracer before the effects were added.
/// </summary>
public class EffectsBaselineTests
{
    /// <summary>
    /// A busy scene: smooth, reflective, transparent and procedurally textured surfaces; distant, point (with range)
    /// and spot lights with shadows; a gradient backdrop; fog; an object moving between keys.
    /// </summary>
    internal static Scene Busy()
    {
        var scene = new Scene { AmbientIntensity = 0.2, GradientBackground = true, Fog = FogType.Linear, FogMinDistance = 6, FogMaxDistance = 30 };
        scene.GradientTop = new Vec3(0.2, 0.3, 0.7);
        scene.GradientBottom = new Vec3(0.4, 0.3, 0.2);
        scene.FogColor = new Vec3(0.5, 0.5, 0.55);
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0.3, 1.6, -5));
        cam.SetRotation(0, new Vec3(-3, 14, 0));
        var sun = scene.AddLight("Sun");
        sun.SetRotation(0, new Vec3(30, 50, 0));
        var point = scene.AddLight("Point", LightType.Point);
        point.SetPosition(0, new Vec3(-1.5, 1.5, -1));
        point.Range = 8;
        point.Color = new Vec3(1, 0.8, 0.6);
        var spot = scene.AddLight("Spot", LightType.Spot);
        spot.SetPosition(0, new Vec3(1.5, 3, -1.5));
        spot.SetRotation(0, new Vec3(-30, 55, 0));
        spot.ConeAngle = 25;
        spot.EdgeAngle = 8;
        spot.Intensity.SetConstant(0.7);

        var floor = Doc(Square(new Vec3(0, -1, 0), 12, Vec3.UnitY), s =>
        {
            s.Name = "Floor";
            s.Reflection = 0.2;
            var check = new TextureLayer { Channel = TextureChannel.Color, Size = new Vec3(0.5, 0.5, 0.5) };
            Procedurals.SetType(check, "Checkerboard");
            check.TextureColor = new Rgb(40, 40, 50);
            s.Textures.Add(check);
        });
        scene.AddObject("Floor", floor);
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1, 32, 16), s => { s.Smoothing = true; s.Specular = 0.6; s.Glossiness = 64; s.Color = new Rgb(200, 60, 40); }));
        var glass = scene.AddObject("Glass", Doc(Sphere(Vec3.Zero, 0.5, 24, 12), s => { s.Name = "Glass"; s.Smoothing = true; s.Transparency = 0.8; s.Diffuse = 0.2; s.Specular = 0.8; s.Glossiness = 256; }));
        glass.SetPosition(0, new Vec3(1.6, -0.5, -0.4));
        glass.Channels[0].AddKey(0, 1.6);
        glass.Channels[0].AddKey(10, 0.8);
        return scene;
    }

    internal static string Hash(RenderImage image) => Convert.ToHexString(SHA256.HashData(image.Rgba));

    private static RenderImage Render(Scene scene, double frame, int samples) =>
        RayTracer.Render(scene, frame, new RenderSettings { Width = 64, Height = 48, SamplesPerPixel = samples, RefractionIndices = { ["Glass"] = 1.4 } }, null,
            TestContext.Current.CancellationToken);

    [Fact]
    public void With_every_effect_off_renders_are_what_they_were()
    {
        var scene = Busy();
        Assert.Equal(Expected1, Hash(Render(scene, 0, 1)));
        Assert.Equal(Expected4, Hash(Render(scene, 5, 4)));
        // A copy (as Render Animation and the MCP server render) renders the same.
        Assert.Equal(Expected4, Hash(Render(scene.Copy(), 5, 4)));
    }

    private const string Expected1 = "0B58818F8259B17E20DB8CBE39CAD1549DD5ABEDC36471429129A2D3899534B2";
    private const string Expected4 = "B9BF5E012E0EE409B3CA21EB299D8A752731AD8C1E76FAFEA96AD42DE7CD8132";
}
