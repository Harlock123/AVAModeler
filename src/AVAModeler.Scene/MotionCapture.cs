using System.Globalization;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>One of a BVH joint's channels.</summary>
public enum BvhChannel { Xposition, Yposition, Zposition, Xrotation, Yrotation, Zrotation }

/// <summary>A joint of a BVH skeleton: its offset from its parent, its channels in file order, and its end site, if any.</summary>
public sealed class BvhJoint(string name, BvhJoint? parent)
{
    public string Name { get; } = name;
    public BvhJoint? Parent { get; } = parent;
    public Vec3 Offset { get; set; }
    public List<BvhChannel> Channels { get; } = [];
    /// <summary>Where its first channel's value is in a frame's numbers.</summary>
    public int FirstValue { get; set; }
    public List<BvhJoint> Children { get; } = [];
    /// <summary>An end site's offset (a chain's tip), or null.</summary>
    public Vec3? EndSite { get; set; }
    public override string ToString() => Name;
}

/// <summary>
/// A Biovision hierarchy (.bvh) motion capture: a skeleton (HIERARCHY: ROOT and JOINTs with OFFSET, CHANNELS and End
/// Sites) and its motion (MOTION: Frames, Frame Time, then a line of channel values a frame). Rotations are degrees,
/// applied in the order the channels are listed; the space is right-handed and Y up, as BVH files are.
/// </summary>
public sealed class BvhFile
{
    public List<BvhJoint> Joints { get; } = [];
    public double FrameTime { get; private set; } = 1 / 30.0;
    public List<double[]> Frames { get; } = [];
    public BvhJoint Root => Joints[0];
    /// <summary>How long the motion runs, in seconds.</summary>
    public double Duration => Math.Max(0, Frames.Count - 1) * FrameTime;

    public static BvhFile Read(string path) => Parse(File.ReadAllText(path));

