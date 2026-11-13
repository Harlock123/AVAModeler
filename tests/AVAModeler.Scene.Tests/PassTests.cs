using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Render passes (depth, normal, object, albedo, emission, shadow, reflection) and the denoiser they guide.</summary>
public class PassTests
{
    private static Mesh Floor()
    {
        var m = new Mesh();
        m.Points.AddRange([new Vec3(-4, 0, -4), new Vec3(-4, 0, 4), new Vec3(4, 0, 4), new Vec3(4, 0, -4)]);
        m.Polygons.Add(new Polygon([0, 1, 2, 3], 0));
        m.Touch();
        return m;
    }

    private static (Scene, ObjectItem Ball, ObjectItem Ground) Stage(bool soft)
    {
        var s = new Scene { AmbientIntensity = 0.05 };
        var cam = s.AddCamera("Camera");
        cam[Channel.PositionY].SetConstant(2);
        cam[Channel.PositionZ].SetConstant(-5);
        cam[Channel.Pitch].SetConstant(20);
        cam.Width = 64;
        cam.Height = 48;
        var ground = s.AddObject("Ground", Doc(Floor(), surf => { surf.Color = new Rgb(200, 200, 200); surf.Reflection = 0.3; }));
        var ball = s.AddObject("Ball", Doc(Sphere(new Vec3(0, 0.6, 0), 0.6, 24, 12), surf => { surf.Color = new Rgb(220, 40, 30); surf.Luminosity = 0.1; }));
        var light = s.AddLight("Key", soft ? LightType.Area : LightType.Distant);
        light[Channel.PositionY].SetConstant(4);
        light[Channel.PositionX].SetConstant(-2);
        light[Channel.Pitch].SetConstant(70);
        light[Channel.Heading].SetConstant(30);
        if (soft) { light.AreaWidth = light.AreaHeight = 2; light.ShadowSamples = 4; }
        return (s, ball, ground);
    }

    [Fact]
    public void The_passes_say_what_each_pixel_sees()
    {
        var (s, ball, ground) = Stage(soft: false);
        var p = RayTracer.Passes(s, 1, null, RenderPass.All, TestContext.Current.CancellationToken);
        int Px(int x, int y) => y * p.Width + x;
        int centre = Px(32, 24), sky = Px(32, 1);
        var objects = s.Objects.ToList();
        Assert.Equal(objects.IndexOf(ball), p.ObjectId![centre]);
        Assert.Equal(-1, p.ObjectId[sky]);
        Assert.True(float.IsNaN(p.Depth![sky]) && p.Depth[centre] > 3 && p.Depth[centre] < 6);
        Assert.Equal(new Vec3(220, 40, 30) / 255, p.Albedo![centre], new Vec3Comparer(0.01));
        Assert.True(p.Emission![centre].X > 0.05);
        // The ground: facing up, partly in the ball's shadow, and reflecting.
        var groundPixels = Enumerable.Range(0, p.Width * p.Height).Where(i => p.ObjectId[i] == objects.IndexOf(ground)).ToList();
        Assert.All(groundPixels, i => Assert.True(p.Normal![i].Y > 0.99));
        Assert.Contains(groundPixels, i => p.Shadow![i] > 0.9);
        Assert.Contains(groundPixels, i => p.Shadow![i] < 0.01);
        Assert.Contains(groundPixels, i => p.Reflection![i].Length > 0.01);
        Assert.Equal(64, p.ToImage(RenderPass.Depth).Width);
        Assert.Throws<ArgumentException>(() => RayTracer.Passes(s, 1, null, RenderPass.Depth, TestContext.Current.CancellationToken).ToImage(RenderPass.Normal));
    }

    [Fact]
    public void The_denoiser_brings_a_noisy_render_closer_to_a_clean_one_without_blurring_across_objects()
    {
        // The same rays a pixel (so the edges alias alike), the soft light's shadows from 1 ray (noisy) or 64 (clean).
        var (s, _, _) = Stage(soft: true);
        var cancel = TestContext.Current.CancellationToken;
        var key = s.Lights.Single();
        key.ShadowSamples = 1;
        var noisy = RayTracer.Render(s, 1, new RenderSettings { SamplesPerPixel = 1 }, null, cancel);
        key.ShadowSamples = 64;
        var clean = RayTracer.Render(s, 1, new RenderSettings { SamplesPerPixel = 1 }, null, cancel);
        var guide = RayTracer.Passes(s, 1, null, RenderPass.Depth | RenderPass.Normal | RenderPass.ObjectId | RenderPass.Albedo, cancel);
        var smooth = RayTracer.Denoise(noisy, guide);
        double Error(RenderImage a)
        {
            double sum = 0;
            for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                {
                    var (r1, g1, b1, _) = a.Pixel(x, y);
                    var (r2, g2, b2, _) = clean.Pixel(x, y);
                    sum += (r1 - r2) * (r1 - r2) + (g1 - g2) * (g1 - g2) + (b1 - b2) * (b1 - b2);
                }
            return Math.Sqrt(sum / (a.Width * a.Height));
        }
        Assert.True(Error(smooth) < Error(noisy) * 0.6, $"denoised {Error(smooth):0.0} against noisy {Error(noisy):0.0}");
        // The render setting does the same in one go.
        key.ShadowSamples = 1;
        var inOne = RayTracer.Render(s, 1, new RenderSettings { SamplesPerPixel = 1, Denoise = true }, null, cancel);
        Assert.Equal(smooth.Rgba, inOne.Rgba);
        // Where nothing is hit, the picture is untouched.
        Assert.Equal(noisy.Pixel(32, 1), smooth.Pixel(32, 1));
        Assert.Throws<ArgumentException>(() => RayTracer.Denoise(noisy, RayTracer.Passes(s, 1, null, RenderPass.Depth, cancel)));
    }

    private sealed class Vec3Comparer(double tolerance) : IEqualityComparer<Vec3>
    {
        public bool Equals(Vec3 a, Vec3 b) => (a - b).Length <= tolerance;
        public int GetHashCode(Vec3 v) => 0;
    }
}
