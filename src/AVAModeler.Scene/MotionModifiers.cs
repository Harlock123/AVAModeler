using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>An item's pose as it moves at a frame: position (metres, in its parent's space), heading, pitch and bank (degrees) and scale.</summary>
public readonly record struct MotionPose(Vec3 Position, Vec3 Rotation, Vec3 Scale)
{
    /// <summary>One of the nine channels, by <see cref="Channel"/>.</summary>
    public double this[Channel channel] => (int)channel switch
    {
        < 3 and var i => Position[i],
        < 6 and var i => Rotation[i - 3],
        var i => Scale[i - 6],
    };

    /// <summary>A copy with an amount added to one channel.</summary>
    public MotionPose Add(Channel channel, double amount)
    {
        int i = (int)channel;
        var d = new Vec3(i % 3 == 0 ? amount : 0, i % 3 == 1 ? amount : 0, i % 3 == 2 ? amount : 0);
        return i < 3 ? this with { Position = Position + d } : i < 6 ? this with { Rotation = Rotation + d } : this with { Scale = Scale + d };
    }
}

/// <summary>
/// Something that changes how an item moves beyond its keys, in the spirit of LightWave Layout 5's motion plug-ins
/// (<see cref="SceneItem.Modifiers"/>, applied in order after keys, path following and IK by <see cref="MotionSolver"/>,
/// so views, renders, deformation and exports all see it). Immutable records: change an item's by setting a new list.
/// The kinds: <see cref="LagModifier"/> (follow another item a few frames behind), <see cref="JitterModifier"/> (smooth
/// noise), <see cref="CycleModifier"/> (play the item's keys by another item's channel, as Layout 5's Cyclist) and
/// <see cref="OscillateModifier"/> (a damped sine on one channel).
/// </summary>
public abstract record MotionModifier
{
    /// <summary>The kind's name as files and agents give it: lag, jitter, cycle, oscillate.</summary>
    public abstract string Kind { get; }

    /// <summary>The other item this one takes its motion from (the lag's leader, the cycle's driver), or null.</summary>
    public virtual SceneItem? Reference => null;

    /// <summary>These settings, or an <see cref="ArgumentException"/> saying what's wrong with them (the reference aside: see <see cref="Problem"/>).</summary>
    public abstract MotionModifier Checked();

    /// <summary>A copy referring to another item instead (for copies of scenes); null if the reference can't be kept.</summary>
    internal virtual MotionModifier? Relinked(Func<SceneItem, SceneItem?> to) => this;

    /// <summary>The pose after this modifier, from the pose before it.</summary>
    internal abstract MotionPose Apply(SceneItem item, double frame, MotionPose pose);

    /// <summary>
    /// Why <paramref name="modifier"/> can't be one of <paramref name="item"/>'s modifiers, or null if it can: the item it
    /// refers to must be in the item's scene and not the item itself; a lag's leader may not move with the item (it would
    /// chase itself); and neither may already take its own motion from the item (a loop). Loops further round are cut
    /// where the scene is worked out: the keys stand in.
    /// </summary>
    public static string? Problem(SceneItem item, MotionModifier modifier)
    {
        if (modifier.Reference is not { } other) return null;
        string role = modifier is LagModifier ? "lag behind" : "drive";
        if (other == item) return $"{item.Name} can't {(modifier is LagModifier ? "lag behind itself" : "drive itself")}: choose another item.";
        if (item.Scene != null && other.Scene != item.Scene) return $"{other.Name} isn't in {item.Name}'s scene.";
        if (modifier is LagModifier)
        {
            for (var a = other.Parent; a != null; a = a.Parent)
                if (a == item) return $"{other.Name} moves with {item.Name}, so {item.Name} would chase itself: choose an item it doesn't carry.";
            if (other is Bone b && (b.Owner == item || b.Owner.Ancestors().Contains(item))) return $"{other.Name} is a bone of {b.Owner.Name}, which moves with {item.Name}.";
        }
        if (other.Modifiers.Any(m => m.Reference == item))
            return $"{other.Name} already takes its motion from {item.Name}, so {item.Name} can't {role} {other.Name} too (they would go round in a loop).";
        return null;
    }

    internal static void Finite(double v, string what)
    {
        if (!double.IsFinite(v)) throw new ArgumentException($"{what} must be a number.");
    }

    internal static void Finite(Vec3 v, string what)
    {
        if (!double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Z)) throw new ArgumentException($"{what} must be three numbers.");
    }
}

/// <summary>
/// Follow another item with a delay (Layout 5's Lag): the item's position becomes where <see cref="Leader"/>'s pivot was
/// in the world <see cref="Delay"/> frames ago (fractions allowed), seen from the item's parent as it is now, plus the
/// item's own position as an offset, the leader's part scaled by <see cref="Strength"/> (0 none, 1 all). With
/// <see cref="Rotation"/> the leader's world rotation then (as heading, pitch and bank in the item's parent's space) is
/// added to the item's own rotation the same way. Immutable: change it with <c>with</c>.
/// </summary>
public sealed record LagModifier(SceneItem Leader) : MotionModifier
{
    public override string Kind => "lag";
    public override SceneItem? Reference => Leader;

