using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using ModelContextProtocol.Server;
using static AVAModeler.Mcp.SceneCensus;

namespace AVAModeler.Mcp;

/// <summary>
/// Motion modifiers (in the spirit of LightWave Layout 5's motion plug-ins): lag, jitter, cycle and oscillate, added,
/// changed and removed on an item through <see cref="LayoutSession.SetModifiers"/>, each call one step on Layout's undo
/// history.
/// </summary>
public sealed partial class ModelerTools
{
    private const string ModifierKinds =
        "lag: follow another item (leader) delay frames behind: the item's position becomes where the leader's pivot was in the world delay frames ago (seen from the item's parent), plus the item's own keyed position as an offset, strength 0 to 1 of it; rotation true adds the leader's rotation then too. " +
        "jitter: smooth, repeatable noise (Perlin, never random) added to position (position_amount, metres per axis either way) and rotation (rotation_amount, degrees of heading, pitch, bank either way), at frequency wobbles per second; another seed gives another wobble. " +
        "cycle (Layout 5's Cyclist): the item's own keys, first key to last, play once as the driver's driver_channel goes from low to high, repeating beyond (a crank turning with a wheel null's heading, a stride with a body's position.z); the driver's value is as it moves, in its parent's space. " +
        "oscillate: adds amplitude · sin(2π(frame − start)/period + phase°) · e^(−damping·(frame − start)) to one channel from the start frame (a bounce, a sway, a wobble dying away).";

    [McpServerTool(Name = "add_motion_modifier", Destructive = true)]
    [Description("Adds a motion modifier to an item, as one undo step: something that moves it beyond its keys, applied after its keys, path and IK, in the order the modifiers are listed (views, renders, bones and glTF export all see it; its keys stay as they are). Kinds: " + ModifierKinds + " Give only the chosen kind's settings; the rest are refused. Returns the item with its modifiers (numbered from 1) and modified_motion (where they put it at the current frame). LightWave scenes (.lws) and motion files can't hold modifiers: the .avascene keeps them.")]
    public Task<string> AddMotionModifier(
        [Description("The item to modify, by name or #index.")] string item,
        [Description("lag, jitter, cycle or oscillate.")] string kind,
        [Description("lag: the item to follow, by name (not the item itself, nor anything it carries).")] string? leader = null,
        [Description("lag: frames behind the leader, 0 or more, fractions allowed (default 5).")] double? delay = null,
        [Description("lag: how much of the leader's motion to take, 0 to 1 (default 1).")] double? strength = null,
        [Description("lag: also add the leader's rotation (default false).")] bool? rotation = null,
        [Description("jitter: [x, y, z] metres each position axis wobbles either way (default [0.1, 0.1, 0.1]).")] double[]? position_amount = null,
        [Description("jitter: [heading, pitch, bank] degrees each wobbles either way (default [0, 0, 0]).")] double[]? rotation_amount = null,
        [Description("jitter: wobbles per second of scene time, more than 0 (default 2).")] double? frequency = null,
        [Description("jitter: a whole number choosing the noise (default 0); the same seed gives the same wobble every time.")] int? seed = null,
        [Description("cycle: the item whose channel drives the cycle, by name (not the item itself).")] string? driver = null,
        [Description("cycle: the driver's channel: position.x, position.y, position.z, heading, pitch, bank, scale.x, scale.y or scale.z (default heading).")] string? driver_channel = null,
        [Description("cycle: the driver's value at the cycle's start (default 0).")] double? low = null,
        [Description("cycle: the driver's value after one whole cycle (default 360), not equal to low.")] double? high = null,
        [Description("oscillate: the channel that swings: position.x … scale.z as for driver_channel (default position.y).")] string? channel = null,
        [Description("oscillate: the swing's size at the start: metres for position, degrees for rotation, a factor for scale (default 0.5).")] double? amplitude = null,
        [Description("oscillate: frames per whole swing, more than 0 (default 30).")] double? period = null,
        [Description("oscillate: where in its swing it starts, degrees (default 0; 90 starts at the top).")] double? phase = null,
        [Description("oscillate: how fast it dies away per frame, 0 or more (default 0: never; 0.05 halves it in about 14 frames).")] double? damping = null,
        [Description("oscillate: the frame it starts at (default the scene's first frame); nothing is added before it.")] double? start = null,
        [Description("Where in the list it goes, from 1 (default: last, applied after the others).")] int? at = null,
        CancellationToken cancel = default) =>
        DoScene("added a motion modifier", (c, l) =>
        {
            var it = Item(l.Scene, item);
            var args = new ModifierArgs(leader, delay, strength, rotation, position_amount, rotation_amount, frequency, seed, driver, driver_channel, low, high,
                channel, amplitude, period, phase, damping, start);
            var m = BuildModifier(l, kind, null, args);
            var list = it.Modifiers.ToList();
            int place = at ?? list.Count + 1;
            if (place < 1 || place > list.Count + 1) throw new ArgumentException($"at is from 1 to {list.Count + 1} ({it.Name} has {list.Count} modifier(s)).");
            list.Insert(place - 1, m);
            l.SetModifiers(it, list);
            return ItemNow(c, l, it, l.Frame);
        }, cancel);

