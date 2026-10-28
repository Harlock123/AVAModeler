using AVAModeler.Core;
using AVAModeler.Rendering;

namespace AVAModeler.Rendering.Tests;

public class CameraTests
{
    private static ViewCamera Cam(ViewKind kind)
    {
        var c = new ViewCamera(kind);
        c.Resize(400, 300);
        return c;
    }

    [Theory]
    [InlineData(ViewKind.Top, 1, 0, 0, 1, 0)]   // +X to the right
    [InlineData(ViewKind.Top, 0, 0, 1, 0, -1)]  // +Z up the screen
    [InlineData(ViewKind.Front, 1, 0, 0, 1, 0)]
    [InlineData(ViewKind.Front, 0, 1, 0, 0, -1)]
    [InlineData(ViewKind.Side, 0, 0, 1, 1, 0)]  // the side view looks from the right: +Z to the right
    [InlineData(ViewKind.Side, 0, 1, 0, 0, -1)]
    public void The_orthographic_views_look_down_their_axes(ViewKind kind, double x, double y, double z, int right, int down)
    {
        var c = Cam(kind);
        var p = c.Project(new Vec3(x, y, z));
        Assert.Equal(200 + right * 100, p.X, 9);
        Assert.Equal(150 + down * 100, p.Y, 9);
    }

    [Fact]
    public void Nearer_things_have_smaller_depth()
    {
        var front = Cam(ViewKind.Front);
        Assert.True(front.Project(new Vec3(0, 0, -1)).Depth < front.Project(new Vec3(0, 0, 1)).Depth);
        var top = Cam(ViewKind.Top);
        Assert.True(top.Project(new Vec3(0, 5, 0)).Depth < top.Project(Vec3.Zero).Depth);
        var side = Cam(ViewKind.Side);
        Assert.True(side.Project(new Vec3(5, 0, 0)).Depth < side.Project(Vec3.Zero).Depth);
    }

    [Fact]
    public void The_perspective_view_is_right_handed_on_screen_like_the_front_view()
    {
        var c = Cam(ViewKind.Perspective);
        c.Heading = 0;
        c.Pitch = 0;
        c.Distance = 10;
        var right = c.Project(new Vec3(1, 0, 0));
        var up = c.Project(new Vec3(0, 1, 0));
        Assert.True(right.X > 200 && Math.Abs(right.Y - 150) < 1e-9);
        Assert.True(up.Y < 150);
        Assert.False(c.Project(new Vec3(0, 0, -20)).Visible); // behind the eye
    }

