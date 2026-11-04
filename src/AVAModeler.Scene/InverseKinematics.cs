using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>Which of a bone's rotation channels: heading, pitch, bank (flags, for locks).</summary>
[Flags]
public enum IKChannels
{
    None = 0,
    Heading = 1,
    Pitch = 2,
    Bank = 4,
}

/// <summary>A joint limit on one rotation channel: the least and most it may turn to, in degrees.</summary>
public readonly record struct IKLimit(double Min, double Max)
{
    /// <summary>The limit, or an <see cref="ArgumentException"/> saying what's wrong with it.</summary>
    public IKLimit Checked(string what)
    {
        if (!double.IsFinite(Min) || !double.IsFinite(Max)) throw new ArgumentException($"{what}'s limits must be numbers (degrees).");
        if (Min > Max) throw new ArgumentException($"{what}'s least ({Min:0.###}°) is more than its most ({Max:0.###}°).");
        return this;
    }

    public double Clamp(double degrees) => Math.Clamp(degrees, Min, Max);
}

/// <summary>
/// A bone's joint limits for IK, as LightWave's Heading, Pitch and Bank Limits: each channel may have one (null: free).
/// The limits bound what IK turns the bone to, not its keys.
/// </summary>
public sealed record IKLimits(IKLimit? Heading = null, IKLimit? Pitch = null, IKLimit? Bank = null)
{
    public bool Any => Heading != null || Pitch != null || Bank != null;

    /// <summary>The limit on a channel by its index (0 heading, 1 pitch, 2 bank).</summary>
    public IKLimit? this[int channel] => channel switch { 0 => Heading, 1 => Pitch, _ => Bank };

    /// <summary>These limits, or an <see cref="ArgumentException"/> saying what's wrong.</summary>
    public IKLimits Checked()
    {
        Heading?.Checked("Heading");
        Pitch?.Checked("Pitch");
        Bank?.Checked("Bank");
        return this;
    }

    /// <summary>A copy with one channel's limit (0 heading, 1 pitch, 2 bank) set or taken away.</summary>
    public IKLimits With(int channel, IKLimit? limit) => channel switch
    {
        0 => this with { Heading = limit },
        1 => this with { Pitch = limit },
        _ => this with { Bank = limit },
    };
}

/// <summary>
/// Inverse kinematics for bones, in the manner of LightWave 5: a bone at the end of a chain is given a goal item (a
/// null, usually), and the chain's bones turn so its tip reaches the goal. The chain runs from that bone up through its
/// parent bones to the object, stopping below a bone marked <see cref="Bone.IKAnchor"/> ("unaffected by IK of
/// descendants"), which stays as keyed. The solver is cyclic coordinate descent (CCD) worked in each bone's own
/// heading, pitch and bank: from the tip's bone to the chain's root, each bone turns (in its parent's space) to point
/// the tip at the goal, its locked channels (<see cref="Bone.IKLocked"/>) keep their keyed values and its limited ones
/// (<see cref="Bone.IKLimits"/>) are clamped, round and round until the tip is there or stops getting closer. It starts
/// every frame from the keyed pose, so the answer depends only on the frame (the same every time, on any thread). A goal
/// beyond reach leaves the chain stretched straight toward it.
/// <para>With <see cref="Bone.FullTimeIK"/> on, the solved rotations replace the chain's keyed rotations wherever the
/// scene is worked out (views, renders, deformation); with it off, the chain plays its keys, and IK only acts when it's
/// baked into keys (<see cref="Bake"/>).</para>
/// </summary>
public static class InverseKinematics
{
    /// <summary>How many times round the chain the solver goes at most.</summary>
    public const int MaxIterations = 100;

    /// <summary>How close the tip must come (as a fraction of the chain's length) for the solver to stop early.</summary>
    public const double Tolerance = 1e-7;

    /// <summary>
    /// The bones IK turns for a goal on <paramref name="end"/>, from the chain's root down to <paramref name="end"/>:
    /// the end and its parent bones, up to (not including) the first that is an <see cref="Bone.IKAnchor"/>.
    /// </summary>
    public static IReadOnlyList<Bone> Chain(Bone end)
    {
        var chain = new List<Bone> { end };
        for (var p = end.Parent as Bone; p != null && !p.IKAnchor && !chain.Contains(p); p = p.Parent as Bone) chain.Add(p);
        chain.Reverse();
        return chain;
    }

