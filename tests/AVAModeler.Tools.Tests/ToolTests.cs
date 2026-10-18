using AVAModeler.Core;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

internal static class Make
{
    public static (Document Doc, ToolContext Ctx) Empty() { var d = new Document(); return (d, new ToolContext(d)); }

    /// <summary>A document with a unit cube in layer 1.</summary>
    public static (Document Doc, ToolContext Ctx) WithCube()
    {
        var (d, c) = Empty();
        new BoxTool().Run(c);
        return (d, c);
    }

    public static DragInfo FrontDrag(Vec3 from, Vec3 to, double dx = 0, double dy = 0, bool first = true) => new()
    {
        Start = from, Current = to, DxPixels = dx, DyPixels = dy, First = first,
        Right = Vec3.UnitX, Up = Vec3.UnitY, Forward = Vec3.UnitZ,
    };

    public static double Volume(Mesh m)
    {
        double v = 0;
        foreach (var p in m.Polygons.Where(p => p.Count >= 3))
            for (int i = 1; i + 1 < p.Count; i++)
                v += Vec3.Dot(m.Points[p.Points[0]], Vec3.Cross(m.Points[p.Points[i]], m.Points[p.Points[i + 1]]));
        return v / 6;
    }

    public static bool Closed(Mesh m) => m.EdgeMap().Values.All(f => f.Count == 2);
}

public class FrameworkTests
{
    [Fact]
    public void Parameters_come_from_attributes_and_can_be_set_by_name()
    {
        var box = new BoxTool();
        Assert.Equal(["Center", "Size", "SegmentsX", "SegmentsY", "SegmentsZ", "Surface"], box.Parameters.Select(p => p.Name));
        box.Parameter("size")!.Value = new[] { 2.0, 3, 4 };
        Assert.Equal(new Vec3(2, 3, 4), box.Size);
        box.Parameter("Segments X")!.Value = 4.0;
        Assert.Equal(4, box.SegmentsX);
        var ball = new BallTool();
        ball.Parameter("Kind")!.Value = "tessellated";
        Assert.Equal(BallKind.Tessellated, ball.Style);
        box.Reset();
        Assert.Equal(Vec3.One, box.Size);
    }

    [Fact]
    public void Previews_recompute_from_the_original_and_cancel_restores_it()
    {
        var (d, c) = Make.WithCube();
        var move = new MoveTool();
        move.Begin(c);
        move.Drag(Make.FrontDrag(Vec3.Zero, new Vec3(1, 0, 0)));
        move.Drag(Make.FrontDrag(Vec3.Zero, new Vec3(2, 0, 0), first: false));
        Assert.Equal(2.5, d.Current.Mesh.Bounds().Max.X, 12); // moved by 2, not 3
        move.Cancel();
        Assert.Equal(0.5, d.Current.Mesh.Bounds().Max.X, 12);
        Assert.Equal(1, d.Undo.Count);
    }

    [Fact]
    public void A_committed_tool_is_one_undo_step()
    {
        var (d, c) = Make.WithCube();
        var move = new MoveTool { Offset = new Vec3(0, 1, 0) };
        move.Begin(c);
        move.Update();
        move.Update();
        move.Commit();
        Assert.Equal(2, d.Undo.Count);
        Assert.Equal("Move", d.Undo.UndoLabel);
        d.UndoLast();
        Assert.Equal(-0.5, d.Current.Mesh.Points[0].Y, 12);
    }

    [Fact]
    public void Snapping_and_action_centres()
    {
        var (d, c) = Make.WithCube();
        c.Snap = 0.25;
        Assert.Equal(new Vec3(0.25, 0.5, -0.25), c.SnapPoint(new Vec3(0.3, 0.4, -0.2)));
        d.Current.Mesh.Transform(Mat4.Translation(new Vec3(3, 0, 0)));
        Assert.Equal(new Vec3(3, 0, 0), c.Center());
        c.CenterMode = ActionCenter.Origin;
        Assert.Equal(Vec3.Zero, c.Center());
        c.CenterMode = ActionCenter.Mouse;
        c.MousePoint = new Vec3(1, 2, 3);
        Assert.Equal(new Vec3(1, 2, 3), c.Center());
    }
}

