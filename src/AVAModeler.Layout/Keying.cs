using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// How settings become keys, with one rule written down: Layout's controls and AI agents' MCP tools (which describe
/// it) both follow it. A channel that has
/// never been keyed holds a constant: one key outside the scene's frames (new items keep it at frame 0). Keying such a
/// channel at a frame replaces that constant, so an animation starts at the first frame keyed rather than being pulled
/// about by a key at frame 0 nobody chose. Any other key is only replaced at its own frame, keeping its tension,
/// continuity, bias and linear flag unless new ones are given.
/// </summary>
public static class Keying
{
    /// <summary>Whether a channel holds one value throughout and was never keyed inside the scene's frames.</summary>
    public static bool IsConstant(Envelope e, Scene.Scene scene) =>
        e.Keys.Count == 0 || e.Keys.Count == 1 && (e.Keys[0].Frame < scene.FirstFrame || e.Keys[0].Frame > scene.LastFrame);

    /// <summary>Whether a channel's value changes over time (it has two keys or more).</summary>
    public static bool IsAnimated(Envelope e) => e.Keys.Count > 1;

    /// <summary>
    /// Sets a channel's value at a frame. With <paramref name="key"/>, a key there (replacing the constant of a channel
    /// never keyed before). Without, the value changes without a new key: a channel with one key or none takes the
    /// value throughout; an animated channel changes its key at the frame, and throws if it has none there.
    /// </summary>
    public static void Set(Envelope e, Scene.Scene scene, double frame, double value, bool key, string what)
    {
        if (key)
        {
            Key(e, scene, new EnvelopeKey(frame, value), keepShape: true);
            return;
        }
        if (e.Keys.Count == 0) e.SetConstant(value);
        else if (e.Keys.Count == 1) e.SetKey(0, e.Keys[0] with { Value = value });
        else if (e.IndexOf(frame) is var i and >= 0) e.SetKey(i, e.Keys[i] with { Value = value });
        else
            throw new InvalidOperationException($"{what} is animated (keys at {Frames(e)}) and has no key at frame {frame:0.###}. Give key true to add one there, or one of those frames to change it.");
    }

    /// <summary>
    /// Puts a key in a channel: replaces the constant of a channel never keyed before, else any key at the same frame.
    /// With <paramref name="keepShape"/>, a key already at that frame keeps its tension, continuity, bias and linear
    /// flag, and only its value changes.
    /// </summary>
    public static void Key(Envelope e, Scene.Scene scene, EnvelopeKey key, bool keepShape)
    {
        if (IsConstant(e, scene))
        {
            if (e.Keys.Count == 1) e.SetKey(0, key);
            else e.AddKey(key);
            return;
        }
        int i = e.IndexOf(key.Frame);
        if (i >= 0 && keepShape) e.SetKey(i, e.Keys[i] with { Value = key.Value });
        else e.AddKey(key);
    }

    /// <summary>A channel's key frames for a message, such as "1, 30 and 60".</summary>
    public static string Frames(Envelope e)
    {
        var f = e.Keys.Select(k => k.Frame.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return f.Count <= 1 ? string.Join("", f) : string.Join(", ", f[..^1]) + " and " + f[^1];
    }
}
