using System.Globalization;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// One picture in the scene's image list (Layout's Images panel): a file, or a numbered sequence of files. A sequence's
/// <see cref="Path"/> is any one of its files (e.g. <c>frames/sky0001.png</c>): the digits just before the extension
/// are the frame number, and the files for other numbers are found beside it by the same name with other digits
/// (<see cref="ImageSequence"/>). The number shown at a frame is the frame plus <see cref="Offset"/>; with
/// <see cref="Loop"/> numbers past the files on disk wrap round through them, without it the first or last file holds.
/// Immutable: change the list by setting a new one (<see cref="Scene.Images"/>).
/// </summary>
public sealed record SceneImage
{
    /// <summary>The file (a sequence: any one of its numbered files). Kept absolute in a scene; .avascene writes it relative.</summary>
    public string Path { get; init; } = "";
    /// <summary>Whether it's a numbered sequence (an image per frame) rather than one still picture.</summary>
    public bool Sequence { get; init; }
    /// <summary>A sequence's number at a frame is the frame plus this (whole frames, default 0).</summary>
    public int Offset { get; init; }
    /// <summary>Whether a sequence starts again after its last file (else the first and last hold outside them).</summary>
    public bool Loop { get; init; }

    /// <summary>Throws an <see cref="ArgumentException"/> saying what's wrong, if anything.</summary>
    public SceneImage Checked()
    {
        if (string.IsNullOrWhiteSpace(Path)) throw new ArgumentException("An image needs a file.");
        if (Sequence && ImageSequence.Pattern(Path) == null)
            throw new ArgumentException($"{System.IO.Path.GetFileName(Path)} has no number before its extension, so it can't be a sequence: give one of its numbered files, such as sky0001.png.");
        return this;
    }
}

/// <summary>How the foreground image is cut out (LightWave's Foreground Key).</summary>
public enum ForegroundKey
{
    /// <summary>Laid over the picture wherever it is (by its own alpha).</summary>
    None,
    /// <summary>Left out where its colour lies between the low and the high clip colours, channel by channel.</summary>
    Color,
    /// <summary>Laid over by an alpha: the foreground alpha image's brightness if one is given, else the foreground's own alpha channel.</summary>
    Alpha,
}

/// <summary>
/// LightWave's Effects › Compositing: a background image behind the scene and a foreground image over it. Paths are
/// images in the scene's list (<see cref="Scene.Images"/>), so a sequence there plays one file a frame; "" is none.
/// <para>
/// The background shows where camera rays meet nothing, stretched to the frame, in place of the backdrop, gradient
/// or environment (reflections and refraction still see those). Its own alpha lets the backdrop show through. The
/// picture's alpha channel stays the objects' coverage: the background doesn't make it opaque.
/// </para>
/// <para>
/// After the picture is made, the foreground is laid over it, stretched to the frame, by a weight: its own alpha (or,
/// keyed by <see cref="ForegroundKey.Alpha"/> with <see cref="ForegroundAlpha"/> given, that image's brightness),
/// times 0 where a <see cref="ForegroundKey.Color"/> key leaves it out, times 1 − <see cref="ForegroundDissolve"/>.
/// The alpha channel becomes covered where it is laid: weight + alpha × (1 − weight).
/// </para>
/// Off (no images) by default; a scene without it renders exactly as before.
/// </summary>
public sealed record CompositingSettings
{
    /// <summary>The background image's path ("" for none).</summary>
    public string Background { get; init; } = "";
    /// <summary>The foreground image's path ("" for none).</summary>
    public string Foreground { get; init; } = "";
    /// <summary>An image whose brightness is the foreground's alpha (LightWave's Foreground Alpha Image), for the alpha key; "" uses the foreground's own alpha.</summary>
    public string ForegroundAlpha { get; init; } = "";
    public ForegroundKey Key { get; init; }
    /// <summary>The colour key's low and high clip colours (0 to 1 each channel); a foreground pixel between them is left out.</summary>
    public Vec3 KeyLow { get; init; }
    public Vec3 KeyHigh { get; init; }
    /// <summary>How far the foreground is faded out, 0 (laid over fully, the default) to 1 (gone).</summary>
    public double ForegroundDissolve { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool On => Background.Length > 0 || Foreground.Length > 0;

    public CompositingSettings Checked()
    {
        if (!(ForegroundDissolve >= 0 && ForegroundDissolve <= 1)) throw new ArgumentException("The foreground dissolve must be from 0 to 1.");
        foreach (var (c, what) in new[] { (KeyLow, "low"), (KeyHigh, "high") })
            if (!double.IsFinite(c.X) || !double.IsFinite(c.Y) || !double.IsFinite(c.Z) || c.X < 0 || c.Y < 0 || c.Z < 0 || c.X > 1 || c.Y > 1 || c.Z > 1)
                throw new ArgumentException($"The key's {what} colour must be from 0 to 1 in each channel.");
        if (Key == ForegroundKey.Color && (KeyLow.X > KeyHigh.X || KeyLow.Y > KeyHigh.Y || KeyLow.Z > KeyHigh.Z))
            throw new ArgumentException("The key's low colour must not be above its high colour in any channel.");
        if (Key != ForegroundKey.None && Foreground.Length == 0) throw new ArgumentException("A foreground key needs a foreground image.");
        if (ForegroundAlpha.Length > 0 && Key != ForegroundKey.Alpha) throw new ArgumentException("A foreground alpha image is for the alpha key.");
        return this;
    }

    /// <summary>Whether a foreground colour is left out by the colour key.</summary>
    public bool KeyedOut(Vec3 c) =>
        Key == ForegroundKey.Color && c.X >= KeyLow.X && c.X <= KeyHigh.X && c.Y >= KeyLow.Y && c.Y <= KeyHigh.Y && c.Z >= KeyLow.Z && c.Z <= KeyHigh.Z;
}

/// <summary>
/// Numbered image sequences: which file a <see cref="SceneImage"/> shows at a frame. A sequence's path ends in digits
/// before its extension (<c>sky0001.png</c>); the folder is scanned once for the files with the same name before and
/// after the digits (any number of digits), and the scan is kept until the folder changes (its write time) or
/// <see cref="Forget"/>. A number with a file on disk gives that file; one without (a gap, or no files at all) is
/// written with the path's digit count (<c>sky0002.png</c>).
/// </summary>
public static class ImageSequence
{
    private sealed record Scan(DateTime Written, SortedDictionary<int, string> Files);

