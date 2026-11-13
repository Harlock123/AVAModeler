using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// A bone: an item belonging to an object that deforms the object's points. In its rest pose the bone starts at
/// <see cref="RestPosition"/> and runs <see cref="RestLength"/> along its +Z axis turned by <see cref="RestRotation"/>
/// (HPB degrees), both relative to its parent bone (or to the object, for a bone with no parent bone). Its channels
/// give its animated pose in the same terms. Each point moves by the blend of every active bone's change from rest
/// to pose, weighted by strength / distance^n, where the distance is from the point to the bone's rest segment and n
/// is the object's <see cref="ObjectItem.BoneFalloffExponent"/>.
/// </summary>
public sealed class Bone : SceneItem
{
    public Vec3 RestPosition;
    /// <summary>The rest direction as heading, pitch and bank in degrees.</summary>
    public Vec3 RestRotation;
    public double RestLength = 1;
    public double Strength = 1;
    public bool Active = true;
    /// <summary>
    /// The object's weight map this bone follows (LightWave 6's Bone Weight Map), or null: a point moves with it by
    /// its weight there (0 where it isn't in the map), times the distance falloff unless <see cref="WeightMapOnly"/>.
    /// </summary>
    public string? WeightMap;
    /// <summary>With a <see cref="WeightMap"/>, weigh points by the map alone (the default), not the map times the falloff.</summary>
    public bool WeightMapOnly = true;

    /// <summary>
    /// The item this bone's tip reaches for by inverse kinematics (LightWave's Goal Object): a null, usually. The chain
    /// is this bone and its parent bones up to an <see cref="IKAnchor"/> (see <see cref="InverseKinematics"/>). Null:
    /// no IK.
    /// </summary>
    public SceneItem? IKGoal;
    /// <summary>LightWave's Full-time IK: the chain follows the goal at every frame (off: it plays its keys, and IK acts only when baked into keys).</summary>
    public bool FullTimeIK = true;
    /// <summary>LightWave's "Unaffected by IK of descendants": IK below this bone stops here, leaving it (and what it hangs from) as keyed.</summary>
    public bool IKAnchor;
    /// <summary>Channels IK leaves at their keyed values (this bone's own heading, pitch or bank, turned only by its keys).</summary>
    public IKChannels IKLocked;
    /// <summary>Joint limits: how far IK may turn each of this bone's channels (degrees).</summary>
    public IKLimits IKLimits = new();

    public Bone(string name, ObjectItem owner) : base(name) => Owner = owner;

    /// <summary>The object the bone deforms.</summary>
    public ObjectItem Owner { get; }

    public override string Kind => "Bone";

    protected override void CheckParent(SceneItem parent)
    {
        if (parent != Owner && !(parent is Bone b && b.Owner == Owner))
            throw new InvalidOperationException($"Bone {Name}'s parent must be its object or another of its bones.");
    }

    internal override SceneItem? TransformParent => Parent ?? Owner;

    /// <summary>The rest pose relative to the parent bone (or the object).</summary>
    public Mat4 RestLocal => Mat4.Translation(RestPosition) * Rotations.Matrix(RestRotation);

    /// <summary>The rest pose in object coordinates.</summary>
    public Mat4 RestInObject()
    {
        var m = RestLocal;
        for (var p = Parent as Bone; p != null; p = p.Parent as Bone) m = p.RestLocal * m;
        return m;
    }

    /// <summary>
    /// The bone whose goal turns this one by full-time IK at the moment (itself, or one further down its chain), or
    /// null when its rotation is its keys'.
    /// </summary>
    public Bone? IKEnd => Scene?.Motion.IKEndOf(this);

    /// <summary>The animated pose in object coordinates (IK included).</summary>
    public Mat4 PoseInObject(double frame)
    {
        var m = LocalMatrix(frame);
        for (var p = Parent as Bone; p != null; p = p.Parent as Bone) m = p.LocalMatrix(frame) * m;
        return m;
    }

    /// <summary>The rest segment's ends in object coordinates.</summary>
    public (Vec3 Start, Vec3 End) RestSegment()
    {
        var m = RestInObject();
        return (m.TransformPoint(Vec3.Zero), m.TransformPoint(new Vec3(0, 0, RestLength)));
    }