    /// <summary>How many frames behind the leader the item is, 0 or more (fractions allowed).</summary>
    public double Delay { get; init; } = 5;

    /// <summary>How much of the leader's motion is taken, 0 to 1.</summary>
    public double Strength { get; init; } = 1;

    /// <summary>Also take the leader's rotation (added to the item's own).</summary>
    public bool Rotation { get; init; }

    public override MotionModifier Checked()
    {
        if (!double.IsFinite(Delay) || Delay < 0) throw new ArgumentException("A lag's delay must be 0 frames or more.");
        if (!double.IsFinite(Strength) || Strength < 0 || Strength > 1) throw new ArgumentException("A lag's strength must be from 0 to 1.");
        return this;
    }

    internal override MotionModifier? Relinked(Func<SceneItem, SceneItem?> to) => to(Leader) is { } l ? this with { Leader = l } : null;

    internal override MotionPose Apply(SceneItem item, double frame, MotionPose pose)
    {
        if (Leader.Scene != item.Scene || item.Scene == null) return pose;
        double then = frame - Delay;
        var leader = Leader.WorldMatrix(then);
        var toParent = item.TransformParent is { } parent ? parent.WorldMatrix(frame).Inverted() : Mat4.Identity;
        var position = pose.Position + Strength * toParent.TransformPoint(leader.TransformPoint(Leader.Pivot));
        var rotation = pose.Rotation;
        if (Rotation) rotation += Strength * Rotations.FromMatrix(toParent * leader);
        return pose with { Position = position, Rotation = rotation };
    }
}

/// <summary>
/// Smooth noise added to an item's motion (Layout 5's Jitter, but smooth rather than frame to frame): each position axis
/// moves up to about <see cref="Position"/> metres and each rotation channel up to about <see cref="Rotation"/>
/// degrees either way, at <see cref="Frequency"/> wobbles per second of the scene's time. It is Perlin noise
/// (<see cref="Noise"/>, a fixed table) along time, a different track per channel and per <see cref="Seed"/>, so it
/// repeats exactly every time and on every machine. Immutable: change it with <c>with</c>.
/// </summary>
public sealed record JitterModifier : MotionModifier
{
    public override string Kind => "jitter";

    /// <summary>The most each position axis moves either way, in metres (about; noise rarely reaches it).</summary>
    public Vec3 Position { get; init; } = new(0.1, 0.1, 0.1);

    /// <summary>The most heading, pitch and bank turn either way, in degrees.</summary>
    public Vec3 Rotation { get; init; }

    /// <summary>How fast it wobbles: noise cycles per second of scene time (more than 0).</summary>
    public double Frequency { get; init; } = 2;

    /// <summary>Which noise: another seed gives another, equally repeatable, wobble.</summary>
    public int Seed { get; init; }

    public override MotionModifier Checked()
    {
        Finite(Position, "Jitter's position amount");
        Finite(Rotation, "Jitter's rotation amount");
        if (!double.IsFinite(Frequency) || Frequency <= 0) throw new ArgumentException("Jitter's frequency must be more than 0 cycles per second.");
        return this;
    }

    /// <summary>The noise on one channel (0–5: position x, y, z, heading, pitch, bank) at a time in seconds, about −1 to 1.</summary>
    public double NoiseAt(int channel, double seconds)
    {
        // Off the lattice in y and z (Perlin noise is 0 at whole numbers), a track per channel and per seed.
        double y = 0.37 + 7.13 * channel;
        double z = 0.61 + 3.31 * (((Seed % 1000) + 1000) % 1000);
        return Noise.Perlin(seconds * Frequency + 0.5, y, z);
    }

    internal override MotionPose Apply(SceneItem item, double frame, MotionPose pose)
    {
        double t = frame / (item.Scene?.FramesPerSecond ?? 30);
        var p = new Vec3(Position.X * NoiseAt(0, t), Position.Y * NoiseAt(1, t), Position.Z * NoiseAt(2, t));
        var r = new Vec3(Rotation.X * NoiseAt(3, t), Rotation.Y * NoiseAt(4, t), Rotation.Z * NoiseAt(5, t));
        return pose with { Position = pose.Position + p, Rotation = pose.Rotation + r };
    }
}

/// <summary>
/// Drives an item's keyed motion from another item's channel, as Layout 5's Cyclist did: as <see cref="Driver"/>'s
/// <see cref="DriverChannel"/> (as it moves, in its parent's space) goes from <see cref="Low"/> to <see cref="High"/>,
/// the item plays its own keys once through, from its first key to its last (over all nine channels), repeating
/// beyond either end (the key range is over its animated channels, those with two keys or more); a wheel null's
/// heading turning a crank's spin, a walker's position.z its stride. Its keyed values
/// at the frame are swapped for its keyed values at that time; anything else moving it (a path, IK, modifiers before
/// this one) still adds. With no animated channel there's nothing to play and it changes nothing. Immutable.
/// </summary>
public sealed record CycleModifier(SceneItem Driver) : MotionModifier
{
    public override string Kind => "cycle";
    public override SceneItem? Reference => Driver;

