using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>The mesh check's tools (Select Problems, Remove Degenerate) and its macro command.</summary>
public class CheckToolTests
{
    [Fact]
    public void The_box_tool_makes_a_printable_box_with_a_positive_volume()
    {
        var (d, c) = Make.Empty();
        new BoxTool { Size = new Vec3(2, 3, 4) }.Run(c);
        var r = MeshCheck.Document(d);
        Assert.True(r.Printable);
        Assert.Equal(24, r.Volume!.Value, 9);
    }

    [Fact]
    public void Select_problems_selects_the_rim_of_a_hole_as_one_undo_step()
    {
        var (d, c) = Make.WithCube();
        d.Current.Mesh.RemovePolygons([0]);
        d.Modified = false;
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).Add(1);
        int steps = d.Undo.Count;
        var tool = new SelectProblemsTool { Problem = MeshProblem.BoundaryEdges };
        tool.Run(c);
        Assert.Equal(4, tool.Selected);
        Assert.Equal(SelectionMode.Edges, d.Selection.Mode);
        Assert.Equal(4, d.Selection.EdgesIn(0).Count);
        Assert.Equal(steps + 1, d.Undo.Count);
        Assert.Equal("Select Problems", d.Undo.UndoLabel);
        Assert.False(d.Modified); // only the selection changed
        Assert.Equal(5, d.Current.Mesh.Polygons.Count);
        d.UndoLast();
        Assert.Equal(SelectionMode.Polygons, d.Selection.Mode);
        Assert.Equal([1], d.Selection.PolygonsIn(0));
        Assert.Empty(d.Selection.EdgesIn(0));
    }

    [Fact]
    public void Select_problems_picks_each_kind_in_its_own_mode()
    {
        var (d, c) = Make.WithCube();
        var m = d.Current.Mesh;
        m.Flip(2);
        m.AddPoint(m.Points[0]);                    // a duplicate, unused
        m.AddPolygon([0, 1, 1]);                    // degenerate
        m.AddPolygon([3, 5]);                       // a line
        void Expect(MeshProblem p, SelectionMode mode, int count)
        {
            var t = new SelectProblemsTool { Problem = p };
            t.Run(c);
            Assert.Equal(mode, d.Selection.Mode);
            Assert.Equal(count, t.Selected);
        }
        Expect(MeshProblem.FlippedPolygons, SelectionMode.Polygons, 1);
        Assert.Equal([2], d.Selection.PolygonsIn(0));
        Expect(MeshProblem.DegeneratePolygons, SelectionMode.Polygons, 1);
        Assert.Equal([6], d.Selection.PolygonsIn(0));
        Expect(MeshProblem.PointsAndLines, SelectionMode.Polygons, 1);
        Expect(MeshProblem.DuplicatePoints, SelectionMode.Points, 2);
        Expect(MeshProblem.UnusedPoints, SelectionMode.Points, 1);
        Assert.Equal([8], d.Selection.PointsIn(0));
        Expect(MeshProblem.NonManifoldEdges, SelectionMode.Edges, 0);
        Expect(MeshProblem.NonPlanarPolygons, SelectionMode.Polygons, 0);
    }

    [Fact]
    public void Align_fixes_what_the_check_calls_flipped()
    {
        var (d, c) = Make.WithCube();
        for (int i = 0; i < 6; i++) d.Current.Mesh.Flip(i);
        Assert.Equal(1, MeshCheck.Document(d).InwardShells);
        new SelectProblemsTool { Problem = MeshProblem.FlippedPolygons }.Run(c);
        Assert.Equal(6, d.Selection.PolygonsIn(0).Count);
        new AlignTool().Run(c);
        var r = MeshCheck.Document(d);
        Assert.True(r.Printable);
        Assert.Equal(1, r.Volume!.Value, 9);
    }

    [Fact]
    public void Remove_degenerate_is_one_undo_step()
    {
        var (d, c) = Make.WithCube();
        var m = d.Current.Mesh;
        m.AddPolygon([0, 1, 1]);
        m.AddPoint(new Vec3(9, 9, 9));
        Assert.Equal(1, MeshCheck.Document(d).DegeneratePolygons);
        var tool = new RemoveDegenerateTool();
        tool.Run(c);
        Assert.Equal(1, tool.RemovedPolygons);
        Assert.Equal(1, tool.RemovedPoints);
        var r = MeshCheck.Document(d);
        Assert.Equal(0, r.DegeneratePolygons + r.UnusedPoints);
        Assert.Equal((8, 6), (d.Current.Mesh.Points.Count, d.Current.Mesh.Polygons.Count));
        d.UndoLast();
        Assert.Equal((9, 7), (d.Current.Mesh.Points.Count, d.Current.Mesh.Polygons.Count));
        Assert.Equal(1, MeshCheck.Document(d).DegeneratePolygons);
    }

    [Fact]
    public void Remove_degenerate_works_on_the_selection_only_when_there_is_one()
    {
        var (d, c) = Make.WithCube();
        var m = d.Current.Mesh;
        m.AddPolygon([0, 1, 1]);
        m.AddPolygon([2, 3, 3]);
        d.Selection.Mode = SelectionMode.Polygons;
        d.Selection.PolygonsIn(0).Add(6);
        new RemoveDegenerateTool { UnusedPoints = false }.Run(c);
        Assert.Equal(7, d.Current.Mesh.Polygons.Count);
        Assert.Equal(1, MeshCheck.Document(d).DegeneratePolygons);
    }

    [Fact]
    public void Run_by_name_like_an_agent_or_a_macro()
    {
        var (d, _) = Make.WithCube();
        d.Current.Mesh.RemovePolygons([3]);
        var cmd = new Commands(d);
        var t = (SelectProblemsTool)cmd.Run("Select Problems", new Dictionary<string, object?> { ["Problem"] = "boundaryedges" });
        Assert.Equal(4, t.Selected);
        Assert.Throws<ArgumentException>(() => cmd.Run("Select Problems", new Dictionary<string, object?> { ["Problem"] = "holes" }));
    }

    [Fact]
    public void The_checkmesh_macro_returns_the_report_as_a_table()
    {
        var (d, _) = Make.WithCube();
        var cmd = new Commands(d);
        Macros.Run("""
            local r = checkmesh()
            print(r.verdict)
            print(string.format("%.3f %.3f %d %d", r.volume, r.area, r.shells, r.holes))
            print(r.size[1], r.center[2], tostring(r.printable))
            tool("Select Problems", { Problem = "BoundaryEdges" })
            selmode("polygons"); selpolygons({1}); tool("Remove", {})
            local open = checkmesh()
            print(open.verdict, open.boundary_edges, tostring(open.volume), #open.report)
            """, cmd);
        Assert.Equal("Closed and printable (1 shell)", cmd.Output[0]);
        Assert.Equal("1.000 6.000 1 0", cmd.Output[1]);
        Assert.Equal("1\t0\ttrue", cmd.Output[2]);
        Assert.StartsWith("Open: 1 hole along 4 edges\t4\tnil\t", cmd.Output[3]);
        Assert.Contains(Macros.Reference, r => r.Signature == "checkmesh()");
    }
}