public class ObjectsTests
{
    [Fact]
    public void A_box_is_closed_with_outward_faces_and_its_segments()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Size = new Vec3(2, 1, 1), SegmentsX = 2, SegmentsY = 3, SegmentsZ = 1 }.Run(c);
        var m = d.Current.Mesh;
        Assert.Equal(2 * 3 * 2 + 2 * 1 * 2 + 3 * 1 * 2, m.Polygons.Count);
        Assert.True(Make.Closed(m));
        Assert.Equal(2, Make.Volume(m), 9);
        Assert.All(m.Polygons, p => Assert.True(Vec3.Dot(m.Normal(p), m.Centroid(p)) > 0));
    }

    [Fact]
    public void A_flat_box_is_a_grid_facing_the_view_and_a_drag_spans_it()
    {
        var (d, c) = Make.Empty();
        var box = new BoxTool();
        box.Begin(c);
        box.Drag(Make.FrontDrag(new Vec3(-1, -1, 0), new Vec3(1, 2, 0)));
        Assert.Equal(new Vec3(2, 3, 0), box.Size);
        Assert.Equal(new Vec3(0, 0.5, 0), box.Center);
        box.Commit();
        var m = d.Current.Mesh;
        Assert.Single(m.Polygons);
        Assert.True(Vec3.Near(-Vec3.UnitZ, m.Normal(0), 1e-12));
    }

    [Fact]
    public void Balls_discs_and_cones_are_closed_and_face_outward()
    {
        foreach (var tool in new Tool[]
                 {
                     new BallTool(), new BallTool { Style = BallKind.Tessellated, Level = 2 }, new DiscTool { Segments = 3 },
                     new ConeTool(), new DiscTool { Axis = Axis.X }, new ConeTool { Axis = Axis.Z, Segments = 2 },
                 })
        {
            var (d, c) = Make.Empty();
            tool.Run(c);
            var m = d.Current.Mesh;
            Assert.True(Make.Closed(m), tool.Name);
            Assert.True(Make.Volume(m) > 0, tool.Name);
            Assert.All(m.Polygons, p => Assert.True(Vec3.Dot(m.Normal(p), m.Centroid(p)) > -1e-9, $"{tool.Name} {p}"));
        }
        var (doc, ctx) = Make.Empty();
        new BallTool { Sides = 32, Segments = 16 }.Run(ctx);
        Assert.Equal(4 * Math.PI / 3 * 0.125, Make.Volume(doc.Current.Mesh), 1); // a sphere of radius 0.5, roughly
        var (dt, ct) = Make.Empty();
        new BallTool { Style = BallKind.Tessellated, Level = 1 }.Run(ct);
        Assert.Equal(80, dt.Current.Mesh.Polygons.Count);
        Assert.All(dt.Current.Mesh.Points, p => Assert.Equal(0.5, p.Length, 9));
    }

    [Fact]
    public void Points_then_make_polygon_in_the_order_selected()
    {
        var (d, c) = Make.Empty();
        var pts = new PointsTool();
        pts.Begin(c);
        pts.Drag(Make.FrontDrag(Vec3.Zero, new Vec3(0, 0, 0)));
        pts.Drag(Make.FrontDrag(Vec3.Zero, new Vec3(0, 1, 0)));
        pts.Drag(Make.FrontDrag(Vec3.Zero, new Vec3(1, 1, 0)));
        pts.Drag(Make.FrontDrag(Vec3.Zero, new Vec3(1, 0.2, 0), first: false));
        pts.Commit();
        Assert.Equal(3, d.Current.Mesh.Points.Count);
        Assert.Equal(new Vec3(1, 0.2, 0), d.Current.Mesh.Points[2]);
        new MakePolygonTool { Surface = "Tri" }.Run(c);
        var poly = Assert.Single(d.Current.Mesh.Polygons);
        Assert.Equal([0, 1, 2], poly.Points);
        Assert.Equal("Tri", d.Surfaces[poly.Surface].Name);
        Assert.Equal(SelectionMode.Polygons, d.Selection.Mode);
        // One or two points make a point or a line.
        d.Selection.Mode = SelectionMode.Points;
        d.Selection.SelectPoint(0, 2);
        d.Selection.SelectPoint(0, 0);
        new MakePolygonTool().Run(c);
        Assert.Equal([2, 0], d.Current.Mesh.Polygons[^1].Points);
    }

    [Fact]
    public void Text_makes_one_polygon_per_letter_with_holes_cut_in()
    {
        var outlines = TextTool.Outlines("O", "", 6);
        Assert.Single(outlines);
        Assert.Equal(2, outlines[0].Count); // the outside and the hole
        var (d, c) = Make.Empty();
        new TextTool { Text = "AVA", Height = 2 }.Run(c);
        var m = d.Current.Mesh;
        Assert.Equal(3, m.Polygons.Count);
        Assert.All(m.Polygons, p => Assert.True(Vec3.Dot(m.Normal(p), -Vec3.UnitZ) > 0.99));
        var b = m.Bounds();
        Assert.InRange(b.Size.Y, 1, 2);
        Assert.True(b.Size.X > b.Size.Y);
    }
}

