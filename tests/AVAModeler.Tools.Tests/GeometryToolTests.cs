using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

public class GeometryToolTests
{
    private static void SelectPolygons(Document d, Func<Mesh, int, bool> which)
    {
        d.Selection.Mode = SelectionMode.Polygons;
        var m = d.Current.Mesh;
        d.Selection.PolygonsIn(0).UnionWith(Enumerable.Range(0, m.Polygons.Count).Where(i => which(m, i)));
    }

    [Fact]
    public void Triple_subdivide_metaform_and_merge()
    {
        var (d, c) = Make.WithCube();
        new TripleTool().Run(c);
        Assert.Equal(12, d.Current.Mesh.Polygons.Count);
        new MergePolygonsTool().Run(c);
        Assert.Equal(6, d.Current.Mesh.Polygons.Count);
        new SubdivideTool { Mode = SubdivideMode.Faceted }.Run(c);
        Assert.Equal(24, d.Current.Mesh.Polygons.Count);
        Assert.Equal(1, MeshMeasure.Volume(d.Current.Mesh), 9);
        var (d2, c2) = Make.WithCube();
        new MetaformTool().Run(c2);
        Assert.Equal(26, d2.Current.Mesh.Points.Count);
        Assert.True(MeshMeasure.Volume(d2.Current.Mesh) < 1);
    }

    [Fact]
    public void Align_and_unify_clean_up()
    {
        var (d, c) = Make.WithCube();
        d.Current.Mesh.Flip(0);
        d.Current.Mesh.Flip(3);
        new AlignTool().Run(c);
        Assert.True(MeshMeasure.IsWatertight(d.Current.Mesh));
        d.Current.Mesh.AddPolygon(d.Current.Mesh.Polygons[2].Points.AsEnumerable().Reverse());
        new UnifyTool().Run(c);
        Assert.Equal(6, d.Current.Mesh.Polygons.Count);
    }