    /// <summary>The chain's full length at rest (the sum of its bones' rest lengths): how far its tip can reach from its root.</summary>
    public static double Reach(Bone end) => Chain(end).Sum(b => b.RestLength);

    /// <summary>
    /// Why an item can't be a bone's goal, or null if it can: it must be in the bone's scene, not a bone of the same
    /// object, and not move with any of that object's bones (it would chase itself).
    /// </summary>
    public static string? GoalProblem(Bone bone, SceneItem goal)
    {
        if (goal == bone) return $"{bone.Name} can't be its own goal: use a null (or another item) for it to reach for.";
        if (bone.Scene != null && goal.Scene != bone.Scene) return $"{goal.Name} isn't in {bone.Name}'s scene.";
        if (goal is Bone gb && gb.Owner == bone.Owner) return $"{goal.Name} is another of {bone.Owner.Name}'s bones, which IK turns: give {bone.Name} a null (or another item) as its goal.";
        for (var p = goal.Parent; p != null; p = p.Parent)
            if (p is Bone pb && pb.Owner == bone.Owner)
                return $"{goal.Name} moves with {pb.Name}, one of {bone.Owner.Name}'s bones, so it would move as the chain reaches for it: parent it to something else.";
        return null;
    }

    /// <summary>
    /// Solves the chain ending at <paramref name="end"/> for its goal at a frame, whether or not full-time IK is on:
    /// each chain bone's heading, pitch and bank (degrees). Empty if the bone has no goal (or one it can't use).
    /// </summary>
    public static IReadOnlyDictionary<Bone, Vec3> Solve(Bone end, double frame)
    {
        if (end.IKGoal is not { } goal || GoalProblem(end, goal) != null || end.Scene is { } s && goal.Scene != s) return new Dictionary<Bone, Vec3>();
        var chain = Chain(end);
        var basis = chain[0].TransformParent?.WorldMatrix(frame) ?? Mat4.Identity;
        return Solve(chain, basis, goal.WorldPosition(frame), frame);
    }

