using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;
using AVAModeler.Tools;

namespace AVAModeler.UI.Tests;

/// <summary>Particles and hair from Layout's item panel, and a picture of both rendered.</summary>
public class ParticleHairUITests
{
    private static T Named<T>(LayoutWorkspace layout, string name) where T : Control =>
        layout.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static Document Ball(double radius, string surface, Rgb colour)
    {
        var doc = new Document();
        new Commands(doc).Run("Ball", new Dictionary<string, object?> { ["Radius"] = new[] { radius, radius, radius }, ["Sides"] = 32, ["Segments"] = 16, ["Surface"] = surface });
        doc.Surfaces[doc.SurfaceIndex(surface)].Color = colour;
        return doc;
    }

    [AvaloniaFact]
    public void An_objects_panel_turns_particles_and_hair_on_each_an_undo_step()
    {
        var w = Ui.Open();
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var ball = s.Add(new ObjectItem("Ball", Ball(0.5, "Skin", new Rgb(200, 180, 160))));
        s.Select(ball);
        Ui.Pump();
        Named<CheckBox>(layout, "EmitParticles").IsChecked = true;
        Ui.Pump();
        Assert.NotNull(ball.Particles);
        Assert.Equal("Emit Particles", s.History.UndoLabel);
        Named<ComboBox>(layout, "EmitFrom").SelectedIndex = (int)EmitFrom.Origin;
        Ui.Pump();
        Assert.Equal(EmitFrom.Origin, ball.Particles!.From);
        Named<CheckBox>(layout, "HideEmitter").IsChecked = true;
        Assert.True(ball.Particles.HideEmitter);
        Named<CheckBox>(layout, "GrowHair").IsChecked = true;
        Ui.Pump();
        Assert.NotNull(ball.Hair);
        // The views draw them (the scene's frame has the extra faces), and undo takes them away again.
        Assert.True(s.Scene.Evaluate(s.Frame).Objects.Single(o => o.Item == ball).Mesh.Polygons.Count > ball.BaseMesh().Polygons.Count);
        s.Undo();
        Assert.Null(ball.Hair);
        Assert.NotNull(w.CaptureRenderedFrame());
    }

