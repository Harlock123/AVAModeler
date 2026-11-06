using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// What moves a scene's items beyond their keys, worked out when asked and remembered until the scene changes: where a
/// path puts an item that follows one (<see cref="FollowPath"/>), the rotations full-time IK gives bones
/// (<see cref="InverseKinematics"/>), and then each item's motion modifiers (<see cref="SceneItem.Modifiers"/>) in order. Each <see cref="Scene"/> has one
/// (<see cref="Scene.Motion"/>); <see cref="SceneItem.LocalMatrix"/> asks it, so views, renders, deformation and exports
/// all see the same motion. It is safe to use from several threads.
/// </summary>
public sealed class MotionSolver
{
    private const int MaxRemembered = 8192;

    private readonly Scene _scene;
    private readonly Lock _lock = new();
    private int _version = int.MinValue;
    private Dictionary<Bone, Bone> _ikEnds = [];
    private readonly Dictionary<(Bone End, double Frame), IReadOnlyDictionary<Bone, Vec3>> _solved = [];
    private readonly Dictionary<(SceneItem Item, double Frame), (Vec3 Position, Vec3 Rotation)> _followed = [];
    private readonly Dictionary<(SceneItem Item, double Frame), MotionPose> _modified = [];

    /// <summary>The items whose modifiers are being applied on this thread (a modifier loop can't recurse forever: the keys stand in).</summary>
    [ThreadStatic] private static HashSet<SceneItem>? t_modifying;

    /// <summary>How many times a modifier loop has been cut on this thread, so a pose worked out across a cut isn't remembered.</summary>
    [ThreadStatic] private static int t_cuts;

    /// <summary>The items whose path is being worked out on this thread (a path through items that follow it can't recurse forever).</summary>
    [ThreadStatic] private static HashSet<SceneItem>? t_following;

    /// <summary>The chains being solved on this thread, so a goal that leads back to its own chain can't recurse forever.</summary>
    [ThreadStatic] private static HashSet<Bone>? t_solving;

    internal MotionSolver(Scene scene) => _scene = scene;

    /// <summary>Starts again if the scene has changed since (call inside the lock).</summary>
    private void Refresh()
    {
        if (_version == _scene.Version) return;
        _version = _scene.Version;
        _solved.Clear();
        _followed.Clear();
        _modified.Clear();
        var ends = new Dictionary<Bone, Bone>();
        foreach (var end in _scene.Bones)
        {
            if (!end.FullTimeIK || end.IKGoal is not { } goal || goal.Scene != _scene || InverseKinematics.GoalProblem(end, goal) != null) continue;
            foreach (var b in InverseKinematics.Chain(end))
                ends.TryAdd(b, end);
        }
        _ikEnds = ends;
    }

    /// <summary>The bone whose goal turns <paramref name="bone"/> by full-time IK, or null.</summary>
    public Bone? IKEndOf(Bone bone)
    {
        lock (_lock)
        {
            Refresh();
            return _ikEnds.GetValueOrDefault(bone);
        }
    }

    /// <summary>An item's position as it moves at a frame (see <see cref="SceneItem.MotionPositionAt"/>).</summary>
    public Vec3 Position(SceneItem item, double frame) => item.Modifiers.Count == 0 ? BasePosition(item, frame) : Modified(item, frame).Position;

    /// <summary>An item's rotation as it moves at a frame (see <see cref="SceneItem.MotionRotationAt"/>).</summary>
    public Vec3 Rotation(SceneItem item, double frame) => item.Modifiers.Count == 0 ? BaseRotation(item, frame) : Modified(item, frame).Rotation;

    /// <summary>An item's scale as it is at a frame (see <see cref="SceneItem.MotionScaleAt"/>).</summary>
    public Vec3 Scale(SceneItem item, double frame) => item.Modifiers.Count == 0 ? item.ScaleAt(frame) : Modified(item, frame).Scale;

    /// <summary>An item's pose at a frame before its modifiers: keys, path following and IK.</summary>
    public MotionPose Unmodified(SceneItem item, double frame) => new(BasePosition(item, frame), BaseRotation(item, frame), item.ScaleAt(frame));

