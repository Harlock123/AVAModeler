using AVAModeler.Core;

namespace AVAModeler.Scene.Tests;

/// <summary>An item's lock against the mouse: kept by copies, snapshots and .avascene files.</summary>
public class LockTests
{
    private static (Scene Scene, NullItem Null) Locked()
    {
        var scene = Scene.CreateDefault();
        var n = new NullItem("Pin") { Locked = true };
        scene.Add(n);
        return (scene, n);
    }

    [Fact]
    public void Items_start_unlocked()
    {
        Assert.False(new NullItem("Free").Locked);
        Assert.All(Scene.CreateDefault().Items, i => Assert.False(i.Locked));
    }

    [Fact]
    public void A_copy_keeps_the_lock()
    {
        var (scene, _) = Locked();
        var copy = scene.Copy();
        Assert.True(copy.Find("Pin")!.Locked);
        Assert.False(copy.Find("Camera")!.Locked);
    }

    [Fact]
    public void A_snapshot_puts_the_lock_back()
    {
        var (scene, pin) = Locked();
        var before = scene.Snapshot();
        pin.Locked = false;
        scene.Notify();
        before.Restore();
        Assert.True(pin.Locked);
    }

    [Fact]
    public void Avascene_files_keep_the_lock_and_leave_it_out_when_off()
    {
        var (scene, _) = Locked();
        var json = SceneJson.Write(scene);
        Assert.Equal(1, json.Split("\"locked\"").Length - 1);
        var back = SceneJson.Read(json).Scene;
        Assert.True(back.Find("Pin")!.Locked);
        Assert.False(back.Find("Light")!.Locked);
        Assert.DoesNotContain("\"locked\"", SceneJson.Write(Scene.CreateDefault()));
    }
}