    [Fact]
    public void Rays_and_unprojection_invert_projection()
    {
        foreach (var kind in Enum.GetValues<ViewKind>())
        {
            var c = Cam(kind);
            c.Center = new Vec3(1, 2, 3);
            var p = new Vec3(1.5, 2.25, 2.6);
            var s = c.Project(p);
            var back = c.Unproject(s.X, s.Y, p);
            Assert.True(Vec3.Near(p, back, 1e-6), $"{kind}: {p} → {back}");
            var ray = c.RayAt(s.X, s.Y);
            Assert.True(ray.DistanceTo(p) < 1e-6, kind.ToString());
        }
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_mouse_and_pan_follows_it()
    {
        var c = Cam(ViewKind.Front);
        var under = c.Unproject(300, 100, Vec3.Zero);
        c.Zoom(2, 300, 100);
        Assert.True(Vec3.Near(under, c.Unproject(300, 100, Vec3.Zero), 1e-9));
        var p = new Vec3(0.3, 0.1, 0);
        var before = c.Project(p);
        c.Pan(25, -10);
        var after = c.Project(p);
        Assert.Equal(before.X + 25, after.X, 6);
        Assert.Equal(before.Y - 10, after.Y, 6);
    }

    [Fact]
    public void Fit_frames_a_box_and_the_grid_adapts_to_zoom()
    {
        var c = Cam(ViewKind.Top);
        c.Fit(new Box3(new Vec3(-10, -1, -5), new Vec3(10, 1, 5)));
        var a = c.Project(new Vec3(-10, 0, -5));
        var b = c.Project(new Vec3(10, 0, 5));
        Assert.InRange(a.X, 0, 400);
        Assert.InRange(b.X, 0, 400);
        Assert.True(b.X - a.X > 300);
        double coarse = c.GridStep();
        c.Zoom(10, 200, 150);
        Assert.True(c.GridStep() < coarse);
        foreach (double step in new[] { coarse, c.GridStep() })
        {
            double m = step / Math.Pow(10, Math.Floor(Math.Log10(step) + 1e-9));
            Assert.Contains(Math.Round(m, 6), new[] { 1.0, 2.0, 5.0 });
        }
        Assert.Equal("500 mm", ViewCamera.FormatLength(0.5));
        Assert.Equal("2 m", ViewCamera.FormatLength(2));
        Assert.Equal("1.5 km", ViewCamera.FormatLength(1500));
    }

    [Fact]
    public void The_gl_matrix_lands_every_point_where_the_software_projection_does()
    {
        var rng = new Random(7);
        foreach (var kind in Enum.GetValues<ViewKind>())
        {
            var c = Cam(kind);
            c.Center = new Vec3(0.5, -0.25, 1);
            c.Heading = 30;
            c.Pitch = 20;
            var m = GlRenderer.ViewProjection(c);
            for (int i = 0; i < 20; i++)
            {
                var p = new Vec3(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);
                var s = c.Project(p);
                if (!s.Visible) continue;
                var (xyz, w) = m.TransformHomogeneous(p);
                double nx = xyz.X / w, ny = xyz.Y / w;
                Assert.Equal(s.X, (nx + 1) / 2 * c.Width, 6);
                Assert.Equal(s.Y, (1 - ny) / 2 * c.Height, 6);
                Assert.InRange(xyz.Z / w, -1, 1);
            }
        }
    }
}

public class SoftwareRendererTests
{
    private static Mesh Square(double z = 0)
    {
        var m = new Mesh();
        // Clockwise as seen from the front: facing the viewer in the Front view.
        m.AddFace(0, new Vec3(-1, -1, z), new Vec3(-1, 1, z), new Vec3(1, 1, z), new Vec3(1, -1, z));
        return m;
    }

    private static (SoftwareRenderer R, ViewCamera C) Render(ViewScene scene, ViewKind kind = ViewKind.Front)
    {
        var c = new ViewCamera(kind);
        c.Resize(200, 200);
        c.PixelsPerUnit = 50;
        if (kind == ViewKind.Perspective) { c.Heading = 0; c.Pitch = 0; c.Distance = 5; }
        var r = new SoftwareRenderer();
        r.Render(scene, c);
        return (r, c);
    }

    private static uint At(SoftwareRenderer r, int x, int y) => r.Pixels[y * r.Width + x];

