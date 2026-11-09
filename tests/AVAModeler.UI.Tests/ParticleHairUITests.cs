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
}