    private static readonly Dictionary<(string Folder, string Prefix, string Suffix), Scan> Cache = [];
    private static readonly Lock CacheLock = new();

    /// <summary>A path's parts around the digits before its extension (the folder and name before them, their count, and the extension), or null if it has none.</summary>
    public static (string Prefix, int Digits, int Number, string Suffix)? Pattern(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string ext = System.IO.Path.GetExtension(path);
        string stem = path[..^ext.Length];
        int end = stem.Length, start = end;
        while (start > 0 && char.IsAsciiDigit(stem[start - 1])) start--;
        int digits = end - start;
        if (digits == 0 || digits > 9) return null;
        int number = int.Parse(stem[start..end], NumberStyles.None, CultureInfo.InvariantCulture);
        return (stem[..start], digits, number, ext);
    }

    /// <summary>The sequence's numbered files on disk, by number (empty if the folder or files aren't there).</summary>
    public static IReadOnlyDictionary<int, string> Files(SceneImage image)
    {
        if (Pattern(image.Path) is not { } p) return new SortedDictionary<int, string>();
        string full;
        try { full = System.IO.Path.GetFullPath(p.Prefix + new string('0', p.Digits) + p.Suffix); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return new SortedDictionary<int, string>(); }
        string folder = System.IO.Path.GetDirectoryName(full) ?? "";
        string fileName = System.IO.Path.GetFileName(full);
        string name = fileName[..^(p.Digits + p.Suffix.Length)];
        var key = (folder, name, p.Suffix);
        DateTime written;
        try { written = Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder) : default; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { written = default; }
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var hit) && hit.Written == written && written != default) return hit.Files;
        var files = new SortedDictionary<int, string>();
        if (written != default)
        {
            var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    string file = System.IO.Path.GetFileName(f);
                    if (file.Length <= name.Length + p.Suffix.Length || !file.StartsWith(name, cmp) || !file.EndsWith(p.Suffix, cmp)) continue;
                    string middle = file[name.Length..^p.Suffix.Length];
                    if (middle.Length is 0 or > 9 || !middle.All(char.IsAsciiDigit)) continue;
                    int n = int.Parse(middle, NumberStyles.None, CultureInfo.InvariantCulture);
                    // Two files for one number (sky1, sky001): the one with the path's digit count wins.
                    if (!files.TryGetValue(n, out var had) || System.IO.Path.GetFileNameWithoutExtension(had).Length - name.Length != p.Digits)
                        files[n] = System.IO.Path.GetFullPath(f);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        lock (CacheLock) Cache[key] = new Scan(written, files);
        return files;
    }

    /// <summary>The first and last numbers on disk, or null if there are no files.</summary>
    public static (int First, int Last)? Range(SceneImage image)
    {
        var files = Files(image);
        return files.Count == 0 ? null : (files.Keys.First(), files.Keys.Last());
    }

    /// <summary>
    /// The number a sequence shows at a frame: the frame (rounded) plus the offset, wrapped round the numbers on disk
    /// when it loops, held at the first or last outside them when it doesn't. Without files on disk it isn't wrapped.
    /// </summary>
    public static int Number(SceneImage image, double frame)
    {
        int n = (int)Math.Clamp(Math.Round(frame, MidpointRounding.AwayFromZero) + image.Offset, int.MinValue / 2, int.MaxValue / 2);
        if (Range(image) is not var (first, last)) return n;
        if (image.Loop)
        {
            long span = (long)last - first + 1;
            return (int)(first + (((n - (long)first) % span) + span) % span);
        }
        return Math.Clamp(n, first, last);
    }

