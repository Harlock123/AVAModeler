using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class LwsTests
{
    private const string Content = "/no/such/content";
    private const string SceneDir = "/no/such/scenes";

    private const string Version1 = """
        LWSC
        1

        FirstFrame 1
        LastFrame 90
        FrameStep 2

        LoadObject Objects\Box.lwo
        ShowObject 6 3
        ObjectMotion (unnamed)
          9
          2
          0 0 0 0 0 0 1 1 1
          0 0 0 0 0
          2 3 0 90 0 0 2 2 2
          10 1 0.5 0 0
        EndBehavior 2
        PivotPoint 0 1 0
        ShadowOptions 4

        AddNullObject Pivot Null
        ObjectMotion (unnamed)
          9
          1
          0 5 0 0 0 0 1 1 1
          0 0 0 0 0
        EndBehavior 1

        LoadObject Objects/Missing.lwo
        ObjectMotion (unnamed)
          9
          1
          0 0 0 0 0 0 1 1 1
          0 0 0 0 0
        EndBehavior 1
        ParentObject 2

        AmbientColor 255 0 51
        AmbIntensity 0.5

        AddLight
        LightName Key Light
        ShowLight 0 7
        LightMotion (unnamed)
          9
          1
          0 0 0 45 30 0 1 1 1
          0 0 0 0 0
        EndBehavior 1
        LightColor 255 255 51
        LgtIntensity 0.8
        LightType 2
        ConeAngle 25
        EdgeAngle 4
        ShadowType 1

        AddLight
        LightName Fill
        LightMotion (unnamed)
          9
          1
          1 2 3 0 0 0 1 1 1
          0 0 0 0 0
        EndBehavior 1
        LgtIntensity (envelope)
          1
          2
          0.5
          0 0 0 0 0
          1
          20 0 0 0 0
        EndBehavior 2
        LightType 1
        ShadowType 0
        ParentObject 1

        ShowCamera 1 2
        CameraMotion (unnamed)
          9
          1
          0 1 -5 0 10 0 1 1 1
          0 0 0 0 0
        EndBehavior 1
        ZoomFactor 2.5
        SomethingUnknown 1 2 3
        UnknownEnveloped (envelope)
          1
          1
          4
          0 0 0 0 0
        EndBehavior 1

        BackdropColor 0 0 255
        ZenithColor 255 0 0
        NadirColor 0 255 0
        SolidBackdrop 0
        FogType 1
        FogMinDist 10
        FogMaxDist 50
        FogColor 51 51 51
        """;

    private const string Version3 = """
        LWSC
        3

        FirstFrame 0
        LastFrame 48
        FrameStep 1
        FramesPerSecond 24

        LoadObjectLayer 2 10000000 Objects/Ship.lwo
        ChangeObject 0
        ShowObject 6 2
        ObjectMotion
        NumChannels 9
        Channel 0
        { Envelope
          2
          Key 0 0 0 0 0 0 0 0 0
          Key 4 1 3 0 0 0 0 0 0
          Behaviors 1 4
        }
        Channel 1
        { Envelope
          1
          Key 2 0 0 0 0 0 0 0 0
          Behaviors 1 1
        }
        Channel 3
        { Envelope
          3
          Key 0 0 0 0.5 -0.5 0.25 0 0 0
          Key 180 0.5 0 0 0 0 0 0 0
          Key 90 2 0 0 0 0 0 0 0
          Behaviors 3 5
        }
        PivotPosition 1 2 3
        ParentItem 10000001

        AddNullObject 10000001 Root
        ObjectMotion
        NumChannels 9
        Channel 2
        { Envelope
          1
          Key 7 0 0 0 0 0 0 0 0
          Behaviors 1 1
        }

        Plugin DisplacementHandler 1 SomePlugin
        ObjectMotion
        { Weird
        EndPlugin

        AmbientColor 0.1 0.2 0.3
        AmbientIntensity 0.75

        AddLight 20000000
        LightName Sun
        LightMotion
        NumChannels 9
        Channel 4
        { Envelope
          1
          Key 45 0 0 0 0 0 0 0 0
          Behaviors 1 1
        }
        LightColor 1 0.5 0.25
        LightIntensity 1.5
        LightFalloffType 1
        LightRange 25
        LightType 2
        LightConeAngle 40
        LightEdgeAngle 10
        ShadowType 0
        ParentItem 30000000

        AddCamera 30000000
        CameraName Main
        CameraMotion
        NumChannels 9
        Channel 2
        { Envelope
          1
          Key -10 0 0 0 0 0 0 0 0
          Behaviors 1 1
        }
        ZoomFactor (envelope)
        { Envelope
          2
          Key 3.2 0 0 0 0 0 0 0 0
          Key 6.4 2 3 0 0 0 0 0 0
          Behaviors 1 1
        }
        FrameSize 320 240
        ResolutionMultiplier 2.0
        PixelAspect 1.2

        { SomeBlock
          Key 1 2 3
          { Nested
          }
        }

        AddCamera 30000001
        CameraName Second
        CurrentCamera 1

        BackdropColor 0.5 0.5 1
        """;

    private const string Bones1 = """
        LWSC
        1

        LoadObject Objects/Figure.lwo
        ObjectMotion (unnamed)
          9
          1
          0 0 0 0 0 0 1 1 1
          0 0 0 0 0
        EndBehavior 1
        BoneFalloffType 1

        AddBone
        BoneName Upper
        BoneActive 1
        BoneRestPosition 0 0 0
        BoneRestDirection 0 0 0
        BoneRestLength 1
        BoneStrength 2
        BoneMotion (unnamed)
          9
          1
          0 0 0 0 0 0 1 1 1
          0 0 0 0 0
        EndBehavior 1

        AddBone
        BoneName Lower
        BoneActive 0
        BoneRestPosition 0 0 1
        BoneRestDirection 0 -90 0
        BoneRestLength 0.5
        BoneStrength 1
        BoneMotion (unnamed)
          9
          2
          0 0 1 0 -90 0 1 1 1
          0 0 0 0 0
          0 0 1 0 0 0 1 1 1
          10 0 0 0 0
        EndBehavior 1
        ParentObject 1
        """;

    private const string Bones3 = """
        LWSC
        3

        LoadObjectLayer 1 Objects/Arm.lwo
        ObjectMotion
        NumChannels 9
        BoneFalloffType 3
        AddBone 40000000
        BoneName Upper
        BoneRestPosition 0 0 0
        BoneRestDirection 0 0 0
        BoneRestLength 2
        BoneStrength 1
        BoneActive 1
        BoneMotion
        NumChannels 9
        Channel 3
        { Envelope
          2
          Key 0 0 0 0 0 0 0 0 0
          Key 90 1 0 0 0 0 0 0 0
          Behaviors 1 1
        }
        AddBone 40010000
        BoneName Lower
        BoneRestPosition 0 0 2
        BoneRestDirection 0 0 0
        BoneRestLength 1
        BoneMotion
        NumChannels 9
        Channel 2
        { Envelope
          1
          Key 2 0 0 0 0 0 0 0 0
          Behaviors 1 1
        }
        ParentItem 40000000
        """;

    private readonly List<string> _loaded = [];

    private Document? Loader(string path)
    {
        _loaded.Add(path);
        return path.Contains("Missing") ? null : Doc(Square(Vec3.Zero, 1, Vec3.UnitZ));
    }

    private SceneLoadResult Read(string text) => LwsReader.Read(text, Loader, SceneDir, Content);

    [Fact]
    public void Version1SettingsAndObjects()
    {
        var (scene, warnings) = Read(Version1);
        Assert.Equal(1, scene.FirstFrame);
        Assert.Equal(90, scene.LastFrame);
        Assert.Equal(2, scene.FrameStep);

        var objects = scene.Items.Where(i => i is ObjectItem or NullItem).ToList();
        Assert.Equal(["Box", "Pivot Null", "Missing"], objects.Select(o => o.Name));
        var box = (ObjectItem)objects[0];
        Assert.Equal(Path.Combine(Content, "Objects/Box.lwo"), box.SourcePath);
        Assert.Contains(box.SourcePath, _loaded);
        Assert.False(box.IsPlaceholder);
        Assert.Null(box.Layer);
        Assert.False(box.CastShadows);
        Assert.True(box.ReceiveShadows);
        AssertNear(new Vec3(0, 1, 0), box.Pivot);

        var x = box[Channel.PositionX];
        Assert.Equal(2, x.Keys.Count);
        Assert.Equal(new EnvelopeKey(10, 2, 0.5, 0, 0, true), x.Keys[1]);
        Assert.Equal(EndBehavior.Repeat, x.PostBehavior);
        AssertNear(90, box.RotationAt(10).X);
        AssertNear(new Vec3(2, 2, 2), box.ScaleAt(10));
        AssertNear(new Vec3(0.4, 0.6, 0), box.PositionAt(12)); // repeating, linear into the last key

        AssertNear(5, objects[1].PositionAt(0).Y);
        var missing = (ObjectItem)objects[2];
        Assert.True(missing.IsPlaceholder);
        Assert.Equal(objects[1], missing.Parent);
        Assert.Contains(warnings, w => w.Contains("Missing.lwo"));
    }

    [Fact]
    public void Version1LightsCameraAndBackground()
    {
        var (scene, _) = Read(Version1);
        Assert.Equal(new Vec3(1, 0, 0.2), scene.AmbientColor);
        Assert.Equal(0.5, scene.AmbientIntensity);

        var lights = scene.Lights.ToList();
        Assert.Equal(["Key Light", "Fill"], lights.Select(l => l.Name));
        var key = lights[0];
        Assert.Equal(LightType.Spot, key.Type);
        AssertNear(new Vec3(45, 30, 0), key.RotationAt(0));
        AssertNear(new Vec3(1, 1, 0.2), key.Color);
        Assert.Equal(0.8, key.Intensity.Evaluate(0));
        Assert.Equal(25, key.ConeAngle);
        Assert.Equal(4, key.EdgeAngle);
        Assert.True(key.Shadows);

        var fill = lights[1];
        Assert.Equal(LightType.Point, fill.Type);
        Assert.False(fill.Shadows);
        AssertNear(new Vec3(1, 2, 3), fill.PositionAt(0));
        AssertNear(0.75, fill.Intensity.Evaluate(10));
        Assert.Equal(EndBehavior.Repeat, fill.Intensity.PostBehavior);
        Assert.Equal(scene.Items[0], fill.Parent);

        var cam = Assert.Single(scene.Cameras);
        Assert.Equal(cam, scene.CurrentCamera);
        AssertNear(new Vec3(0, 1, -5), cam.PositionAt(0));
        AssertNear(10, cam.RotationAt(0).Y);
        Assert.Equal(2.5, cam.Zoom.Evaluate(0));

        Assert.Equal(new Vec3(0, 0, 1), scene.BackgroundColor);
        Assert.True(scene.GradientBackground);
        Assert.Equal(new Vec3(1, 0, 0), scene.GradientTop);
        Assert.Equal(new Vec3(0, 1, 0), scene.GradientBottom);
        Assert.Equal(FogType.Linear, scene.Fog);
        Assert.Equal(10, scene.FogMinDistance);
        Assert.Equal(50, scene.FogMaxDistance);
        Assert.Equal(new Vec3(0.2, 0.2, 0.2), scene.FogColor);
    }

    [Fact]
    public void Version3ObjectsAndEnvelopes()
    {
        var (scene, warnings) = Read(Version3);
        Assert.Empty(warnings);
        Assert.Equal(0, scene.FirstFrame);
        Assert.Equal(48, scene.LastFrame);
        Assert.Equal(24, scene.FramesPerSecond);

        var ship = Assert.Single(scene.Objects);
        Assert.Equal("Ship:2", ship.Name);
        Assert.Equal(1, ship.Layer);
        var root = Assert.Single(scene.Nulls);
        Assert.Equal("Root", root.Name);
        Assert.Equal(root, ship.Parent);
        AssertNear(7, root.PositionAt(0).Z);
        AssertNear(new Vec3(1, 2, 3), ship.Pivot);

        var x = ship[Channel.PositionX];
        Assert.Equal([0.0, 24], x.Keys.Select(k => k.Frame));
        Assert.True(x.Keys[1].Linear);
        Assert.Equal(EndBehavior.Constant, x.PreBehavior);
        Assert.Equal(EndBehavior.OffsetRepeat, x.PostBehavior);
        AssertNear(6, x.Evaluate(36));
        Assert.Equal(2, ship.PositionAt(100).Y);

        var h = ship[Channel.Heading];
        Assert.Equal([0.0, 12, 48], h.Keys.Select(k => k.Frame));
        Assert.Equal(new EnvelopeKey(0, 0, 0.5, -0.5, 0.25), h.Keys[0]);
        Assert.Equal(EndBehavior.Oscillate, h.PreBehavior);
        Assert.Equal(EndBehavior.Linear, h.PostBehavior);
    }

    [Fact]
    public void Version3LightsAndCameras()
    {
        var (scene, _) = Read(Version3);
        Assert.Equal(new Vec3(0.1, 0.2, 0.3), scene.AmbientColor);
        Assert.Equal(0.75, scene.AmbientIntensity);

        var sun = Assert.Single(scene.Lights);
        Assert.Equal("Sun", sun.Name);
        Assert.Equal(LightType.Spot, sun.Type);
        AssertNear(45, sun.RotationAt(0).Y);
        Assert.Equal(new Vec3(1, 0.5, 0.25), sun.Color);
        Assert.Equal(1.5, sun.Intensity.Evaluate(0));
        Assert.Equal(25, sun.Range);
        Assert.Equal(40, sun.ConeAngle);
        Assert.Equal(10, sun.EdgeAngle);
        Assert.False(sun.Shadows);

        var cams = scene.Cameras.ToList();
        Assert.Equal(["Main", "Second"], cams.Select(c => c.Name));
        Assert.Equal(cams[0], sun.Parent);
        Assert.Equal(cams[1], scene.CurrentCamera);
        AssertNear(-10, cams[0].PositionAt(0).Z);
        Assert.Equal((640, 480), (cams[0].Width, cams[0].Height));
        Assert.Equal(1.2, cams[0].PixelAspect);
        Assert.Equal([0.0, 48], cams[0].Zoom.Keys.Select(k => k.Frame));
        AssertNear(6.4, cams[0].Zoom.Evaluate(48));
        Assert.Equal(new Vec3(0.5, 0.5, 1), scene.BackgroundColor);
    }

    [Fact]
    public void Version1Bones()
    {
        var (scene, _) = Read(Bones1);
        var figure = Assert.Single(scene.Objects);
        Assert.Equal(2, figure.BoneFalloffExponent);
        Assert.Equal(["Upper", "Lower"], figure.Bones.Select(b => b.Name));
        var (upper, lower) = (figure.Bones[0], figure.Bones[1]);
        Assert.Equal(2, upper.Strength);
        Assert.True(upper.Active);
        Assert.False(lower.Active);
        Assert.Equal(upper, lower.Parent);
        Assert.Equal(new Vec3(0, -90, 0), lower.RestRotation);
        Assert.Equal(0.5, lower.RestLength);
        AssertNear(0, lower.RotationAt(10).Y);
        AssertNear(-45, lower.RotationAt(5).Y);
    }

    [Fact]
    public void Version3Bones()
    {
        var (scene, _) = Read(Bones3);
        var arm = Assert.Single(scene.Objects);
        Assert.Equal(8, arm.BoneFalloffExponent);
        var (upper, lower) = (arm.Bones[0], arm.Bones[1]);
        Assert.Equal(upper, lower.Parent);
        Assert.Equal(2, upper.RestLength);
        AssertNear(90, upper.RotationAt(30).X);
        AssertNear(new Vec3(2, 0, 0), lower.WorldPosition(30), 1e-9);
    }

    [Fact]
    public void NotAScene()
    {
        Assert.Throws<InvalidDataException>(() => LwsReader.Read("FORM", Loader));
        Assert.Throws<InvalidDataException>(() => LwsReader.Read("LWSC\nabc", Loader));
    }

    [Fact]
    public void BrokenLinesWarnInsteadOfThrowing()
    {
        var (scene, warnings) = Read("LWSC\n1\nFirstFrame lots\nAddBone\nObjectMotion (unnamed)\n  9\nLastFrame 5\nParentObject 9\n");
        Assert.Equal(5, scene.LastFrame);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void RelativePathsPreferFilesThatExist()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Objects"));
        string real = Path.Combine(dir.Path, "Objects", "Box.lwo");
        File.WriteAllText(real, "");
        string scenePath = Path.Combine(dir.Path, "test.lws");
        File.WriteAllText(scenePath, "LWSC\n1\nLoadObject Objects\\Box.lwo\nLoadObject C:\\Old\\Content\\Objects\\Box.lwo\n");
        var (scene, _) = LwsReader.ReadFile(scenePath, Loader, Content);
        Assert.All(scene.Objects, o => Assert.Equal(real, o.SourcePath));
        // Both load the same file, so they share one document.
        Assert.Same(scene.Objects.First().Document, scene.Objects.Last().Document);
    }

    [Fact]
    public void WrittenScenesReadBackTheSame()
    {
        var (original, _) = Read(Version3);
        var bones = Read(Bones1).Scene;
        // Add the bone object and its bones to the scene too, so the writer covers them.
        var fig = bones.Objects.Single();
        var copy = original.AddObject("Figure", fig.Document);
        copy.SourcePath = fig.SourcePath;
        var upper = original.AddBone(copy, "Upper", Vec3.Zero, Vec3.Zero, 1);
        var lower = original.AddBone(copy, "Lower", new Vec3(0, 0, 1), new Vec3(0, -90, 0), 0.5);
        lower.Parent = upper;
        lower.Active = false;
        upper.Strength = 2;
        upper[Channel.Pitch].AddKey(new EnvelopeKey(12, 30, 0.2, 0.1, -0.3));

        var warnings = new List<string>();
        string text = LwsWriter.Write(original, warnings);
        Assert.Empty(warnings);
        var (read, readWarnings) = Read(text);
        Assert.Empty(readWarnings);
        AssertSameScenes(original, read);
    }

    private static void AssertSameScenes(Scene a, Scene b)
    {
        Assert.Equal(a.FirstFrame, b.FirstFrame);
        Assert.Equal(a.LastFrame, b.LastFrame);
        Assert.Equal(a.FramesPerSecond, b.FramesPerSecond);
        Assert.Equal(a.AmbientColor, b.AmbientColor);
        Assert.Equal(a.BackgroundColor, b.BackgroundColor);
        Assert.Equal(a.CurrentCamera?.Name, b.CurrentCamera?.Name);
        var ai = a.Items.OrderBy(i => i.Kind).ThenBy(i => i.Name).ToList();
        var bi = b.Items.OrderBy(i => i.Kind).ThenBy(i => i.Name).ToList();
        Assert.Equal(ai.Select(i => i.ToString()), bi.Select(i => i.ToString()));
        for (int i = 0; i < ai.Count; i++)
        {
            var (x, y) = (ai[i], bi[i]);
            Assert.Equal(x.Parent?.Name, y.Parent?.Name);
            Assert.Equal(x.Pivot, y.Pivot);
            for (int c = 0; c < 9; c++) AssertSameEnvelope(x.Channels[c], y.Channels[c]);
            switch (x)
            {
                case ObjectItem o:
                    var p = (ObjectItem)y;
                    Assert.Equal(o.SourcePath, p.SourcePath);
                    Assert.Equal(o.Layer, p.Layer);
                    Assert.Equal(o.BoneFalloffExponent, p.BoneFalloffExponent);
                    break;
                case Bone bone:
                    var other = (Bone)y;
                    Assert.Equal((bone.RestPosition, bone.RestRotation, bone.RestLength, bone.Strength, bone.Active),
                        (other.RestPosition, other.RestRotation, other.RestLength, other.Strength, other.Active));
                    break;
                case LightItem l:
                    var m = (LightItem)y;
                    Assert.Equal((l.Type, l.Color, l.Range, l.ConeAngle, l.EdgeAngle, l.Shadows), (m.Type, m.Color, m.Range, m.ConeAngle, m.EdgeAngle, m.Shadows));
                    AssertSameEnvelope(l.Intensity, m.Intensity);
                    break;
                case CameraItem c:
                    var d = (CameraItem)y;
                    Assert.Equal((c.Width, c.Height, c.PixelAspect), (d.Width, d.Height, d.PixelAspect));
                    AssertSameEnvelope(c.Zoom, d.Zoom);
                    break;
            }
        }
    }

    private static void AssertSameEnvelope(Envelope a, Envelope b)
    {
        Assert.Equal(a.Keys.Count, b.Keys.Count);
        for (int k = 0; k < a.Keys.Count; k++)
        {
            AssertNear(a.Keys[k].Frame, b.Keys[k].Frame, 1e-9);
            Assert.Equal(a.Keys[k] with { Frame = 0 }, b.Keys[k] with { Frame = 0 });
        }
        Assert.Equal(a.PreBehavior, b.PreBehavior);
        Assert.Equal(a.PostBehavior, b.PostBehavior);
    }
}