    [Fact]
    public void A_wireframe_square_draws_its_edges_on_the_grey_ground()
    {
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = Square() }], ShowGrid = false };
        var (r, _) = Render(scene);
        Assert.Equal(Palette.Background, At(r, 5, 5));
        Assert.Equal(Palette.Background, At(r, 100, 100)); // inside the square: not filled
        Assert.True(Dark(At(r, 50, 100)), "the left edge (x = -1 → 50 px)");
        Assert.True(Dark(At(r, 100, 50)), "the top edge");
    }

    private static bool Dark(uint c) => ((c >> 16) & 0xFF) < 70 && ((c >> 8) & 0xFF) < 70 && (c & 0xFF) < 70;

    [Fact]
    public void The_shaded_perspective_fills_faces_with_their_surface_colour()
    {
        var surfaces = new List<Surface> { new("Red") { Color = new Rgb(255, 0, 0) } };
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = Square() }], Surfaces = surfaces, Style = DisplayStyle.Shaded, ShowGrid = false };
        var (r, _) = Render(scene, ViewKind.Perspective);
        uint mid = At(r, 100, 100);
        Assert.True(((mid >> 16) & 0xFF) > 120 && ((mid >> 8) & 0xFF) < 40, $"{mid:X8}");
        Assert.Equal(Palette.PerspectiveBackground, At(r, 2, 2));
    }

    [Fact]
    public void Nearer_faces_hide_farther_ones()
    {
        var mesh = Square(1);
        var near = Square(-1);
        near.Polygons[0].Surface = 1;
        mesh.Append(near);
        var surfaces = new List<Surface> { new("Blue") { Color = new Rgb(0, 0, 255) }, new("Green") { Color = new Rgb(0, 255, 0) } };
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = mesh }], Surfaces = surfaces, Style = DisplayStyle.Shaded, ShowGrid = false };
        var (r, _) = Render(scene, ViewKind.Perspective);
        uint mid = At(r, 100, 100);
        Assert.True(((mid >> 8) & 0xFF) > 100 && (mid & 0xFF) < 60, $"{mid:X8}");
    }

    [Fact]
    public void Smoothing_shades_a_faceted_ball_smoothly()
    {
        // An octahedron-ish ball of eight flat faces: flat it shows eight shades, smoothed many more.
        var m = new Mesh();
        Vec3[] v = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
        foreach (var p in v) m.AddPoint(p);
        int[][] faces = [[0, 2, 4], [2, 1, 4], [1, 3, 4], [3, 0, 4], [2, 0, 5], [1, 2, 5], [3, 1, 5], [0, 3, 5]];
        foreach (var f in faces)
        {
            int i = m.AddPolygon(f);
            if (Vec3.Dot(m.Normal(i), m.Centroid(m.Polygons[i])) < 0) m.Flip(i);
        }
        int Shades(bool smooth)
        {
            var surface = new Surface("Ball") { Color = Rgb.White, Smoothing = smooth, SmoothingAngle = 120 };
            var scene = new ViewScene { Layers = [new ViewLayer { Mesh = m }], Surfaces = [surface], Style = DisplayStyle.Shaded, ShowGrid = false };
            var c = new ViewCamera(ViewKind.Perspective) { Heading = 20, Pitch = 15, Distance = 4 };
            c.Resize(160, 160);
            var r = new SoftwareRenderer();
            r.Render(scene, c);
            return r.Pixels.Where(px => px != Palette.PerspectiveBackground).Distinct().Count();
        }
        int flat = Shades(false), smooth = Shades(true);
        Assert.True(flat <= 8, $"flat {flat}");
        Assert.True(smooth > 100, $"smooth {smooth}");
    }

    [Fact]
    public void A_face_seen_from_behind_is_tinted()
    {
        var m = Square();
        m.Flip(0);
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = m }], Surfaces = [new Surface("White") { Color = Rgb.White }], Style = DisplayStyle.Shaded, ShowGrid = false };
        var (r, _) = Render(scene, ViewKind.Perspective);
        uint mid = At(r, 100, 100);
        Assert.True((mid & 0xFF) > ((mid >> 16) & 0xFF) + 20, $"{mid:X8}");
    }

    [Fact]
    public void Selected_points_and_polygons_show_in_yellow()
    {
        var scene = new ViewScene
        {
            Layers = [new ViewLayer { Mesh = Square(), SelectedPoints = new HashSet<int> { 0 }, SelectedPolygons = new HashSet<int> { 0 } }],
            ShowGrid = false,
        };
        var (r, _) = Render(scene);
        uint corner = At(r, 50, 150);
        Assert.True(((corner >> 16) & 0xFF) > 200 && ((corner >> 8) & 0xFF) > 180 && (corner & 0xFF) < 150, $"{corner:X8}");
    }

    [Fact]
    public void The_grid_and_off_screen_geometry_and_tiny_views_are_safe()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(-1e7, 0, 0), new Vec3(1e7, 1, 0));
        m.AddFace(0, new Vec3(0, 0, -100), new Vec3(0, 0, 100), new Vec3(1, 0, 100));
        var scene = new ViewScene { Layers = [new ViewLayer { Mesh = m }], Style = DisplayStyle.ShadedWireframe };
        foreach (var kind in Enum.GetValues<ViewKind>())
        {
            var c = new ViewCamera(kind);
            foreach (var (w, h) in new[] { (1, 1), (3, 17), (320, 240) })
            {
                c.Resize(w, h);
                new SoftwareRenderer().Render(scene, c);
            }
        }
    }
}

public class PickingTests
{
    private static ViewCamera Front()
    {
        var c = new ViewCamera(ViewKind.Front);
        c.Resize(200, 200);
        c.PixelsPerUnit = 50;
        return c;
    }

