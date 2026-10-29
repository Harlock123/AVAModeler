using System.Text.Json;
using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class SceneJsonTests
{
    /// <summary>A scene that uses every setting the format holds.</summary>
    private static Scene FullScene()
    {
        var scene = new Scene
        {
            FramesPerSecond = 25, FirstFrame = 0, LastFrame = 250, FrameStep = 2,
            AmbientColor = new Vec3(0.1, 0.2, 0.3), AmbientIntensity = 0.123456789,
            BackgroundColor = new Vec3(0.3, 0.2, 0.1), GradientBackground = true,
            GradientTop = new Vec3(0, 0, 1), GradientBottom = new Vec3(0.5, 0.25, 0),
            Fog = FogType.Linear, FogMinDistance = 3, FogMaxDistance = 30.5, FogColor = new Vec3(0.7, 0.7, 0.7),
        };
        var doc = Doc(Sphere(Vec3.Zero, 1, 8, 4), s =>
        {
            s.Color = new Rgb(200, 10, 20);
            s.Specular = 0.4;
            s.Glossiness = 256;
            s.Reflection = 0.1;
            s.Transparency = 0.2;
            s.Luminosity = 0.05;
            s.Smoothing = true;
            s.SmoothingAngle = 60;
            s.DoubleSided = true;
        });
        doc.Layers[0].Name = "Ball";
        doc.Layers[0].Pivot = new Vec3(0, 1, 0);
        doc.Layers[0].Mesh.Polygons[0].Details = [new Polygon([0, 1])];
        var ball = scene.AddObject("Ball", doc);
        ball.Pivot = new Vec3(0.1, 0.2, 0.3);
        ball.CastShadows = false;
        ball.BoneFalloffExponent = 8;
        ball[Channel.PositionX].AddKey(new EnvelopeKey(10, 1.0 / 3, 0.1, -0.2, 0.3, true));
        ball[Channel.PositionX].PostBehavior = EndBehavior.Oscillate;
        var target = ball.BaseMesh();
        target.Transform(Mat4.Scaling(1.5));
        var weight = new Envelope();
        weight.AddKey(0, 0);
        weight.AddKey(20, 1);
        ball.AddMorph(new MorphTarget("Big", target, weight));
        var wave = ball.AddDisplacement(new WaveDisplacement { Shape = WaveShape.Ripple, Direction = Axis.Z, Along = Axis.Y, Center = new Vec3(1, 2, 3), Wavelength = 2.5, Speed = 0.1, Phase = 45 });
        wave.Amplitude.AddKey(5, 0.25);

        var root = scene.AddBone(ball, "Root", new Vec3(0, -1, 0), new Vec3(0, -90, 0), 1);
        var tip = scene.AddBone(ball, "Tip", new Vec3(0, 0, 1), Vec3.Zero, 0.5);
        tip.Parent = root;
        tip.Strength = 0.5;
        tip.Active = false;
        tip[Channel.Bank].AddKey(30, 45);

        var second = scene.AddObject("Second ball", doc, 0);
        var n = scene.AddNull("Null");
        n.Visible = false;
        second.Parent = n;

        var spot = scene.AddLight("Spot", LightType.Spot);
        spot.Color = new Vec3(1, 0.9, 0.8);
        spot.Range = 40;
        spot.ConeAngle = 22.5;
        spot.EdgeAngle = 3;
        spot.Shadows = false;
        spot.Intensity.AddKey(48, 0.5);
        spot.Parent = tip;

        var cam1 = scene.AddCamera("Wide");
        var cam2 = scene.AddCamera("Close");
        cam2.Width = 1920;
        cam2.Height = 1080;
        cam2.PixelAspect = 1.1;
        cam2.ActionSafe = 0.93;
        cam2.TitleSafe = 0.85;
        cam2.Zoom.AddKey(100, 8);
        cam1.SetPosition(0, new Vec3(0, 1, -10));
        scene.CurrentCamera = cam2;
        return scene;
    }

    [Fact]
    public void EmbeddedRoundTripIsExact()
    {
        var scene = FullScene();
        string json = SceneJson.Write(scene, embedMeshes: true);
        var (read, warnings) = SceneJson.Read(json);
        Assert.Empty(warnings);
        Assert.Equal(json, SceneJson.Write(read, embedMeshes: true));

        Assert.Equal(scene.Items.Select(i => i.ToString()), read.Items.Select(i => i.ToString()));
        Assert.Equal("Close", read.CurrentCamera!.Name);
        Assert.Equal(0.123456789, read.AmbientIntensity);
        var ball = read.Objects.First();
        var second = read.Objects.Last();
        Assert.Same(ball.Document, second.Document);
        Assert.Equal(0, second.Layer);
        Assert.Equal("Null", second.Parent!.Name);
        Assert.False(second.Parent.Visible);
        var surface = ball.Surfaces[0];
        Assert.Equal((new Rgb(200, 10, 20), 256.0, true, 60.0), (surface.Color, surface.Glossiness, surface.DoubleSided, surface.SmoothingAngle));
        Assert.Equal(1.0 / 3, ball[Channel.PositionX].Keys[1].Value);
        Assert.Single(ball.Document!.Layers[0].Mesh.Polygons[0].Details!);
        Assert.Equal(["Root", "Tip"], ball.Bones.Select(b => b.Name));
        Assert.Equal(ball.Bones[0], ball.Bones[1].Parent);
        Assert.Equal("Tip", read.Lights.Single().Parent!.Name);
        // Deformation reads back the same too.
        var a = scene.Evaluate(13).Objects[0].WorldMesh.Points;
        var b = read.Evaluate(13).Objects[0].WorldMesh.Points;
        Assert.Equal(a, b);
    }

    [Fact]
    public void ReferencedObjectsLoadThroughTheCallback()
    {
        var scene = new Scene();
        var doc = Doc(Square(Vec3.Zero, 1, Vec3.UnitZ));
        var box = scene.AddObject("Box", doc);
        box.SourcePath = "Objects/Box.lwo";
        scene.AddObject("Unsaved", Doc(Square(Vec3.One, 1, Vec3.UnitZ)));
        string json = SceneJson.Write(scene);
        Assert.Contains("\"path\": \"Objects/Box.lwo\"", json);

        var asked = new List<string>();
        var (read, warnings) = SceneJson.Read(json, p => { asked.Add(p); return doc; }, "/base");
        Assert.Empty(warnings);
        Assert.Equal([Path.Combine("/base", "Objects/Box.lwo")], asked);
        Assert.Same(doc, read.Objects.First().Document);
        Assert.Equal(Path.Combine("/base", "Objects/Box.lwo"), read.Objects.First().SourcePath); // taken from the scene's folder
        // An object with no file is embedded anyway.
        Assert.Equal(4, read.Objects.Last().BaseMesh().Points.Count);

        var (noLoader, noLoaderWarnings) = SceneJson.Read(json);
        Assert.True(noLoader.Objects.First().IsPlaceholder);
        Assert.Single(noLoaderWarnings);
        var (failing, failingWarnings) = SceneJson.Read(json, _ => throw new IOException("gone"));
        Assert.True(failing.Objects.First().IsPlaceholder);
        Assert.Contains("gone", failingWarnings.Single());
    }

    [Fact]
    public void PlaceholdersStayPlaceholders()
    {
        var scene = new Scene();
        scene.AddObject("Lost").SourcePath = "lost.lwo";
        var (read, _) = SceneJson.Read(SceneJson.Write(scene));
        var lost = read.Objects.Single();
        Assert.True(lost.IsPlaceholder);
        Assert.Equal("lost.lwo", lost.SourcePath);
    }

    [Fact]
    public void FilesRoundTrip()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "scene" + SceneJson.Extension);
        var scene = FullScene();
        SceneJson.WriteFile(scene, path, embedMeshes: true);
        var (read, _) = SceneJson.ReadFile(path);
        Assert.Equal(SceneJson.Write(scene, true), SceneJson.Write(read, true));
    }

    [Fact]
    public void ObjectsBesideTheSceneAreWrittenRelativeToIt()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "scene" + SceneJson.Extension);
        string inside = Path.Combine(dir.Path, "objects", "box.lwo"), outside = Path.Combine(Path.GetTempPath(), "elsewhere", "ball.lwo");
        var scene = new Scene();
        var box = Doc(Square(Vec3.Zero, 1, Vec3.UnitZ));
        box.FilePath = inside;
        scene.AddObject("Box", box).SourcePath = inside;
        var ball = Doc(Square(Vec3.One, 1, Vec3.UnitZ));
        scene.AddObject("Ball", ball).SourcePath = outside;
        SceneJson.WriteFile(scene, path);

        string json = File.ReadAllText(path);
        Assert.Contains("\"path\": \"objects/box.lwo\"", json);
        Assert.Contains("\"source\": \"objects/box.lwo\"", json);
        Assert.Contains(JsonSerializer.Serialize(outside), json); // outside the scene's folder: the full path
        var asked = new List<string>();
        var (read, warnings) = SceneJson.ReadFile(path, p => { asked.Add(p); return p == inside ? box : ball; });
        Assert.Empty(warnings);
        Assert.Equal([inside, outside], asked.Select(Path.GetFullPath));
        Assert.Equal(inside, Path.GetFullPath(((ObjectItem)read.Find("Box")!).SourcePath!));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"format\": \"something else\"}")]
    [InlineData("null")]
    public void OtherFilesAreRejected(string text) => Assert.Throws<InvalidDataException>(() => SceneJson.Read(text));
}