    public static BvhFile Parse(string text)
    {
        var words = new Queue<string>(text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var bvh = new BvhFile();
        string Next(string what) => words.Count > 0 ? words.Dequeue() : throw new InvalidDataException($"The BVH file ends where {what} should be.");
        double Number(string what)
        {
            string w = Next(what);
            return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
                ? v : throw new InvalidDataException($"The BVH file has \"{w}\" where {what} should be a number.");
        }
        void Expect(string word)
        {
            string w = Next(word);
            if (!string.Equals(w, word, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The BVH file has \"{w}\" where \"{word}\" should be.");
        }
        Expect("HIERARCHY");
        Expect("ROOT");
        int values = 0;
        void ReadJoint(BvhJoint joint)
        {
            Expect("{");
            Expect("OFFSET");
            joint.Offset = new Vec3(Number("an offset"), Number("an offset"), Number("an offset"));
            Expect("CHANNELS");
            int count = (int)Number("a channel count");
            if (count is < 0 or > 6) throw new InvalidDataException($"Joint {joint.Name} has {count} channels; a BVH joint has 0 to 6.");
            joint.FirstValue = values;
            for (int i = 0; i < count; i++)
            {
                string c = Next("a channel");
                if (!Enum.TryParse<BvhChannel>(c, true, out var ch)) throw new InvalidDataException($"Joint {joint.Name} has a channel \"{c}\"; BVH channels are Xposition, Yposition, Zposition, Xrotation, Yrotation and Zrotation.");
                joint.Channels.Add(ch);
            }
            values += count;
            while (true)
            {
                string w = Next("a joint or }");
                if (w == "}") return;
                if (string.Equals(w, "JOINT", StringComparison.OrdinalIgnoreCase))
                {
                    var child = new BvhJoint(Next("a joint's name"), joint);
                    joint.Children.Add(child);
                    bvh.Joints.Add(child);
                    ReadJoint(child);
                }
                else if (string.Equals(w, "End", StringComparison.OrdinalIgnoreCase))
                {
                    Expect("Site");
                    Expect("{");
                    Expect("OFFSET");
                    joint.EndSite = new Vec3(Number("an offset"), Number("an offset"), Number("an offset"));
                    Expect("}");
                }
                else throw new InvalidDataException($"The BVH file has \"{w}\" inside joint {joint.Name}, where JOINT, End Site or }} should be.");
            }
        }
        var root = new BvhJoint(Next("the root's name"), null);
        bvh.Joints.Add(root);
        ReadJoint(root);
        Expect("MOTION");
        Expect("Frames:");
        int frames = (int)Number("the frame count");
        Expect("Frame");
        Expect("Time:");
        bvh.FrameTime = Number("the frame time");
        if (!(bvh.FrameTime > 0)) throw new InvalidDataException("The BVH file's frame time must be more than 0 seconds.");
        for (int f = 0; f < frames; f++)
        {
            var row = new double[values];
            for (int i = 0; i < values; i++) row[i] = Number($"frame {f + 1}'s values");
            bvh.Frames.Add(row);
        }
        return bvh;
    }

    /// <summary>A joint's turn at a frame (its rotation channels in their order), in the file's space.</summary>
    public Mat4 LocalRotation(BvhJoint joint, int frame)
    {
        var m = Mat4.Identity;
        var row = Frames[Math.Clamp(frame, 0, Frames.Count - 1)];
        for (int i = 0; i < joint.Channels.Count; i++)
        {
            double deg = row[joint.FirstValue + i];
            m = joint.Channels[i] switch
            {
                BvhChannel.Xrotation => m * Mat4.RotationX(Mat4.Deg(deg)),
                BvhChannel.Yrotation => m * Mat4.RotationY(Mat4.Deg(deg)),
                BvhChannel.Zrotation => m * Mat4.RotationZ(Mat4.Deg(deg)),
                _ => m,
            };
        }
        return m;
    }

    /// <summary>A joint's position channels at a frame (zero where it has none).</summary>
    public Vec3 Position(BvhJoint joint, int frame)
    {
        var row = Frames[Math.Clamp(frame, 0, Frames.Count - 1)];
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < joint.Channels.Count; i++)
            switch (joint.Channels[i])
            {
                case BvhChannel.Xposition: x = row[joint.FirstValue + i]; break;
                case BvhChannel.Yposition: y = row[joint.FirstValue + i]; break;
                case BvhChannel.Zposition: z = row[joint.FirstValue + i]; break;
            }
        return new Vec3(x, y, z);
    }

    /// <summary>Each joint's rest position (every rotation zero), in the file's space.</summary>
    public Dictionary<BvhJoint, Vec3> RestPositions()
    {
        var at = new Dictionary<BvhJoint, Vec3>();
        foreach (var j in Joints) at[j] = (j.Parent == null ? Vec3.Zero : at[j.Parent]) + j.Offset;
        return at;
    }
}

/// <summary>How <see cref="MotionCapture.Apply"/> puts a capture on an object's bones.</summary>
public sealed record RetargetOptions
{
    /// <summary>The scene frame the capture's first frame lands on.</summary>
    public double StartFrame { get; init; } = 1;
    /// <summary>A key every this many scene frames (1: every frame).</summary>
    public double Step { get; init; } = 1;
    /// <summary>Bone name → joint name, for bones whose names don't match a joint's.</summary>
    public IReadOnlyDictionary<string, string>? Map { get; init; }
    /// <summary>Whether the root joint's moves move the root bone (scaled), or only its turns are used.</summary>
    public bool MoveRoot { get; init; } = true;
    /// <summary>Metres a unit of the file's moves (null: from the two skeletons' heights).</summary>
    public double? Scale { get; init; }
    /// <summary>Whether the bones' rotation keys (and the root's position keys) are cleared first.</summary>
    public bool Replace { get; init; } = true;
}

/// <summary>What a retarget did: the bones matched to joints, the bones left alone, the keys made, the frames, the scale.</summary>
public sealed record RetargetResult(IReadOnlyList<(string Bone, string Joint)> Matched, IReadOnlyList<string> Unmatched, int Keys, double FirstFrame, double LastFrame, double Scale);

/// <summary>
/// Motion capture onto bones: each bone matched to a joint (by name, ignoring case, punctuation and a "prefix:" such as
/// Mixamo's, or by the map given) takes the joint's turn away from the capture's rest pose, applied to the bone's own
/// rest pose, so a capture made in a T-pose drives a rig at rest in a T-pose whatever its bones' lengths and rest
/// angles. The root joint's moves move the root bone, scaled by the two skeletons' heights. BVH's right-handed space is
/// turned into LightWave's left-handed one at the edge (Z flipped). Keys go on the bones' heading, pitch and bank (and
/// the root's position), every <see cref="RetargetOptions.Step"/> scene frames, angles kept continuous from key to key.
/// </summary>
public static class MotionCapture
{
    private static readonly Mat4 Flip = Mat4.Scaling(new Vec3(1, 1, -1));