    /// <summary>
    /// The file an image shows at a frame: a still's own path; a sequence's numbered file (as on disk, or else written
    /// with the path's digit count).
    /// </summary>
    public static string Resolve(SceneImage image, double frame)
    {
        if (!image.Sequence || Pattern(image.Path) is not { } p) return image.Path;
        int n = Number(image, frame);
        if (Files(image).TryGetValue(n, out var file)) return file;
        return p.Prefix + (n < 0 ? "-" + (-n).ToString("D" + p.Digits, CultureInfo.InvariantCulture) : n.ToString("D" + p.Digits, CultureInfo.InvariantCulture)) + p.Suffix;
    }

    /// <summary>What's wrong with an image (a missing file, a sequence with no files), or null.</summary>
    public static string? Problem(SceneImage image)
    {
        string name = System.IO.Path.GetFileName(image.Path);
        if (image.Sequence)
        {
            if (Pattern(image.Path) == null) return $"The image sequence {name} has no number before its extension.";
            return Range(image) == null ? $"The image sequence {name} has no frames on disk." : null;
        }
        try
        {
            return File.Exists(image.Path) ? null : $"Can't find the image {name}.";
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return $"Can't find the image {name}."; }
    }

    /// <summary>Forgets every folder scanned, so the next use looks at the disk again.</summary>
    public static void Forget()
    {
        lock (CacheLock) Cache.Clear();
    }
}

/// <summary>The scene's image list and compositing, as the renderer and the panels use them.</summary>
public static class SceneImages
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A path made full for comparing (as it is if it can't be).</summary>
    public static string Full(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>Whether two image paths name the same file.</summary>
    public static bool Same(string a, string b) => PathComparer.Equals(Full(a), Full(b));

    /// <summary>The list's entry for a path, or null.</summary>
    public static SceneImage? Find(Scene scene, string path) =>
        string.IsNullOrWhiteSpace(path) ? null : scene.Images.FirstOrDefault(i => Same(i.Path, path));

    /// <summary>Whether a path is in the scene's image list.</summary>
    public static bool Contains(Scene scene, string path) => Find(scene, path) != null;

    /// <summary>
    /// The file a texture layer's image (as resolved from its object's folder) shows at a frame: a sequence's numbered
    /// file when the image is a sequence in the scene's list, else null (the layer's own image).
    /// </summary>
    public static string? TextureFile(Scene scene, string image, string? baseDirectory, double frame)
    {
        if (scene.Images.Count == 0 || string.IsNullOrWhiteSpace(image)) return null;
        string? file = TextureImages.Resolve(image, baseDirectory);
        if (file == null) return null;
        var entry = scene.Images.FirstOrDefault(i => i.Sequence && Same(i.Path, file));
        return entry == null ? null : ImageSequence.Resolve(entry, frame);
    }

    /// <summary>The file a compositing path shows at a frame: through its list entry (a sequence's numbered file), or as it is.</summary>
    public static string FileAt(Scene scene, string path, double frame) =>
        Find(scene, path) is { } entry ? ImageSequence.Resolve(entry, frame) : path;

    /// <summary>A compositing picture at a frame, or null when there's none or it can't be read.</summary>
    public static TextureImage? Picture(Scene scene, string path, double frame)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var image = TextureImages.Load(FileAt(scene, path, frame));
        return image.IsPlaceholder ? null : image;
    }

    /// <summary>What's wrong with the scene's images and compositing at a frame (render warnings), each once.</summary>
    public static List<string> Problems(Scene scene, double frame)
    {
        var problems = new List<string>();
        if (scene.Images.Count == 0 && !scene.Compositing.On) return problems;
        foreach (var image in scene.Images)
        {
            if (ImageSequence.Problem(image) is { } p) problems.Add(p);
            else if (image.Sequence && TextureImages.Load(ImageSequence.Resolve(image, frame)) is { IsPlaceholder: true, Problem: { } q })
                problems.Add($"{System.IO.Path.GetFileName(image.Path)} at frame {frame.ToString("0.###", CultureInfo.InvariantCulture)}: {q}");
        }
        var c = scene.Compositing;
        foreach (var (path, what) in new[] { (c.Background, "background"), (c.Foreground, "foreground"), (c.ForegroundAlpha, "foreground alpha") })
        {
            if (path.Length == 0) continue;
            var entry = Find(scene, path);
            if (entry != null && ImageSequence.Problem(entry) != null) continue;
            if (Picture(scene, path, frame) == null)
                problems.Add($"The {what} image {System.IO.Path.GetFileName(path)} can't be read at frame {frame.ToString("0.###", CultureInfo.InvariantCulture)}, so it's left out.");
        }
        return problems.Distinct().ToList();
    }
}