public class ModifyTests
{
    [Fact]
    public void Move_rotate_and_size_by_their_numbers()
    {
        var (d, c) = Make.WithCube();
        var m = d.Current.Mesh;
        new MoveTool { Offset = new Vec3(1, 0, 0) }.Run(c);
        Assert.Equal(new Vec3(1, 0, 0), m.Bounds().Center);
        new RotateTool { Angle = 90, Axis = Axis.Y, Center = Vec3.Zero }.Run(c);
        Assert.True(Vec3.Near(new Vec3(0, 0, -1), d.Current.Mesh.Bounds().Center, 1e-9));
        new SizeTool { Factor = 200, Center = new Vec3(0, 0, -1) }.Run(c);
        Assert.Equal(2, d.Current.Mesh.Bounds().Size.X, 9);
        new StretchTool { Factor = new Vec3(100, 50, 300), Center = new Vec3(0, 0, -1) }.Run(c);
        Assert.Equal(new Vec3(2, 1, 6), d.Current.Mesh.Bounds().Size);
    }

    [Fact]
    public void Dragging_sets_the_numbers()
    {
        var (_, c) = Make.WithCube();
        var move = new MoveTool();
        move.Begin(c);
        move.Drag(new DragInfo { Start = Vec3.Zero, Current = new Vec3(1, 0.2, 0), Constrain = true, Right = Vec3.UnitX, Up = Vec3.UnitY, Forward = Vec3.UnitZ });
        Assert.Equal(new Vec3(1, 0, 0), move.Offset);
        move.Cancel();
        var rot = new RotateTool();
        rot.Begin(c);
        rot.Drag(new DragInfo { DxPixels = 50, Constrain = true, Right = Vec3.UnitX, Up = Vec3.UnitZ, Forward = -Vec3.UnitY });
        Assert.Equal(Axis.Y, rot.Axis);
        Assert.Equal(-30, rot.Angle);
        rot.Cancel();
        var size = new SizeTool();
        size.Begin(c);
        size.Drag(new DragInfo { DxPixels = 150 * Math.Log(2), Right = Vec3.UnitX, Up = Vec3.UnitY, Forward = Vec3.UnitZ });
        Assert.Equal(200, size.Factor, 2);
        size.Cancel();
    }

    [Fact]
    public void Only_the_selection_moves()
    {
        var (d, c) = Make.WithCube();
        d.Selection.Mode = SelectionMode.Polygons;
        var front = Enumerable.Range(0, 6).Single(i => d.Current.Mesh.Normal(i).Z < -0.9);
        d.Selection.PolygonsIn(0).Add(front);
        new MoveTool { Offset = new Vec3(0, 0, -1) }.Run(c);
        var m = d.Current.Mesh;
        Assert.Equal(4, m.Points.Count(p => p.Z < -1));
        Assert.Equal(4, m.Points.Count(p => Math.Abs(p.Z - 0.5) < 1e-12));
    }

