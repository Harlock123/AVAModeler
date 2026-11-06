using System.Globalization;
using System.Text;

namespace AVAModeler.Scene;

/// <summary>A motion read from a file: the nine channels in <see cref="Channel"/> order, the file's version, and anything that couldn't be read as it should.</summary>
public sealed record MotionLoadResult(IReadOnlyList<Envelope> Channels, int Version, IReadOnlyList<string> Warnings);

/// <summary>
/// LightWave motion files (.mot), as Layout's Load Motion and Save Motion read and write them: one item's nine motion
/// channels (position x, y, z in metres; heading, pitch, bank in degrees; scale x, y, z), with no item, parent or
/// anything else.
/// <para>Version 1 (LightWave 4 and 5): <c>LWMO</c>, <c>1</c>, the channel count (9), the key count, then per key a
/// line of the nine values and a line of "frame linear tension continuity bias" (times in frames; every channel is
/// keyed at the same frames), and optionally <c>EndBehavior n</c> (0 reset, 1 stop, 2 repeat) for all nine after the
/// last key. Version 3 (LightWave 6 on): <c>LWMO</c>, <c>3</c>, <c>NumChannels 9</c>, then per channel
/// <c>Channel n</c> and a <c>{ Envelope … }</c> block (key count, <c>Key value time shape tension continuity bias …</c>
/// lines with times in seconds, <c>Behaviors pre post</c>), as in a version 3 scene.</para>
/// <para>Writing defaults to version 3: it keeps each channel's own keys and its behaviours before and after, so a save
/// and load gives back exactly the same motion. Version 1 is what Layout 4 and 5 read: it keys all nine channels at
/// the same frames (the union of every channel's keys, each channel's value there, so channels keyed at different
/// frames can bend a little between keys), holds one behaviour after the last key for all nine (reset, stop or repeat)
/// and none before the first; a warning says when something didn't fit. Neither version holds path following, motion
/// modifiers, IK or Bézier and Hermite spans (read as TCB keys with no tension, continuity or bias).</para>
/// </summary>
public static class MotionFile
{
    public const string Extension = ".mot";

    /// <summary>The version written unless asked otherwise (see the class notes).</summary>
    public const int DefaultVersion = 3;

    /// <summary>The channels' values when a file leaves them out: 0, and 1 for scale.</summary>
    private static double Default(int channel) => channel >= 6 ? 1 : 0;

    public static MotionLoadResult ReadFile(string path, double framesPerSecond = 30) => Read(File.ReadAllText(path), framesPerSecond);