    private static Mesh TwoSquares()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(-1, -1, 1), new Vec3(-1, 1, 1), new Vec3(1, 1, 1), new Vec3(1, -1, 1));
        m.AddFace(0, new Vec3(-0.5, -0.5, -1), new Vec3(-0.5, 0.5, -1), new Vec3(0.5, 0.5, -1), new Vec3(0.5, -0.5, -1));
        m.AddFace(0, new Vec3(1.5, 1.5, 0), new Vec3(1.9, 1.5, 0));
        return m;
    }

    [Fact]
    public void The_front_most_polygon_under_the_mouse_is_picked()
    {
        var layers = new List<(int, Mesh)> { (0, TwoSquares()) };
        var c = Front();
        Assert.Equal(1, Picking.PolygonAt(c, layers, 100, 100)!.Value.Index);
        Assert.Equal(0, Picking.PolygonAt(c, layers, 60, 100)!.Value.Index);
        Assert.Equal(2, Picking.PolygonAt(c, layers, 185, 26)!.Value.Index); // near the line
        Assert.Null(Picking.PolygonAt(c, layers, 2, 198));
    }

    [Fact]
    public void Points_and_edges_are_picked_within_a_radius()
    {
        var layers = new List<(int, Mesh)> { (3, TwoSquares()) };
        var c = Front();
        var hit = Picking.NearestPoint(c, layers, 52, 148);
        Assert.Equal(new Picking.Hit(3, 0), hit);
        Assert.Null(Picking.NearestPoint(c, layers, 30, 30, 5));
        var e = Picking.NearestEdge(c, layers, 51, 100)!.Value;
        Assert.Equal(Edge.Of(0, 1), e.Edge);
    }

    [Fact]
    public void Boxes_and_lassos_select_what_falls_inside()
    {
        var m = TwoSquares();
        var c = Front();
        var box = Picking.Box(70, 70, 130, 130);
        Assert.Equal([4, 5, 6, 7], Picking.PointsInside(c, m, box).Order());
        Assert.Equal([0, 1], Picking.PolygonsInside(c, m, box).Order());
        Assert.Equal(4, Picking.EdgesInside(c, m, box).Count());
        var lasso = new (double, double)[] { (40, 40), (100, 40), (40, 100) };
        Assert.Equal([1], Picking.PointsInside(c, m, lasso));
    }

    [Fact]
    public void A_volume_dragged_in_two_views_builds_a_box()
    {
        var front = Front();
        var top = new ViewCamera(ViewKind.Top);
        top.Resize(200, 200);
        top.PixelsPerUnit = 50;
        var v = Picking.VolumeFromDrag(front, 50, 50, 150, 150, Box3.Empty);
        Assert.Equal(-1, v.Min.X, 9);
        Assert.Equal(1, v.Max.Y, 9);
        Assert.True(v.Min.Z < -1e8);
        v = Picking.VolumeFromDrag(top, 100, 50, 150, 100, v);
        Assert.Equal(0, v.Min.X, 9);
        Assert.Equal(1, v.Max.X, 9);
        Assert.Equal(0, v.Min.Z, 9);
        Assert.Equal(1, v.Max.Z, 9);
        Assert.Equal(-1, v.Min.Y, 9);
    }
}

public class ViewSceneTests
{
    [Fact]
    public void A_document_becomes_what_the_views_draw()
    {
        var d = new Document();
        d.Layers[0].Mesh.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0));
        d.Layers[1].Mesh.AddFace(0, new Vec3(0, 0, 1), new Vec3(0, 1, 1), new Vec3(1, 1, 1));
        d.Layers[2].Mesh.AddFace(0, new Vec3(0, 0, 2), new Vec3(0, 1, 2), new Vec3(1, 1, 2));
        d.SetLayers([0, 3], [1]);
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).Add(0);
        var scene = ViewScene.FromDocument(d, DisplayStyle.Shaded, backfaceTint: false, smoothed: l => l == 0 ? d.Layers[0].Mesh : null);
        // Layer 2 is off and layer 3 empty, so neither is drawn; layer 1 is drawn as background.
        Assert.Equal(2, scene.Layers.Count);
        Assert.Same(d.Layers[0].Mesh, scene.Layers[0].Mesh);
        Assert.False(scene.Layers[0].Background);
        Assert.Equal([0], scene.Layers[0].SelectedPolygons!);
        Assert.Null(scene.Layers[0].SelectedPoints);
        Assert.NotNull(scene.Layers[0].Subdivided);
        Assert.True(scene.Layers[1].Background);
        Assert.Null(scene.Layers[1].SelectedPolygons);
        Assert.Equal(DisplayStyle.Shaded, scene.Style);
        Assert.False(scene.BackfaceTint);
        Assert.False(scene.ShowPoints);
        Assert.Same(d.Surfaces, scene.Surfaces);
    }
}