    [Fact]
    public void Split_cuts_a_face_between_two_selected_points()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Size = new Vec3(2, 2, 0) }.Run(c);
        var m = d.Current.Mesh;
        d.Selection.SelectPoint(0, m.Polygons[0].Points[0]);
        d.Selection.SelectPoint(0, m.Polygons[0].Points[2]);
        new SplitTool().Run(c);
        Assert.Equal(2, d.Current.Mesh.Polygons.Count);
        Assert.All(d.Current.Mesh.Polygons, p => Assert.Equal(3, p.Count));
    }

    [Fact]
    public void Extrude_a_face_makes_a_longer_closed_box()
    {
        var (d, c) = Make.WithCube();
        SelectPolygons(d, (m, i) => m.Normal(i).Z < -0.9);
        new ExtrudeTool { Offset = new Vec3(0, 0, -1), Segments = 2 }.Run(c);
        var mesh = d.Current.Mesh;
        Assert.True(MeshMeasure.IsWatertight(mesh));
        Assert.Equal(2, MeshMeasure.Volume(mesh), 9);
        Assert.Equal(14, mesh.Polygons.Count);
        var drag = new ExtrudeTool();
        drag.Begin(c);
        drag.Drag(Make.FrontDrag(Vec3.Zero, new Vec3(0.3, 0.1, 0)) is var di ? new DragInfo { Start = di.Start, Current = di.Current, Constrain = true, Right = di.Right, Up = di.Up, Forward = di.Forward } : null!);
        Assert.Equal(new Vec3(0.3, 0, 0), drag.Offset);
        drag.Cancel();
    }

    [Fact]
    public void Lathe_a_profile_into_a_closed_solid_of_revolution()
    {
        var (d, c) = Make.Empty();
        // A rectangle in the XY plane, off the axis: lathed round Y it makes a ring.
        d.Current.Mesh.AddFace(0, new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(2, 1, 0), new Vec3(2, 0, 0));
        new LatheTool { Axis = Axis.Y, Sides = 64 }.Run(c);
        var m = d.Current.Mesh;
        Assert.True(MeshMeasure.IsWatertight(m));
        double exact = Math.PI * (4 - 1) * 1;
        Assert.Equal(exact, MeshMeasure.Volume(m), 1);
    }

    [Fact]
    public void Mirror_clone_and_array_copy_the_selection()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Center = new Vec3(1, 0, 0) }.Run(c);
        new MirrorTool { Axis = Axis.X, Center = 0 }.Run(c);
        Assert.Equal(12, d.Current.Mesh.Polygons.Count);
        Assert.Equal(-1.5, d.Current.Mesh.Bounds().Min.X, 9);
        Assert.Equal(2, MeshMeasure.Volume(d.Current.Mesh), 9);

        var (d2, c2) = Make.WithCube();
        new CloneTool { Copies = 3, Offset = new Vec3(2, 0, 0) }.Run(c2);
        Assert.Equal(24, d2.Current.Mesh.Polygons.Count);
        Assert.Equal(6.5, d2.Current.Mesh.Bounds().Max.X, 9);

        var (d3, c3) = Make.WithCube();
        new ArrayTool { CountX = 2, CountY = 2, CountZ = 2, Spacing = new Vec3(2, 2, 2) }.Run(c3);
        Assert.Equal(48, d3.Current.Mesh.Polygons.Count);
        var (d4, c4) = Make.Empty();
        new BoxTool { Center = new Vec3(3, 0, 0) }.Run(c4);
        new ArrayTool { Mode = ArrayTool.ArrayMode.Radial, RadialCount = 4, Axis = Axis.Y }.Run(c4);
        Assert.Equal(24, d4.Current.Mesh.Polygons.Count);
        Assert.Equal(-3.5, d4.Current.Mesh.Bounds().Min.Z, 9);
    }

    [Fact]
    public void Bevel_and_smooth_shift_grow_the_box()
    {
        var (d, c) = Make.WithCube();
        SelectPolygons(d, (m, i) => m.Normal(i).Y > 0.9);
        new BevelTool { Inset = 0.1, Shift = 0.2 }.Run(c);
        Assert.Equal(10, d.Current.Mesh.Polygons.Count);
        Assert.Equal(0.7, d.Current.Mesh.Bounds().Max.Y, 9);
        Assert.True(MeshMeasure.IsWatertight(d.Current.Mesh));
        var (d2, c2) = Make.WithCube();
        new SmoothShiftTool { Offset = 0.1 }.Run(c2);
        Assert.True(MeshMeasure.Volume(d2.Current.Mesh) > 1.5);
    }

    [Fact]
    public void Path_and_rail_extrude_follow_the_background()
    {
        var (d, c) = Make.Empty();
        // The path, in layer 2: an L of lines.
        var path = d.Layers[1].Mesh;
        int a = path.AddPoint(Vec3.Zero), b = path.AddPoint(new Vec3(0, 2, 0)), e = path.AddPoint(new Vec3(2, 2, 0));
        path.AddPolygon([a, b]);
        path.AddPolygon([b, e]);
        // The profile in layer 1: a small square across the start of the path.
        d.Current.Mesh.AddFace(0, new Vec3(-0.1, 0, -0.1), new Vec3(-0.1, 0, 0.1), new Vec3(0.1, 0, 0.1), new Vec3(0.1, 0, -0.1));
        d.SetLayers([0], [1]);
        Assert.Equal(3, BackgroundPath.Of(d).Count);
        new PathExtrudeTool().Run(c);
        Assert.True(d.Current.Mesh.Bounds().Max.X > 1.8);
        var (d2, c2) = Make.Empty();
        d2.Layers[1].Mesh = path.Clone();
        d2.Current.Mesh.AddFace(0, new Vec3(-0.1, 0, -0.1), new Vec3(-0.1, 0, 0.1), new Vec3(0.1, 0, 0.1), new Vec3(0.1, 0, -0.1));
        d2.SetLayers([0], [1]);
        new RailExtrudeTool { Segments = 8 }.Run(c2);
        Assert.Equal(1 + 8 * 4, d2.Current.Mesh.Polygons.Count);
        var (d3, c3) = Make.Empty();
        Assert.False(new Commands(d3).Registry.Create("Path Extrude") is var t && SafeRun(t, c3));
    }

    private static bool SafeRun(Tool t, ToolContext c)
    {
        try { t.Run(c); return true; }
        catch (InvalidOperationException) { t.Cancel(); return false; }
    }

    [Fact]
    public void Booleans_and_drills_work_against_the_background()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Size = new Vec3(2, 2, 2) }.Run(c);
        d.SetLayers([1]);
        new BoxTool { Size = new Vec3(1, 1, 4) }.Run(c);
        d.SetLayers([0], [1]);
        new BooleanTool { Operation = BooleanOperation.Subtract, CutSurface = "Inside" }.Run(c);
        var m = d.Layers[0].Mesh;
        Assert.Equal(8 - 2, MeshMeasure.Volume(m), 6);
        Assert.True(MeshMeasure.IsWatertight(m));
        Assert.Contains(m.Polygons, p => d.Surfaces[p.Surface].Name == "Inside");
        d.UndoLast();
        new BooleanTool { Operation = BooleanOperation.Union }.Run(c);
        Assert.Equal(8 + 2, MeshMeasure.Volume(d.Layers[0].Mesh), 6); // the tall box sticks out a unit cube each side

        var (d2, c2) = Make.Empty();
        new BoxTool { Size = new Vec3(4, 4, 0), SegmentsX = 2, SegmentsY = 2 }.Run(c2);
        d2.SetLayers([1]);
        new DiscTool { Radius = new Vec3(1, 1, 1), Height = 0, Axis = Axis.Z, Sides = 32 }.Run(c2);
        d2.SetLayers([0], [1]);
        new DrillTool { Operation = DrillOperation.Stencil, Axis = Axis.Z }.Run(c2);
        var stencilled = d2.Layers[0].Mesh;
        Assert.Contains(stencilled.Polygons, p => d2.Surfaces[p.Surface].Name == "Stencil");
        Assert.Equal(16, MeshMeasure.Area(stencilled), 6);
        d2.UndoLast();
        new DrillTool { Operation = DrillOperation.Tunnel, Axis = Axis.Z }.Run(c2);
        Assert.Equal(16 - Math.PI, MeshMeasure.Area(d2.Layers[0].Mesh), 1);

        var (d3, c3) = Make.WithCube();
        Assert.Throws<InvalidOperationException>(() => new BooleanTool().Run(c3));
    }

    [Fact]
    public void The_spaceship_milestone_builds_a_closed_symmetrical_ship()
    {
        var cmd = new Commands(new Document());
        Macros.Run(File.ReadAllText(Sample("spaceship.lua")), cmd);
        var m = cmd.Document.Current.Mesh;
        var b = m.Bounds();
        Assert.Equal(-b.Min.X, b.Max.X, 6);       // symmetrical
        Assert.True(b.Size.X > 3);                // wings
        Assert.True(b.Size.Z > 3);
        Assert.Contains(cmd.Document.Surfaces, s => s.Name == "Engine");
        // The hull itself (the faces on the default surface) is one closed piece.
        var hull = new Mesh { Points = m.Points };
        hull.Polygons.AddRange(m.Polygons.Where(p => p.Surface == 0).Select(p => p.Clone()));
        Assert.Empty(MeshMeasure.OpenEdges(hull));
        Assert.StartsWith("Spaceship:", cmd.Output.Single());
    }

    [Fact]
    public void The_logo_milestone_makes_solid_bevelled_type()
    {
        var cmd = new Commands(new Document());
        Macros.Run(File.ReadAllText(Sample("logo.lua")), cmd);
        var m = cmd.Document.Current.Mesh;
        Assert.True(m.Polygons.Count > 30);
        Assert.True(m.Bounds().Size.Z > 0.25);
        Assert.Contains(cmd.Document.Surfaces, s => s.Name == "Gold");
    }

    private static string Sample(string name) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "scripts", name);
}
