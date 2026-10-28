using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

public class UVToolTests
{
    /// <summary>The front view's planar UVs for a unit image per metre centred on the origin: u = x + ½, v = y + ½.</summary>
    private static Vec2 FrontUV(Vec3 p) => new(p.X + 0.5, p.Y + 0.5);

    /// <summary>A cube (or what's in layer 1) given front-planar UVs, unfitted, so every point's UVs follow from where it is.</summary>
    private static (Document, ToolContext) Mapped(Action<ToolContext>? build = null)
    {
        var (d, c) = Make.Empty();
        (build ?? (x => new BoxTool().Run(x)))(c);
        new MakeUVsTool { Projection = UVProjection.Planar, Axis = Axis.Z, Fit = false, Center = Vec3.Zero, Size = Vec3.One }.Run(c);
        d.Selection.Clear(allModes: true);
        return (d, c);
    }

    /// <summary>No UVs out of range or broken, and every face mapped.</summary>
    private static void Sound(Mesh m, bool allMapped = true)
    {
        foreach (var map in m.UVMaps)
            foreach (var (i, uv) in map.Points)
            {
                Assert.InRange(i, 0, m.Points.Count - 1);
                Assert.True(double.IsFinite(uv.X) && double.IsFinite(uv.Y));
            }
        foreach (var p in m.Polygons)
        {
            foreach (var (_, corners) in p.CornerUVs ?? [])
                foreach (var (i, uv) in corners)
                {
                    Assert.InRange(i, 0, m.Points.Count - 1);
                    Assert.True(double.IsFinite(uv.X) && double.IsFinite(uv.Y));
                }
            if (allMapped && p.Count >= 3) Assert.True(m.IsMapped("UV", p), $"polygon {p} isn't mapped");
        }
    }

    /// <summary>Every corner's UVs are the front-planar ones of where its point is.</summary>
    private static void FollowsFront(Mesh m)
    {
        Sound(m);
        foreach (var p in m.Polygons)
            foreach (int i in p.Points)
            {
                Assert.True(m.TryGetUV("UV", p, i, out var uv));
                var want = FrontUV(m.Points[i]);
                Assert.True(Math.Abs(uv.X - want.X) < 1e-9 && Math.Abs(uv.Y - want.Y) < 1e-9, $"point {m.Points[i]} has {uv}, wanted {want}");
            }
    }

    private static void SelectPolygons(Document d, Func<Mesh, Polygon, bool> which)
    {
        d.Selection.Mode = SelectionMode.Polygons;
        var m = d.Current.Mesh;
        d.Selection.PolygonsIn(0).UnionWith(Enumerable.Range(0, m.Polygons.Count).Where(i => which(m, m.Polygons[i])));
    }

