using System.Diagnostics;
using System.Globalization;

namespace AVAModeler.Layout;

/// <summary>The movies <see cref="MovieEncoder"/> makes from a run of PNG frames.</summary>
public enum MovieFormat { Mp4, Gif }

/// <summary>What an encode made, and what went wrong if anything did (null when all went well).</summary>
public sealed record MovieResult(IReadOnlyList<string> Files, string? Problem);

/// <summary>
/// Joins numbered PNG frames (name0001.png, name0002.png…) into an MP4 (H.264, yuv420p, faststart) and a looping GIF
/// (one palette, ordered dither, only the changed rectangle of each frame stored) with ffmpeg, if it's on the PATH.
/// The arguments are those of <c>samples/mcp/carbon_atom.py</c>'s join(), which are tuned and checked. Nothing is
/// bundled: without ffmpeg the frames stay as they are and <see cref="NotInstalled"/> says what to do.
/// </summary>
public sealed class MovieEncoder(string? ffmpeg)
{
    public const string NotInstalled = "ffmpeg isn't installed (or isn't on the PATH), so no movie was made; the PNG frames are all saved. Install ffmpeg (from ffmpeg.org or your system's package manager) to make MP4 and GIF movies.";

    /// <summary>The GIF is scaled down to at most this many pixels across, as the carbon atom sample's is.</summary>
    public const int GifMaxWidth = 640;

    /// <summary>The ffmpeg found, or null.</summary>
    public string? Ffmpeg { get; } = ffmpeg;
    public bool Available => Ffmpeg != null;

    /// <summary>An encoder using the ffmpeg on the PATH, if there is one.</summary>
    public static MovieEncoder FromPath() => new(Find());

    /// <summary>
    /// ffmpeg's path, searching <paramref name="pathVariable"/> (default: the PATH) for ffmpeg, or ffmpeg.exe on
    /// Windows; null if it isn't there.
    /// </summary>
    public static string? Find(string? pathVariable = null, bool? windows = null)
    {
        bool win = windows ?? OperatingSystem.IsWindows();
        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        string name = win ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in pathVariable.Split(win ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), name); }
            catch (ArgumentException) { continue; }
            if (!File.Exists(candidate)) continue;
            if (!win && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) continue;
            return candidate;
        }
        return null;
    }

    /// <summary>The file name of a movie of <paramref name="name"/> in <paramref name="folder"/>.</summary>
    public static string MoviePath(string folder, string name, MovieFormat format) => Path.Combine(folder, name + (format == MovieFormat.Mp4 ? ".mp4" : ".gif"));

    /// <summary>ffmpeg's arguments to read <paramref name="count"/> frames, name0001.png onwards, at a frame rate.</summary>
    public static List<string> InputArguments(string folder, string name, double fps, int count) =>
    [
        "-y", "-loglevel", "error", "-framerate", fps.ToString("0.###", CultureInfo.InvariantCulture), "-start_number", "1",
        "-i", Path.Combine(folder, name.Replace("%", "%%") + "%04d.png"),
        // Only this run's frames, even if more from an earlier, longer one are in the folder.
        "-frames:v", count.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>The MP4's arguments. H.264 in yuv420p needs an even size, so an odd one is padded by a pixel.</summary>
    public static List<string> Mp4Arguments(string folder, string name, double fps, int count, int width, int height, string output)
    {
        var args = InputArguments(folder, name, fps, count);
        if (width % 2 != 0 || height % 2 != 0) args.AddRange(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2"]);
        args.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", "-movflags", "+faststart", output]);
        return args;
    }

    /// <summary>
    /// The GIF's arguments: one 128-colour palette made from the whole run (dark backdrops band less with ordered
    /// dithering), and only the changed part of each frame stored. It loops forever.
    /// </summary>
    public static List<string> GifArguments(string folder, string name, double fps, int count, int width, string output)
    {
        var args = InputArguments(folder, name, fps, count);
        int across = Math.Min(GifMaxWidth, width);
        string palette = string.Create(CultureInfo.InvariantCulture, $"scale={across}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];") +
                         "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle";
        args.AddRange(["-vf", palette, "-loop", "0", output]);
        return args;
    }

    /// <summary>
    /// Makes each movie asked for from <paramref name="count"/> frames in <paramref name="folder"/>, saving them there
    /// as <paramref name="movieName"/>.mp4 and .gif. Cancelling stops ffmpeg. Never throws for ffmpeg's failures: the
    /// result says what went wrong.
    /// </summary>
    public async Task<MovieResult> EncodeAsync(string folder, string name, double fps, int count, int width, int height, IEnumerable<MovieFormat> formats, string movieName, CancellationToken cancel = default)
    {
        var wanted = formats.Distinct().ToList();
        if (wanted.Count == 0) return new MovieResult([], null);
        if (Ffmpeg == null) return new MovieResult([], NotInstalled);
        if (count < 1) return new MovieResult([], "There are no frames to make a movie of.");
        var made = new List<string>();
        foreach (var format in wanted)
        {
            string output = MoviePath(folder, movieName, format);
            var args = format == MovieFormat.Mp4
                ? Mp4Arguments(folder, name, fps, count, width, height, output)
                : GifArguments(folder, name, fps, count, width, output);
            var problem = await Run(args, cancel).ConfigureAwait(false);
            if (problem != null) return new MovieResult(made, $"ffmpeg couldn't make {Path.GetFileName(output)}: {problem}");
            made.Add(output);
        }
        return new MovieResult(made, null);
    }

    /// <summary>Runs ffmpeg, returning null if it worked or what it said if it didn't.</summary>
    private async Task<string?> Run(IReadOnlyList<string> args, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(Ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("it didn't start"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return e.Message; }
        using (process)
        {
            process.StandardInput.Close();
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try { await process.WaitForExitAsync(cancel).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return "stopped.";
            }
            string said = (await errors.ConfigureAwait(false)).Trim();
            await output.ConfigureAwait(false);
            if (process.ExitCode == 0) return null;
            var lines = said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? $"it stopped with code {process.ExitCode}." : string.Join(" ", lines.TakeLast(3));
        }
    }
}
