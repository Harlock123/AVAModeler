using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Tools;

namespace AVAModeler.Tools.Tests;

/// <summary>History steps, and instances: placed, following their source, saved, exported and frozen.</summary>
public class WorkflowToolTests
{
    private static Dictionary<string, object?> Args(params (string, object?)[] a) => a.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_history_lists_steps_and_goes_back_and_forward_through_them()
    {
        var d = new Document();
        var cmd = new Commands(d);
        cmd.Run("Box");
        cmd.Run("Move", Args(("Offset", new[] { 1.0, 0, 0 })));
        cmd.Run("Size", Args(("Factor", 200.0)));
        Assert.Equal(["Box", "Move", "Size"], d.Undo.UndoLabels);
        Assert.Equal(2, d.Step(-2));
        Assert.Equal(["Box"], d.Undo.UndoLabels);
        Assert.Equal(["Move", "Size"], d.Undo.RedoLabels);
        Assert.Equal(1, d.Step(1));
        Assert.Equal(["Size"], d.Undo.RedoLabels);
        Assert.Equal(2, d.Step(-5));
        Assert.Empty(d.Undo.UndoLabels);
    }

    [Fact]
    public void Instances_follow_their_source_and_are_kept_exported_and_frozen()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"ava-inst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var d = new Document();
            var cmd = new Commands(d);
            cmd.Run("Box", Args(("Size", new[] { 0.5, 0.5, 0.5 })));
            cmd.Run("Instance", Args(("Copies", 3), ("Offset", new[] { 1.0, 0, 0 })));
            var target = d.Layers[1];
            Assert.Equal(0, target.Instances!.Source);
            Assert.Equal(3, target.Instances.Places.Count);
            Assert.Equal(LayerState.Background, target.State);
            Assert.True(target.Mesh.IsEmpty);
            var copies = Instancing.Copies(d, 1);
            Assert.Equal(24, copies.Points.Count);
            Assert.Equal(3.25, copies.Bounds().Max.X, 9);
            // They follow the source.
            cmd.Run("Size", Args(("Factor", 200.0)));
            Assert.Equal(3.5, Instancing.Copies(d, 1).Bounds().Max.X, 9);
            // Undo takes them away; redo brings them back.
            d.Step(-2);
            Assert.Null(d.Layers[1].Instances);
            d.Step(2);
            Assert.NotNull(d.Layers[1].Instances);

            // An .lwo keeps them as instances; an .obj gets the copies as real geometry.
            string lwo = Path.Combine(folder, "row.lwo"), obj = Path.Combine(folder, "row.obj");
            ObjectFiles.Save(d, lwo);
            var back = ObjectFiles.Load(lwo);
            Assert.Equal(3, back.Layers[1].Instances!.Places.Count);
            Assert.Equal(3.5, Instancing.Copies(back, 1).Bounds().Max.X, 5);
            Assert.Equal(File.ReadAllBytes(lwo), Lwob.Write(back));
            ObjectFiles.Save(d, obj);
            Assert.Equal(32, File.ReadAllLines(obj).Count(l => l.StartsWith("v ")));

            // Freeze makes them real, in their own layer.
            d.Layers[1].State = LayerState.Foreground;
            d.Layers[0].State = LayerState.Background;
            cmd.Run("Freeze Instances");
            Assert.Null(d.Layers[1].Instances);
            Assert.Equal(24, d.Layers[1].Mesh.Points.Count);
            Assert.Throws<InvalidOperationException>(() => cmd.Run("Freeze Instances"));
            // A layer can't hold instances of itself.
            Assert.Throws<ArgumentException>(() => cmd.Run("Instance", Args(("TargetLayer", 2))));
        }
        finally { Directory.Delete(folder, true); }
    }
}
