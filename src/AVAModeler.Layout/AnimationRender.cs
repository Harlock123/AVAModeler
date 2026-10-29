using System.Diagnostics;
using System.Globalization;
using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>What Render › Render Animation renders: the frames, the camera and picture, and where the PNGs go.</summary>
public sealed record AnimationSettings
{
    public int First { get; init; } = 1;
    public int Last { get; init; } = 60;
    /// <summary>Render every step-th frame.</summary>
    public int Step { get; init; } = 1;
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 480;
    /// <summary>Rays a pixel (antialiasing).</summary>
    public int Samples { get; init; } = 4;
    public bool Shadows { get; init; } = true;
    public bool Reflections { get; init; } = true;
    public bool Transparency { get; init; } = true;
    /// <summary>The folder the PNGs go in; made if needed.</summary>
    public string Folder { get; init; } = "";
    /// <summary>The PNGs' names before the number: frame0001.png, frame0002.png and so on.</summary>
    public string Name { get; init; } = "frame";
    /// <summary>The camera to look through, in the scene being worked on (default: its current camera).</summary>
    public CameraItem? Camera { get; init; }

    /// <summary>The scene frames rendered, in order: first, first + step and so on, up to last.</summary>
    public IReadOnlyList<int> Frames => Last < First || Step < 1 ? [] : Enumerable.Range(0, (Last - First) / Step + 1).Select(i => First + i * Step).ToList();

    /// <summary>The PNGs, numbered from 1 in the order rendered (not by scene frame), so ffmpeg reads them as one run.</summary>
    public IReadOnlyList<string> Paths => Enumerable.Range(1, Frames.Count).Select(n => Path.Combine(Folder, AnimationRender.FileName(Name, n))).ToList();

    /// <summary>What's wrong with these settings, or null if they'll do.</summary>
    public string? Problem()
    {
        if (Last < First) return $"The last frame ({Last}) is before the first ({First}).";
        if (Step < 1) return "The step must be 1 or more.";
        if (Width < 1 || Height < 1 || Width > 8192 || Height > 8192) return "The size must be between 1 and 8192.";
        if (Samples < 1 || Samples > 64) return "The samples must be between 1 and 64.";
        if (string.IsNullOrWhiteSpace(Folder)) return "Choose a folder for the frames.";
        if (string.IsNullOrWhiteSpace(Name) || Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || Name.Contains('/') || Name.Contains('\\') || Name.Contains('%'))
            return "The frames' name must be a plain file name, such as \"frame\".";
        return null;
    }
}

/// <summary>Where an animation render has got to, reported as each frame's rows finish and again when the frame is saved.</summary>
public readonly record struct AnimationProgress(int Index, int Count, int Frame, double FrameFraction, RenderImage? Image, string? File, TimeSpan Elapsed, TimeSpan? Remaining)
{
    /// <summary>The fraction of the whole run done.</summary>
    public double Fraction => Count == 0 ? 1 : Math.Clamp((Index + (Image != null ? 1 : FrameFraction)) / Count, 0, 1);

    /// <summary>"Frame 37 of 120, about 1:20 left".</summary>
    public string Text
    {
        get
        {
            int shown = Image != null ? Index + 1 : Math.Min(Index + 1, Count);
            string text = $"Frame {shown} of {Count}";
            if (Remaining is { } left && Index + (Image != null ? 1 : 0) < Count) text += $", about {AnimationRender.Duration(left)} left";
            return text;
        }
    }
}

/// <summary>How an animation render ended: the files written (all of them, or those done before it was cancelled).</summary>
public sealed record AnimationResult(IReadOnlyList<string> Files, IReadOnlyList<int> Frames, bool Cancelled, TimeSpan Elapsed);

/// <summary>
/// Renders a run of a scene's frames with the ray tracer into numbered PNGs, off the thread that owns the scene. The
/// job works on a <see cref="Scene.Scene.Copy"/> with copied documents, taken when it's made, so the scene can be
/// edited while it renders. No UI: Layout's Render Animation window drives it.
/// </summary>
public sealed class AnimationRender
{
    /// <summary>Warn before a render expected to take longer than this (measured on the first frame)…</summary>
    public static readonly TimeSpan LongRender = TimeSpan.FromMinutes(30);
    /// <summary>…or to write more than this many bytes of PNGs.</summary>
    public const long LargeOutput = 3L * 1024 * 1024 * 1024;