    /// <summary>A name as it's matched: lower case, letters and digits only, after any "prefix:".</summary>
    public static string Normalised(string name)
    {
        int colon = name.LastIndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        return new string([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
    }

    public static RetargetResult Apply(Scene scene, ObjectItem obj, BvhFile bvh, RetargetOptions? options = null)
    {
        options ??= new RetargetOptions();
        if (!(options.Step > 0)) throw new ArgumentException("The step must be more than 0 frames.");
        if (bvh.Frames.Count == 0) throw new InvalidOperationException("The capture has no frames of motion.");
        var bones = obj.Bones.ToList();
        if (bones.Count == 0) throw new InvalidOperationException($"{obj.Name} has no bones to put the motion on: make them first (skelegons_to_bones, or add_bone).");

        // Bones to joints.
        var byName = new Dictionary<string, BvhJoint>();
        foreach (var j in bvh.Joints) byName.TryAdd(Normalised(j.Name), j);
        var exact = bvh.Joints.GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var jointOf = new Dictionary<Bone, BvhJoint>();
        foreach (var b in bones)
        {
            if (options.Map != null && options.Map.TryGetValue(b.Name, out var wanted))
            {
                if (!exact.TryGetValue(wanted, out var j) && !byName.TryGetValue(Normalised(wanted), out j))
                    throw new ArgumentException($"The capture has no joint \"{wanted}\" for bone {b.Name}. Its joints: {string.Join(", ", bvh.Joints.Select(x => x.Name))}.");
                jointOf[b] = j;
            }
            else if (byName.TryGetValue(Normalised(b.Name), out var j)) jointOf[b] = j;
        }
        if (jointOf.Count == 0)
            throw new InvalidOperationException($"None of {obj.Name}'s bones ({string.Join(", ", bones.Select(b => b.Name))}) matches a joint of the capture ({string.Join(", ", bvh.Joints.Select(j => j.Name))}): give a map from bone names to joint names.");

        // The scale for the root's moves: the two skeletons' heights.
        double scale = options.Scale ?? AutoScale(bvh, bones);

        // Parents before children, so each bone's posed parent is known.
        var ordered = new List<Bone>();
        var placed = new HashSet<Bone>();
        void Place(Bone b)
        {
            if (placed.Contains(b)) return;
            if (b.Parent is Bone p) Place(p);
            placed.Add(b);
            ordered.Add(b);
        }
        foreach (var b in bones) Place(b);
        var restRotation = bones.ToDictionary(b => b, b => RotationOf(b.RestInObject()));
        var root = jointOf.Where(kv => kv.Value == bvh.Root && kv.Key.Parent is not Bone).Select(kv => kv.Key).FirstOrDefault();
        var rootStart = bvh.Position(bvh.Root, 0);

        double fps = scene.FramesPerSecond;
        double last = options.StartFrame + bvh.Duration * fps;
        if (options.Replace)
            foreach (var b in jointOf.Keys)
            {
                foreach (var ch in new[] { Channel.Heading, Channel.Pitch, Channel.Bank }) b[ch].Clear();
                if (b == root && options.MoveRoot) foreach (var ch in new[] { Channel.PositionX, Channel.PositionY, Channel.PositionZ }) b[ch].Clear();
            }

        int keys = 0;
        var previous = new Dictionary<Bone, Vec3>();
        var frames = new List<double>();
        for (double f = options.StartFrame; f <= last + 1e-9; f += options.Step) frames.Add(f);
        if (frames.Count == 0 || frames[^1] < last - 1e-9) frames.Add(last);
        foreach (double f in frames)
        {
            int k = (int)Math.Round((f - options.StartFrame) / fps / bvh.FrameTime);
            // Each joint's world turn at the frame, in LightWave's space.
            var world = new Dictionary<BvhJoint, Mat4>();
            foreach (var j in bvh.Joints) world[j] = (j.Parent == null ? Mat4.Identity : world[j.Parent]) * bvh.LocalRotation(j, k);
            var posed = new Dictionary<Bone, Mat4>();
            foreach (var b in ordered)
            {
                var parent = b.Parent is Bone p ? posed[p] : Mat4.Identity;
                if (!jointOf.TryGetValue(b, out var j))
                {
                    posed[b] = parent * Rotations.Matrix(b.RotationAt(f));
                    continue;
                }
                var turn = Flip * world[j] * Flip;
                var pose = turn * restRotation[b];
                posed[b] = pose;
                var hpb = Rotations.FromMatrix(parent.Inverted() * pose);
                if (previous.TryGetValue(b, out var before)) hpb = new Vec3(Near(hpb.X, before.X), Near(hpb.Y, before.Y), Near(hpb.Z, before.Z));
                previous[b] = hpb;
                b[Channel.Heading].AddKey(f, hpb.X);
                b[Channel.Pitch].AddKey(f, hpb.Y);
                b[Channel.Bank].AddKey(f, hpb.Z);
                keys += 3;
                if (b == root && options.MoveRoot)
                {
                    var moved = Flip.TransformVector(bvh.Position(bvh.Root, k) - rootStart) * scale;
                    var at = b.RestPosition + moved;
                    b[Channel.PositionX].AddKey(f, at.X);
                    b[Channel.PositionY].AddKey(f, at.Y);
                    b[Channel.PositionZ].AddKey(f, at.Z);
                    keys += 3;
                }
            }
        }
        scene.Notify();
        return new RetargetResult(
            [.. jointOf.Select(kv => (kv.Key.Name, kv.Value.Name))],
            [.. bones.Where(b => !jointOf.ContainsKey(b)).Select(b => b.Name)],
            keys, options.StartFrame, last, scale);
    }

    /// <summary>The angle nearest another, by whole turns (so keys don't spin the long way round).</summary>
    private static double Near(double angle, double to) => angle + 360 * Math.Round((to - angle) / 360);

    private static Mat4 RotationOf(Mat4 m)
    {
        var x = m.TransformVector(Vec3.UnitX).Normalized();
        var y = m.TransformVector(Vec3.UnitY).Normalized();
        var z = m.TransformVector(Vec3.UnitZ).Normalized();
        return Mat4.FromRows(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0, 0, 0, 0, 1);
    }

    /// <summary>Metres a unit of the capture: the rig's height over the capture's (its joints at rest), or 1.</summary>
    private static double AutoScale(BvhFile bvh, List<Bone> bones)
    {
        var rest = bvh.RestPositions();
        var ys = rest.Values.Select(p => p.Y).Concat(bvh.Joints.Where(j => j.EndSite != null).Select(j => (rest[j] + j.EndSite!.Value).Y)).ToList();
        double source = ys.Max() - ys.Min();
        var rig = bones.SelectMany(b =>
        {
            var m = b.RestInObject();
            return new[] { m.TransformPoint(Vec3.Zero).Y, m.TransformPoint(new Vec3(0, 0, b.RestLength)).Y };
        }).ToList();
        double target = rig.Max() - rig.Min();
        return source > 1e-9 && target > 1e-9 ? target / source : 1;
    }
}
