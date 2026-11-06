using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>
/// Undo steps that merge (<c>LayoutSession.Edit(..., merge: true)</c>), for updates that arrive in a run (an object
/// synced live from AVA Modeler): one step for the run, but never across another step, an undo, a redo, a different
/// name or the saved scene; redo cleared and the modified flag right.
/// </summary>
public class MergedUndoTests
{
    private static string State(LayoutSession s) => SceneJson.Write(s.Scene, embedMeshes: true);

    private static (LayoutSession, NullItem) Session()
    {
        var s = new LayoutSession();
        var n = new NullItem("Pawn");
        s.Add(n);
        return (s, n);
    }

    private static void Sync(LayoutSession s, NullItem n, double x, string label = "Update Pawn") =>
        s.Edit(label, () => s.SetVector(n, 0, new Vec3(x, 0, 0)), merge: true);

    [Fact]
    public void A_run_of_merged_edits_is_one_step_and_one_undo_takes_it_back()
    {
        var (s, n) = Session();
        var before = State(s);
        int depth = s.History.Count;
        for (int i = 1; i <= 5; i++) Sync(s, n, i);
        var after = State(s);
        Assert.Equal(depth + 1, s.History.Count);
        Assert.Equal("Update Pawn", s.History.UndoLabel);
        Assert.Equal(5, n.PositionAt(s.Frame).X);
        Assert.Equal("Update Pawn", s.Undo());
        Assert.Equal(before, State(s));
        Assert.Equal(depth, s.History.Count);
        Assert.Equal("Update Pawn", s.Redo());
        Assert.Equal(after, State(s));
        Assert.False(s.History.CanRedo);
    }

    [Fact]
    public void Without_merge_each_edit_is_its_own_step()
    {
        var (s, n) = Session();
        int depth = s.History.Count;
        for (int i = 1; i <= 3; i++) s.Edit("Update Pawn", () => s.SetVector(n, 0, new Vec3(i, 0, 0)));
        Assert.Equal(depth + 3, s.History.Count);
    }

    [Fact]
    public void Another_step_between_stops_the_merge()
    {
        var (s, n) = Session();
        Sync(s, n, 1);
        int depth = s.History.Count;
        s.Add(new NullItem("Other"));
        Sync(s, n, 2);
        Assert.Equal(depth + 2, s.History.Count);
        Sync(s, n, 3);
        Assert.Equal(depth + 2, s.History.Count);
        // Undo takes back the second run only: the pawn is where the first left it, the other null still there.
        s.Undo();
        Assert.Equal(1, n.PositionAt(s.Frame).X);
        Assert.Contains(s.Scene.Items, i => i.Name == "Other");
    }

    [Fact]
    public void A_different_name_is_a_new_step()
    {
        var (s, n) = Session();
        Sync(s, n, 1);
        int depth = s.History.Count;
        Sync(s, n, 2, "Update Rook");
        Assert.Equal(depth + 1, s.History.Count);
        Assert.Equal("Update Rook", s.History.UndoLabel);
    }

    [Fact]
    public void After_an_undo_or_a_redo_it_doesnt_merge()
    {
        var (s, n) = Session();
        Sync(s, n, 1);
        Sync(s, n, 2);
        int depth = s.History.Count;
        s.Undo();
        Assert.True(s.History.CanRedo);
        // The step under the undone one has another name, but even one of the same name stays apart after an undo.
        Sync(s, n, 3);
        Assert.Equal(depth, s.History.Count);
        Assert.False(s.History.CanRedo);
        Assert.Equal(3, n.PositionAt(s.Frame).X);

        Sync(s, n, 4);
        Assert.Equal(depth, s.History.Count);
        s.Undo();
        s.Redo();
        Sync(s, n, 5);
        Assert.Equal(depth + 1, s.History.Count);
        s.Undo();
        Assert.Equal(4, n.PositionAt(s.Frame).X);
    }

    [Fact]
    public void Undoing_a_step_with_the_same_name_below_doesnt_reopen_it()
    {
        var (s, n) = Session();
        Sync(s, n, 1);
        s.Add(new NullItem("Other"));
        s.Undo();
        // The scene is back in the state the first run led to, but an undo came between: a new step.
        int depth = s.History.Count;
        Sync(s, n, 2);
        Assert.Equal(depth + 1, s.History.Count);
    }

    [Fact]
    public void A_change_straight_on_the_scene_between_stops_the_merge()
    {
        var (s, n) = Session();
        Sync(s, n, 1);
        int depth = s.History.Count;
        s.Scene.AddNull("Unrecorded");
        Sync(s, n, 2);
        Assert.Equal(depth + 1, s.History.Count);
    }

    [Fact]
    public void The_modified_flag_follows_a_merged_run_and_it_never_merges_across_a_save()
    {
        string path = Path.Combine(Path.GetTempPath(), "avamodeler-merge-" + Guid.NewGuid().ToString("N")[..8] + ".avascene");
        try
        {
            var (s, n) = Session();
            Sync(s, n, 1);
            s.SaveScene(path);
            Assert.False(s.Modified);
            int depth = s.History.Count;
            Sync(s, n, 2);
            Sync(s, n, 3);
            Assert.True(s.Modified);
            // The run after the save is a step of its own, so one undo comes back to the saved scene.
            Assert.Equal(depth + 1, s.History.Count);
            s.Undo();
            Assert.False(s.Modified);
            Assert.Equal(1, n.PositionAt(s.Frame).X);
            s.Redo();
            Assert.True(s.Modified);
            Assert.Equal(3, n.PositionAt(s.Frame).X);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_merged_edit_that_fails_changes_nothing()
    {
        var (s, n) = Session();
        Sync(s, n, 1);
        int depth = s.History.Count;
        var before = State(s);
        Assert.Throws<InvalidOperationException>(() => s.Edit("Update Pawn", () =>
        {
            s.SetVector(n, 0, new Vec3(9, 0, 0));
            throw new InvalidOperationException("no");
        }, merge: true));
        Assert.Equal(before, State(s));
        Assert.Equal(depth, s.History.Count);
        // The run carries on afterwards.
        Sync(s, n, 2);
        Assert.Equal(depth, s.History.Count);
    }

    [Fact]
    public void Scene_undo_says_when_a_step_would_merge()
    {
        var (s, n) = Session();
        Assert.False(s.History.CanMerge(s.Scene, "Update Pawn"));
        Sync(s, n, 1);
        Assert.True(s.History.CanMerge(s.Scene, "Update Pawn"));
        Assert.False(s.History.CanMerge(s.Scene, "Update Rook"));
        Assert.False(s.History.CanMerge(new Scene.Scene(), "Update Pawn"));
    }
}