    [AvaloniaFact]
    public void A_spark_fountain_beside_a_furry_ball()
    {
        var scene = new Scene.Scene { FramesPerSecond = 30, AmbientIntensity = 0.25, GradientBackground = true, GradientTop = new Vec3(0.05, 0.06, 0.12), GradientBottom = new Vec3(0.18, 0.16, 0.2) };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 0.9, -3.4));
        cam.SetRotation(0, new Vec3(0, 8, 0));
        cam.Width = 640;
        cam.Height = 360;
        scene.AddLight("Key").SetRotation(0, new Vec3(30, 35, 0));
        var floorDoc = new Document();
        new Commands(floorDoc).Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 8.0, 0, 8 } });
        floorDoc.Surfaces[0].Color = new Rgb(70, 70, 80);
        scene.AddObject("Floor", floorDoc);
        var ball = scene.AddObject("Furry Ball", Ball(0.45, "Skin", new Rgb(150, 100, 60)));
        ball.SetPosition(0, new Vec3(0.75, 0.45, 0));
        ball.Hair = new HairSettings { Density = 9000, Length = 0.09, Thickness = 0.004, Droop = 0.35, Frizz = 25, Color = new Rgb(170, 115, 60) }.Checked();
        var fountain = scene.AddObject("Fountain", Ball(0.05, "Nozzle", new Rgb(90, 90, 100)));
        fountain.SetPosition(0, new Vec3(-0.8, 0.05, 0));
        fountain.Particles = new ParticleSettings
        {
            From = EmitFrom.Origin, Rate = 300, EndFrame = 120, Life = 1.4, Speed = 3.4, SpeedJitter = 0.25, Spread = 14,
            Size = 0.03, SizeAtDeath = 0.2, Floor = 0, Color = new Rgb(255, 190, 80), Glow = 1,
        }.Checked();
        var img = RayTracer.Render(scene, 60, new RenderSettings { Width = 640, Height = 360, SamplesPerPixel = 2 }, null, TestContext.Current.CancellationToken);
        Assert.Equal(640, img.Width);
        if (Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            img.SavePng(Path.Combine(dir, "AVAModeler-particles-hair.png"), alpha: false);
        }
    }

    [AvaloniaFact]
    public void The_panel_sets_a_look_collisions_hair_swing_and_cloth()
    {
        var w = Ui.Open();
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var ball = s.Add(new ObjectItem("Ball", Ball(0.5, "Skin", new Rgb(200, 180, 160))));
        s.Add(new ObjectItem("Pebble", Ball(0.1, "Stone", new Rgb(90, 90, 90))));
        s.Select(ball);
        Ui.Pump();
        Named<CheckBox>(layout, "EmitParticles").IsChecked = true;
        Ui.Pump();
        Named<ComboBox>(layout, "ParticleLook").SelectedIndex = (int)ParticleLook.Object;
        Ui.Pump();
        // Copies of an object start with the first other object (the Modeler's own, here); Spin and Align to motion appear.
        Assert.Equal((ParticleLook.Object, s.Scene.Objects.First(o => o != ball).Name), (ball.Particles!.Look, ball.Particles.InstanceOf));
        Named<ComboBox>(layout, "ParticleInstance").SelectedItem = "Pebble";
        Assert.Equal("Pebble", ball.Particles.InstanceOf);
        Named<CheckBox>(layout, "AlignToMotion").IsChecked = true;
        Assert.True(ball.Particles.AlignToMotion);
        Named<CheckBox>(layout, "ParticleCollide").IsChecked = true;
        Named<ComboBox>(layout, "ParticleHit").SelectedIndex = (int)ParticleHit.Bounce;
        Ui.Pump();
        Assert.Equal((true, ParticleHit.Bounce), (ball.Particles.Collide, ball.Particles.OnHit));
        Named<CheckBox>(layout, "GrowHair").IsChecked = true;
        Ui.Pump();
        Named<CheckBox>(layout, "HairDynamics").IsChecked = true;
        Ui.Pump();
        Assert.True(ball.Hair!.Dynamics);
        Named<CheckBox>(layout, "SoftBody").IsChecked = true;
        Ui.Pump();
        Assert.NotNull(ball.SoftBody);
        Assert.Equal("Soft Body", s.History.UndoLabel);
        Named<ComboBox>(layout, "SoftPin").SelectedItem = "Skin";
        Named<CheckBox>(layout, "SoftFloor").IsChecked = true;
        Ui.Pump();
        Assert.Equal(("Skin", (double?)0), (ball.SoftBody!.Pin, ball.SoftBody.Floor));
        s.Undo();
        s.Undo();
        s.Undo();
        Assert.Null(ball.SoftBody);
        Assert.NotNull(w.CaptureRenderedFrame());
    }

    /// <summary>A square of cloth, n × n quads, flat at a height.</summary>
    private static Document Cloth(int n, double size, double height, Rgb colour)
    {
        var doc = new Document();
        var mesh = doc.Layers[0].Mesh;
        var ids = new int[n + 1, n + 1];
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++) ids[i, j] = mesh.AddPoint(new Vec3(-size / 2 + size * i / n, height, -size / 2 + size * j / n));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                int f = mesh.AddPolygon([ids[i, j], ids[i, j + 1], ids[i + 1, j + 1], ids[i + 1, j]]);
                if (mesh.Normal(f).Y < 0) mesh.Flip(f);
            }
        doc.Surfaces[0].Name = "Cloth";
        doc.Surfaces[0].Color = colour;
        doc.Surfaces[0].Smoothing = true;
        doc.Surfaces[0].DoubleSided = true;
        return doc;
    }

    [AvaloniaFact]
    public void Cloth_over_a_ball_goo_and_smoke()
    {
        var scene = new Scene.Scene { FramesPerSecond = 30, FirstFrame = 1, LastFrame = 60, AmbientIntensity = 0.3, GradientBackground = true,
            GradientTop = new Vec3(0.08, 0.1, 0.16), GradientBottom = new Vec3(0.2, 0.18, 0.2) };
        var cam = scene.AddCamera("Camera");
        cam.SetPosition(0, new Vec3(0, 1.3, -3.2));
        cam.SetRotation(0, new Vec3(0, 16, 0));
        cam.Width = 640;
        cam.Height = 360;
        var key = scene.AddLight("Key");
        key.SetRotation(0, new Vec3(35, 30, 0));
        var floorDoc = new Document();
        new Commands(floorDoc).Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 8.0, 0, 8 } });
        floorDoc.Surfaces[0].Color = new Rgb(70, 70, 80);
        scene.AddObject("Floor", floorDoc);
        var ball = scene.AddObject("Ball", Ball(0.35, "Wood", new Rgb(160, 110, 70)));
        ball.SetPosition(0, new Vec3(0.75, 0.35, 0.2));
        var cloth = scene.AddObject("Cloth", Cloth(28, 1.3, 1.1, new Rgb(180, 40, 50)));
        cloth.SetPosition(0, new Vec3(0.75, 0, 0.2));
        cloth.SoftBody = new SoftBodySettings { Collide = true, Floor = 0, Bend = 0.05, Thickness = 0.01 }.Checked();
        var goo = scene.AddObject("Goo", Ball(0.04, "Spout", new Rgb(90, 90, 100)));
        goo.SetPosition(0, new Vec3(-1.1, 0.8, 0));
        goo.SetRotation(0, new Vec3(0, 0, -55));
        goo.Particles = new ParticleSettings
        {
            From = EmitFrom.Origin, Rate = 120, StartFrame = 1, EndFrame = 60, Life = 3, Speed = 1.3, SpeedJitter = 0.1, Spread = 6, Size = 0.1,
            Floor = 0, Look = ParticleLook.Blob, Color = new Rgb(90, 200, 70), Glow = 0.1,
        }.Checked();
        var smoke = scene.AddObject("Smoke", Ball(0.03, "Vent", new Rgb(60, 60, 60)));
        smoke.SetPosition(0, new Vec3(-0.2, 0.0, 1.2));
        smoke.Particles = new ParticleSettings
        {
            From = EmitFrom.Origin, Rate = 90, StartFrame = -60, EndFrame = 60, Life = 3, Speed = 0.45, Spread = 35, Gravity = -0.25, Drag = 0.6,
            Size = 0.18, SizeAtDeath = 3.5, Look = ParticleLook.Puff, Opacity = 0.1, Color = new Rgb(150, 150, 158), Glow = 0, HideEmitter = true,
        }.Checked();
        var img = RayTracer.Render(scene, 60, new RenderSettings { Width = 640, Height = 360, SamplesPerPixel = 2 }, null, TestContext.Current.CancellationToken);
        Assert.Equal(640, img.Width);
        if (Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            img.SavePng(Path.Combine(dir, "AVAModeler-cloth-goo-smoke.png"), alpha: false);
        }
    }
}
