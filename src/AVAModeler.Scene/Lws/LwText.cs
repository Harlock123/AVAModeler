using System.Globalization;

namespace AVAModeler.Scene;

/// <summary>
/// LightWave's text files (scenes, .lws, and motions, .mot) read line by line: trimmed lines, blank ones skipped, and
/// the envelopes both kinds of file hold, version 1's (LightWave 4–5: a channel count, a key count, then per key a line
/// of values and a line of "frame linear tension continuity bias", times in frames) and version 3's (LightWave 6 on: a
/// <c>{ Envelope … }</c> block per channel, times in seconds, left as they are for the caller to convert).
/// <see cref="LwsReader"/> and <see cref="MotionFile"/> share it.
/// </summary>
internal sealed class LwText(string text, Action<string> warn)
{
    private readonly string[] _lines = text.Split('\n').Select(l => l.Trim()).ToArray();

    /// <summary>The number of the line last read (1 for the first), for messages.</summary>
    public int Position { get; private set; }

    public string? Next()
    {
        while (Position < _lines.Length)
        {
            var l = _lines[Position++];
            if (l.Length > 0) return l;
        }
        return null;
    }

    public string? Peek()
    {
        for (int i = Position; i < _lines.Length; i++)
            if (_lines[i].Length > 0) return _lines[i];
        return null;
    }

    public static (string Key, string Tail) Split(string line)
    {
        int i = line.IndexOfAny([' ', '\t']);
        return i < 0 ? (line, "") : (line[..i], line[(i + 1)..].Trim());
    }

    public static string[] Tokens(string s) => s.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    public static bool TryNumber(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>A setting's numbers from the rest of its line, with a warning (and zeros) if there aren't enough.</summary>
    public double[] Numbers(string rest, int count, string key)
    {
        var t = Tokens(rest);
        var r = new double[count];
        for (int i = 0; i < count; i++)
            if (i >= t.Length || !TryNumber(t[i], out r[i]))
            {
                warn($"Line {Position}: {key} needs {count} number(s).");
                break;
            }
        return r;
    }

    /// <summary>Reads numbers from as many lines as it takes; null if a line holds something else first.</summary>
    public double[]? ReadNumbers(int count)
    {
        var r = new List<double>(count);
        while (r.Count < count)
        {
            var l = Peek();
            if (l == null) return null;
            var t = Tokens(l);
            if (t.Length == 0 || !TryNumber(t[0], out _)) return null;
            Next();
            foreach (var s in t)
                if (r.Count < count && TryNumber(s, out double v)) r.Add(v);
        }
        return [.. r];
    }

    /// <summary>
    /// A version 1 envelope: channel count, key count, then per key a line of values and a line of "frame linear tension
    /// continuity bias". Null if there's no channel and key count; <paramref name="cut"/> says whether the keys ended
    /// early (those read so far are kept). The caller decides what to say about either.
    /// </summary>
    public Envelope[]? V1Envelopes(out bool cut)
    {
        cut = false;
        var head = ReadNumbers(2);
        if (head == null) return null;
        int channels = (int)head[0], keys = (int)head[1];
        var envelopes = Enumerable.Range(0, Math.Max(0, channels)).Select(_ => new Envelope()).ToArray();
        for (int k = 0; k < keys; k++)
        {
            var values = ReadNumbers(channels);
            var spline = ReadNumbers(5);
            if (values == null || spline == null)
            {
                cut = true;
                break;
            }
            for (int c = 0; c < channels; c++)
                envelopes[c].AddKey(new EnvelopeKey(spline[0], values[c], spline[2], spline[3], spline[4], spline[1] != 0));
        }
        return envelopes;
    }

    /// <summary>
    /// A version 3 envelope block: <c>{ Envelope</c>, the key count, <c>Key</c> lines, <c>Behaviors</c>, <c>}</c>. Its
    /// key times are as written (seconds).
    /// </summary>
    public Envelope V3Envelope()
    {
        var e = new Envelope();
        var open = Next();
        if (open == null || !open.StartsWith('{'))
        {
            warn($"Line {Position}: expected an envelope block.");
            return e;
        }
        int depth = 1;
        while (depth > 0 && Next() is { } l)
        {
            if (l.StartsWith('{')) { depth++; continue; }
            if (l.StartsWith('}')) { depth--; continue; }
            if (depth > 1) continue;
            var (key, rest) = Split(l);
            if (key == "Key")
            {
                var t = Tokens(rest).Select(s => TryNumber(s, out double v) ? v : 0).ToArray();
                if (t.Length < 2)
                {
                    warn($"Line {Position}: a key needs a value and a time.");
                    continue;
                }
                double At(int i) => i < t.Length ? t[i] : 0;
                int shape = (int)At(2);
                if (shape == 5) warn("2D Bézier keys are read as TCB keys with no tension, continuity or bias.");
                // p1 to p3 are tension, continuity and bias; p4 and p5 the incoming and outgoing tangents, which
                // AVA Modeler reads as slopes in value per second (they're turned into value per frame with the times).
                bool tcb = shape is 0 or 1 or 2;
                var k = new EnvelopeKey(t[1], t[0], tcb ? At(3) : 0, tcb ? At(4) : 0, tcb ? At(5) : 0, shape == 3,
                    InSlope: shape != 5 ? At(6) : 0, OutSlope: shape != 5 ? At(7) : 0);
                e.AddKey(shape switch
                {
                    1 or 2 => k.WithSpan(SpanKind.Bezier),
                    4 => k.WithSpan(SpanKind.Stepped),
                    _ => k,
                });
            }
            else if (key == "Behaviors")
            {
                var b = Numbers(rest, 2, key);
                e.PreBehavior = Behavior((int)b[0]);
                e.PostBehavior = Behavior((int)b[1]);
            }
        }
        return e;
    }

    /// <summary>A version 3 behaviour code (0 reset … 5 linear), constant for anything else.</summary>
    public static EndBehavior Behavior(int code) => code is >= 0 and <= 5 ? (EndBehavior)code : EndBehavior.Constant;

    /// <summary>
    /// Keys read in seconds turned to frames at a rate, any that land within rounding of a whole frame snapped to it.
    /// </summary>
    public static void SecondsToFrames(Envelope e, double framesPerSecond)
    {
        e.ScaleTime(framesPerSecond);
        // Seconds rarely convert back to exact frames; snap what is a whole frame to within rounding.
        for (int k = 0; k < e.Keys.Count; k++)
        {
            var key = e.Keys[k];
            double whole = Math.Round(key.Frame);
            if (whole != key.Frame && Math.Abs(whole - key.Frame) < 1e-6) e.SetKey(k, key with { Frame = whole });
        }
    }
}