    private readonly Scene.Scene _copy;
    private readonly CameraItem _camera;

    public AnimationSettings Settings { get; }

    /// <summary>Copies the scene (on the thread that owns it) and checks the settings.</summary>
    public AnimationRender(Scene.Scene scene, AnimationSettings settings)
    {
        if (settings.Problem() is { } problem) throw new ArgumentException(problem);
        var camera = settings.Camera ?? scene.CurrentCamera ?? throw new InvalidOperationException("The scene has no camera to render through. Add one with Add › Camera.");
        int index = scene.Items.ToList().IndexOf(camera);
        if (index < 0) throw new ArgumentException($"The camera {camera.Name} isn't in this scene.");
        _copy = scene.Copy(d => d.Copy());
        _camera = (CameraItem)_copy.Items[index];
        Settings = settings;
    }

    public static string FileName(string name, int number) => string.Create(CultureInfo.InvariantCulture, $"{name}{number:0000}.png");

    /// <summary>
    /// Where the frames go by default: beside the saved scene, in "&lt;scene name&gt;_frames", or for a scene never
    /// saved, "scene_frames" in an AVAModeler folder in <paramref name="documents"/> (default: the person's documents).
    /// </summary>
    public static string DefaultFolder(string? scenePath, string? documents = null)
    {
        if (!string.IsNullOrEmpty(scenePath))
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scenePath))!, Path.GetFileNameWithoutExtension(scenePath) + "_frames");
        documents ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents)) documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(documents, "AVAModeler", "scene_frames");
    }

    /// <summary>The frames this render would replace.</summary>
    public static IReadOnlyList<string> Existing(AnimationSettings settings) => settings.Paths.Where(File.Exists).ToList();

    /// <summary>A rough size for the PNGs (rendered frames compress to about two bytes a pixel).</summary>
    public static long EstimatedBytes(AnimationSettings settings) => 2L * settings.Width * settings.Height * settings.Frames.Count;

    /// <summary>How long the whole run should take, from how long the frames done so far took.</summary>
    public static TimeSpan Estimate(TimeSpan elapsed, int done, int count) => done <= 0 ? TimeSpan.Zero : elapsed / done * count;

    /// <summary>A length of time as 1:20 or 1:02:03.</summary>
    public static string Duration(TimeSpan t)
    {
        long s = (long)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        return s >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");
    }

    /// <summary>
    /// Renders every frame on a worker thread, saving each as a PNG as it finishes. Cancelling stops between rows (the
    /// frame being rendered is dropped) and returns the frames done, with <see cref="AnimationResult.Cancelled"/> set.
    /// </summary>
    public Task<AnimationResult> RunAsync(IProgress<AnimationProgress>? progress = null, CancellationToken cancel = default) =>
        Task.Run(() => Run(progress, cancel), CancellationToken.None);

    private AnimationResult Run(IProgress<AnimationProgress>? progress, CancellationToken cancel)
    {
        var s = Settings;
        var frames = s.Frames;
        var paths = s.Paths;
        var done = new List<string>();
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(s.Folder);
        var render = new RenderSettings
        {
            Width = s.Width, Height = s.Height, Camera = _camera, SamplesPerPixel = s.Samples,
            Shadows = s.Shadows, Reflections = s.Reflections, Transparency = s.Transparency,
        };
        for (int i = 0; i < frames.Count; i++)
        {
            if (cancel.IsCancellationRequested) break;
            int index = i;
            TimeSpan? left = i == 0 ? null : Estimate(clock.Elapsed, i, frames.Count) - clock.Elapsed;
            progress?.Report(new AnimationProgress(i, frames.Count, frames[i], 0, null, null, clock.Elapsed, left));
            RenderImage img;
            try
            {
                img = RayTracer.Render(_copy, frames[i], render, f => progress?.Report(new AnimationProgress(index, frames.Count, frames[index], f, null, null, clock.Elapsed, left)), cancel);
            }
            catch (OperationCanceledException) { break; }
            File.WriteAllBytes(paths[i], img.ToPng(alpha: false));
            done.Add(paths[i]);
            progress?.Report(new AnimationProgress(i, frames.Count, frames[i], 1, img, paths[i], clock.Elapsed, Estimate(clock.Elapsed, i + 1, frames.Count) - clock.Elapsed));
        }
        return new AnimationResult(done, frames.Take(done.Count).ToList(), done.Count < frames.Count, clock.Elapsed);
    }
}
