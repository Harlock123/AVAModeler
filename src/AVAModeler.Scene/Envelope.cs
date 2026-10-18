namespace AVAModeler.Scene;

/// <summary>What an envelope does before its first key or after its last.</summary>
public enum EndBehavior
{
    /// <summary>The value is 0.</summary>
    Reset,
    /// <summary>The value holds at the end key's value.</summary>
    Constant,
    /// <summary>The keyed span repeats.</summary>
    Repeat,
    /// <summary>The keyed span plays forwards, then backwards, and so on.</summary>
    Oscillate,
    /// <summary>The keyed span repeats, shifted each time by the difference between the last and first values.</summary>
    OffsetRepeat,
    /// <summary>The curve carries on in a straight line at the end key's slope.</summary>
    Linear,
}

/// <summary>
/// A key: a value at a frame, and the Kochanek–Bartels tension, continuity and bias that shape the curve through it
/// (each from -1 to 1; 0 is a plain Catmull–Rom-like curve, tension 1 flattens the curve at the key). When
/// <see cref="Linear"/> is set, the segment that ends at this key is a straight line, as with LightWave's linear flag.
/// </summary>
public readonly record struct EnvelopeKey(double Frame, double Value, double Tension = 0, double Continuity = 0, double Bias = 0, bool Linear = false);

/// <summary>
/// An animation channel: keys interpolated by LightWave's classic TCB (Kochanek–Bartels) spline, with behaviours
/// before the first key and after the last. Keys are kept in frame order and no two share a frame. With one key the
/// envelope is constant; with none it is 0.
/// </summary>
public sealed class Envelope
{
    private const double SameFrame = 1e-9;
    private readonly List<EnvelopeKey> _keys = [];
    private EndBehavior _pre = EndBehavior.Constant, _post = EndBehavior.Constant;

    public Envelope() { }

    /// <summary>A constant envelope: one key at frame 0.</summary>
    public Envelope(double value) => _keys.Add(new EnvelopeKey(0, value));

    public static Envelope Constant(double value) => new(value);

    public IReadOnlyList<EnvelopeKey> Keys => _keys;

    /// <summary>Goes up on every change.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after any change to the keys or behaviours.</summary>
    public event Action? Changed;

    public EndBehavior PreBehavior
    {
        get => _pre;
        set { if (_pre != value) { _pre = value; Touch(); } }
    }

    public EndBehavior PostBehavior
    {
        get => _post;
        set { if (_post != value) { _post = value; Touch(); } }
    }

    /// <summary>Whether the value never changes (no more than one key).</summary>
    public bool IsConstant => _keys.Count <= 1;

    private void Touch()
    {
        Version++;
        Changed?.Invoke();
    }

    /// <summary>Replaces every key with a single one holding a value.</summary>
    public void SetConstant(double value)
    {
        _keys.Clear();
        _keys.Add(new EnvelopeKey(0, value));
        Touch();
    }

    public void Clear()
    {
        _keys.Clear();
        Touch();
    }

    /// <summary>The index of the key at a frame, or -1.</summary>
    public int IndexOf(double frame) => _keys.FindIndex(k => Math.Abs(k.Frame - frame) < SameFrame);

    private int Insert(EnvelopeKey key)
    {
        int i = IndexOf(key.Frame);
        if (i >= 0)
        {
            _keys[i] = key;
            return i;
        }
        i = 0;
        while (i < _keys.Count && _keys[i].Frame < key.Frame) i++;
        _keys.Insert(i, key);
        return i;
    }

    /// <summary>Adds a key, replacing any key at the same frame. Returns its index.</summary>
    public int AddKey(EnvelopeKey key)
    {
        int i = Insert(key);
        Touch();
        return i;
    }

    /// <summary>Sets the value at a frame: a key there keeps its tension, continuity, bias and linear flag.</summary>
    public int AddKey(double frame, double value)
    {
        int i = IndexOf(frame);
        return AddKey(i >= 0 ? _keys[i] with { Value = value } : new EnvelopeKey(frame, value));
    }

    /// <summary>Replaces a key (its frame may change; the keys are re-sorted). Returns its new index.</summary>
    public int SetKey(int index, EnvelopeKey key)
    {
        _keys.RemoveAt(index);
        int i = Insert(key);
        Touch();
        return i;
    }

    /// <summary>Moves a key to another frame, replacing any key already there. Returns its new index.</summary>
    public int MoveKey(int index, double frame) => SetKey(index, _keys[index] with { Frame = frame });

    public void RemoveKey(int index)
    {
        _keys.RemoveAt(index);
        Touch();
    }

    /// <summary>Removes the key at a frame, if there is one.</summary>
    public bool RemoveKeyAt(double frame)
    {
        int i = IndexOf(frame);
        if (i < 0) return false;
        RemoveKey(i);
        return true;
    }

    /// <summary>Replaces this envelope's keys and behaviours with a copy of another's.</summary>
    public void CopyFrom(Envelope other)
    {
        _keys.Clear();
        _keys.AddRange(other._keys);
        _pre = other._pre;
        _post = other._post;
        Touch();
    }

    public Envelope Clone()
    {
        var e = new Envelope();
        e.CopyFrom(this);
        return e;
    }

