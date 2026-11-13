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

/// <summary>The scene's sound track: kept relative to the scene in .avascene, in copies and snapshots, and its timing.</summary>
public class AudioTests
{
    [Fact]
    public void The_sound_is_kept_relative_and_put_back_and_timed()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var scene = Scene.CreateDefault();
            var wav = Path.Combine(dir.FullName, "sound", "voice.wav");
            scene.Audio = new SceneAudio { Path = wav, StartFrame = 11, Volume = 0.8 };
            var path = Path.Combine(dir.FullName, "a.avascene");
            SceneJson.WriteFile(scene, path);
            Assert.Contains("sound/voice.wav", File.ReadAllText(path).Replace('\\', '/'));
            Assert.DoesNotContain(dir.FullName, File.ReadAllText(path));
            var back = SceneJson.ReadFile(path, _ => null).Scene;
            Assert.Equal(scene.Audio, back.Audio);
            Assert.Equal(scene.Audio, scene.Copy().Audio);
            var snap = scene.Snapshot();
            scene.Audio = new SceneAudio();
            snap.Restore();
            Assert.Equal(wav, scene.Audio.Path);
            Assert.Equal(1, scene.Audio.SecondsAt(41, scene.FirstFrame, 30), 9);
            Assert.Equal(-1, new SceneAudio { Path = "x" }.SecondsAt(-29, 1, 30), 9);
            Assert.Throws<ArgumentException>(() => (scene.Audio with { Volume = 3 }).Checked());
        }
        finally { dir.Delete(true); }
    }
}