    /// <summary>Puts the bone's channels back to its rest pose, unanimated.</summary>
    public void ResetPose()
    {
        for (int i = 0; i < 3; i++)
        {
            this[(Channel)i].SetConstant(RestPosition[i]);
            this[(Channel)(i + 3)].SetConstant(RestRotation[i]);
            this[(Channel)(i + 6)].SetConstant(1);
        }
    }

    /// <summary>Makes the pose at a frame the rest pose (Layout's Rest Bone).</summary>
    public void RestFromPose(double frame)
    {
        RestPosition = PositionAt(frame);
        RestRotation = RotationAt(frame);
        Scene?.Notify();
    }
}

/// <summary>Moves an object's points by its bones.</summary>
internal static class BoneDeformer
{
    public static void Apply(ObjectItem obj, Vec3[] points, double frame, Mesh? source = null)
    {
        var bones = obj.Bones.Where(b => b.Active && b.Strength != 0).ToList();
        var maps = MapsOf(bones, source ?? obj.BaseMesh());
        if (bones.Count == 0) return;
        var starts = new Vec3[bones.Count];
        var ends = new Vec3[bones.Count];
        var moves = new Mat4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            var rest = bones[i].RestInObject();
            starts[i] = rest.TransformPoint(Vec3.Zero);
            ends[i] = rest.TransformPoint(new Vec3(0, 0, bones[i].RestLength));
            moves[i] = bones[i].PoseInObject(frame) * rest.Inverted();
        }
        double n = obj.BoneFalloffExponent;
        for (int p = 0; p < points.Length; p++)
        {
            var sum = Vec3.Zero;
            double total = 0;
            for (int i = 0; i < bones.Count; i++)
            {
                double w = Weight(bones[i], maps[i], p, points[p], starts[i], ends[i], n);
                if (w == 0) continue;
                sum += moves[i].TransformPoint(points[p]) * w;
                total += w;
            }
            if (total != 0) points[p] = sum / total;
        }
    }

    /// <summary>Each bone's weight map in the mesh (null where it has none, or the mesh hasn't that map).</summary>
    private static PointMap?[] MapsOf(List<Bone> bones, Mesh mesh) =>
        [.. bones.Select(b => b.WeightMap is { } name ? mesh.FindPointMap(name, PointMapKind.Weight) : null)];

    /// <summary>
    /// How much a bone pulls a point: strength / distance^n, or with a weight map the point's weight there (alone, or
    /// times that falloff). <paramref name="index"/> is the point's in the mesh (-1: not known, so the map isn't used).
    /// </summary>
    private static double Weight(Bone bone, PointMap? map, int index, Vec3 p, Vec3 start, Vec3 end, double n)
    {
        if (map != null && index >= 0)
        {
            double mw = map.Weight(index);
            if (mw == 0) return 0;
            if (bone.WeightMapOnly) return bone.Strength * mw;
            return bone.Strength * mw / Math.Pow(Math.Max(DistanceToSegment(p, start, end), 1e-6), n);
        }
        return bone.Strength / Math.Pow(Math.Max(DistanceToSegment(p, start, end), 1e-6), n);
    }

    /// <summary>The bones that move an object's points (active, with strength), in the order <see cref="Weights"/> and <see cref="Moves"/> use.</summary>
    public static List<Bone> Moving(ObjectItem obj) => obj.Bones.Where(b => b.Active && b.Strength != 0).ToList();

    /// <summary>Each bone's move at a frame: from its rest to its pose, in the object's coordinates.</summary>
    public static Mat4[] Moves(List<Bone> bones, double frame) =>
        [.. bones.Select(b => b.PoseInObject(frame) * b.RestInObject().Inverted())];

    /// <summary>How much each bone moves a point at its rest place (adding to 1), as <see cref="Apply"/> weighs them.</summary>
    public static Func<Vec3, double[]> Weights(ObjectItem obj, List<Bone> bones)
    {
        var starts = new Vec3[bones.Count];
        var ends = new Vec3[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            var rest = bones[i].RestInObject();
            starts[i] = rest.TransformPoint(Vec3.Zero);
            ends[i] = rest.TransformPoint(new Vec3(0, 0, bones[i].RestLength));
        }
        double n = obj.BoneFalloffExponent;
        var mesh = obj.BaseMesh();
        var maps = MapsOf(bones, mesh);
        // A place that is one of the mesh's points (a hair's root) takes that point's weights.
        Dictionary<Vec3, int>? at = maps.Any(m => m != null) ? [] : null;
        if (at != null) for (int i = 0; i < mesh.Points.Count; i++) at.TryAdd(mesh.Points[i], i);
        return p =>
        {
            var w = new double[bones.Count];
            double total = 0;
            int index = at != null && at.TryGetValue(p, out int k) ? k : -1;
            for (int i = 0; i < bones.Count; i++)
            {
                w[i] = Weight(bones[i], maps[i], index, p, starts[i], ends[i], n);
                total += w[i];
            }
            if (total != 0) for (int i = 0; i < w.Length; i++) w[i] /= total;
            return w;
        };
    }

    public static double DistanceToSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 == 0 ? 0 : Math.Clamp(Vec3.Dot(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}

/// <summary>
/// Turns Modeler skelegons into bones. A skelegon is a 2-point polygon on a surface whose name starts with
/// "Skelegon" (as in "Skelegon" or "Skelegon_Arm"), running from its first point (the bone's base) to its second (its
/// tip). A skelegon whose base sits on another's tip becomes that bone's child. Bones are named after the rest of
/// the surface name ("Arm"), or "Bone n" when that is empty, with a number added if the name is taken.
/// </summary>
public static class Skelegons
{
    public const string SurfacePrefix = "Skelegon";

    /// <summary>Adds a bone to the scene for every skelegon in an object's mesh, and returns them in mesh order.</summary>
    public static IReadOnlyList<Bone> CreateBones(Scene scene, ObjectItem obj, string surfacePrefix = SurfacePrefix)
    {
        var mesh = obj.BaseMesh();
        var surfaces = obj.Surfaces;
        var segments = mesh.Polygons
            .Where(p => p.Count == 2 && p.Surface >= 0 && p.Surface < surfaces.Count
                        && surfaces[p.Surface].Name.StartsWith(surfacePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Start: mesh.Points[p.Points[0]], End: mesh.Points[p.Points[1]], Surface: surfaces[p.Surface].Name))
            .ToList();

        var parents = new int[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            parents[i] = -1;
            for (int j = 0; j < segments.Count && parents[i] < 0; j++)
                if (j != i && Vec3.Near(segments[j].End, segments[i].Start, 1e-6)) parents[i] = j;
        }

        var bones = new Bone[segments.Count];
        var restInObject = new Mat4[segments.Count];
        var used = obj.Bones.Select(b => b.Name).ToHashSet();
        var visiting = new bool[segments.Count];

        Bone Make(int i)
        {
            if (bones[i] != null) return bones[i];
            // A loop of skelegons can't all be parents of each other: break it by making this one a root.
            visiting[i] = true;
            int parent = parents[i] >= 0 && !visiting[parents[i]] ? parents[i] : -1;
            var parentBone = parent >= 0 ? Make(parent) : null;
            var (start, end, surface) = segments[i];
            var objectRest = Mat4.Translation(start) * Rotations.Matrix(Rotations.FromDirection(end - start));
            var local = parentBone == null ? objectRest : restInObject[parent].Inverted() * objectRest;
            var bone = new Bone(UniqueName(BoneName(surface, surfacePrefix, i), used), obj)
            {
                RestPosition = local.TransformPoint(Vec3.Zero),
                RestRotation = Rotations.FromMatrix(local),
                RestLength = (end - start).Length,
            };
            // A weight map of the bone's name (as Weights from Skelegons makes) is the one it follows.
            bone.WeightMap = mesh.FindPointMap(bone.Name, PointMapKind.Weight)?.Name;
            restInObject[i] = objectRest;
            scene.Add(bone);
            if (parentBone != null) bone.Parent = parentBone;
            bone.ResetPose();
            bones[i] = bone;
            visiting[i] = false;
            return bone;
        }

        for (int i = 0; i < segments.Count; i++) Make(i);
        return bones;
    }

    private static string BoneName(string surface, string prefix, int index)
    {
        string rest = surface.Length > prefix.Length ? surface[prefix.Length..].Trim(' ', '_', '-', '.', ':') : "";
        return rest.Length > 0 ? rest : $"Bone {index + 1}";
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        string n = name;
        for (int i = 2; !used.Add(n); i++) n = $"{name} ({i})";
        return n;
    }
}