    /// <summary>Multiplies every key's frame by a factor (for converting between seconds and frames).</summary>
    public void ScaleTime(double factor)
    {
        for (int i = 0; i < _keys.Count; i++) _keys[i] = _keys[i] with { Frame = _keys[i].Frame * factor };
        Touch();
    }

    /// <summary>The value at a frame (fractional frames are fine).</summary>
    public double Evaluate(double frame)
    {
        int n = _keys.Count;
        if (n == 0) return 0;
        if (n == 1) return _keys[0].Value;
        var first = _keys[0];
        var last = _keys[^1];
        double offset = 0, t = frame;
        if (frame < first.Frame || frame > last.Frame)
        {
            bool before = frame < first.Frame;
            switch (before ? _pre : _post)
            {
                case EndBehavior.Reset:
                    return 0;
                case EndBehavior.Constant:
                    return before ? first.Value : last.Value;
                case EndBehavior.Linear:
                    return before
                        ? first.Value + StartSlope() * (frame - first.Frame)
                        : last.Value + EndSlope() * (frame - last.Frame);
                case var b:
                    double span = last.Frame - first.Frame;
                    double cycles = Math.Floor((frame - first.Frame) / span);
                    t = frame - cycles * span;
                    if (b == EndBehavior.Oscillate && Math.Abs(cycles % 2) == 1) t = first.Frame + last.Frame - t;
                    else if (b == EndBehavior.OffsetRepeat) offset = cycles * (last.Value - first.Value);
                    break;
            }
        }
        int i = Segment(t);
        var k0 = _keys[i];
        var k1 = _keys[i + 1];
        if (t <= k0.Frame) return k0.Value + offset;
        if (t >= k1.Frame) return k1.Value + offset;
        double u = (t - k0.Frame) / (k1.Frame - k0.Frame);
        if (k1.Linear) return k0.Value + u * (k1.Value - k0.Value) + offset;
        double u2 = u * u, u3 = u2 * u;
        double h1 = 2 * u3 - 3 * u2 + 1, h2 = -2 * u3 + 3 * u2, h3 = u3 - 2 * u2 + u, h4 = u3 - u2;
        return h1 * k0.Value + h2 * k1.Value + h3 * Outgoing(i) + h4 * Incoming(i) + offset;
    }

    /// <summary>The index of the key that starts the segment holding a frame (clamped to the first and last segments).</summary>
    private int Segment(double frame)
    {
        int lo = 0, hi = _keys.Count - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_keys[mid].Frame <= frame) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    // The tangents follow LightWave's own envelope code: Kochanek–Bartels tangents scaled for uneven key spacing
    // by the ratio of this segment's length to the length of the two segments around the key.

    /// <summary>The tangent leaving key i, for the segment from key i to key i + 1 (in value per segment).</summary>
    private double Outgoing(int i)
    {
        var k0 = _keys[i];
        var k1 = _keys[i + 1];
        double d = k1.Value - k0.Value;
        double a = 1, b = 1;
        if (!k0.Linear)
        {
            a = (1 - k0.Tension) * (1 + k0.Continuity) * (1 + k0.Bias);
            b = (1 - k0.Tension) * (1 - k0.Continuity) * (1 - k0.Bias);
        }
        if (i == 0) return b * d;
        var prev = _keys[i - 1];
        double t = (k1.Frame - k0.Frame) / (k1.Frame - prev.Frame);
        return t * (a * (k0.Value - prev.Value) + b * d);
    }

    /// <summary>The tangent arriving at key i + 1, for the segment from key i to key i + 1 (in value per segment).</summary>
    private double Incoming(int i)
    {
        var k0 = _keys[i];
        var k1 = _keys[i + 1];
        double d = k1.Value - k0.Value;
        double a = 1, b = 1;
        if (!k1.Linear)
        {
            a = (1 - k1.Tension) * (1 - k1.Continuity) * (1 + k1.Bias);
            b = (1 - k1.Tension) * (1 + k1.Continuity) * (1 - k1.Bias);
        }
        if (i + 2 >= _keys.Count) return a * d;
        var next = _keys[i + 2];
        double t = (k1.Frame - k0.Frame) / (next.Frame - k0.Frame);
        return t * (b * (next.Value - k1.Value) + a * d);
    }

    private double StartSlope()
    {
        double span = _keys[1].Frame - _keys[0].Frame;
        return _keys[1].Linear ? (_keys[1].Value - _keys[0].Value) / span : Outgoing(0) / span;
    }

    private double EndSlope()
    {
        int i = _keys.Count - 2;
        double span = _keys[i + 1].Frame - _keys[i].Frame;
        return _keys[i + 1].Linear ? (_keys[i + 1].Value - _keys[i].Value) / span : Incoming(i) / span;
    }

    /// <summary>Evenly spaced samples of the curve from one frame to another, for drawing it in a graph editor.</summary>
    public (double Frame, double Value)[] Sample(double from, double to, int count)
    {
        if (count < 1) return [];
        if (count == 1) return [(from, Evaluate(from))];
        var r = new (double, double)[count];
        for (int i = 0; i < count; i++)
        {
            double f = from + (to - from) * i / (count - 1);
            r[i] = (f, Evaluate(f));
        }
        return r;
    }
}
