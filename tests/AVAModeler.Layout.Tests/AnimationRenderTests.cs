using System.Collections.Concurrent;
using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>Render Animation's work without its window: numbered frames, progress, cancelling, and the scene copy.</summary>
public class AnimationRenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-anim-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Progress reported straight away on the rendering thread (Progress&lt;T&gt; would post it later).</summary>
    private sealed class Reports(Action<AnimationProgress>? also = null) : IProgress<AnimationProgress>
    {
        public ConcurrentQueue<AnimationProgress> All { get; } = new();
        public void Report(AnimationProgress value)
        {
            All.Enqueue(value);
            also?.Invoke(value);
        }
    }

    /// <summary>A scene with a box that slides along X from frame 1 to 9, so every frame looks different.</summary>
    private static (Scene.Scene Scene, ObjectItem Box) Sliding()
    {
        var doc = new Document();
        var m = doc.Current.Mesh;
        var p = new[] { new Vec3(-1, -1, 0), new Vec3(1, -1, 0), new Vec3(1, 1, 0), new Vec3(-1, 1, 0) };
        int a = m.AddPoint(p[0]), b = m.AddPoint(p[1]), c = m.AddPoint(p[2]), d = m.AddPoint(p[3]);
        m.Polygons.Add(new Polygon([a, d, c, b], 0));
        m.Touch();
        var session = new LayoutSession();
        var box = session.ShowModelerObject(doc);
        session.Scene.FirstFrame = 1;
        session.Scene.LastFrame = 9;
        box[Channel.PositionX].AddKey(1, -2);
        box[Channel.PositionX].AddKey(9, 2);
        return (session.Scene, box);
    }

    private AnimationSettings Settings(int first = 1, int last = 9, int step = 1) => new()
    {
        First = first, Last = last, Step = step, Width = 32, Height = 18, Samples = 1, Folder = _dir,
    };

    private static byte[] Reference(Scene.Scene scene, double frame) =>
        RayTracer.Render(scene, frame, new RenderSettings { Width = 32, Height = 18, SamplesPerPixel = 1 }).ToPng(alpha: false);

    [Fact]
    public async Task Frames_are_numbered_from_one_in_the_order_rendered_with_the_step()
    {
        var (scene, _) = Sliding();
        var settings = Settings(1, 9, 3);
        Assert.Equal([1, 4, 7], settings.Frames);
        var reports = new Reports();
        var result = await new AnimationRender(scene, settings).RunAsync(reports, TestContext.Current.CancellationToken);

        Assert.False(result.Cancelled);
        Assert.Equal([1, 4, 7], result.Frames);
        Assert.Equal(["frame0001.png", "frame0002.png", "frame0003.png"], result.Files.Select(Path.GetFileName));
        Assert.Equal(3, Directory.GetFiles(_dir).Length);
        // frame0002 is scene frame 4.
        Assert.Equal(Reference(scene, 4), File.ReadAllBytes(Path.Combine(_dir, "frame0002.png")));
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(_dir, "frame0001.png")), File.ReadAllBytes(Path.Combine(_dir, "frame0002.png")));

        // Progress: rows within each frame, then the finished frame with its picture and file.
        var all = reports.All.ToList();
        var finished = all.Where(r => r.Image != null).ToList();
        Assert.Equal([0, 1, 2], finished.Select(r => r.Index));
        Assert.Equal([1, 4, 7], finished.Select(r => r.Frame));
        Assert.Equal(result.Files, finished.Select(r => r.File!));
        Assert.All(all, r => Assert.Equal(3, r.Count));
        Assert.Contains(all, r => r.Image == null && r.FrameFraction > 0 && r.FrameFraction < 1);
        Assert.Equal(1, finished[^1].Fraction);
        Assert.Null(all[0].Remaining);
        Assert.NotNull(finished[0].Remaining);
        Assert.Equal("Frame 3 of 3", finished[^1].Text);
    }

    [Fact]
    public async Task Cancelling_stops_promptly_and_keeps_the_frames_done()
    {
        var (scene, _) = Sliding();
        using var cancel = new CancellationTokenSource();
        // Cancelled part way through the second frame's rows: that frame is dropped, the first stays.
        var reports = new Reports(r => { if (r.Index == 1 && r.FrameFraction > 0) cancel.Cancel(); });
        var settings = Settings() with { Width = 64, Height = 64 };
        var result = await new AnimationRender(scene, settings).RunAsync(reports, cancel.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(["frame0001.png"], result.Files.Select(Path.GetFileName));
        Assert.Equal([1], result.Frames);
        Assert.Single(Directory.GetFiles(_dir));
        Assert.DoesNotContain(reports.All, r => r.Index > 1);
        Assert.DoesNotContain(reports.All, r => r.Index == 1 && r.Image != null);
    }

    [Fact]
    public async Task Cancelling_between_frames_stops_before_the_next()
    {
        var (scene, _) = Sliding();
        using var cancel = new CancellationTokenSource();
        var reports = new Reports(r => { if (r.Image != null && r.Index == 2) cancel.Cancel(); });
        var result = await new AnimationRender(scene, Settings()).RunAsync(reports, cancel.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(3, result.Files.Count);
        Assert.Equal(3, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public async Task Editing_the_scene_while_it_renders_doesnt_change_the_frames()
    {
        var (scene, box) = Sliding();
        var before = new[] { Reference(scene, 1), Reference(scene, 2), Reference(scene, 3) };
        var job = new AnimationRender(scene, Settings(1, 3));
        // Edits after the job is made, and more while it renders: moved, its mesh emptied, the camera turned.
        box[Channel.PositionX].AddKey(2, 50);
        var reports = new Reports(r =>
        {
            if (r.Index == 0 && r.Image != null)
            {
                box[Channel.PositionY].AddKey(1, 30);
                scene.CurrentCamera![Channel.Heading].AddKey(1, 90);
            }
        });
        box.Document!.Current.Mesh.Polygons.Clear();
        box.Document.Current.Mesh.Touch();
        var result = await job.RunAsync(reports, TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Files.Count);
        for (int i = 0; i < 3; i++) Assert.Equal(before[i], File.ReadAllBytes(result.Files[i]));
        Assert.NotEqual(before[0], Reference(scene, 1));
    }

    [Fact]
    public void Settings_are_checked_before_starting()
    {
        var (scene, _) = Sliding();
        Assert.Null(Settings().Problem());
        Assert.Contains("before the first", Settings(5, 2).Problem());
        Assert.Contains("step", (Settings() with { Step = 0 }).Problem());
        Assert.Contains("size", (Settings() with { Width = 0 }).Problem());
        Assert.Contains("folder", (Settings() with { Folder = " " }).Problem());
        Assert.Contains("plain file name", (Settings() with { Name = "a/b" }).Problem());
        Assert.Throws<ArgumentException>(() => new AnimationRender(scene, Settings(5, 2)));
        var other = Scene.Scene.CreateDefault().Cameras.Single();
        Assert.Throws<ArgumentException>(() => new AnimationRender(scene, Settings() with { Camera = other }));
    }

    [Fact]
    public void The_folder_defaults_beside_the_saved_scene_or_in_documents()
    {
        var scenePath = Path.Combine(_dir, "atom.avascene");
        Assert.Equal(Path.Combine(_dir, "atom_frames"), AnimationRender.DefaultFolder(scenePath));
        Assert.Equal(Path.Combine(_dir, "AVAModeler", "scene_frames"), AnimationRender.DefaultFolder(null, _dir));
        Assert.EndsWith(Path.Combine("AVAModeler", "scene_frames"), AnimationRender.DefaultFolder(null));
    }

    [Fact]
    public void Existing_frames_sizes_and_times_for_the_warnings()
    {
        var settings = Settings(1, 4);
        Assert.Empty(AnimationRender.Existing(settings));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "frame0002.png"), "x");
        File.WriteAllText(Path.Combine(_dir, "frame0009.png"), "x"); // not one this render writes
        Assert.Equal([Path.Combine(_dir, "frame0002.png")], AnimationRender.Existing(settings));

        Assert.Equal(2L * 32 * 18 * 4, AnimationRender.EstimatedBytes(settings));
        var huge = settings with { Width = 3840, Height = 2160, Last = 300 };
        Assert.True(AnimationRender.EstimatedBytes(huge) > AnimationRender.LargeOutput);
        Assert.Equal(TimeSpan.FromMinutes(40), AnimationRender.Estimate(TimeSpan.FromSeconds(20), 1, 120));
        Assert.True(AnimationRender.Estimate(TimeSpan.FromSeconds(20), 1, 120) > AnimationRender.LongRender);
        Assert.Equal("1:20", AnimationRender.Duration(TimeSpan.FromSeconds(80)));
        Assert.Equal("0:05", AnimationRender.Duration(TimeSpan.FromSeconds(4.2)));
        Assert.Equal("1:02:03", AnimationRender.Duration(new TimeSpan(1, 2, 3)));
        var p = new AnimationProgress(36, 120, 37, 0.5, null, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(80));
        Assert.Equal("Frame 37 of 120, about 1:20 left", p.Text);
        Assert.Equal(36.5 / 120, p.Fraction, 9);
    }
}