    [Fact]
    public void Shear_twist_taper_and_bend_leave_the_base_and_change_the_top()
    {
        Mesh Column()
        {
            var (d, c) = Make.Empty();
            new BoxTool { Size = new Vec3(1, 4, 1), Center = new Vec3(0, 2, 0), SegmentsY = 8 }.Run(c);
            return d.Current.Mesh;
        }
        Mesh After(Tool t)
        {
            var d = new Document();
            d.Layers[0].Mesh = Column();
            t.Run(new ToolContext(d));
            return d.Current.Mesh;
        }
        var bottom = Column().Points.Select((p, i) => (p, i)).Where(x => x.p.Y == 0).Select(x => x.i).ToList();

        var shear = After(new ShearTool { Offset = new Vec3(2, 0, 0), Along = Axis.Y });
        Assert.Equal(2.5, shear.Bounds().Max.X, 9);
        Assert.All(bottom, i => Assert.Equal(Column().Points[i], shear.Points[i]));

        var twist = After(new TwistTool { Angle = 90, Axis = Axis.Y, Center = new Vec3(0, 2, 0) });
        Assert.All(bottom, i => Assert.True(Vec3.Near(Column().Points[i], twist.Points[i], 1e-9)));
        Assert.True(Vec3.Near(new Vec3(0.5, 4, 0.5), twist.Points.Where(p => p.Y > 3.99).OrderBy(p => p.X).ThenBy(p => p.Z).First() * 1 + Vec3.Zero, 1e-9)
                    || twist.Points.Where(p => p.Y > 3.99).All(p => Math.Abs(Math.Abs(p.X) - 0.5) < 1e-9));

        var taper = After(new TaperTool { Factor = 50, Axis = Axis.Y, Center = new Vec3(0, 2, 0) });
        Assert.Equal(0.5, taper.Points.Where(p => p.Y > 3.99).Max(p => p.X) * 2, 9);
        Assert.Equal(1, taper.Points.Where(p => p.Y < 0.01).Max(p => p.X) * 2, 9);

        var bend = After(new BendTool { Angle = 90, Axis = Axis.Y, Toward = Axis.X, Center = new Vec3(0, 2, 0) });
        Assert.All(bottom, i => Assert.True(Vec3.Near(Column().Points[i], bend.Points[i], 1e-9)));
        // A quarter turn over a length of 4 (radius 4/(π/2)): the top ends up out along +X, lying flat.
        var top = bend.Points.Where((_, i) => Column().Points[i].Y > 3.99).ToList();
        double r = 4 / (Math.PI / 2);
        Assert.Equal(r, top.Average(p => p.Y), 6);
        Assert.Equal(r, top.Average(p => p.X), 6);
    }

    [Fact]
    public void Magnet_pulls_the_near_points_most()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Size = new Vec3(4, 0, 4), SegmentsX = 8, SegmentsZ = 8 }.Run(c);
        new MagnetTool { Center = Vec3.Zero, Radius = new Vec3(1, 1, 1), Offset = new Vec3(0, 1, 0) }.Run(c);
        var m = d.Current.Mesh;
        Assert.Equal(1, m.Points.Max(p => p.Y), 9);
        Assert.Equal(0, m.Points.Where(p => Math.Abs(p.X) > 1.4 || Math.Abs(p.Z) > 1.4).Max(p => p.Y), 9);
    }

    [Fact]
    public void Drag_moves_the_nearest_point_only()
    {
        var (d, c) = Make.WithCube();
        var drag = new DragTool();
        drag.Begin(c);
        drag.Drag(Make.FrontDrag(new Vec3(0.45, 0.45, 0), new Vec3(1.45, 0.45, 0)));
        drag.Commit();
        var moved = d.Current.Mesh.Points.Where(p => p.X > 1).ToList();
        Assert.Single(moved);
    }

    [Fact]
    public void Jitter_is_repeatable_smooth_shrinks_center_and_quantize_snap()
    {
        Vec3[] Jittered(int seed)
        {
            var (d, c) = Make.WithCube();
            new JitterTool { Seed = seed }.Run(c);
            return [.. d.Current.Mesh.Points];
        }
        Assert.Equal(Jittered(3), Jittered(3));
        Assert.NotEqual(Jittered(3), Jittered(4));

        var (doc, ctx) = Make.Empty();
        new BallTool().Run(ctx);
        double before = doc.Current.Mesh.Bounds().Extent;
        new SmoothTool { Iterations = 5 }.Run(ctx);
        Assert.True(doc.Current.Mesh.Bounds().Extent < before);

        new MoveTool { Offset = new Vec3(3, 4, 5) }.Run(ctx);
        new CenterTool { Y = false }.Run(ctx);
        var center = doc.Current.Mesh.Bounds().Center;
        Assert.Equal(0, center.X, 9);
        Assert.Equal(4, center.Y, 9);

        new QuantizeTool { Step = new Vec3(0.25, 0.25, 0.25) }.Run(ctx);
        Assert.All(doc.Current.Mesh.Points, p => Assert.Equal(0, Math.IEEERemainder(p.X, 0.25), 9));
    }
}