    /// <summary>The driver's channel whose value plays the cycle.</summary>
    public Channel DriverChannel { get; init; } = Channel.Heading;

    /// <summary>The driver's value where the cycle starts (its first key).</summary>
    public double Low { get; init; }

    /// <summary>The driver's value where the cycle has gone once round (back at the start); not equal to <see cref="Low"/>.</summary>
    public double High { get; init; } = 360;

    public override MotionModifier Checked()
    {
        Finite(Low, "A cycle's low value");
        Finite(High, "A cycle's high value");
        if (Low == High) throw new ArgumentException("A cycle's low and high values must differ: the driver goes from one to the other for one cycle.");
        if (!Enum.IsDefined(DriverChannel)) throw new ArgumentException("A cycle's driver channel must be one of the nine motion channels.");
        return this;
    }

    internal override MotionModifier? Relinked(Func<SceneItem, SceneItem?> to) => to(Driver) is { } d ? this with { Driver = d } : null;

    /// <summary>The first and last key frames over the item's animated channels (two keys or more), or null if none is.</summary>
    public static (double First, double Last)? KeyRange(SceneItem item)
    {
        double first = double.PositiveInfinity, last = double.NegativeInfinity;
        foreach (var c in item.Channels.Where(c => c.Keys.Count > 1))
            foreach (var k in c.Keys)
            {
                first = Math.Min(first, k.Frame);
                last = Math.Max(last, k.Frame);
            }
        return last > first ? (first, last) : null;
    }

    /// <summary>The time (frame) of the item's keys the driver's value plays, or null with no key range.</summary>
    public double? DrivenFrame(SceneItem item, double frame)
    {
        if (KeyRange(item) is not { } range || Driver.Scene != item.Scene || item.Scene == null) return null;
        var d = new MotionPose(Driver.MotionPositionAt(frame), Driver.MotionRotationAt(frame), Driver.MotionScaleAt(frame));
        double u = (d[DriverChannel] - Low) / (High - Low);
        if (!double.IsFinite(u)) return null;
        u -= Math.Floor(u);
        return range.First + u * (range.Last - range.First);
    }

    internal override MotionPose Apply(SceneItem item, double frame, MotionPose pose)
    {
        if (DrivenFrame(item, frame) is not { } t) return pose;
        return new MotionPose(
            pose.Position + (item.PositionAt(t) - item.PositionAt(frame)),
            pose.Rotation + (item.RotationAt(t) - item.RotationAt(frame)),
            pose.Scale + (item.ScaleAt(t) - item.ScaleAt(frame)));
    }
}

/// <summary>
/// A damped wave on one motion channel (Layout 5's Oscillator): from <see cref="Start"/> on, it adds
/// amplitude · sin(2π(frame − start) / period + phase) · e^(−damping · (frame − start)) to <see cref="Channel"/>
/// (metres for position, degrees for rotation, a factor for scale); before the start it adds nothing. Immutable.
/// </summary>
public sealed record OscillateModifier : MotionModifier
{
    public override string Kind => "oscillate";

    /// <summary>The channel that swings.</summary>
    public Channel Channel { get; init; } = Channel.PositionY;

    /// <summary>The swing's size at the start: metres, degrees or scale.</summary>
    public double Amplitude { get; init; } = 0.5;

    /// <summary>Frames for one whole swing, more than 0.</summary>
    public double Period { get; init; } = 30;

    /// <summary>Where in its swing it starts, in degrees (90 starts at the top).</summary>
    public double Phase { get; init; }

    /// <summary>How fast the swing dies away, per frame, 0 or more (0: never; 0.05 halves it in about 14 frames).</summary>
    public double Damping { get; init; }

    /// <summary>The frame it starts swinging at.</summary>
    public double Start { get; init; }

    public override MotionModifier Checked()
    {
        Finite(Amplitude, "An oscillation's amplitude");
        Finite(Phase, "An oscillation's phase");
        Finite(Start, "An oscillation's start frame");
        if (!double.IsFinite(Period) || Period <= 0) throw new ArgumentException("An oscillation's period must be more than 0 frames.");
        if (!double.IsFinite(Damping) || Damping < 0) throw new ArgumentException("An oscillation's damping must be 0 or more (per frame).");
        if (!Enum.IsDefined(Channel)) throw new ArgumentException("An oscillation's channel must be one of the nine motion channels.");
        return this;
    }

    /// <summary>What it adds to its channel at a frame.</summary>
    public double At(double frame)
    {
        if (frame < Start) return 0;
        double t = frame - Start;
        return Amplitude * Math.Sin(2 * Math.PI * t / Period + Phase * Math.PI / 180) * Math.Exp(-Damping * t);
    }

    internal override MotionPose Apply(SceneItem item, double frame, MotionPose pose) => pose.Add(Channel, At(frame));
}
