using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Rendering;
using AVAModeler.Scene;
using SelectionMode = AVAModeler.Core.SelectionMode;

namespace AVAModeler.UI.Tests;

/// <summary>
/// Pictures of the app at work, drawn in a real (headless) window. Set AVAMODELER_SHOTS to a folder to save them as
/// PNGs: the README's screenshots come from here.
/// </summary>
public class ScreenshotTests
{
    private static string? Folder => Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } d ? d : null;

    private static void Shot(Window w, string name)
    {
        Ui.Pump();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Folder is not { } dir) return;
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, $"AVAModeler-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static MainWindow WithMacro(string macro)
    {
        var w = Ui.Open(1400, 860);
        var run = w.Modeler.RunMacroFile(Ui.Sample(macro));
        for (int i = 0; i < 2000 && !run.IsCompleted; i++) { Ui.Pump(); Thread.Sleep(1); }
        Ui.Fit(w);
        return w;
    }

    private static void Persp(MainWindow w, double heading, double pitch, double closer = 1)
    {
        var p = Ui.View(w, ViewKind.Perspective).Camera;
        p.Heading = heading;
        p.Pitch = pitch;
        p.Distance /= closer;
        w.Modeler.Views.Redraw();
    }

    [AvaloniaFact]
    public void The_spaceship_in_the_quad_view()
    {
        var w = WithMacro("spaceship.lua");
        w.Session.SetSelectionMode(SelectionMode.Polygons);
        var mesh = w.Session.Document.Current.Mesh;
        // The wings chosen, as if about to be worked on.
        w.Session.Document.Selection.PolygonsIn(0).UnionWith(Enumerable.Range(0, mesh.Polygons.Count).Where(i => Math.Abs(mesh.Centroid(mesh.Polygons[i]).X) > 0.9));
        w.Session.Document.Notify();
        Persp(w, 215, 28, 1.3);
        Shot(w, "spaceship");
    }

    [AvaloniaFact]
    public void A_table_an_ai_agent_built_live()
    {
        var w = Ui.Open(1400, 860);
        w.AgentPipe = Agent.Pipe();
        w.Session.Preferences.AllowAgents = true;
        w.SyncAgents();
        try
        {
            var client = Agent.Connect(w.AgentPipe, Path.GetTempPath());
            Agent.BuildATable(client);
            Agent.Json(client, "set_selection_mode", new { mode = "polygons" });
            Agent.Json(client, "select_box", new { min = new[] { -1.0, 0.7, -1 }, max = new[] { 1.0, 0.8, 1 } });
            Agent.Wait(client.DisposeAsync().AsTask().ContinueWith(_ => 0, TaskScheduler.Default));
            Assert.Equal(4 * 80 + 6, w.Session.Document.Current.Mesh.Polygons.Count);
            Ui.Fit(w);
            Persp(w, 35, 24, 1.25);
            Shot(w, "mcp");
        }
        finally
        {
            Agent.Wait(w.Agents.StopAsync().ContinueWith(_ => 0, TaskScheduler.Default));
        }
    }

    [AvaloniaFact]
    public void MetaNURBS_smooths_a_cage()
    {
        var w = Ui.Open(1400, 860);
        var cmd = w.Session.Commands;
        // A simple cage: a box with an arm pulled out of one side, then smoothed.
        cmd.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 1.0, 1, 1 } });
        cmd.SelectMode(SelectionMode.Polygons);
        cmd.SelectInside(new Box3(new Vec3(0.49, -1, -1), new Vec3(1, 1, 1)));
        cmd.Run("Extrude", new Dictionary<string, object?> { ["Offset"] = new[] { 1.0, 0.3, 0 }, ["Segments"] = 2 });
        cmd.SelectNone();
        cmd.SelectInside(new Box3(new Vec3(-1, 0.49, -1), new Vec3(1, 1, 1)));
        cmd.Run("Extrude", new Dictionary<string, object?> { ["Offset"] = new[] { 0, 0.8, 0.2 } });
        cmd.SelectNone();
        w.Session.Document.Surfaces[0].Color = new Rgb(214, 160, 96);
        w.Session.ToggleMetaNurbs();
        Ui.Fit(w);
        Persp(w, 30, 22, 1.2);
        Shot(w, "metanurbs");
    }

    [AvaloniaFact]
    public void The_logo_and_the_numeric_panel()
    {
        var w = WithMacro("logo.lua");
        w.Session.Document.Surfaces[w.Session.Document.SurfaceIndex("Gold")].Color = new Rgb(232, 184, 64);
        w.Session.Document.Surfaces[w.Session.Document.SurfaceIndex("Gold")].Specular = 0.6;
        w.Modeler.Tabs.ShowTab("Multiply");
        w.Modeler.PickTool("Bevel");
        Persp(w, 200, 18, 1.4);
        Shot(w, "logo");
        w.Session.CancelTool();
    }

    [AvaloniaFact]
    public void Textures_in_the_perspective_view_and_the_uv_view()
    {
        var w = WithMacro("still-life.lua");
        var side = Ui.View(w, ViewKind.Side);
        w.Modeler.Views.ToggleUV(side);
        Ui.Pump();
        side.FitAll();
        // Shaded without the wireframe, so the textures show plainly.
        w.Modeler.Execute("Style Shaded");
        Persp(w, 20, 20, 2.3);
        Shot(w, "textures");
    }

    [AvaloniaFact]
    public void The_uv_view_with_part_of_the_map_chosen()
    {
        var w = WithMacro("still-life.lua");
        var doc = w.Session.Document;
        var mesh = doc.Current.Mesh;
        int planet = doc.SurfaceIndex("Planet");
        // The planet's northern faces on the near side, as if about to be worked on.
        w.Session.SetSelectionMode(SelectionMode.Polygons);
        doc.Selection.PolygonsIn(0).UnionWith(Enumerable.Range(0, mesh.Polygons.Count).Where(i =>
            mesh.Polygons[i].Surface == planet && mesh.Centroid(mesh.Polygons[i]).Y > 0.75 && mesh.Centroid(mesh.Polygons[i]).Z < 0.1));
        doc.Notify();
        var side = Ui.View(w, ViewKind.Side);
        w.Modeler.Views.ToggleUV(side);
        w.Modeler.Views.ToggleMaximize(side);
        Ui.Pump();
        side.FitAll();
        w.Modeler.Tabs.ShowTab("Modify");
        w.Modeler.PickTool("Rotate");
        Shot(w, "uv");
        w.Session.CancelTool();
    }

    [AvaloniaFact]
    public void The_surface_editor_and_its_texture_panel()
    {
        var w = WithMacro("still-life.lua");
        var doc = w.Session.Document;
        var editor = new SurfaceEditor(w.Session) { Width = 520, Height = 640 };
        editor.Show(w);
        editor.Choose(doc.SurfaceIndex("Crate"));
        Ui.Pump();
        var panel = editor.OpenTexture(doc.Surfaces[doc.SurfaceIndex("Crate")], TextureChannel.Color);
        panel.Height = 900;
        Ui.Pump();
        // Drawn, with the crate's picture in the panel (not saved: the README doesn't show them).
        Assert.NotNull(editor.CaptureRenderedFrame());
        Assert.NotNull(panel.CaptureRenderedFrame());
        Assert.Equal("crate.png", Path.GetFileName(Assert.Single(panel.Layers).Image));
        panel.Close();
        editor.Close();
    }

    [AvaloniaFact]
    public void A_textured_ray_traced_frame()
    {
        var w = WithMacro("still-life.lua");
        var doc = w.Session.Document;
        doc.Surfaces[doc.SurfaceIndex("Planet")].Specular = 0.35;
        doc.Surfaces[doc.SurfaceIndex("Planet")].Glossiness = 64;
        doc.Surfaces[doc.SurfaceIndex("Floor")].Reflection = 0.15;
        var s = new LayoutSession();
        s.ShowModelerObject(doc);
        s.Scene.GradientBackground = true;
        s.Scene.GradientTop = new Vec3(0.12, 0.16, 0.3);
        s.Scene.GradientBottom = new Vec3(0.4, 0.36, 0.42);
        s.Scene.AmbientIntensity = 0.3;
        var cam = s.Scene.CurrentCamera!;
        cam.SetPosition(1, new Vec3(-0.6, 1.9, -4.4));
        cam.SetRotation(1, new Vec3(8, 18, 0));
        var light = s.Scene.Lights.First();
        light.SetRotation(1, new Vec3(35, 40, 0));
        var img = RayTracer.Render(s.Scene, 1, new RenderSettings { Width = 640, Height = 400, SamplesPerPixel = 4 }, null, TestContext.Current.CancellationToken);
        Assert.Equal(640 * 400 * 4, img.Rgba.Length);
        if (Folder is { } dir)
        {
            Directory.CreateDirectory(dir);
            img.SavePng(Path.Combine(dir, "AVAModeler-textured-render.png"), alpha: false);
        }
    }

    [AvaloniaFact]
    public void Layout_with_the_ship_animated()
    {
        var w = WithMacro("spaceship.lua");
        var doc = w.Session.Document;
        doc.Surfaces[0].Color = new Rgb(180, 190, 205);
        doc.Surfaces[doc.SurfaceIndex("Engine")].Color = new Rgb(255, 120, 40);
        doc.Surfaces[doc.SurfaceIndex("Canopy")].Color = new Rgb(60, 120, 220);
        w.ShowLayout();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var ship = s.Scene.Objects.Single();
        s.SetFrame(1);
        s.SetVector(ship, 0, new Vec3(-3, 0, 0));
        s.SetVector(ship, 3, new Vec3(90, 0, 0));
        s.SetFrame(30);
        s.SetVector(ship, 0, new Vec3(0, 0.5, 0));
        s.SetVector(ship, 3, new Vec3(70, -10, -25));
        s.SetFrame(60);
        s.SetVector(ship, 0, new Vec3(3, 1.5, 1));
        s.SetVector(ship, 3, new Vec3(45, -20, 0));
        s.SetFrame(24);
        layout.View.Mode = LayoutViewMode.Perspective;
        layout.View.Fit();
        layout.View.Camera.Heading = 20;
        layout.View.Camera.Pitch = 18;
        Shot(w, "layout");
    }

    [AvaloniaFact]
    public void The_AI_chat_panel_mid_conversation()
    {
        // A fake Claude (no real API call) lathes a chess pawn, looks at it, answers, and is asked for more.
        var profile = new[] { new[] { 0.0, 0, 0 }, [0.32, 0, 0], [0.32, 0.07, 0], [0.24, 0.11, 0], [0.2, 0.18, 0], [0.13, 0.42, 0], [0.11, 0.56, 0],
            [0.19, 0.6, 0], [0.19, 0.64, 0], [0.1, 0.67, 0], [0.15, 0.74, 0], [0.16, 0.82, 0], [0.12, 0.9, 0], [0.05, 0.94, 0], [0, 0.95, 0] };
        var lines = Enumerable.Range(0, profile.Length - 1).Select(i => new[] { i, i + 1 }).ToArray();
        var api = new AVAModeler.Chat.Tests.FakeClaude()
            .Reply(AVAModeler.Chat.Tests.FakeClaude.Say("I'll draw half a pawn's profile and lathe it round the Y axis."),
                AVAModeler.Chat.Tests.FakeClaude.Call("toolu_1", "add_points", new { points = profile }))
            .Reply(AVAModeler.Chat.Tests.FakeClaude.Call("toolu_2", "add_polygons", new { polygons = lines, surface = "Pawn" }))
            .Reply(AVAModeler.Chat.Tests.FakeClaude.Call("toolu_3", "run_tool", new { name = "Lathe", @params = new { Axis = "Y", Center = new[] { 0.0, 0, 0 }, Sides = 24 } }))
            .Reply(AVAModeler.Chat.Tests.FakeClaude.Call("toolu_4", "set_surface", new { name = "Pawn", color = new[] { 236, 228, 210 }, smoothing = true, specular = 0.4, assign = false }))
            .Reply(AVAModeler.Chat.Tests.FakeClaude.Call("toolu_5", "render_view", new { view = "perspective", width = 320, height = 240, heading = 30, pitch = 15 }))
            .Reply(AVAModeler.Chat.Tests.FakeClaude.Say("Here's your pawn: a lathed 24-sided solid 0.95 m tall, with a round head, a collar and a wide base, in an ivory surface."))
            .Reply(AVAModeler.Chat.Tests.FakeClaude.Say("I'll set it on a board with a warm spotlight."),
                AVAModeler.Chat.Tests.FakeClaude.Call("toolu_6", "add_object", new { name = "Pawn" }))
            .Hang();
        var w = ChatPanelTests.WithChat(api, 1400, 860);
        w.ToggleChat();
        ChatPanelTests.Type(w, "make a chess pawn");
        ChatPanelTests.Finish(w.Chat.Running);
        ChatPanelTests.Type(w, "now put it in a scene with a spotlight and render it");
        for (int i = 0; i < 2000 && api.Left > 0; i++) { Ui.Pump(); Thread.Sleep(1); }
        for (int i = 0; i < 50; i++) { Ui.Pump(); Thread.Sleep(2); }
        Assert.True(w.Chat.Session.Working);
        Assert.Equal(24 * 14, w.Session.Document.Current.Mesh.Polygons.Count);
        w.Session.Document.Selection.Clear();
        Ui.Fit(w);
        Persp(w, 30, 18, 1.2);
        Shot(w, "chat");
        w.Chat.Stop();
        ChatPanelTests.Finish(w.Chat.Running);
    }

    [AvaloniaFact]
    public void A_ray_traced_frame()
    {
        var s = new LayoutSession();
        var w = WithMacro("spaceship.lua");
        var doc = w.Session.Document;
        doc.Surfaces[0].Color = new Rgb(190, 198, 210);
        doc.Surfaces[0].Specular = 0.5;
        doc.Surfaces[0].Glossiness = 128;
        doc.Surfaces[doc.SurfaceIndex("Engine")].Color = new Rgb(255, 110, 30);
        doc.Surfaces[doc.SurfaceIndex("Engine")].Luminosity = 0.6;
        doc.Surfaces[doc.SurfaceIndex("Canopy")].Color = new Rgb(40, 90, 200);
        doc.Surfaces[doc.SurfaceIndex("Canopy")].Specular = 0.9;
        doc.Surfaces[doc.SurfaceIndex("Canopy")].Reflection = 0.3;
        var ship = s.ShowModelerObject(doc);
        s.SetVector(ship, 3, new Vec3(30, -12, -18));
        // A floor for the shadow.
        var floor = new Document();
        new AVAModeler.Tools.BoxTool { Size = new Vec3(30, 0, 30), Center = new Vec3(0, -0.8, 0), Surface = "Floor" }.Run(new AVAModeler.Tools.ToolContext(floor));
        floor.Surfaces[floor.SurfaceIndex("Floor")].Color = new Rgb(70, 74, 86);
        s.Scene.AddObject("Floor", floor);
        s.Scene.GradientBackground = true;
        s.Scene.GradientTop = new Vec3(0.05, 0.08, 0.2);
        s.Scene.GradientBottom = new Vec3(0.35, 0.3, 0.45);
        var cam = s.Scene.CurrentCamera!;
        cam.SetPosition(1, new Vec3(0, 2.2, -6.5));
        cam.SetRotation(1, new Vec3(0, 17, 0));
        var img = RayTracer.Render(s.Scene, 1, new RenderSettings { Width = 640, Height = 400, SamplesPerPixel = 4 }, null, TestContext.Current.CancellationToken);
        Assert.Equal(640 * 400 * 4, img.Rgba.Length);
        if (Folder is { } dir)
        {
            Directory.CreateDirectory(dir);
            img.SavePng(Path.Combine(dir, "AVAModeler-render.png"), alpha: false);
        }
    }
}
