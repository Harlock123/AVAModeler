namespace AVAModeler.Scene.Tests;

/// <summary>A program's own data kept with the scene: saved, read back, copied and put back by undo.</summary>
public class ExtrasTests
{
    [Fact]
    public void Extras_are_saved_copied_and_put_back_by_a_snapshot()
    {
        var scene = Scene.CreateDefault();
        Assert.Empty(scene.Extras);
        Assert.DoesNotContain("extras", SceneJson.Write(scene), StringComparison.OrdinalIgnoreCase);
        int version = scene.Version;
        scene.SetExtra("app.bookmarks", """[{"name": "Wide", "zoom": 2}]""");
        Assert.True(scene.Version > version);
        var back = SceneJson.Read(SceneJson.Write(scene)).Scene;
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(scene.Extra("app.bookmarks")!), System.Text.Json.Nodes.JsonNode.Parse(back.Extra("app.bookmarks")!)));
        Assert.Equal(scene.Extra("app.bookmarks"), scene.Copy().Extra("app.bookmarks"));

        var snap = scene.Snapshot();
        scene.SetExtra("app.bookmarks", null);
        scene.SetExtra("app.other", "1");
        Assert.Null(scene.Extra("app.bookmarks"));
        snap.Restore();
        Assert.NotNull(scene.Extra("app.bookmarks"));
        Assert.Null(scene.Extra("app.other"));
        Assert.Throws<ArgumentException>(() => scene.SetExtra("app.bad", "{not json"));
    }
}
