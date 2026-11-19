using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>SubPatch cages, creases (whole, semi-sharp and none) and freezing, with their undo.</summary>
public class SubPatchToolTests
{
    private static Box3 SurfaceBounds(Mesh m, int levels) => SubPatches.Surface(m, levels).Bounds();

    [Fact]
    public void A_cage_rounds_off_and_hard_creases_keep_it_a_box()
    {
        var (d, c) = Make.WithCube();
        var mesh = d.Current.Mesh;
        Assert.Same(mesh, SubPatches.Surface(mesh, 2));     // not a SubPatch: the mesh itself
        new SubPatchTool().Run(c);
        Assert.True(d.Current.Mesh.SubPatch);
        var round = SurfaceBounds(d.Current.Mesh, 3);
        Assert.True(round.Max.X < 0.5 - 0.05, $"a smooth cube shrinks to a rounded blob ({round.Max.X})");
        Assert.Equal(6 * 64, SubPatches.Surface(d.Current.Mesh, 3).Polygons.Count);

        // Every edge hard: the cube stays a cube however far it's subdivided.
        d.Selection.Mode = SelectionMode.Edges;
        foreach (var e in d.Current.Mesh.EdgeMap().Keys) d.Selection.EdgesIn(0).Add(e);
        new SetCreaseTool { Crease = 1 }.Run(c);
        var hard = SurfaceBounds(d.Current.Mesh, 3);
        Assert.Equal(0.5, hard.Max.X, 9);
        Assert.Equal(-0.5, hard.Min.Y, 9);

        // Half sharp: a corner (the original points keep their indices) comes in, but less than with no creases.
        double Corner(Mesh m) => SubPatches.Surface(m, 3).Points[0].Length;
        double sharpCorner = Corner(d.Current.Mesh);
        Assert.Equal(Math.Sqrt(0.75), sharpCorner, 9);
        new SetCreaseTool { Crease = 0.5 }.Run(c);
        double halfCorner = Corner(d.Current.Mesh);
        var smoothCube = d.Current.Mesh.Clone();
        smoothCube.RemovePointMap(SubPatches.CreaseMap);
        double roundCorner = Corner(smoothCube);
        Assert.InRange(halfCorner, roundCorner + 1e-3, sharpCorner - 1e-4);
    }

    [Fact]
    public void Undo_takes_SubPatch_and_creases_back_and_freeze_bakes_the_surface()
    {
        var (d, c) = Make.WithCube();
        new SubPatchTool().Run(c);
        d.Selection.Mode = SelectionMode.Edges;
        var e = d.Current.Mesh.EdgeMap().Keys.First();
        d.Selection.EdgesIn(0).Add(e);
        new SetCreaseTool().Run(c);
        Assert.Equal(2, d.Current.Mesh.FindPointMap(SubPatches.CreaseMap)!.Values.Count);
        d.UndoLast();
        Assert.Null(d.Current.Mesh.FindPointMap(SubPatches.CreaseMap));
        Assert.True(d.Current.Mesh.SubPatch);
        d.UndoLast();
        Assert.False(d.Current.Mesh.SubPatch);
        d.RedoLast();
        Assert.True(d.Current.Mesh.SubPatch);

        new FreezeSubPatchTool { Level = 2 }.Run(c);
        Assert.False(d.Current.Mesh.SubPatch);
        Assert.Equal(96, d.Current.Mesh.Polygons.Count);
        var check = MeshCheck.Mesh(d.Current.Mesh);
        Assert.True(check.Watertight);
        Assert.True(check.Volume > 0);
        Assert.Throws<InvalidOperationException>(() => new FreezeSubPatchTool().Run(c));
    }

    [Fact]
    public void Set_Crease_with_nothing_selected_says_what_to_select()
    {
        var (d, c) = Make.WithCube();
        var ex = Assert.Throws<InvalidOperationException>(() => new SetCreaseTool().Run(c));
        Assert.Contains("select", ex.Message);
        Assert.Null(d.Current.Mesh.FindPointMap(SubPatches.CreaseMap));
    }

    [Fact]
    public void Creases_follow_the_points_through_edits_that_renumber_them()
    {
        var (d, c) = Make.WithCube();
        new SubPatchTool().Run(c);
        d.Selection.Mode = SelectionMode.Points;
        d.Selection.SelectPoint(0, 7);
        d.Selection.SelectPoint(0, 6);
        new SetCreaseTool().Run(c);
        var p7 = d.Current.Mesh.Points[7];
        var mesh = d.Current.Mesh;
        mesh.RemovePoints([0]);
        var map = mesh.FindPointMap(SubPatches.CreaseMap)!;
        int moved = mesh.Points.FindIndex(p => p == p7);
        Assert.Equal(1, map.Weight(moved), 6);
    }
}

/// <summary>Quad Remesh as a tool: one undo step, the selection's border kept stitched.</summary>
public class QuadRemeshToolTests
{
    [Fact]
    public void A_ball_is_remeshed_into_quads_in_one_undo_step()
    {
        var (d, c) = Make.Empty();
        new BallTool().Run(c);
        int before = d.Current.Mesh.Polygons.Count;
        var tool = new QuadRemeshTool { EdgeLength = 0.08 };
        tool.Run(c);
        Assert.All(d.Current.Mesh.Polygons, p => Assert.Equal(4, p.Count));
        Assert.True(MeshCheck.Mesh(d.Current.Mesh).Solid);
        Assert.Single(tool.Results);
        d.UndoLast();
        Assert.Equal(before, d.Current.Mesh.Polygons.Count);
        Assert.Throws<ArgumentException>(() => new QuadRemeshTool { EdgeLength = 0 }.Run(c));
    }
}