    [McpServerTool(Name = "set_motion_modifier", Destructive = true)]
    [Description("Changes one of an item's motion modifiers (numbered from 1 as get_scene lists them), as one undo step: give only the settings to change, those of its kind (see add_motion_modifier); its kind can't change (remove it and add another). Returns the item with its modifiers.")]
    public Task<string> SetMotionModifier(
        [Description("The item, by name or #index.")] string item,
        [Description("Which modifier, from 1.")] int number,
        [Description("lag: the item to follow, by name.")] string? leader = null,
        [Description("lag: frames behind, 0 or more.")] double? delay = null,
        [Description("lag: 0 to 1.")] double? strength = null,
        [Description("lag: also add the leader's rotation.")] bool? rotation = null,
        [Description("jitter: [x, y, z] metres either way.")] double[]? position_amount = null,
        [Description("jitter: [heading, pitch, bank] degrees either way.")] double[]? rotation_amount = null,
        [Description("jitter: wobbles per second, more than 0.")] double? frequency = null,
        [Description("jitter: the noise's seed, a whole number.")] int? seed = null,
        [Description("cycle: the driving item, by name.")] string? driver = null,
        [Description("cycle: the driver's channel (position.x … scale.z).")] string? driver_channel = null,
        [Description("cycle: the driver's value at the start.")] double? low = null,
        [Description("cycle: the driver's value after one cycle.")] double? high = null,
        [Description("oscillate: the channel that swings (position.x … scale.z).")] string? channel = null,
        [Description("oscillate: metres, degrees or scale factor.")] double? amplitude = null,
        [Description("oscillate: frames per swing, more than 0.")] double? period = null,
        [Description("oscillate: degrees.")] double? phase = null,
        [Description("oscillate: per frame, 0 or more.")] double? damping = null,
        [Description("oscillate: the frame it starts at.")] double? start = null,
        CancellationToken cancel = default) =>
        DoScene("changed a motion modifier", (c, l) =>
        {
            var it = Item(l.Scene, item);
            var list = it.Modifiers.ToList();
            if (list.Count == 0) throw new ArgumentException($"{it.Name} has no motion modifiers: add_motion_modifier adds one.");
            if (number < 1 || number > list.Count) throw new ArgumentException($"{it.Name}'s modifiers are numbered 1 to {list.Count}, not {number}.");
            var args = new ModifierArgs(leader, delay, strength, rotation, position_amount, rotation_amount, frequency, seed, driver, driver_channel, low, high,
                channel, amplitude, period, phase, damping, start);
            if (args.None) throw new ArgumentException($"Give a setting to change (modifier {number} is a {list[number - 1].Kind}).");
            list[number - 1] = BuildModifier(l, list[number - 1].Kind, list[number - 1], args);
            l.SetModifiers(it, list);
            return ItemNow(c, l, it, l.Frame);
        }, cancel);

    [McpServerTool(Name = "remove_motion_modifier", Destructive = true)]
    [Description("Removes one of an item's motion modifiers (by number, from 1, as get_scene lists them) or all of them, as one undo step; its keys play as they are again (apart from any others left). Returns the item.")]
    public Task<string> RemoveMotionModifier(
        [Description("The item, by name or #index.")] string item,
        [Description("Which modifier, from 1. Leave out with all = true.")] int? number = null,
        [Description("Remove every modifier the item has (default false).")] bool all = false,
        CancellationToken cancel = default) =>
        DoScene("removed a motion modifier", (c, l) =>
        {
            var it = Item(l.Scene, item);
            var list = it.Modifiers.ToList();
            if (list.Count == 0) throw new ArgumentException($"{it.Name} has no motion modifiers.");
            if (all == (number != null)) throw new ArgumentException("Give number (which modifier, from 1) or all = true, not both.");
            if (number is int n)
            {
                if (n < 1 || n > list.Count) throw new ArgumentException($"{it.Name}'s modifiers are numbered 1 to {list.Count}, not {n}.");
                list.RemoveAt(n - 1);
            }
            else list.Clear();
            l.SetModifiers(it, list);
            return ItemNow(c, l, it, l.Frame);
        }, cancel);