    /// <summary>
    /// Reads a motion, a version 3 file's times in seconds turned to frames at <paramref name="framesPerSecond"/> (the
    /// scene's rate). Throws <see cref="InvalidDataException"/> if it isn't a motion file version 1 or 3, or is cut short.
    /// </summary>
    public static MotionLoadResult Read(string text, double framesPerSecond = 30)
    {
        if (!(framesPerSecond > 0) || !double.IsFinite(framesPerSecond)) throw new ArgumentException("The frame rate must be more than 0 frames per second.");
        var warnings = new List<string>();
        var warned = new HashSet<string>();
        void Warn(string w)
        {
            if (warned.Add(w)) warnings.Add(w);
        }
        var t = new LwText(text, Warn);
        if (t.Next() != "LWMO") throw new InvalidDataException("Not a LightWave motion file: it doesn't start with LWMO.");
        var v = t.Next();
        if (v == null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            throw new InvalidDataException("The LWMO header has no version number.");
        var channels = new Envelope?[9];
        if (version == 1) ReadV1(t, channels, Warn);
        else if (version == 3) ReadV3(t, channels, framesPerSecond, Warn);
        else throw new InvalidDataException($"Motion file version {version} isn't one this reads (1, LightWave 4 and 5; 3, LightWave 6 on).");
        var missing = Enumerable.Range(0, 9).Where(i => channels[i] == null).ToList();
        if (missing.Count == 9) throw new InvalidDataException("The motion file holds no channels.");
        if (missing.Count > 0)
            Warn($"The motion file has no {string.Join(", ", missing.Select(i => (Channel)i))} channel{(missing.Count == 1 ? "" : "s")}: left at rest (0; scale 1).");
        return new MotionLoadResult(channels.Select((e, i) => e ?? new Envelope(Default(i))).ToArray(), version, warnings);
    }

    private static void ReadV1(LwText t, Envelope?[] channels, Action<string> warn)
    {
        var envelopes = t.V1Envelopes(out bool cut);
        if (envelopes == null) throw new InvalidDataException($"Line {t.Position}: the motion has no channel and key count.");
        if (cut) throw new InvalidDataException($"Line {t.Position}: the motion is cut short before its last key.");
        if (envelopes.Length is < 1 or > 9) throw new InvalidDataException($"A motion has 1 to 9 channels, not {envelopes.Length}.");
        if (envelopes[0].Keys.Count == 0) throw new InvalidDataException("The motion holds no keys.");
        for (int c = 0; c < envelopes.Length; c++) channels[c] = envelopes[c];
        for (var line = t.Next(); line != null; line = t.Next())
        {
            var (key, rest) = LwText.Split(line);
            if (key == "EndBehavior")
            {
                var b = (int)Math.Round(t.Numbers(rest, 1, key)[0]) switch { 0 => EndBehavior.Reset, 2 => EndBehavior.Repeat, _ => EndBehavior.Constant };
                foreach (var e in envelopes) e.PostBehavior = b;
            }
            else warn($"Line {t.Position}: {key} isn't part of a version 1 motion; it is skipped.");
        }
    }

    private static void ReadV3(LwText t, Envelope?[] channels, double framesPerSecond, Action<string> warn)
    {
        var head = t.Next();
        if (head == null || LwText.Split(head).Key != "NumChannels") throw new InvalidDataException($"Line {t.Position}: a version 3 motion starts with NumChannels.");
        for (var line = t.Next(); line != null; line = t.Next())
        {
            var (key, rest) = LwText.Split(line);
            if (key != "Channel")
            {
                if (line.StartsWith('{')) throw new InvalidDataException($"Line {t.Position}: an envelope block comes without its Channel line.");
                warn($"Line {t.Position}: {key} isn't part of a motion; it is skipped.");
                continue;
            }
            if (t.Peek()?.StartsWith('{') != true) throw new InvalidDataException($"Line {t.Position}: Channel {rest} has no envelope block after it.");
            var e = t.V3Envelope();
            LwText.SecondsToFrames(e, framesPerSecond);
            if (!LwText.TryNumber(rest, out double n) || n != Math.Floor(n) || n is < 0 or > 8)
            {
                warn($"Line {t.Position}: channel {rest} isn't one of the nine (0 to 8); it is skipped.");
                continue;
            }
            channels[(int)n] = e;
        }
    }

    /// <summary>Writes an item's nine motion channels (its keys: path following and modifiers aren't motion files', and a warning says so).</summary>
    public static string Write(SceneItem item, double framesPerSecond, int version = DefaultVersion, List<string>? warnings = null)
    {
        if (item.Path != null)
            warnings?.Add($"{item.Name} follows a path, which a motion file has no place for: only its keys are written (bake the path first to keep the motion).");
        if (item.Modifiers.Count > 0)
            warnings?.Add($"{item.Name}'s motion modifiers ({string.Join(", ", item.Modifiers.Select(m => m.Kind))}) have no place in a motion file: only its keys are written.");
        return Write(item.Channels, framesPerSecond, version, warnings);
    }

    public static void WriteFile(SceneItem item, string path, double framesPerSecond, int version = DefaultVersion, List<string>? warnings = null) =>
        File.WriteAllText(path, Write(item, framesPerSecond, version, warnings));

    /// <summary>Writes nine channels (in <see cref="Channel"/> order) as a motion file of version 1 or 3, a version 3 file's times in seconds at <paramref name="framesPerSecond"/>.</summary>
    public static string Write(IReadOnlyList<Envelope> channels, double framesPerSecond, int version = DefaultVersion, List<string>? warnings = null)
    {
        if (channels.Count != 9) throw new ArgumentException($"A motion has nine channels, not {channels.Count}.");
        if (!(framesPerSecond > 0) || !double.IsFinite(framesPerSecond)) throw new ArgumentException("The frame rate must be more than 0 frames per second.");
        var text = new StringBuilder();
        void L(string line = "") => text.Append(line).Append('\n');
        L("LWMO");
        if (version == 3)
        {
            L("3");
            L();
            L("NumChannels 9");
            for (int c = 0; c < 9; c++)
            {
                L($"Channel {c}");
                LwsWriter.V3Envelope(text, channels[c], framesPerSecond);
            }
            return text.ToString();
        }
        if (version != 1) throw new ArgumentException($"A motion file is version 1 (LightWave 4 and 5) or 3 (LightWave 6 on), not {version}.");
        L("1");
        L("9");
        // The frames keyed on any animated channel (a constant's one key, at frame 0 usually, isn't a key frame).
        var frames = new SortedSet<double>();
        foreach (var e in channels.Where(e => e.Keys.Count > 1))
            foreach (var k in e.Keys)
                if (!frames.Any(f => Math.Abs(f - k.Frame) < 1e-9)) frames.Add(k.Frame);
        if (frames.Count == 0) frames.Add(0);
        bool uneven = channels.Any(e => e.Keys.Count > 1 && (e.Keys.Count != frames.Count || frames.Any(f => e.IndexOf(f) < 0)));
        bool shapes = false;
        L(frames.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var f in frames)
        {
            L(string.Join(" ", channels.Select(e => N(e.Evaluate(f)))));
            // One spline line for all nine: the first animated channel keyed here gives it.
            var keyed = channels.Where(e => e.Keys.Count > 1).Select(e => e.IndexOf(f) is var i and >= 0 ? e.Keys[i] : (EnvelopeKey?)null).Where(k => k != null).Select(k => k!.Value).ToList();
            var k0 = keyed.Count > 0 ? keyed[0] : new EnvelopeKey(f, 0);
            if (keyed.Any(k => k.Tension != k0.Tension || k.Continuity != k0.Continuity || k.Bias != k0.Bias || k.Linear != k0.Linear)) shapes = true;
            L($"{N(f)} {(k0.Linear ? 1 : 0)} {N(k0.Tension)} {N(k0.Continuity)} {N(k0.Bias)}");
        }
        var post = channels.Where(e => e.Keys.Count > 1).Select(e => e.PostBehavior).Distinct().ToList();
        var end = post.Count == 0 ? EndBehavior.Constant : post[0];
        if (post.Count > 1 || end is not (EndBehavior.Reset or EndBehavior.Constant or EndBehavior.Repeat))
        {
            warnings?.Add("A version 1 motion has one behaviour after the last key for all nine channels, reset, stop or repeat: " +
                $"the channels' {string.Join(", ", post.Select(b => b.ToString().ToLowerInvariant()))} became {(end is EndBehavior.Reset or EndBehavior.Repeat ? end.ToString().ToLowerInvariant() : "stop")}.");
            if (end is not (EndBehavior.Reset or EndBehavior.Repeat)) end = EndBehavior.Constant;
        }
        if (channels.Any(e => e.Keys.Count > 1 && e.PreBehavior != EndBehavior.Constant))
            warnings?.Add("A version 1 motion has no behaviour before the first key: it holds there (save version 3 to keep it).");
        if (uneven)
            warnings?.Add("A version 1 motion keys all nine channels at the same frames: channels keyed at different frames were keyed at every one of them, which can bend the curves a little between keys (save version 3 to keep them exactly).");
        if (shapes)
            warnings?.Add("A version 1 motion has one tension, continuity, bias and linear flag per frame for all nine channels: where the channels' keys differed, the first channel's was used.");
        L($"EndBehavior {end switch { EndBehavior.Reset => 0, EndBehavior.Repeat => 2, _ => 1 }}");
        return text.ToString();
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Replaces an item's nine motion channels with copies of these (in <see cref="Channel"/> order), as one change to its scene.</summary>
    public static void Apply(SceneItem item, IReadOnlyList<Envelope> channels)
    {
        if (channels.Count != 9) throw new ArgumentException($"A motion has nine channels, not {channels.Count}.");
        void Copy()
        {
            for (int c = 0; c < 9; c++) item.Channels[c].CopyFrom(channels[c]);
        }
        if (item.Scene is { } s) s.Batch(Copy);
        else Copy();
    }
}
