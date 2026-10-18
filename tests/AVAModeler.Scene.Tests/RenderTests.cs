using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class RenderTests
{
    private static readonly Vec3 Blue = new(0, 0, 1);

    /// <summary>A camera at z = -4 looking down +Z, a light shining the same way, a blue backdrop and no ambient light.</summary>
    private static (Scene Scene, LightItem Light) Stage()
    {
        var scene = new Scene { BackgroundColor = Blue, AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -4));
        cam.Width = 40;
        cam.Height = 30;
        return (scene, scene.AddLight("Light"));
    }

    private static RenderImage Render(Scene scene, double frame, RenderSettings? settings = null, Action<double>? progress = null) =>
        RayTracer.Render(scene, frame, settings, progress, TestContext.Current.CancellationToken);

    private static int Brightness((byte R, byte G, byte B, byte A) p) => p.R + p.G + p.B;

    /// <summary>Where a world point lands in the image of the scene's camera.</summary>
    private static (int X, int Y) PixelOf(Scene scene, Vec3 p, int width, int height)
    {
        var cam = scene.CurrentCamera!;
        var local = cam.CameraToWorld(0).Inverted().TransformPoint(p);
        double tanV = 1 / cam.Zoom.Evaluate(0), tanH = tanV * width * cam.PixelAspect / height;
        double x = (local.X / local.Z / tanH + 1) / 2 * width;
        double y = (1 - local.Y / local.Z / tanV) / 2 * height;
        return ((int)x, (int)y);
    }

    [Fact]
    public void SphereLitFromTheFrontIsBrightInTheMiddle()
    {
        var (scene, _) = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1), s => s.Smoothing = true));
        var image = Render(scene, 0);
        Assert.Equal(40 * 30 * 4, image.Rgba.Length);
        var centre = image.Pixel(20, 15);
        Assert.True(Brightness(centre) > 500, $"Centre is {centre}.");
        Assert.Equal(255, centre.A);
        foreach (var (x, y) in new[] { (0, 0), (39, 0), (0, 29), (39, 29) })
            Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)0), image.Pixel(x, y));
        // Toward the rim the light hits at a glancing angle, so it's darker.
        var nearRim = image.Pixel(PixelOf(scene, new Vec3(0.95, 0, -0.2), 40, 30).X, 15);
        Assert.True(Brightness(nearRim) < Brightness(centre));
    }

    [Fact]
    public void RedSurfaceRendersRed()
    {
        var (scene, _) = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1), s => s.Color = new Rgb(255, 0, 0)));
        var p = Render(scene, 0).Pixel(20, 15);
        Assert.True(p.R > 200 && p.G < 10 && p.B < 10, $"Got {p}.");
    }

    [Fact]
    public void AmbientAndLuminosityLightTheUnlitSide()
    {
        var (scene, light) = Stage();
        light.Intensity.SetConstant(0);
        scene.AmbientIntensity = 0.5;
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1), s => s.Color = new Rgb(200, 200, 200)));
        var p = Render(scene, 0).Pixel(20, 15);
        AssertNear(100, p.R, 1);
        scene.AmbientIntensity = 0;
        scene.Objects.Single().Surfaces[0].Luminosity = 1;
        AssertNear(200, Render(scene, 0).Pixel(20, 15).G, 1);
    }

    [Fact]
    public void SpecularAddsAHighlight()
    {
        var (scene, _) = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1, 32, 16), s => { s.Color = new Rgb(100, 0, 0); s.Smoothing = true; }));
        int plain = Render(scene, 0).Pixel(20, 15).G;
        scene.Objects.Single().Surfaces[0].Specular = 1;
        int shiny = Render(scene, 0).Pixel(20, 15).G;
        Assert.Equal(0, plain);
        Assert.True(shiny > 100, $"Highlight {shiny}.");
    }

    [Fact]
    public void ShadowsFallWhereTheLightIsBlocked()
    {
        var scene = new Scene { AmbientIntensity = 0.1 };
        var cam = scene.AddCamera("Top");
        cam.SetPosition(0, new Vec3(0, 6, 0));
        cam.SetRotation(0, new Vec3(0, 90, 0));
        var light = scene.AddLight("Sun");
        light.SetRotation(0, new Vec3(0, 60, 0)); // shining down and toward +Z
        scene.AddObject("Ground", Doc(Square(new Vec3(0, -1, 0), 20, Vec3.UnitY)));
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 0.5)));

        var settings = new RenderSettings { Width = 48, Height = 48 };
        var image = Render(scene, 0, settings);
        var shadowed = PixelOf(scene, new Vec3(0, -1, 0.9), 48, 48);
        var lit = PixelOf(scene, new Vec3(0, -1, -0.9), 48, 48);
        int dark = Brightness(image.Pixel(shadowed.X, shadowed.Y));
        int bright = Brightness(image.Pixel(lit.X, lit.Y));
        Assert.True(dark < bright / 4, $"Shadow {dark}, lit {bright}.");

        settings.Shadows = false;
        var unshadowed = Render(scene, 0, settings);
        AssertNear(bright, Brightness(unshadowed.Pixel(shadowed.X, shadowed.Y)), 3);

        settings.Shadows = true;
        light.Shadows = false;
        AssertNear(bright, Brightness(Render(scene, 0, settings).Pixel(shadowed.X, shadowed.Y)), 3);
        light.Shadows = true;
        scene.Objects.Last().CastShadows = false;
        AssertNear(bright, Brightness(Render(scene, 0, settings).Pixel(shadowed.X, shadowed.Y)), 3);
    }

    [Fact]
    public void PointAndSpotLightsReachOnlyWhereTheyShould()
    {
        var scene = new Scene { AmbientIntensity = 0 };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0, -10));
        scene.AddObject("Wall", Doc(Square(Vec3.Zero, 20, -Vec3.UnitZ)));
        var spot = scene.AddLight("Spot", LightType.Spot);
        spot.SetPosition(0, new Vec3(0, 0, -1));
        spot.ConeAngle = 10;
        spot.EdgeAngle = 2;
        var settings = new RenderSettings { Width = 40, Height = 40 };
        var image = Render(scene, 0, settings);
        Assert.True(Brightness(image.Pixel(20, 20)) > 500);
        var outside = PixelOf(scene, new Vec3(3, 0, 0), 40, 40);
        Assert.Equal(0, Brightness(image.Pixel(outside.X, outside.Y)));

        spot.Type = LightType.Point;
        spot.Range = 2;
        image = Render(scene, 0, settings);
        var near = PixelOf(scene, new Vec3(1, 0, 0), 40, 40);
        Assert.True(Brightness(image.Pixel(near.X, near.Y)) > 0);
        var far = PixelOf(scene, new Vec3(2.5, 0, 0), 40, 40);
        Assert.Equal(0, Brightness(image.Pixel(far.X, far.Y)));
    }

    [Fact]
    public void MirrorReflectsWhatsBehindTheCamera()
    {
        var (scene, _) = Stage();
        scene.AddObject("Mirror", Doc(Square(new Vec3(0, 0, 5), 20, -Vec3.UnitZ), s => { s.Diffuse = 0; s.Reflection = 1; }));
        scene.AddObject("Red card", Doc(Square(new Vec3(0, 0, -10), 2, Vec3.UnitZ), s => { s.Color = new Rgb(255, 0, 0); s.Luminosity = 1; s.Diffuse = 0; }));
        var image = Render(scene, 0);
        var centre = image.Pixel(20, 15);
        Assert.True(centre.R > 250 && centre.G == 0 && centre.B == 0, $"Centre {centre}.");
        // Away from the card the mirror shows the backdrop.
        Assert.Equal((0, 0, 255), (image.Pixel(0, 0).R, image.Pixel(0, 0).G, (int)image.Pixel(0, 0).B));

        var off = Render(scene, 0, new RenderSettings { Reflections = false }).Pixel(20, 15);
        Assert.Equal(0, Brightness(off));
    }

    [Fact]
    public void GradientBackdropShowsInReflectionsAndMisses()
    {
        var (scene, _) = Stage();
        scene.GradientBackground = true;
        scene.GradientTop = new Vec3(1, 0, 0);
        scene.GradientBottom = new Vec3(0, 1, 0);
        var image = Render(scene, 0);
        Assert.True(image.Pixel(20, 0).R > image.Pixel(20, 29).R);
        Assert.True(image.Pixel(20, 29).G > image.Pixel(20, 0).G);
    }

    [Fact]
    public void TransparencyAndRefraction()
    {
        var (scene, _) = Stage();
        scene.AddObject("Glass", Doc(Square(Vec3.Zero, 4, -Vec3.UnitZ), s => { s.Transparency = 1; s.Diffuse = 0; }));
        scene.AddObject("Red card", Doc(Square(new Vec3(0, 0, 3), 1, -Vec3.UnitZ), s => { s.Color = new Rgb(255, 0, 0); s.Luminosity = 1; s.Diffuse = 0; }));
        var p = Render(scene, 0).Pixel(20, 15);
        Assert.True(p.R > 250 && p.B == 0, $"Got {p}.");
        var opaque = Render(scene, 0, new RenderSettings { Transparency = false }).Pixel(20, 15);
        Assert.Equal(0, Brightness(opaque));

        // A refracting ball turns the view around: through its middle the card still shows, off-centre it doesn't line up.
        var (glassScene, _) = Stage();
        glassScene.AddObject("Lens", Doc(Sphere(Vec3.Zero, 1, 48, 24), s => { s.Transparency = 1; s.Diffuse = 0; s.Smoothing = true; s.Name = "Glass"; }));
        glassScene.AddObject("Red card", Doc(Square(new Vec3(0, 0, 3), 0.5, -Vec3.UnitZ), s => { s.Color = new Rgb(255, 0, 0); s.Luminosity = 1; s.Diffuse = 0; }));
        var straight = Render(glassScene, 0);
        var bent = Render(glassScene, 0, new RenderSettings { RefractionIndices = { ["Glass"] = 1.5 } });
        Assert.True(straight.Pixel(20, 15).R > 250);
        Assert.NotEqual(straight.Rgba, bent.Rgba);
    }

    [Fact]
    public void SingleSidedFacesAreInvisibleFromBehind()
    {
        var (scene, _) = Stage();
        scene.AddObject("Back", Doc(Square(Vec3.Zero, 4, Vec3.UnitZ), s => s.Luminosity = 1));
        Assert.Equal(0, Render(scene, 0).Pixel(20, 15).A);
        scene.Objects.Single().Surfaces[0].DoubleSided = true;
        Assert.Equal(255, Render(scene, 0).Pixel(20, 15).A);
    }

    [Fact]
    public void HiddenAndPlaceholderObjectsDontRender()
    {
        var (scene, _) = Stage();
        var ball = scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1)));
        scene.AddObject("Lost");
        ball.Visible = false;
        Assert.All(Enumerable.Range(0, 40 * 30), i => Assert.Equal(0, Render(scene, 0).Rgba[i * 4 + 3]));
    }

    [Fact]
    public void FogFadesDistantThingsToTheFogColour()
    {
        var (scene, _) = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1)));
        scene.Fog = FogType.Linear;
        scene.FogColor = new Vec3(0, 1, 0);
        scene.FogMinDistance = 0;
        scene.FogMaxDistance = 2;
        var p = Render(scene, 0).Pixel(20, 15);
        Assert.Equal((0, 255, 0), (p.R, p.G, (int)p.B));
    }

    [Fact]
    public void SmoothingSoftensTheFacets()
    {
        double Roughness(bool smooth)
        {
            var (scene, _) = Stage();
            scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1, 10, 6), s => s.Smoothing = smooth));
            var image = Render(scene, 0, new RenderSettings { Width = 80, Height = 60 });
            double worst = 0;
            for (int x = 25; x < 55; x++) worst = Math.Max(worst, Math.Abs(image.Pixel(x, 30).R - image.Pixel(x + 1, 30).R));
            return worst;
        }
        Assert.True(Roughness(true) < Roughness(false));
    }

    [Fact]
    public void AnimatedObjectsRenderWhereTheyAreAtTheFrame()
    {
        var (scene, _) = Stage();
        var ball = scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 0.3)));
        ball.SetPosition(0, new Vec3(-1, 0, 0));
        ball.SetPosition(10, new Vec3(1, 0, 0));
        var at = PixelOf(scene, new Vec3(1, 0, -0.3), 40, 30);
        Assert.Equal(0, Render(scene, 0).Pixel(at.X, at.Y).A);
        Assert.Equal(255, Render(scene, 10).Pixel(at.X, at.Y).A);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 1, 5)]
    [InlineData(3, 7, 4)]
    [InlineData(17, 1, 2)]
    public void OddSizesDontCrash(int width, int height, int samples)
    {
        var (scene, _) = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1)));
        var image = Render(scene, 0, new RenderSettings { Width = width, Height = height, SamplesPerPixel = samples });
        Assert.Equal(width * height * 4, image.Rgba.Length);
        Assert.Equal((width, height), (image.Width, image.Height));
    }

    [Fact]
    public void AntialiasingIsDeterministicAndBlendsEdges()
    {
        var (scene, _) = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1)));
        var settings = new RenderSettings { SamplesPerPixel = 9, MaxThreads = 3 };
        var a = Render(scene, 0, settings);
        var b = Render(scene, 0, new RenderSettings { SamplesPerPixel = 9, MaxThreads = 1 });
        Assert.Equal(a.Rgba, b.Rgba);
        var alphas = Enumerable.Range(0, 40 * 30).Select(i => a.Rgba[i * 4 + 3]).ToHashSet();
        Assert.Contains(alphas, x => x is > 0 and < 255);
    }

    [Fact]
    public void ProgressReachesTheEnd()
    {
        var (scene, _) = Stage();
        var reports = new List<double>();
        Render(scene, 0, progress: p => { lock (reports) reports.Add(p); });
        Assert.Equal(30, reports.Count);
        Assert.Equal(1, reports.Max());
    }

    [Fact]
    public void CancellingStopsTheRender()
    {
        var (scene, _) = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RayTracer.Render(scene, 0, cancel: cancelled.Token));

        using var midway = new CancellationTokenSource();
        int rows = 0;
        Assert.Throws<OperationCanceledException>(() => RayTracer.Render(scene, 0, new RenderSettings { Width = 200, Height = 200 },
            _ => { if (Interlocked.Increment(ref rows) == 2) midway.Cancel(); }, midway.Token));
        Assert.True(rows < 200);
    }

    [Fact]
    public void NoCameraIsAnError()
    {
        var scene = new Scene();
        Assert.Throws<InvalidOperationException>(() => Render(scene, 0));
    }

    [Fact]
    public void PngHoldsThePixels()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120];
        var png = Png.Encode(3, 2, rgba);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);

        var chunks = new Dictionary<string, byte[]>();
        for (int i = 8; i < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(i));
            string type = Encoding.ASCII.GetString(png, i + 4, 4);
            var data = png[(i + 8)..(i + 8 + length)];
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(i + 8 + length));
            Assert.Equal(Png.Crc32(png.AsSpan(i + 4, 4 + length)), crc);
            chunks[type] = data;
            i += 12 + length;
        }
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Keys);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(chunks["IHDR"]));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(chunks["IHDR"].AsSpan(4)));
        Assert.Equal([8, 6, 0, 0, 0], chunks["IHDR"][8..]);

        using var z = new ZLibStream(new MemoryStream(chunks["IDAT"]), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var rows = raw.ToArray();
        Assert.Equal(2 * (1 + 12), rows.Length);
        Assert.Equal(0, rows[0]);
        Assert.Equal(rgba[..12], rows[1..13]);
        Assert.Equal(rgba[12..], rows[14..26]);
    }

    [Fact]
    public void Crc32MatchesTheStandard()
    {
        Assert.Equal(0xCBF43926u, Png.Crc32("123456789"u8));
        Assert.Equal(0xAE426082u, Png.Crc32("IEND"u8));
    }

    [Fact]
    public void RenderedImagesSaveAsPng()
    {
        using var dir = new TempDir();
        var (scene, _) = Stage();
        scene.AddObject("Ball", Doc(Sphere(Vec3.Zero, 1)));
        string path = Path.Combine(dir.Path, "ball.png");
        Render(scene, 0).SavePng(path);
        Assert.Equal(0x89, File.ReadAllBytes(path)[0]);
        Assert.Throws<ArgumentException>(() => Png.Encode(2, 2, new byte[3]));
    }
}