    /// <summary>
    /// An item's pose with its modifiers applied in order, remembered per frame. An item already being worked out
    /// further up this thread (a loop of modifiers) gets its pose without them: the keys (and path, and IK) stand in.
    /// </summary>
    private MotionPose Modified(SceneItem item, double frame)
    {
        int version;
        lock (_lock)
        {
            Refresh();
            if (_modified.TryGetValue((item, frame), out var known)) return known;
            version = _version;
        }
        var modifying = t_modifying ??= [];
        if (!modifying.Add(item))
        {
            t_cuts++;
            return Unmodified(item, frame);
        }
        int cuts = t_cuts;
        MotionPose pose;
        try
        {
            pose = Unmodified(item, frame);
            foreach (var m in item.Modifiers) pose = m.Apply(item, frame, pose);
        }
        finally
        {
            modifying.Remove(item);
        }
        // A pose worked out across a cut loop depends on where the loop was entered: don't remember it.
        if (cuts == t_cuts)
            lock (_lock)
            {
                if (_version == version && _scene.Version == version)
                {
                    if (_modified.Count >= MaxRemembered) _modified.Clear();
                    _modified[(item, frame)] = pose;
                }
            }
        return pose;
    }

    private Vec3 BasePosition(SceneItem item, double frame) => item.Path is { } path && Follow(item, path, frame) is { } f ? f.Position : item.PositionAt(frame);

    /// <summary>
    /// Where a path puts an item at a frame, in its parent's space, and how it turns it (keyed, or aligned along the path
    /// with any banking), or null while the path can't be made (or is being worked out further up this thread).
    /// </summary>
    private (Vec3 Position, Vec3 Rotation)? Follow(SceneItem item, FollowPath path, double frame)
    {
        int version;
        lock (_lock)
        {
            Refresh();
            if (_followed.TryGetValue((item, frame), out var known)) return known;
            version = _version;
        }
        var following = t_following ??= [];
        if (!following.Add(item)) return null;
        (Vec3, Vec3)? result;
        try
        {
            result = Work(item, path, frame);
        }
        finally
        {
            following.Remove(item);
        }
        if (result is { } r)
            lock (_lock)
            {
                if (_version == version && _scene.Version == version)
                {
                    if (_followed.Count >= MaxRemembered) _followed.Clear();
                    _followed[(item, frame)] = r;
                }
            }
        return result;
    }

    private static (Vec3, Vec3)? Work(SceneItem item, FollowPath path, double frame)
    {
        if (MotionPath.Of(item, path, frame) is not { } on) return null;
        var toParent = item.TransformParent is { } parent ? parent.WorldMatrix(frame).Inverted() : Mat4.Identity;
        var position = toParent.TransformPoint(on.Point);
        var keyed = item.RotationAt(frame);
        if (!path.AlignToPath) return (position, keyed);
        var ahead = path.LookAhead > 0 ? on.Path.PointAt(on.Distance + path.LookAhead) - on.Point : on.Path.TangentAt(on.Distance);
        // At an open path's end there's nothing ahead: look along the tangent there.
        if (ahead.Length < 1e-9) ahead = on.Path.TangentAt(on.Distance);
        var hp = Rotations.FromDirection(toParent.TransformVector(ahead));
        double bank = keyed.Z;
        if (path.Banking != 0) bank += Math.Clamp(-path.Banking * on.Path.TurnAt(on.Distance), -90, 90);
        return (position, new Vec3(hp.X, hp.Y, bank));
    }

    private Vec3 BaseRotation(SceneItem item, double frame)
    {
        if (item.Path is { } path) return Follow(item, path, frame) is { } f ? f.Rotation : item.RotationAt(frame);
        if (item is not Bone bone) return item.RotationAt(frame);
        Bone? end;
        int version;
        lock (_lock)
        {
            Refresh();
            if (!_ikEnds.TryGetValue(bone, out end)) return bone.RotationAt(frame);
            if (_solved.TryGetValue((end, frame), out var known)) return known.TryGetValue(bone, out var r) ? r : bone.RotationAt(frame);
            version = _version;
        }
        var solving = t_solving ??= [];
        // A chain that is already being solved further up this thread's stack: its keys stand in.
        if (!solving.Add(end)) return bone.RotationAt(frame);
        IReadOnlyDictionary<Bone, Vec3> solved;
        try
        {
            solved = InverseKinematics.Solve(end, frame);
        }
        finally
        {
            solving.Remove(end);
        }
        lock (_lock)
        {
            if (_version == version && _scene.Version == version)
            {
                if (_solved.Count >= MaxRemembered) _solved.Clear();
                _solved[(end, frame)] = solved;
            }
        }
        return solved.TryGetValue(bone, out var rotation) ? rotation : bone.RotationAt(frame);
    }
}