    /// <summary>The settings an agent gave for a modifier (null: not given).</summary>
    private sealed record ModifierArgs(
        string? Leader, double? Delay, double? Strength, bool? Rotation, double[]? PositionAmount, double[]? RotationAmount, double? Frequency, int? Seed,
        string? Driver, string? DriverChannel, double? Low, double? High, string? Channel, double? Amplitude, double? Period, double? Phase, double? Damping,
        double? Start)
    {
        /// <summary>Each setting's name with whether it was given, by the kind it belongs to.</summary>
        public IEnumerable<(string Kind, string Name, bool Given)> All() =>
        [
            ("lag", "leader", Leader != null), ("lag", "delay", Delay != null), ("lag", "strength", Strength != null), ("lag", "rotation", Rotation != null),
            ("jitter", "position_amount", PositionAmount != null), ("jitter", "rotation_amount", RotationAmount != null),
            ("jitter", "frequency", Frequency != null), ("jitter", "seed", Seed != null),
            ("cycle", "driver", Driver != null), ("cycle", "driver_channel", DriverChannel != null), ("cycle", "low", Low != null), ("cycle", "high", High != null),
            ("oscillate", "channel", Channel != null), ("oscillate", "amplitude", Amplitude != null), ("oscillate", "period", Period != null),
            ("oscillate", "phase", Phase != null), ("oscillate", "damping", Damping != null), ("oscillate", "start", Start != null),
        ];

        public bool None => All().All(a => !a.Given);
    }

    /// <summary>A modifier of a kind from an agent's settings, over an existing one's (or the kind's defaults); checked.</summary>
    private static MotionModifier BuildModifier(LayoutSession l, string kind, MotionModifier? existing, ModifierArgs a)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (kind is not ("lag" or "jitter" or "cycle" or "oscillate")) throw new ArgumentException($"kind is lag, jitter, cycle or oscillate, not \"{kind}\".");
        var stray = a.All().Where(x => x.Given && x.Kind != kind).Select(x => x.Name).ToList();
        if (stray.Count > 0) throw new ArgumentException($"{string.Join(", ", stray)} {(stray.Count == 1 ? "isn't a setting" : "aren't settings")} of a {kind} modifier.");
        var s = l.Scene;
        MotionModifier m;
        switch (kind)
        {
            case "lag":
            {
                var was = existing as LagModifier;
                var leader = a.Leader != null ? Item(s, a.Leader, "leader") : was?.Leader ?? throw new ArgumentException("A lag needs a leader: the item to follow, by name.");
                var lag = (was ?? new LagModifier(leader)) with { Leader = leader };
                m = lag with { Delay = a.Delay ?? lag.Delay, Strength = a.Strength ?? lag.Strength, Rotation = a.Rotation ?? lag.Rotation };
                break;
            }
            case "jitter":
            {
                var j = existing as JitterModifier ?? new JitterModifier();
                m = j with
                {
                    Position = a.PositionAmount != null ? Vector(a.PositionAmount, "position_amount") : j.Position,
                    Rotation = a.RotationAmount != null ? Vector(a.RotationAmount, "rotation_amount") : j.Rotation,
                    Frequency = a.Frequency ?? j.Frequency, Seed = a.Seed ?? j.Seed,
                };
                break;
            }
            case "cycle":
            {
                var was = existing as CycleModifier;
                var driver = a.Driver != null ? Item(s, a.Driver, "driver") : was?.Driver ?? throw new ArgumentException("A cycle needs a driver: the item whose channel plays it, by name.");
                var cy = (was ?? new CycleModifier(driver)) with { Driver = driver };
                m = cy with
                {
                    DriverChannel = a.DriverChannel != null ? MotionChannel(a.DriverChannel, "driver_channel") : cy.DriverChannel,
                    Low = a.Low ?? cy.Low, High = a.High ?? cy.High,
                };
                break;
            }
            default:
            {
                var o = existing as OscillateModifier ?? new OscillateModifier { Start = s.FirstFrame };
                m = o with
                {
                    Channel = a.Channel != null ? MotionChannel(a.Channel, "channel") : o.Channel,
                    Amplitude = a.Amplitude ?? o.Amplitude, Period = a.Period ?? o.Period, Phase = a.Phase ?? o.Phase,
                    Damping = a.Damping ?? o.Damping, Start = a.Start ?? o.Start,
                };
                break;
            }
        }
        return m.Checked();
    }

    /// <summary>One of the nine motion channels by its name (position.x … scale.z).</summary>
    private static Channel MotionChannel(string name, string what)
    {
        int i = Array.FindIndex(ChannelNames, n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new ArgumentException($"{what} is one of {string.Join(", ", ChannelNames)}, not \"{name}\".");
        return (Channel)i;
    }
}