    /// <summary>
    /// The solver itself: turns <paramref name="chain"/> (root first), hanging from <paramref name="basis"/> (the world
    /// transform of the root's parent), so the last bone's tip reaches <paramref name="target"/> (world coordinates).
    /// </summary>
    internal static IReadOnlyDictionary<Bone, Vec3> Solve(IReadOnlyList<Bone> chain, Mat4 basis, Vec3 target, double frame)
    {
        int n = chain.Count;
        var keyed = chain.Select(b => b.RotationAt(frame)).ToArray();
        var rotation = (Vec3[])keyed.Clone();
        var position = chain.Select(b => b.PositionAt(frame)).ToArray();
        var scale = chain.Select(b => b.ScaleAt(frame)).ToArray();
        double length = Math.Max(chain.Sum(b => b.RestLength), 1e-9);
        var tipLocal = new Vec3(0, 0, chain[^1].RestLength);
        var world = new Mat4[n];

        Mat4 Local(int i) => Mat4.Translation(position[i]) * Rotations.Matrix(rotation[i]) * Mat4.Scaling(scale[i]) * Mat4.Translation(-chain[i].Pivot);
        void Place()
        {
            var m = basis;
            for (int i = 0; i < n; i++) world[i] = m = m * Local(i);
        }
        Vec3 Tip() => world[n - 1].TransformPoint(tipLocal);

        Place();
        double best = (Tip() - target).Length;
        var bestRotation = (Vec3[])rotation.Clone();
        int stalled = 0;
        for (int iteration = 0; iteration < MaxIterations && best > Tolerance * length && stalled < 4; iteration++)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                var joint = world[i].TransformPoint(chain[i].Pivot);
                var parent = i == 0 ? basis : world[i - 1];
                var toParent = parent.Inverted();
                // The tip's and the goal's directions from the joint, in the space the bone's rotation is measured in.
                var a = toParent.TransformVector(Tip() - joint);
                var b = toParent.TransformVector(target - joint);
                if (a.Length < 1e-12 || b.Length < 1e-12) continue;
                a = a.Normalized();
                b = b.Normalized();
                var axis = Vec3.Cross(a, b);
                double sin = axis.Length, cos = Math.Clamp(Vec3.Dot(a, b), -1, 1);
                if (sin < 1e-12)
                {
                    if (cos > 0) continue;
                    // Pointing straight away: turn about any axis across it.
                    axis = a.AnyPerpendicular();
                }
                var turned = Mat4.Rotation(axis, Math.Atan2(sin, cos)) * Rotations.Matrix(rotation[i]);
                rotation[i] = Constrain(chain[i], Nearest(Rotations.FromMatrix(turned), rotation[i]), keyed[i]);
                Place();
            }
            double now = (Tip() - target).Length;
            // Stop once rounds no longer get the tip closer (a goal out of reach, or limits in the way), keeping the
            // closest pose found.
            if (now < best - 1e-12 * length)
            {
                best = now;
                Array.Copy(rotation, bestRotation, n);
                stalled = 0;
            }
            else stalled++;
        }
        var result = new Dictionary<Bone, Vec3>();
        for (int i = 0; i < n; i++) result[chain[i]] = bestRotation[i];
        return result;
    }

    /// <summary>A rotation's angles moved by whole turns to lie nearest another's (so heading and bank don't jump by 360°).</summary>
    private static Vec3 Nearest(Vec3 hpb, Vec3 near) => new(
        hpb.X + 360 * Math.Round((near.X - hpb.X) / 360),
        hpb.Y,
        hpb.Z + 360 * Math.Round((near.Z - hpb.Z) / 360));

    /// <summary>A bone's locked channels back at their keyed values, and its limited ones clamped.</summary>
    internal static Vec3 Constrain(Bone bone, Vec3 hpb, Vec3 keyed)
    {
        double[] v = [hpb.X, hpb.Y, hpb.Z];
        for (int c = 0; c < 3; c++)
        {
            if ((bone.IKLocked & (IKChannels)(1 << c)) != 0) v[c] = keyed[c];
            else if (bone.IKLimits[c] is { } limit) v[c] = limit.Clamp(v[c]);
        }
        return new Vec3(v[0], v[1], v[2]);
    }

    /// <summary>
    /// Bakes IK into keys: solves the chain ending at <paramref name="end"/> at every frame from
    /// <paramref name="first"/> to <paramref name="last"/> (every <paramref name="step"/> frames, and the last), keys each
    /// chain bone's heading, pitch and bank there (by <paramref name="key"/>, which applies the keying rule), and turns
    /// full-time IK off so the keys play. All the frames are solved before any key is written, so the keys don't change
    /// the poses the solver starts from. Returns the frames keyed.
    /// </summary>
    public static IReadOnlyList<double> Bake(Bone end, double first, double last, double step, Action<Envelope, double, double> key)
    {
        if (end.IKGoal == null) throw new InvalidOperationException($"{end.Name} has no IK goal to bake: give it one first.");
        if (end.IKGoal is { } g && GoalProblem(end, g) is { } problem) throw new InvalidOperationException(problem);
        if (!(step > 0) || !double.IsFinite(step)) throw new ArgumentException("The step must be more than 0 frames.");
        if (last < first) throw new ArgumentException($"The last frame ({last:0.###}) is before the first ({first:0.###}).");
        var frames = new List<double>();
        for (double f = first; f < last - 1e-9; f += step) frames.Add(f);
        frames.Add(last);
        var poses = frames.Select(f => Solve(end, f)).ToList();
        for (int i = 0; i < frames.Count; i++)
            foreach (var (bone, hpb) in poses[i])
                for (int c = 0; c < 3; c++)
                {
                    if ((bone.IKLocked & (IKChannels)(1 << c)) != 0) continue;
                    key(bone[(Channel)(3 + c)], frames[i], hpb[c]);
                }
        end.FullTimeIK = false;
        end.Scene?.Notify();
        return frames;
    }
}
