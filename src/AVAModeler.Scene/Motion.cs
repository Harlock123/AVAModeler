using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// What moves a scene's items beyond their keys, worked out when asked and remembered until the scene changes: the
/// rotations full-time IK gives bones (<see cref="InverseKinematics"/>). Each <see cref="Scene"/> has one
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

    /// <summary>The chains being solved on this thread, so a goal that leads back to its own chain can't recurse forever.</summary>
    [ThreadStatic] private static HashSet<Bone>? t_solving;

    internal MotionSolver(Scene scene) => _scene = scene;

    /// <summary>Starts again if the scene has changed since (call inside the lock).</summary>
    private void Refresh()
    {
        if (_version == _scene.Version) return;
        _version = _scene.Version;
        _solved.Clear();
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

    /// <summary>An item's rotation as it moves at a frame (see <see cref="SceneItem.MotionRotationAt"/>).</summary>
    public Vec3 Rotation(SceneItem item, double frame)
    {
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
