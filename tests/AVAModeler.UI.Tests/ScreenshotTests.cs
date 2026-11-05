using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Chat.UI;
using AVAModeler.Core;
using AVAModeler.Geometry;
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
    public void The_still_life_back_from_a_glb()
    {
        // The textured still life exported as glTF binary (its images inside, the cubic and planar maps baked into
        // UVs), then opened again from the .glb in place of the original.
        var w = WithMacro("still-life.lua");
        string dir = Path.Combine(Path.GetTempPath(), "avamodeler-shot-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string glb = Path.Combine(dir, "still-life.glb");
            var (_, _, warnings) = AVAModeler.Formats.Gltf.Write(w.Session.Document, AVAModeler.Formats.GltfPacking.Binary);
            Assert.Empty(warnings);
            AVAModeler.Formats.ObjectFiles.Save(w.Session.Document, glb);
            w.Session.Document.Modified = false;
            var open = w.Modeler.Open(glb);
            for (int i = 0; i < 2000 && !open.IsCompleted; i++) { Ui.Pump(); Thread.Sleep(1); }
            var doc = w.Session.Document;
            Assert.Equal(glb, doc.FilePath);
            Assert.Equal(3, doc.Surfaces.Count(s => s.Textures.Count == 1));
            Assert.Empty(TextureImages.Problems(doc.Surfaces, doc.ImageBase));
            w.Session.SetSelectionMode(SelectionMode.Polygons);
            Ui.Fit(w);
            w.Modeler.Execute("Style Shaded");
            Persp(w, 20, 20, 2.3);
            Shot(w, "gltf");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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
    public void Blueprints_behind_the_views_and_the_backdrop_panel()
    {
        // The rocket sample: its side, front and top drawings behind the flat views, lined up in metres, and the body
        // lathed over them, the fins still to do; the Backdrop panel open on the side view. The sample runs from a copy
        // in a temp folder, so the panel's image box shows a short path rather than the build machine's.
        var root = Path.Combine(Path.GetTempPath(), "avamodeler-samples");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        var samples = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(Ui.Sample("rocket-blueprints.lua"))))!;
        File.Copy(Path.Combine(samples, "scripts", "rocket-blueprints.lua"), Path.Combine(root, "scripts", "rocket-blueprints.lua"), true);
        foreach (var view in new[] { "side", "front", "top" })
            File.Copy(Path.Combine(samples, "images", $"rocket-{view}.png"), Path.Combine(root, "images", $"rocket-{view}.png"), true);
        var w = Ui.Open(1400, 860);
        var run = w.Modeler.RunMacroFile(Path.Combine(root, "scripts", "rocket-blueprints.lua"));
        for (int i = 0; i < 2000 && !run.IsCompleted; i++) { Ui.Pump(); Thread.Sleep(1); }
        Ui.Fit(w);
        Assert.Equal(3, w.Session.Document.Backdrops.All().Count());
        w.Session.SetSelectionMode(SelectionMode.Polygons);
        w.Modeler.Numeric.Collapsed = true;
        w.Modeler.Execute("Backdrop");
        w.Modeler.Backdrop.ShowView(BackdropView.Side);
        Ui.Pump();
        Ui.Fit(w);
        Persp(w, 30, 14, 1.9);
        Shot(w, "backdrops");
    }

    [AvaloniaFact]
    public void Mesh_info_finds_a_hole_and_a_flipped_face()
    {
        // A ball with its top cut off and one face turned the wrong way, as a careless edit might leave it: Mesh Info
        // says why it won't print, and its Select button has picked out the rim of the hole.
        var w = Ui.Open(1400, 860);
        var cmd = w.Session.Commands;
        var doc = w.Session.Document;
        cmd.Run("Ball", new Dictionary<string, object?> { ["Radius"] = new[] { 1.0, 1, 1 }, ["Sides"] = 32, ["Segments"] = 16, ["Surface"] = "Clay" });
        doc.Surfaces[doc.SurfaceIndex("Clay")].Color = new Rgb(214, 140, 92);
        var mesh = doc.Current.Mesh;
        cmd.SelectPolygons(Enumerable.Range(0, mesh.Polygons.Count).Where(i => mesh.Centroid(mesh.Polygons[i]).Y > 0.8));
        cmd.Run("Remove");
        var front = Enumerable.Range(0, mesh.Polygons.Count).OrderBy(i => mesh.Centroid(mesh.Polygons[i]).Z + Math.Abs(mesh.Centroid(mesh.Polygons[i]).Y - 0.1) + Math.Abs(mesh.Centroid(mesh.Polygons[i]).X)).First();
        cmd.SelectPolygons([front]);
        cmd.Run("Flip");
        cmd.SelectNone();
        w.Modeler.Execute("Mesh Info");
        Ui.Pump();
        var panel = w.Modeler.MeshInfo;
        Assert.StartsWith("Open: 1 hole along 32 edges; inconsistent winding: 4 edges", panel.Verdict.Text);
        Assert.Equal(1, panel.Report!.FlippedPolygons);
        panel.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SelectBoundaryEdges").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
        Assert.Equal(32, doc.Selection.EdgesIn(0).Count);
        Ui.Fit(w);
        Persp(w, 20, 32, 1.3);
        Shot(w, "mesh-info");
    }

    [AvaloniaFact]
    public void Edge_tools_round_a_box_and_cut_loops_round_a_tube()
    {
        // A case with every edge chamfered round in three segments and a panel inset and sunk into its top; beside it a
        // tube with three loops just cut round it (Loop Cut selects them). The Multiply tab shows Inset, Chamfer and
        // Bridge, with Chamfer's numbers in the panel.
        var w = Ui.Open(1400, 860);
        var cmd = w.Session.Commands;
        var doc = w.Session.Document;
        cmd.Run("Box", new Dictionary<string, object?> { ["Center"] = new[] { -0.75, 0.35, 0 }, ["Size"] = new[] { 1.1, 0.7, 0.8 }, ["Surface"] = "Case" });
        cmd.Run("Chamfer", new Dictionary<string, object?> { ["Width"] = 0.08, ["Segments"] = 3 });
        cmd.SelectMode(SelectionMode.Polygons);
        cmd.SelectInside(new Box3(new Vec3(-2, 0.69, -1), new Vec3(1, 0.71, 1)));
        Assert.Single(doc.Selection.PolygonsIn(0));
        cmd.Run("Inset", new Dictionary<string, object?> { ["Thickness"] = 0.1, ["Depth"] = -0.08 });
        cmd.SelectNone();
        cmd.Run("Disc", new Dictionary<string, object?> { ["Center"] = new[] { 0.8, 0.5, 0 }, ["Radius"] = new[] { 0.35, 0.35, 0.35 }, ["Height"] = 1.0, ["Sides"] = 16, ["Surface"] = "Tube" });
        var mesh = doc.Current.Mesh;
        var upright = mesh.EdgeMap().Keys.First(e => Vec3.Near(mesh.Points[e.A], new Vec3(1.15, 0, 0), 1e-9) && Vec3.Near(mesh.Points[e.B], new Vec3(1.15, 1, 0), 1e-9)
            || Vec3.Near(mesh.Points[e.B], new Vec3(1.15, 0, 0), 1e-9) && Vec3.Near(mesh.Points[e.A], new Vec3(1.15, 1, 0), 1e-9));
        cmd.SelectEdges([(upright.A, upright.B)]);
        cmd.Run("Loop Cut", new Dictionary<string, object?> { ["Count"] = 3 });
        Assert.Equal(48, doc.Selection.EdgesIn(0).Count);
        Assert.True(MeshCheck.Document(doc).Printable);
        doc.Surfaces[doc.SurfaceIndex("Case")].Color = new Rgb(120, 160, 210);
        doc.Surfaces[doc.SurfaceIndex("Case")].Specular = 0.4;
        doc.Surfaces[doc.SurfaceIndex("Tube")].Color = new Rgb(214, 150, 96);
        doc.Notify();
        w.Modeler.Tabs.ShowTab("Multiply");
        w.Modeler.PickTool("Chamfer");
        Ui.Fit(w);
        Persp(w, 150, 34, 1.8);
        Shot(w, "edge-tools");
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
    public void Procedural_textures_in_the_view_and_the_texture_panel()
    {
        var w = WithMacro("procedurals.lua");
        var doc = w.Session.Document;
        w.Session.SetSelectionMode(SelectionMode.Polygons);
        w.Modeler.Execute("Style Shaded");
        Persp(w, 190, 24, 2.6);
        Shot(w, "procedurals");
        // The marble's colour texture panel, Marble chosen in its Texture type list.
        var editor = new SurfaceEditor(w.Session) { Width = 520, Height = 640 };
        editor.Show(w);
        editor.Choose(doc.SurfaceIndex("Marble"));
        Ui.Pump();
        var panel = editor.OpenTexture(doc.Surfaces[doc.SurfaceIndex("Marble")], TextureChannel.Color);
        panel.Width = 380;
        panel.Height = 880;
        Ui.Pump();
        Assert.Equal("Marble", Assert.Single(panel.Layers).Procedural);
        Shot(panel, "procedural-panel");
        panel.Close();
        editor.Close();
    }

    [AvaloniaFact]
    public void A_ray_traced_frame_with_procedural_textures()
    {
        var w = WithMacro("procedurals.lua");
        var doc = w.Session.Document;
        void Look(string name, Action<Surface> set) => set(doc.Surfaces[doc.SurfaceIndex(name)]);
        Look("Marble", s => { s.Specular = 0.6; s.Glossiness = 256; s.Reflection = 0.06; });
        Look("Wood", s => s.Specular = 0.15);
        Look("Rock", s => { s.Specular = 0.1; s.Diffuse = 0.9; });
        Look("Floor", s => { s.Reflection = 0.1; s.Specular = 0.2; });
        var s = new LayoutSession();
        s.ShowModelerObject(doc);
        s.Scene.GradientBackground = true;
        s.Scene.GradientTop = new Vec3(0.12, 0.16, 0.3);
        s.Scene.GradientBottom = new Vec3(0.4, 0.36, 0.42);
        s.Scene.AmbientIntensity = 0.3;
        var cam = s.Scene.CurrentCamera!;
        cam.SetPosition(1, new Vec3(-0.3, 1.7, -3.6));
        cam.SetRotation(1, new Vec3(4, 20, 0));
        s.Scene.Lights.First().SetRotation(1, new Vec3(35, 40, 0));
        var img = RayTracer.Render(s.Scene, 1, new RenderSettings { Width = 640, Height = 400, SamplesPerPixel = 4 }, null, TestContext.Current.CancellationToken);
        // The same picture every time: the noise is Perlin's, with its fixed table.
        var again = RayTracer.Render(s.Scene, 1, new RenderSettings { Width = 64, Height = 40, SamplesPerPixel = 1 }, null, TestContext.Current.CancellationToken);
        Assert.Equal(again.Rgba, RayTracer.Render(s.Scene, 1, new RenderSettings { Width = 64, Height = 40, SamplesPerPixel = 1 }, null, TestContext.Current.CancellationToken).Rgba);
        if (Folder is { } dir)
        {
            Directory.CreateDirectory(dir);
            img.SavePng(Path.Combine(dir, "AVAModeler-procedural-render.png"), alpha: false);
        }
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
        var layout = AnimatedShip(w);
        layout.View.Mode = LayoutViewMode.Perspective;
        layout.View.Fit();
        layout.View.Camera.Heading = 20;
        layout.View.Camera.Pitch = 18;
        Shot(w, "layout");
    }

    /// <summary>The spaceship in Layout, flying across from frame 1 to 60.</summary>
    private static LayoutWorkspace AnimatedShip(MainWindow w)
    {
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
        return layout;
    }

    [AvaloniaFact]
    public void Rendering_the_ships_animation()
    {
        var w = WithMacro("spaceship.lua");
        var layout = AnimatedShip(w);
        var scene = layout.Session.Scene;
        scene.CurrentCamera!.SetPosition(1, new Vec3(0, 1, -9));
        scene.CurrentCamera.SetRotation(1, new Vec3(0, 6, 0));
        scene.GradientBackground = true;
        scene.GradientTop = new Vec3(0.05, 0.08, 0.2);
        scene.GradientBottom = new Vec3(0.35, 0.3, 0.45);
        var folder = Path.Combine(Path.GetTempPath(), "avamodeler-shots", "ship_frames");
        try
        {
            var a = new AnimationWindow(layout.Session) { Width = 860, Height = 720 };
            a.Show();
            void Set(string name, string text) => a.GetLogicalDescendants().OfType<TextBox>().First(b => b.Name == name).Text = text;
            Set("Last", "60");
            Set("Step", "12");
            Set("Width", "480");
            Set("Height", "270");
            Set("Folder", folder);
            foreach (var c in a.GetLogicalDescendants().OfType<CheckBox>().Where(c => c.Name is "Mp4" or "Gif")) c.IsChecked = false;
            var run = a.Start();
            for (int i = 0; i < 30000 && !run.IsCompleted; i++) { Ui.Pump(); Thread.Sleep(2); }
            Assert.True(run.IsCompleted);
            Assert.Equal(5, a.Result!.Files.Count);
            Shot(a, "animation");
            a.Close();
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(folder))) Directory.Delete(Path.GetDirectoryName(folder)!, true);
        }
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
    public void The_AI_chat_on_a_local_model()
    {
        // A stand-in for Ollama (no real server is called) plays qwen3 building a table with a few tool calls, the four
        // legs in one turn; the panel's Setup shows the OpenAI-compatible server it's set to.
        static AVAModeler.Chat.Tests.FakeModelServer.Part Box(string id, double[] size, double[] center) =>
            AVAModeler.Chat.Tests.FakeModelServer.Call(id, "run_tool", new { name = "Box", @params = new { Size = size, Center = center, Surface = "Wood" } });
        var server = new AVAModeler.Chat.Tests.FakeModelServer()
            .Reply(AVAModeler.Chat.Tests.FakeModelServer.Say("I'll make the table top first, then the four legs."), Box("call_1", [1.2, 0.06, 0.8], [0, 0.75, 0]))
            .Reply(Box("call_2", [0.06, 0.72, 0.06], [-0.55, 0.36, -0.35]), Box("call_3", [0.06, 0.72, 0.06], [0.55, 0.36, -0.35]),
                Box("call_4", [0.06, 0.72, 0.06], [-0.55, 0.36, 0.35]), Box("call_5", [0.06, 0.72, 0.06], [0.55, 0.36, 0.35]))
            .Reply(AVAModeler.Chat.Tests.FakeModelServer.Call("call_6", "set_surface", new { name = "Wood", color = new[] { 168, 112, 62 }, specular = 0.2 }),
                AVAModeler.Chat.Tests.FakeModelServer.Call("call_7", "check_mesh", new { }))
            .Reply(AVAModeler.Chat.Tests.FakeModelServer.Say("Done: a 1.2 m by 0.8 m table, 0.78 m high, with a top and four legs in a warm wood colour. check_mesh finds five closed boxes and no problems."));
        var w = ChatPanelTests.WithServer(server, 1400, 860);
        ChatPanelTests.Type(w, "make a simple wooden table");
        ChatPanelTests.Finish(w.Chat.Running);
        Assert.Equal(0, server.Left);
        Assert.Equal(40, w.Session.Document.Current.Mesh.Points.Count);
        Assert.DoesNotContain(w.Chat.Session.Entries.OfType<AVAModeler.Chat.ToolEntry>(), t => t.IsError);
        Assert.Equal("Using qwen3 on localhost:11434 (OpenAI-compatible, core tools)", w.Chat.UsingText);
        w.Session.Document.Selection.Clear();
        Ui.Fit(w);
        Persp(w, 35, 22, 1.1);
        Shot(w, "chat-local");
    }

    [AvaloniaFact]
    public void The_AI_chat_orchestrating_a_scene_in_Layout()
    {
        // The carbon atom sample's own calls (samples/mcp/carbon_atom.py), sent by a fake Claude (no real API call) a
        // few at a time: modeling, then staging and animating in Layout, past the 60-call pause, which Auto-continue
        // carries on through. Then a ray-traced check of a frame, and the answer.
        static AVAModeler.Chat.Tests.FakeClaude.Part C(string name, object input) => AVAModeler.Chat.Tests.FakeClaude.Call("toolu_" + Guid.NewGuid().ToString("N")[..12], name, input);
        var calls = new List<AVAModeler.Chat.Tests.FakeClaude.Part>
        {
            C("set_surface", new { name = "Proton", color = new[] { 245, 245, 240 }, specular = 0.5, glossiness = 128, smoothing = true, assign = false }),
            C("set_surface", new { name = "Neutron", color = new[] { 120, 122, 128 }, specular = 0.3, glossiness = 64, smoothing = true, assign = false }),
            C("set_surface", new { name = "Electron", color = new[] { 60, 255, 90 }, luminosity = 0.7, specular = 0.6, glossiness = 256, smoothing = true, assign = false }),
            C("set_surface", new { name = "Orbit", color = new[] { 150, 200, 255 }, luminosity = 0.5, smoothing = true, double_sided = true, assign = false }),
            C("set_layers", new { foreground = new[] { 1 } }),
        };
        double phi = (1 + Math.Sqrt(5)) / 2, spread = 0.513 / Math.Sqrt(1 + phi * phi);
        var corners = new List<double[]>();
        foreach (var a in new[] { 1, -1 }) foreach (var b in new[] { 1, -1 }) corners.Add([0, a, b * phi]);
        foreach (var a in new[] { 1, -1 }) foreach (var b in new[] { 1, -1 }) corners.Add([a, b * phi, 0]);
        foreach (var a in new[] { 1, -1 }) foreach (var b in new[] { 1, -1 }) corners.Add([b * phi, 0, a]);
        int[] protons = [0, 3, 5, 6, 8, 11];
        for (int i = 0; i < corners.Count; i++)
            calls.Add(C("run_tool", new { name = "Ball", @params = new { Center = corners[i].Select(c => Math.Round(c * spread, 4)).ToArray(), Radius = new[] { 0.21, 0.21, 0.21 }, Sides = 32, Segments = 16, Surface = protons.Contains(i) ? "Proton" : "Neutron" } }));
        const double radius = 1.6;
        int first = 0;
        foreach (var layer in new[] { 2, 3, 4 })
        {
            calls.Add(C("set_layers", new { foreground = new[] { layer } }));
            calls.Add(C("add_points", new { points = Enumerable.Range(0, 8).Select(a => new[] { radius + 0.018 * Math.Cos(a * Math.PI / 4), 0.018 * Math.Sin(a * Math.PI / 4), 0 }).ToArray() }));
            calls.Add(C("add_polygons", new { polygons = new[] { Enumerable.Range(first, 8).ToArray() }, surface = "Orbit" }));
            calls.Add(C("run_tool", new { name = "Lathe", @params = new { Axis = "Y", Sides = 96 } }));
            foreach (var x in new[] { radius, -radius })
                calls.Add(C("run_tool", new { name = "Ball", @params = new { Center = new[] { x, 0, 0 }, Radius = new[] { 0.1, 0.1, 0.1 }, Sides = 16, Segments = 8, Surface = "Electron" } }));
        }
        calls.Add(C("set_layers", new { foreground = new[] { 1, 2, 3, 4 } }));
        int modeled = calls.Count;
        calls.Add(C("set_scene", new { first_frame = 1, last_frame = 120, fps = 30, gradient = true, gradient_top = new[] { 12, 16, 40 }, gradient_bottom = new[] { 0, 0, 0 }, ambient_intensity = 0.15, frame = 1 }));
        calls.Add(C("add_object", new { layer = 1, name = "Nucleus" }));
        calls.Add(C("set_key", new { item = "Nucleus", channel = "heading", frame = 1, value = 0, linear = true }));
        calls.Add(C("set_key", new { item = "Nucleus", channel = "heading", frame = 120, value = 357, linear = true }));
        calls.Add(C("set_end_behavior", new { item = "Nucleus", channel = "heading", post = "repeat" }));
        foreach (var (k, layer, period, phase) in new[] { (1, 2, 30, 0), (2, 3, 24, 60), (3, 4, 40, 120) })
        {
            calls.Add(C("add_null", new { name = $"Tilt {k}", rotation = new[] { (k - 1) * 120, 62, 0 } }));
            calls.Add(C("add_null", new { name = $"Spin {k}", parent = $"Tilt {k}" }));
            calls.Add(C("add_object", new { layer, name = $"Orbit {k}", parent = $"Spin {k}" }));
            calls.Add(C("set_key", new { item = $"Spin {k}", channel = "heading", frame = 1, value = phase, linear = true }));
            calls.Add(C("set_key", new { item = $"Spin {k}", channel = "heading", frame = 1 + period, value = phase + 360, linear = true }));
            calls.Add(C("set_end_behavior", new { item = $"Spin {k}", channel = "heading", post = "repeat" }));
        }
        calls.Add(C("set_item", new { item = "Light", intensity = 0.9, color = new[] { 255, 245, 235 } }));
        calls.Add(C("add_light", new { type = "distant", name = "Rim", rotation = new[] { 200, 25, 0 }, color = new[] { 120, 150, 255 }, intensity = 0.5 }));
        calls.Add(C("set_transform", new { item = "Camera", frame = 1, position = new[] { 0, 1.6, -6.8 }, key = false }));
        calls.Add(C("look_at", new { item = "Camera", point = new[] { 0, 0, 0 }, frame = 1, key = false }));
        Assert.True(calls.Count > 60);

        var api = new AVAModeler.Chat.Tests.FakeClaude();
        api.Reply([AVAModeler.Chat.Tests.FakeClaude.Say("I'll model it first: four surfaces, a nucleus of twelve balls packed on an icosahedron's corners (six protons, six neutrons), and three orbit rings with two electrons each, one ring to a layer so each can spin on its own."), .. calls.Take(5)]);
        foreach (var chunk in calls.Skip(5).Take(modeled - 5).Chunk(6)) api.Reply([.. chunk]);
        api.Reply([AVAModeler.Chat.Tests.FakeClaude.Say("Now Layout: a 120-frame loop at 30 fps, the nucleus turning once, and each ring spinning in its own tilted plane under a pair of nulls."), .. calls.Skip(modeled).Take(5)]);
        foreach (var chunk in calls.Skip(modeled + 5).Chunk(6)) api.Reply([.. chunk]);
        api.Reply(AVAModeler.Chat.Tests.FakeClaude.Say("Let me ray-trace a frame to check it."), C("render_scene", new { frame = 12, width = 320, height = 180, samples = 2 }));
        api.Reply(AVAModeler.Chat.Tests.FakeClaude.Say("Done: a carbon atom with six white protons and six grey neutrons, and six green electrons on three tilted rings that each take a whole number of turns in 120 frames, so the animation loops without a jump. Press F10 to render it as a movie."));
        var w = ChatPanelTests.WithChat(api, 1400, 860);
        w.ToggleChat();
        Assert.True(w.Chat.AutoContinue.IsChecked);
        ChatPanelTests.Type(w, "build a stylized carbon atom: white protons, grey neutrons, and green electrons whizzing round the nucleus");
        ChatPanelTests.Finish(w.Chat.Running);
        Assert.Equal(0, api.Left);
        Assert.Contains(w.Chat.Session.Entries.OfType<AVAModeler.Chat.NoticeEntry>(), n => n.Text.Contains("carrying on by itself"));
        Assert.DoesNotContain(w.Chat.Session.Entries.OfType<AVAModeler.Chat.ToolEntry>(), t => t.IsError);

        w.ShowLayout();
        var layout = (LayoutWorkspace)w.Content!;
        // Switching to Layout leaves the agent's staging alone: no whole Modeler object on top of its layers.
        Assert.Equal(4, layout.Session.Scene.Objects.Count());
        layout.Session.Select(layout.Session.Scene.Items.Single(i => i.Name == "Nucleus"));
        layout.Session.SetFrame(12);
        layout.View.Mode = LayoutViewMode.Camera;
        Shot(w, "chat-layout");
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

    /// <summary>The sample studio environment, samples/images/studio.hdr (drawn by make_environment.py).</summary>
    private static string Studio => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "images", "studio.hdr"));

    /// <summary>procedurals.lua's marble ball, wooden block and rock on a checkerboard floor, with a chrome ball, in Layout.</summary>
    private static (LayoutSession Session, ObjectItem Chrome) EffectsStage()
    {
        var w = WithMacro("procedurals.lua");
        var doc = w.Session.Document;
        void Look(string name, Action<Surface> set) => set(doc.Surfaces[doc.SurfaceIndex(name)]);
        Look("Marble", s => { s.Specular = 0.6; s.Glossiness = 256; s.Reflection = 0.06; });
        Look("Wood", s => s.Specular = 0.15);
        Look("Rock", s => { s.Specular = 0.1; s.Diffuse = 0.9; });
        Look("Floor", s => { s.Reflection = 0.08; s.Specular = 0.2; });
        // A wider floor, so it reaches back into the blur.
        var floor = doc.Layers[0].Mesh;
        floor.Points = floor.Points.Select(p => new Vec3(p.X * 2, p.Y, p.Z * 2)).ToList();
        floor.Touch();
        var s = new LayoutSession();
        s.ShowModelerObject(doc);
        var chromeDoc = new Document();
        new AVAModeler.Tools.BallTool { Radius = new Vec3(0.32, 0.32, 0.32), Center = new Vec3(-0.15, 0.32, -0.7), Sides = 48, Segments = 24, Surface = "Chrome" }.Run(new AVAModeler.Tools.ToolContext(chromeDoc));
        var chromeSurface = chromeDoc.Surfaces[chromeDoc.SurfaceIndex("Chrome")];
        (chromeSurface.Color, chromeSurface.Diffuse, chromeSurface.Specular, chromeSurface.Glossiness, chromeSurface.Reflection, chromeSurface.Smoothing) =
            (new Rgb(230, 232, 240), 0.1, 0.8, 512, 0.85, true);
        var chrome = s.Scene.AddObject("Chrome", chromeDoc);
        s.Scene.Remove(s.Scene.Lights.First());
        return (s, chrome);
    }

    [AvaloniaFact]
    public void A_ray_traced_frame_with_the_render_effects()
    {
        var (s, chrome) = EffectsStage();
        var scene = s.Scene;
        scene.AmbientIntensity = 0.12;
        scene.Occlusion = new AmbientOcclusion { On = true, Samples = 32, Distance = 0.6 };
        scene.Environment = new EnvironmentSettings { Image = Studio, Lighting = true, Samples = 32, Heading = -20, Intensity = 0.5 };
        var key = scene.AddLight("Key", LightType.Area);
        key.SetPosition(1, new Vec3(-2.6, 3.2, -2.4));
        key.SetRotation(1, key.RotationToward(new Vec3(0, 0.3, 0), 1));
        (key.AreaWidth, key.AreaHeight, key.ShadowSamples) = (1.6, 1.6, 32);
        key.Intensity.SetConstant(0.7);
        var cam = scene.CurrentCamera!;
        cam.SetPosition(1, new Vec3(-0.4, 1.45, -3.7));
        cam.SetRotation(1, new Vec3(6, 19, 0));
        s.SetFrame(1);
        s.Select(cam);
        // Focus on the chrome ball with Layout's helper, then on the marble ball's near side (part of the Modeler's object).
        double focus = s.FocusOn(cam, chrome);
        Assert.InRange(focus, 2, 4);
        cam.DepthOfField = cam.DepthOfField with { FocalDistance = cam.DepthOf(new Vec3(-0.95, 0.5, 0.2), 1) - 0.35, FStop = 0.12, Samples = 81 };
        bool full = Folder != null;
        var settings = new RenderSettings { Width = full ? 640 : 96, Height = full ? 400 : 60, SamplesPerPixel = 4 };
        if (!full) cam.DepthOfField = cam.DepthOfField with { Samples = 4 };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var img = RayTracer.Render(scene, 1, settings, null, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"Effects showcase {img.Width}×{img.Height}: {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains(img.Rgba.Where((_, i) => i % 4 == 0), r => r > 40);
        if (Folder is { } dir)
        {
            Directory.CreateDirectory(dir);
            img.SavePng(Path.Combine(dir, "AVAModeler-effects-render.png"), alpha: false);
        }
    }

    [AvaloniaFact]
    public void A_motion_blurred_frame()
    {
        var (s, chrome) = EffectsStage();
        var scene = s.Scene;
        scene.GradientBackground = true;
        scene.GradientTop = new Vec3(0.12, 0.16, 0.3);
        scene.GradientBottom = new Vec3(0.4, 0.36, 0.42);
        scene.AmbientIntensity = 0.3;
        var sun = scene.AddLight("Sun");
        sun.SetRotation(1, new Vec3(35, 40, 0));
        scene.Remove(chrome);
        // The scene's object holds all four shapes, so it moves as one: it sweeps to the right and turns, fast.
        var objects = scene.Objects.Single();
        var cam = scene.CurrentCamera!;
        cam.SetPosition(1, new Vec3(-0.3, 1.7, -3.6));
        cam.SetRotation(1, new Vec3(4, 20, 0));
        // A ball rolls across in front, quickly, while the rest stands still.
        var ballDoc = new Document();
        new AVAModeler.Tools.BallTool { Radius = new Vec3(0.25, 0.25, 0.25), Center = Vec3.Zero, Sides = 32, Segments = 16, Surface = "Ball" }.Run(new AVAModeler.Tools.ToolContext(ballDoc));
        var ballSurface = ballDoc.Surfaces[ballDoc.SurfaceIndex("Ball")];
        (ballSurface.Color, ballSurface.Specular, ballSurface.Glossiness, ballSurface.Smoothing) = (new Rgb(220, 50, 35), 0.6, 128, true);
        var ball = scene.AddObject("Ball", ballDoc);
        ball.SetPosition(1, new Vec3(-2.2, 0.25, -1.2));
        ball.SetPosition(11, new Vec3(2.2, 0.25, -1.2));
        // The wooden block spins on the spot.
        var spinDoc = new Document();
        new AVAModeler.Tools.BoxTool { Size = new Vec3(0.5, 0.12, 0.12), Center = Vec3.Zero, Surface = "Spinner" }.Run(new AVAModeler.Tools.ToolContext(spinDoc));
        spinDoc.Surfaces[spinDoc.SurfaceIndex("Spinner")].Color = new Rgb(60, 140, 230);
        var spinner = scene.AddObject("Spinner", spinDoc);
        spinner.SetPosition(1, new Vec3(0.2, 0.75, 0.75));
        spinner.Channels[3].AddKey(1, 0);
        spinner.Channels[3].AddKey(11, 900);
        Assert.Same(objects, scene.Objects.First());
        cam.MotionBlur = new MotionBlur { On = true, Shutter = 0.5, Samples = 32 };
        bool full = Folder != null;
        var settings = new RenderSettings { Width = full ? 640 : 96, Height = full ? 400 : 60, SamplesPerPixel = 4 };
        if (!full) cam.MotionBlur = cam.MotionBlur with { Samples = 4 };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var img = RayTracer.Render(scene, 6, settings, null, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"Motion blur {img.Width}×{img.Height}: {clock.Elapsed.TotalSeconds:0.0} s");
        if (Folder is { } dir)
        {
            Directory.CreateDirectory(dir);
            img.SavePng(Path.Combine(dir, "AVAModeler-motion-blur.png"), alpha: false);
        }
    }
}
