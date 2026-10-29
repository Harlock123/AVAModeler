using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>Joining frames into movies with ffmpeg: its arguments, finding it, and what happens without it.</summary>
public class MovieEncoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avamodeler-movie-" + Guid.NewGuid().ToString("N")[..8]);

    public MovieEncoderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    private static readonly string[] Input = ["-y", "-loglevel", "error", "-framerate", "30", "-start_number", "1", "-i", Path.Combine("out", "frame%04d.png"), "-frames:v", "120"];

    [Fact]
    public void The_mp4_arguments_are_the_carbon_atom_samples()
    {
        var args = MovieEncoder.Mp4Arguments("out", "frame", 30, 120, 960, 540, "atom.mp4");
        Assert.Equal([.. Input, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", "-movflags", "+faststart", "atom.mp4"], args);
        // An odd size is padded to even, which H.264 in yuv420p needs.
        var odd = MovieEncoder.Mp4Arguments("out", "frame", 30, 120, 961, 541, "atom.mp4");
        Assert.Equal("-vf", odd[Input.Length]);
        Assert.Equal("pad=ceil(iw/2)*2:ceil(ih/2)*2", odd[Input.Length + 1]);
        Assert.Equal("23.976", MovieEncoder.Mp4Arguments("out", "frame", 23.976, 1, 2, 2, "a.mp4")[4]);
    }

    [Fact]
    public void The_gif_arguments_are_the_carbon_atom_samples()
    {
        var args = MovieEncoder.GifArguments("out", "frame", 30, 120, 960, "atom.gif");
        Assert.Equal([.. Input, "-vf",
            "scale=640:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
            "-loop", "0", "atom.gif"], args);
        // A small animation isn't scaled up.
        Assert.StartsWith("scale=32:-1:", MovieEncoder.GifArguments("out", "frame", 30, 3, 32, "a.gif")[Input.Length + 1]);
    }

    [Fact]
    public void Ffmpeg_is_found_on_the_path()
    {
        string bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        Assert.Null(MovieEncoder.Find("", windows: false));
        Assert.Null(MovieEncoder.Find(bin, windows: false));
        string fake = Path.Combine(bin, "ffmpeg");
        File.WriteAllText(fake, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            // Not executable: not it.
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Null(MovieEncoder.Find(bin, windows: false));
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(fake, MovieEncoder.Find($"/nowhere:{bin}", windows: false));
        }
        File.WriteAllText(Path.Combine(bin, "ffmpeg.exe"), "");
        Assert.Equal(Path.Combine(bin, "ffmpeg.exe"), MovieEncoder.Find($"C:\\nowhere;{bin}", windows: true));
    }

    [Fact]
    public async Task Without_ffmpeg_it_says_to_install_it_and_makes_nothing()
    {
        var encoder = new MovieEncoder(null);
        Assert.False(encoder.Available);
        var result = await encoder.EncodeAsync(_dir, "frame", 30, 3, 32, 18, [MovieFormat.Mp4, MovieFormat.Gif], "atom", TestContext.Current.CancellationToken);
        Assert.Empty(result.Files);
        Assert.Equal(MovieEncoder.NotInstalled, result.Problem);
        Assert.Contains("Install ffmpeg", result.Problem);
        Assert.Empty(Directory.GetFiles(_dir));
        // Nothing asked for: nothing to say.
        Assert.Null((await encoder.EncodeAsync(_dir, "frame", 30, 3, 32, 18, [], "atom", TestContext.Current.CancellationToken)).Problem);
    }

    [Fact]
    public async Task An_ffmpeg_that_fails_is_reported()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/false")) Assert.Skip("Needs /bin/false.");
        var result = await new MovieEncoder("/bin/false").EncodeAsync(_dir, "frame", 30, 3, 32, 18, [MovieFormat.Mp4], "atom", TestContext.Current.CancellationToken);
        Assert.Empty(result.Files);
        Assert.Contains("couldn't make atom.mp4", result.Problem);
    }

    [Fact]
    public async Task Tiny_frames_become_an_mp4_and_a_gif()
    {
        var encoder = MovieEncoder.FromPath();
        if (!encoder.Available) Assert.Skip("ffmpeg isn't installed here.");
        for (int n = 1; n <= 4; n++)
        {
            var rgba = new byte[32 * 18 * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = (byte)(n * 60);
                rgba[i + 1] = (byte)(i / 4 % 32 * 8);
                rgba[i + 2] = 80;
                rgba[i + 3] = 255;
            }
            Png.Save(Path.Combine(_dir, $"frame{n:0000}.png"), 32, 18, rgba);
        }
        // Three frames asked for: the fourth (left from an earlier run) isn't used.
        var result = await encoder.EncodeAsync(_dir, "frame", 30, 3, 32, 18, [MovieFormat.Mp4, MovieFormat.Gif], "atom", TestContext.Current.CancellationToken);
        Assert.Null(result.Problem);
        Assert.Equal([Path.Combine(_dir, "atom.mp4"), Path.Combine(_dir, "atom.gif")], result.Files);
        var mp4 = File.ReadAllBytes(result.Files[0]);
        Assert.Equal("ftyp"u8.ToArray(), mp4[4..8]);
        var gif = File.ReadAllBytes(result.Files[1]);
        Assert.Equal("GIF89a"u8.ToArray(), gif[..6]);
        Assert.Equal(32, gif[6] | gif[7] << 8);
        Assert.Equal(18, gif[8] | gif[9] << 8);
        // Loops forever: the NETSCAPE2.0 extension with a count of 0.
        int loop = IndexOf(gif, "NETSCAPE2.0"u8.ToArray());
        Assert.True(loop > 0);
        Assert.Equal(0, gif[loop + 13] | gif[loop + 14] << 8);
        // faststart puts the index (moov) before the pictures (mdat).
        Assert.True(IndexOf(mp4, "moov"u8.ToArray()) < IndexOf(mp4, "mdat"u8.ToArray()));
    }

    private static int IndexOf(byte[] data, byte[] what) => data.AsSpan().IndexOf(what);
}
