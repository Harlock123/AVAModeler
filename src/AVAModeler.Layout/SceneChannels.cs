using System.Globalization;
using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// Every animatable channel an item has, by the names agents and Layout's dope sheet use: its nine motion channels,
/// a light's intensity, a camera's zoom, an object's dissolve ("dissolve", 0 solid to 1 gone), morph weights ("morph.Smile") and displacement amplitudes
/// ("displacement.1.amplitude", numbered from 1), and for an item that follows a path, where along it it is
/// ("path.position"). The MCP server's census reads its channel names through this, so a
/// new animatable setting is added here once.
/// </summary>
public static class SceneChannels
{
    /// <summary>The nine motion channels' names, in the order of <see cref="Channel"/>.</summary>
    public static readonly string[] MotionNames = ["position.x", "position.y", "position.z", "heading", "pitch", "bank", "scale.x", "scale.y", "scale.z"];

    public const string MorphPrefix = "morph.", DisplacementPrefix = "displacement.";

    /// <summary>An item's channels, each with its name and envelope.</summary>
    public static IEnumerable<(string Name, Envelope Envelope)> Of(SceneItem item)
    {
        for (int i = 0; i < 9; i++) yield return (MotionNames[i], item.Channels[i]);
        if (item is LightItem l) yield return ("intensity", l.Intensity);
        if (item is CameraItem c) yield return ("zoom", c.Zoom);
        if (item is ObjectItem o)
        {
            yield return (Dissolve, o.Dissolve);
            foreach (var m in o.Morphs) yield return (MorphChannel(m), m.Weight);
            for (int i = 0; i < o.Displacements.Count; i++) yield return (DisplacementChannel(i), o.Displacements[i].Amplitude);
        }
        if (item.Path != null) yield return (PathPosition, item.PathPosition);
    }

    /// <summary>An object's dissolve channel (0 solid, 1 gone).</summary>
    public const string Dissolve = "dissolve";

    /// <summary>The channel of where an item following a path is along it (0 the start, 1 the end).</summary>
    public const string PathPosition = "path.position";

    /// <summary>An item's channel by its exact name, or null.</summary>
    public static Envelope? Find(SceneItem item, string name) => Of(item).FirstOrDefault(c => c.Name == name).Envelope;

    /// <summary>The channels that are animated in the scene's sense: keyed inside its frames (not a never-keyed constant).</summary>
    public static IEnumerable<(string Name, Envelope Envelope)> Keyed(SceneItem item, Scene.Scene scene) =>
        Of(item).Where(c => !Keying.IsConstant(c.Envelope, scene));

    /// <summary>A morph's weight channel: "morph." and its name.</summary>
    public static string MorphChannel(MorphTarget m) => MorphPrefix + m.Name;

    /// <summary>A displacement's amplitude channel, numbered from 1: "displacement.1.amplitude".</summary>
    public static string DisplacementChannel(int index) => string.Create(CultureInfo.InvariantCulture, $"{DisplacementPrefix}{index + 1}.amplitude");
}
