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

    /// <summary>The animated pose in object coordinates.</summary>
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
    public static void Apply(ObjectItem obj, Vec3[] points, double frame)
    {
        var bones = obj.Bones.Where(b => b.Active && b.Strength != 0).ToList();
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
                double d = Math.Max(DistanceToSegment(points[p], starts[i], ends[i]), 1e-6);
                double w = bones[i].Strength / Math.Pow(d, n);
                sum += moves[i].TransformPoint(points[p]) * w;
                total += w;
            }
            if (total != 0) points[p] = sum / total;
        }
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