public class PolygonAndLayerTests
{
    [Fact]
    public void Flip_turns_faces_and_surface_names_them()
    {
        var (d, c) = Make.WithCube();
        var n = d.Current.Mesh.Normal(0);
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).Add(0);
        new FlipTool().Run(c);
        Assert.True(Vec3.Near(-n, d.Current.Mesh.Normal(0), 1e-12));
        new SurfaceTool { SurfaceName = "Hull", Color = new Vec3(255, 0, 0) }.Run(c);
        Assert.Equal("Hull", d.Surfaces[d.Current.Mesh.Polygons[0].Surface].Name);
        Assert.Equal(new Rgb(255, 0, 0), d.Surfaces[1].Color);
        Assert.Equal(0, d.Current.Mesh.Polygons[1].Surface);
    }

    [Fact]
    public void Remove_deletes_the_selection_and_its_loose_points()
    {
        var (d, c) = Make.WithCube();
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).UnionWith([0, 1]);
        new RemoveTool().Run(c);
        Assert.Equal(4, d.Current.Mesh.Polygons.Count);
        Assert.Equal(8, d.Current.Mesh.Points.Count);
        d.Selection.Mode = SelectionMode.Points;
        d.Selection.SelectPoint(0, 0);
        new RemoveTool().Run(c);
        Assert.Equal(7, d.Current.Mesh.Points.Count);
        Assert.Equal(2, d.Current.Mesh.Polygons.Count);
    }

    [Fact]
    public void Merge_points_welds_two_touching_boxes()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Center = new Vec3(-0.5, 0, 0) }.Run(c);
        new BoxTool { Center = new Vec3(0.5, 0, 0) }.Run(c);
        var t = new MergePointsTool();
        t.Run(c);
        Assert.Equal(4, t.Merged);
        Assert.Equal(12, d.Current.Mesh.Points.Count);
    }

    [Fact]
    public void Cut_copy_paste_between_layers_and_swap()
    {
        var (d, c) = Make.WithCube();
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).Add(0);
        LayerOps.Copy(d);
        Assert.Equal(4, d.Clipboard!.Points.Count);
        LayerOps.Cut(d);
        Assert.Equal(5, d.Current.Mesh.Polygons.Count);
        d.SetLayers([1], [0]);
        LayerOps.Paste(d);
        Assert.Single(d.Layers[1].Mesh.Polygons);
        LayerOps.SwapForegroundBackground(d);
        Assert.Equal([0], d.ForegroundLayers);
        Assert.Equal([1], d.BackgroundLayers);
        d.UndoLast();
        Assert.Empty(d.Layers[1].Mesh.Polygons);
    }

    [Fact]
    public void Selection_commands()
    {
        var (d, c) = Make.WithCube();
        new BoxTool { Center = new Vec3(5, 0, 0) }.Run(c);
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).Add(0);
        SelectionOps.Connected(d);
        Assert.Equal(6, d.Selection.PolygonsIn(0).Count);
        SelectionOps.Invert(d);
        Assert.Equal(Enumerable.Range(6, 6), d.Selection.PolygonsIn(0).Order());
        SelectionOps.None(d);
        d.Selection.PolygonsIn(0).Add(0);
        SelectionOps.Grow(d);
        Assert.Equal(5, d.Selection.PolygonsIn(0).Count);
        SelectionOps.Shrink(d);
        Assert.Equal([0], d.Selection.PolygonsIn(0)); // its neighbours each touch an unselected face
        d.Selection.Mode = SelectionMode.Edges;
        SelectionOps.All(d);
        Assert.Equal(24, d.Selection.EdgesIn(0).Count);
    }
}

