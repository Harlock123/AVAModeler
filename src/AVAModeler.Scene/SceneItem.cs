using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>An item's nine motion channels, in LightWave's order.</summary>
public enum Channel { PositionX, PositionY, PositionZ, Heading, Pitch, Bank, ScaleX, ScaleY, ScaleZ }

/// <summary>
/// Something in a scene that can move: an object, a null, a light, a camera or a bone. It has a parent (any item, or
/// none), a pivot, nine animated channels (position, heading/pitch/bank in degrees, and scale), and a visibility flag.
/// Its local transform is translate(position) · rotation · scale · translate(-pivot): the pivot, in the item's own
/// coordinates, is the point it rotates and scales about, and it sits at the item's position. Its world transform is
/// its parent's world transform times its local one.
/// <para>Changes to envelopes are noticed by the scene; after changing a field directly, call
/// <see cref="Scene.Notify"/>.</para>
/// </summary>
public abstract class SceneItem
{
    private readonly Envelope[] _channels = new Envelope[9];
    private SceneItem? _parent;

    public string Name;
    public Vec3 Pivot;
    public bool Visible = true;

    /// <summary>The scene the item belongs to (set when it is added to one).</summary>
    public Scene? Scene { get; internal set; }

    protected SceneItem(string name)
    {
        Name = name;
        for (int i = 0; i < 9; i++) _channels[i] = Watch(new Envelope(i >= 6 ? 1 : 0));
    }

    /// <summary>The channels, indexed by <see cref="Channel"/>.</summary>
    public IReadOnlyList<Envelope> Channels => _channels;

    public Envelope this[Channel channel] => _channels[(int)channel];

    /// <summary>The kind of item, for lists and file formats.</summary>
    public abstract string Kind { get; }

    /// <summary>Makes changes to an envelope the item owns bump the scene's version.</summary>
    protected Envelope Watch(Envelope e)
    {
        e.Changed += () => Scene?.Notify();
        return e;
    }

    /// <summary>The item this one moves with. Setting a parent that would make a cycle throws.</summary>
    public SceneItem? Parent
    {
        get => _parent;
        set
        {
            if (value == _parent) return;
            if (value != null)
            {
                for (var p = value; p != null; p = p.TransformParent)
                    if (p == this) throw new InvalidOperationException($"Parenting {Name} to {value.Name} would make a cycle.");
                if (Scene != null && value.Scene != null && value.Scene != Scene)
                    throw new InvalidOperationException("An item's parent must be in the same scene.");
                CheckParent(value);
            }
            _parent = value;
            Scene?.Notify();
        }
    }

    /// <summary>Throws if an item can't be this one's parent (beyond the cycle check).</summary>
    protected virtual void CheckParent(SceneItem parent) { }

    /// <summary>The item whose world transform this one's local transform is applied to.</summary>
    internal virtual SceneItem? TransformParent => _parent;

    /// <summary>The parent, its parent, and so on.</summary>
    public IEnumerable<SceneItem> Ancestors()
    {
        for (var p = _parent; p != null; p = p.Parent) yield return p;
    }

    public Vec3 PositionAt(double frame) => Vector(0, frame);
    /// <summary>Heading, pitch and bank in degrees.</summary>
    public Vec3 RotationAt(double frame) => Vector(3, frame);
    public Vec3 ScaleAt(double frame) => Vector(6, frame);

    private Vec3 Vector(int first, double frame) =>
        new(_channels[first].Evaluate(frame), _channels[first + 1].Evaluate(frame), _channels[first + 2].Evaluate(frame));

    public Mat4 LocalMatrix(double frame) =>
        Mat4.Translation(PositionAt(frame)) * Rotations.Matrix(RotationAt(frame)) * Mat4.Scaling(ScaleAt(frame)) * Mat4.Translation(-Pivot);

    public Mat4 WorldMatrix(double frame) => WorldMatrix(frame, null);

    /// <summary>The world transform, reusing (and filling) a cache of world transforms already worked out at this frame.</summary>
    internal Mat4 WorldMatrix(double frame, Dictionary<SceneItem, Mat4>? cache)
    {
        if (cache != null && cache.TryGetValue(this, out var m)) return m;
        var parent = TransformParent;
        m = parent == null ? LocalMatrix(frame) : parent.WorldMatrix(frame, cache) * LocalMatrix(frame);
        if (cache != null) cache[this] = m;
        return m;
    }

    /// <summary>Where the item's pivot is in the world.</summary>
    public Vec3 WorldPosition(double frame) => WorldMatrix(frame).TransformPoint(Pivot);

    public void SetPosition(double frame, Vec3 p) => SetVector(0, frame, p);
    public void SetRotation(double frame, Vec3 hpb) => SetVector(3, frame, hpb);
    public void SetScale(double frame, Vec3 s) => SetVector(6, frame, s);

    private void SetVector(int first, double frame, Vec3 v)
    {
        _channels[first].AddKey(frame, v.X);
        _channels[first + 1].AddKey(frame, v.Y);
        _channels[first + 2].AddKey(frame, v.Z);
    }

    /// <summary>Keys every channel at a frame with its current value there (Layout's Create Key).</summary>
    public void CreateKey(double frame)
    {
        var values = _channels.Select(c => c.Evaluate(frame)).ToArray();
        for (int i = 0; i < 9; i++) _channels[i].AddKey(frame, values[i]);
    }

    /// <summary>Removes every channel's key at a frame (Layout's Delete Key).</summary>
    public void DeleteKey(double frame)
    {
        foreach (var c in _channels) c.RemoveKeyAt(frame);
    }

    /// <summary>The frames that hold a key on any channel, in order: the item's row in a dope sheet.</summary>
    public IReadOnlyList<double> KeyFrames()
    {
        var frames = new SortedSet<double>();
        foreach (var c in _channels)
            foreach (var k in c.Keys)
                if (!frames.Any(f => Math.Abs(f - k.Frame) < 1e-9)) frames.Add(k.Frame);
        return [.. frames];
    }

    /// <summary>A channel's curve sampled between two frames, for a graph editor.</summary>
    public (double Frame, double Value)[] Curve(Channel channel, double from, double to, int count) =>
        this[channel].Sample(from, to, count);

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>A null: an invisible item to parent things to and animate.</summary>
public sealed class NullItem(string name) : SceneItem(name)
{
    public override string Kind => "Null";
}