    [Fact]
    public void Make_uvs_lays_out_a_planar_map_fitted_to_the_selection()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Size = new Vec3(2, 1, 1), Center = new Vec3(1, 0, 0) }.Run(c);
        new MakeUVsTool { MapName = "Front" }.Run(c);
        var m = d.Current.Mesh;
        Assert.Equal("Front", d.CurrentUVMap);
        Assert.Equal("Make UVs", d.Undo.UndoLabel);
        foreach (var p in m.Polygons)
            foreach (int i in p.Points)
            {
                Assert.True(m.TryGetUV("Front", p, i, out var uv));
                Assert.Equal(m.Points[i].X / 2, uv.X, 9);
                Assert.Equal(m.Points[i].Y + 0.5, uv.Y, 9);
            }
        Assert.All(m.Polygons, p => Assert.Null(p.CornerUVs));
        d.UndoLast();
        Assert.Empty(d.Current.Mesh.UVMaps);
    }

    [Fact]
    public void A_cylindrical_layout_cuts_a_seam_where_it_wraps()
    {
        var (d, c) = Make.Empty();
        new DiscTool { Sides = 8, Caps = false, Axis = Axis.Y }.Run(c);
        new MakeUVsTool { Projection = UVProjection.Cylindrical, Axis = Axis.Y }.Run(c);
        var m = d.Current.Mesh;
        Sound(m);
        var seam = m.Polygons.Where(p => p.CornerUVs != null).ToList();
        Assert.Single(seam);
        foreach (var p in m.Polygons)
        {
            var us = m.CornerUVsOf("UV", p).Select(u => u!.Value.X).ToList();
            // No face is stretched back across the whole image.
            Assert.True(us.Max() - us.Min() < 0.2, string.Join(", ", us));
            Assert.All(m.CornerUVsOf("UV", p), u => Assert.InRange(u!.Value.Y, 0, 1));
        }
    }

    [Fact]
    public void A_spherical_layout_of_a_ball_stays_in_the_image_with_its_poles_over_their_columns()
    {
        var (d, c) = Make.Empty();
        new BallTool { Sides = 32, Segments = 16 }.Run(c);
        new MakeUVsTool { Projection = UVProjection.Spherical, Axis = Axis.Y }.Run(c);
        var m = d.Current.Mesh;
        Sound(m);
        // Every u is within the image: the seam column ends at exactly 1, its other side at 0.
        var all = m.Polygons.SelectMany(p => m.CornerUVsOf("UV", p)).Select(u => u!.Value).ToList();
        Assert.All(all, u => Assert.InRange(u.X, 0, 1));
        Assert.Contains(all, u => u.X == 1);
        Assert.Contains(all, u => u.X == 0);
        // Each pole's triangle spans the same u as the quad below it (they share that edge).
        int pole = 0;
        foreach (var tri in m.Polygons.Where(p => p.Count == 3))
        {
            int apexPoint = tri.Points.MaxBy(i => Math.Abs(m.Points[i].Y));
            var edge = tri.Points.Where(i => i != apexPoint).ToList();
            var quad = m.Polygons.Single(q => q.Count == 4 && edge.All(q.Points.Contains));
            double[] Us(Polygon p, IEnumerable<int> pts) => pts.Select(i => m.TryGetUV("UV", p, i, out var uv) ? uv.X : double.NaN).Order().ToArray();
            Assert.Equal(Us(quad, edge), Us(tri, edge));
            Assert.True(m.TryGetUV("UV", tri, apexPoint, out var top));
            Assert.Equal(Us(tri, edge).Average(), top.X, 9);
            pole++;
        }
        Assert.Equal(64, pole);
    }

    [Fact]
    public void An_atlas_gives_each_side_a_cell_of_its_own()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Size = new Vec3(3, 1, 2) }.Run(c);
        new MakeUVsTool { Projection = UVProjection.Atlas }.Run(c);
        var m = d.Current.Mesh;
        Sound(m);
        var cells = new HashSet<(int, int)>();
        foreach (var p in m.Polygons)
        {
            var uvs = m.CornerUVsOf("UV", p).Select(u => u!.Value).ToList();
            Assert.All(uvs, u => { Assert.InRange(u.X, 0, 1); Assert.InRange(u.Y, 0, 1); });
            var mid = uvs.Aggregate(Vec2.Zero, (s, u) => s + u) / uvs.Count;
            cells.Add(((int)(mid.X * 3), (int)(mid.Y * 2)));
            // Kept in proportion: the sides' shapes in UV match their shapes in space.
            var bounds = m.Bounds(p.Points);
            double du = uvs.Max(u => u.X) - uvs.Min(u => u.X), dv = uvs.Max(u => u.Y) - uvs.Min(u => u.Y);
            var sides = new[] { bounds.Size.X, bounds.Size.Y, bounds.Size.Z }.Where(x => x > 1e-9).OrderBy(x => x).ToList();
            Assert.Equal(sides[0] / sides[1], Math.Min(du, dv) / Math.Max(du, dv), 6);
        }
        Assert.Equal(6, cells.Count);
    }

    [Fact]
    public void Remapping_part_leaves_its_neighbours_uvs_as_they_were()
    {
        var (d, c) = Mapped();
        SelectPolygons(d, (m, p) => m.Normal(p).Y > 0.9);
        new MakeUVsTool { Projection = UVProjection.Planar, Axis = Axis.Y }.Run(c);
        var mesh = d.Current.Mesh;
        foreach (var p in mesh.Polygons)
        {
            bool top = mesh.Normal(p).Y > 0.9;
            foreach (int i in p.Points)
            {
                Assert.True(mesh.TryGetUV("UV", p, i, out var uv));
                var want = top ? new Vec2(mesh.Points[i].X + 0.5, mesh.Points[i].Z + 0.5) : FrontUV(mesh.Points[i]);
                Assert.Equal(want, uv);
            }
        }
    }

    [Fact]
    public void Tools_that_cut_faces_up_interpolate_their_uvs()
    {
        foreach (var tool in new Tool[] { new TripleTool(), new SubdivideTool { Mode = SubdivideMode.Faceted }, new MergePolygonsTool() })
        {
            var (d, c) = Mapped();
            if (tool is MergePolygonsTool) new TripleTool().Run(c);
            tool.Run(c);
            FollowsFront(d.Current.Mesh);
        }
        var (d2, c2) = Mapped();
        new MetaformTool().Run(c2);
        Sound(d2.Current.Mesh);
        var (d3, c3) = Mapped(x => new BoxTool { Size = new Vec3(2, 2, 0) }.Run(x));
        var m3 = d3.Current.Mesh;
        d3.Selection.SelectPoint(0, m3.Polygons[0].Points[0]);
        d3.Selection.SelectPoint(0, m3.Polygons[0].Points[2]);
        new SplitTool().Run(c3);
        Assert.Equal(2, d3.Current.Mesh.Polygons.Count);
        FollowsFront(d3.Current.Mesh);
    }

    [Fact]
    public void Tools_that_copy_carry_the_uvs_with_the_copies()
    {
        var (d, c) = Mapped();
        SelectPolygons(d, (m, p) => m.Normal(p).Z < -0.9);
        new ExtrudeTool { Offset = new Vec3(0, 0, -1), Segments = 2 }.Run(c);
        // Along Z, the front-planar UVs don't change: copies agree with where they are.
        FollowsFront(d.Current.Mesh);

        foreach (var make in new Func<Tool>[]
        {
            () => new CloneTool { Copies = 2, Offset = new Vec3(3, 0, 0) },
            () => new ArrayTool { CountX = 2, Spacing = new Vec3(3, 0, 0) },
            () => new MirrorTool { Axis = Axis.X, Center = 2 },
        })
        {
            var (d2, c2) = Mapped();
            int before = d2.Current.Mesh.Points.Count;
            make().Run(c2);
            var m = d2.Current.Mesh;
            Sound(m);
            Assert.True(m.Points.Count > before);
            // Every copy has the UVs of the point it was copied from.
            var uv = m.FindUVMap("UV")!;
            for (int i = before; i < m.Points.Count; i++)
            {
                var source = Enumerable.Range(0, before).Single(j => Math.Abs(m.Points[j].Y - m.Points[i].Y) < 1e-9 && Math.Abs(m.Points[j].Z - m.Points[i].Z) < 1e-9
                    && (Math.Abs(m.Points[j].X + 3 * Math.Round((m.Points[i].X - m.Points[j].X) / 3) - m.Points[i].X) < 1e-9 || Math.Abs(4 - m.Points[j].X - m.Points[i].X) < 1e-9));
                Assert.Equal(uv.Points[source], uv.Points[i]);
            }
        }
    }

    [Fact]
    public void Bevel_smooth_shift_lathe_and_merging_keep_the_uvs_sound()
    {
        var (d, c) = Mapped();
        SelectPolygons(d, (m, p) => m.Normal(p).Z < -0.9);
        new BevelTool { Inset = 0.1, Shift = 0.2 }.Run(c);
        Sound(d.Current.Mesh);
        // The bevelled face's corners have the UVs of the corners they came from.
        var face = d.Current.Mesh.Polygons.First(p => d.Current.Mesh.Normal(p).Z < -0.9 && d.Current.Mesh.Centroid(p).Z < -0.6);
        Assert.Equal(new[] { 0.0, 1.0 }, d.Current.Mesh.CornerUVsOf("UV", face).Select(u => u!.Value.X).Distinct().Order());

        var (d2, c2) = Mapped();
        new SmoothShiftTool { Offset = 0.2 }.Run(c2);
        Sound(d2.Current.Mesh);

        var (d3, c3) = Mapped();
        new MergePointsTool { Distance = 0.6 }.Run(c3);
        Sound(d3.Current.Mesh, allMapped: false);

        var (d4, c4) = Mapped(x =>
        {
            var m = x.Document.Current.Mesh;
            m.Points.AddRange([new Vec3(0.5, -0.5, 0), new Vec3(0.7, 0, 0), new Vec3(0.5, 0.5, 0)]);
            m.AddPolygon([0, 1]);
            m.AddPolygon([1, 2]);
        });
        new LatheTool { Axis = Axis.Y, Sides = 12 }.Run(c4);
        Sound(d4.Current.Mesh);
        Assert.True(d4.Current.Mesh.Polygons.Count >= 24);
    }

    [Fact]
    public void Cut_and_paste_take_the_uvs_between_layers()
    {
        var (d, c) = Mapped();
        d.Current.Mesh.SetCornerUV("UV", d.Current.Mesh.Polygons[0], d.Current.Mesh.Polygons[0].Points[0], new Vec2(5, 5));
        SelectPolygons(d, (m, p) => p == m.Polygons[0] || m.Normal(p).Y > 0.9);
        LayerOps.Cut(d);
        Assert.Equal(4, d.Current.Mesh.Polygons.Count);
        Sound(d.Current.Mesh);
        d.SetLayers([1]);
        LayerOps.Paste(d);
        var pasted = d.Layers[1].Mesh;
        Assert.Equal(2, pasted.Polygons.Count);
        Sound(pasted);
        Assert.Equal(new Vec2(5, 5), pasted.CornerUVsOf("UV", pasted.Polygons[0])[0]);
        Assert.Equal(FrontUV(pasted.Points[pasted.Polygons[1].Points[1]]), pasted.CornerUVsOf("UV", pasted.Polygons[1])[1]);
    }

    [Fact]
    public void Booleans_and_drills_take_the_uvs_of_the_faces_they_cut()
    {
        foreach (var op in new[] { BooleanOperation.Subtract, BooleanOperation.Union, BooleanOperation.Intersect })
        {
            var (d, c) = Mapped();
            d.SetLayers([1]);
            new BoxTool { Size = new Vec3(0.5, 0.5, 2), Center = new Vec3(0.3, 0.2, 0) }.Run(c);
            new MakeUVsTool { Fit = false, Center = Vec3.Zero, Size = Vec3.One }.Run(c);
            d.SetLayers([0], [1]);
            new BooleanTool { Operation = op }.Run(c);
            FollowsFront(d.Current.Mesh);
        }
        foreach (var op in Enum.GetValues<DrillOperation>())
        {
            var (d, c) = Mapped();
            d.SetLayers([1]);
            new DiscTool { Axis = Axis.Z, Radius = new Vec3(0.3, 0.3, 0.3), Sides = 12, Center = new Vec3(0.1, 0, 0) }.Run(c);
            d.SetLayers([0], [1]);
            new DrillTool { Operation = op, Axis = Axis.Z }.Run(c);
            FollowsFront(d.Current.Mesh);
        }
        // An operand with no UVs leaves its faces unmapped (the bottom of the pit it digs), and nothing breaks.
        var (d3, c3) = Mapped();
        d3.SetLayers([1]);
        new BoxTool { Size = new Vec3(0.5, 0.5, 1), Center = new Vec3(0, 0, -0.6) }.Run(c3);
        d3.SetLayers([0], [1]);
        new BooleanTool { Operation = BooleanOperation.Subtract }.Run(c3);
        Sound(d3.Current.Mesh, allMapped: false);
        Assert.Contains(d3.Current.Mesh.Polygons, p => !d3.Current.Mesh.IsMapped("UV", p));
    }

    [Fact]
    public void Undo_after_a_tool_puts_the_uvs_back_and_previews_start_from_the_originals()
    {
        var (d, c) = Mapped();
        var before = d.Current.Mesh.FindUVMap("UV")!.Points.ToDictionary();
        SelectPolygons(d, (m, p) => m.Normal(p).Z < -0.9);
        var bevel = new BevelTool { Inset = 0.1 };
        bevel.Begin(c);
        bevel.Update();
        bevel.Inset = 0.2;
        bevel.Update();
        Assert.Equal(before.Count + 4, d.Current.Mesh.FindUVMap("UV")!.Points.Count);
        bevel.Commit();
        d.UndoLast();
        Assert.Equal(before, d.Current.Mesh.FindUVMap("UV")!.Points);
    }

    [Fact]
    public void The_uv_space_carries_selections_and_moves_back_to_the_model()
    {
        var (d, c) = Make.Empty();
        new DiscTool { Sides = 8, Caps = false, Axis = Axis.Y }.Run(c);
        new MakeUVsTool { Projection = UVProjection.Cylindrical, Axis = Axis.Y }.Run(c);
        var model = d.Current.Mesh;
        var seamPoly = model.Polygons.Single(p => p.CornerUVs != null);
        int seamPoint = seamPoly.CornerUVs!["UV"].Keys.First();
        d.Selection.Mode = SelectionMode.Points;
        d.Selection.SelectPoint(0, seamPoint);
        var space = new UVSpace(d, "UV");
        var flat = space.Flat.Current.Mesh;
        Assert.Equal(model.Points.Count + seamPoly.CornerUVs["UV"].Count, flat.Points.Count);
        Assert.Equal(model.Polygons.Count, flat.Polygons.Count);
        // The seam point is selected on both sides of the seam.
        Assert.Equal(2, space.Flat.Selection.PointsIn(0).Count);
        var was = model.CornerUVsOf("UV", seamPoly);
        new MoveTool { Offset = new Vec3(0.1, 0.05, 0) }.Run(new ToolContext(space.Flat));
        Assert.True(space.WriteBack());
        var now = model.CornerUVsOf("UV", seamPoly);
        int at = seamPoly.Points.IndexOf(seamPoint);
        Assert.Equal(was[at]!.Value + new Vec2(0.1, 0.05), now[at]!.Value);
        var other = model.Polygons.First(p => p != seamPoly && p.Points.Contains(seamPoint));
        Assert.True(model.TryGetUV("UV", other, seamPoint, out var otherSide));
        Assert.NotEqual(now[at]!.Value.X, otherSide.X, 3);

        // Picking in the flat space selects in the model.
        space.Flat.Selection.Clear(allModes: true);
        space.Flat.Selection.Mode = SelectionMode.Polygons;
        space.Flat.Selection.PolygonsIn(0).Add(2);
        space.SelectionToModel();
        Assert.Equal(SelectionMode.Polygons, d.Selection.Mode);
        Assert.Equal([space.ModelPolygon(0, 2)], d.Selection.PolygonsIn(0));

        // A tool that makes geometry can't be written back as UVs.
        new BoxTool().Run(new ToolContext(space.Flat));
        Assert.False(space.SameShape());
        Assert.False(space.WriteBack());
    }
}