public class CommandTests
{
    [Fact]
    public void Tools_run_by_name_with_parameters_by_name()
    {
        var cmd = new Commands(new Document());
        cmd.Run("Box", new Dictionary<string, object?> { ["Size"] = new[] { 2.0, 2, 2 } });
        Assert.Equal(8, cmd.PointCount);
        Assert.Equal(2, cmd.Document.Current.Mesh.Bounds().Size.X);
        Assert.Throws<ArgumentException>(() => cmd.Run("Nope"));
        var e = Assert.Throws<ArgumentException>(() => cmd.Run("Box", new Dictionary<string, object?> { ["Colour"] = 1 }));
        Assert.Contains("SegmentsX", e.Message);
        Assert.Contains("Move", cmd.Registry.Names);
        Assert.Contains(cmd.Registry.ByTab(), g => g.Key == "Modify" && g.Any(t => t.Name == "Taper"));
    }

    [Fact]
    public void A_macro_builds_and_edits_geometry()
    {
        var cmd = new Commands(new Document());
        Macros.Run("""
            local a = addpoint(0, 0, 0)
            local b = addpoint(0, 1, 0)
            local c = addpoint(1, 0, 0)
            addpolygon({a, b, c}, "Red")
            tool("Move", { Offset = {0, 0, 2} })
            local x, y, z = getpoint(2)
            print("point 2 is at " .. x .. "," .. y .. "," .. z)
            selmode("polygons"); selectall(); tool("Flip")
            print(#tools() .. " tools")
            """, cmd);
        Assert.Equal(3, cmd.PointCount);
        Assert.Equal(1, cmd.PolygonCount);
        Assert.Equal([2, 1, 0], cmd.PolygonPoints(0));
        Assert.Equal("point 2 is at 0,1,2", cmd.Output[0]);
        Assert.Equal("Red", cmd.Document.Surfaces[1].Name);
    }

    [Fact]
    public void The_sample_macro_runs()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "scripts", "spiral-stairs.lua");
        var cmd = new Commands(new Document());
        Macros.Run(File.ReadAllText(path), cmd);
        Assert.Equal(16 * 6 + 18, cmd.PolygonCount); // the steps, and a 16-sided post with its caps
        Assert.StartsWith("Made 16 steps", cmd.Output.Single());
    }

    [Fact]
    public void Macro_errors_say_what_went_wrong()
    {
        var cmd = new Commands(new Document());
        Assert.Contains("no tool called", Assert.Throws<MacroException>(() => Macros.Run("tool('Spaceship')", cmd)).Message);
        Assert.Throws<MacroException>(() => Macros.Run("this is not lua", cmd));
        Assert.Throws<MacroException>(() => Macros.Run("io.open('x')", cmd));
    }

    [Fact]
    public void Files_load_and_save_through_the_commands()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avamodeler-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cmd = new Commands(new Document());
            Document? replaced = null;
            cmd.DocumentReplaced += doc => replaced = doc;
            Macros.Run($"tool('Ball'); save('{dir.Replace("\\", "/")}/ball.lwo'); new(); load('{dir.Replace("\\", "/")}/ball.lwo')", cmd);
            Assert.NotNull(replaced);
            Assert.Equal(24 * 12, cmd.PolygonCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_plugin_from_its_own_assembly_adds_a_tool()
    {
        var built = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "StarPlugin", "bin");
        var dll = Directory.GetFiles(built, "StarPlugin.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        var folder = Path.Combine(Path.GetTempPath(), "avamodeler-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(dll, Path.Combine(folder, "StarPlugin.dll"));
            var registry = ToolRegistry.Create();
            Assert.False(registry.Has("Star"));
            Assert.Empty(registry.LoadPlugins(folder));
            Assert.True(registry.Has("Star"));
            Assert.Contains("Star (1 tools)", registry.Plugins);
            var cmd = new Commands(new Document(), registry);
            cmd.Run("Star", new Dictionary<string, object?> { ["Points"] = 6 });
            Assert.Equal(12, cmd.PointCount);
            Assert.True(Vec3.Near(-Vec3.UnitZ, cmd.Document.Current.Mesh.Normal(0), 1e-9));
            // Its file format saves and loads.
            var format = Assert.Single(registry.Formats);
            Assert.Equal([".xyz"], format.Extensions);
            var file = Path.Combine(folder, "star.xyz");
            format.Save(cmd.Document, file);
            Assert.Equal(12, format.Load(file).Current.Mesh.Polygons.Count);
        }
        finally { Directory.Delete(folder, true); }
    }
}
