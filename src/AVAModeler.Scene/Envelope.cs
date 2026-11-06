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
/// How the stretch of curve that ends at a key (from the key before it) is drawn. The span belongs to the key it
/// ends at, as LightWave's own span types do.
/// </summary>
public enum SpanKind
{
    /// <summary>Kochanek–Bartels: a smooth curve shaped by the keys' tension, continuity and bias (the default).</summary>
    Tcb,
    /// <summary>A straight line from the previous key (the same as <see cref="EnvelopeKey.Linear"/>).</summary>
    Linear,
    /// <summary>Holds the previous key's value until this key's frame, then jumps to this key's value.</summary>
    Stepped,
    /// <summary>
    /// A cubic Hermite curve through explicit slopes: the previous key's <see cref="EnvelopeKey.OutSlope"/> leaving it and
    /// this key's <see cref="EnvelopeKey.InSlope"/> arriving here, both in value per frame. It is the same curve as a
    /// one-dimensional Bézier with its handles a third of the way along the span (LightWave's Hermite and 1D Bézier
    /// spans), so one kind covers both.
    /// </summary>
    Bezier,
}

/// <summary>
/// A key: a value at a frame, and the Kochanek–Bartels tension, continuity and bias that shape the curve through it
/// (each from -1 to 1; 0 is a plain Catmull–Rom-like curve, tension 1 flattens the curve at the key). When
/// <see cref="Linear"/> is set, the segment that ends at this key is a straight line, as with LightWave's linear flag.
/// <para>
/// <see cref="Span"/> says how the segment ending at this key is drawn (<see cref="Kind"/> is the one that counts:
/// the <see cref="Linear"/> flag on a <see cref="SpanKind.Tcb"/> key makes it linear, as old code and files set it;
/// <see cref="WithSpan"/> keeps the two in step). <see cref="InSlope"/> and <see cref="OutSlope"/> are the curve's
/// slopes arriving at and leaving the key, in value per frame: a <see cref="SpanKind.Bezier"/> span uses the previous
/// key's out-slope and its own in-slope, and the TCB span after a Bezier key leaves it at its out-slope. Both are 0 by
/// default, and a key with the defaults evaluates exactly as keys did before spans existed.
/// </para>
/// </summary>
public readonly record struct EnvelopeKey(double Frame, double Value, double Tension = 0, double Continuity = 0, double Bias = 0, bool Linear = false,
    SpanKind Span = SpanKind.Tcb, double InSlope = 0, double OutSlope = 0)
{
    /// <summary>How the segment ending at this key is drawn: <see cref="Span"/>, or Linear for a TCB key with the linear flag.</summary>
    public SpanKind Kind => Span == SpanKind.Tcb && Linear ? SpanKind.Linear : Span;

    /// <summary>This key with another span kind, the linear flag set for <see cref="SpanKind.Linear"/> and cleared otherwise.</summary>
    public EnvelopeKey WithSpan(SpanKind kind) => this with { Span = kind, Linear = kind == SpanKind.Linear };

    /// <summary>Whether the span and slopes are the defaults (a TCB key with no slopes).</summary>
    public bool HasDefaultSpan => Kind == SpanKind.Tcb && InSlope == 0 && OutSlope == 0;
}

/// <summary>
/// An animation channel: keys interpolated by LightWave's classic TCB (Kochanek–Bartels) spline, or span by span as
/// linear, stepped or Bezier (<see cref="SpanKind"/>), with behaviours before the first key and after the last. Keys are kept in frame order and no two share a frame. With one key the
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

    /// <summary>
    /// Multiplies every key's frame by a factor (for converting between seconds and frames). Slopes, in value per
    /// frame, are divided by it, so Bezier spans keep their shape.
    /// </summary>
    public void ScaleTime(double factor)
    {
        for (int i = 0; i < _keys.Count; i++)
        {
            var k = _keys[i];
            _keys[i] = k.InSlope == 0 && k.OutSlope == 0
                ? k with { Frame = k.Frame * factor }
                : k with { Frame = k.Frame * factor, InSlope = k.InSlope / factor, OutSlope = k.OutSlope / factor };
        }
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
        var kind = k1.Kind;
        if (kind == SpanKind.Linear) return k0.Value + u * (k1.Value - k0.Value) + offset;
        if (kind == SpanKind.Stepped) return k0.Value + offset;
        double u2 = u * u, u3 = u2 * u;
        double h1 = 2 * u3 - 3 * u2 + 1, h2 = -2 * u3 + 3 * u2, h3 = u3 - 2 * u2 + u, h4 = u3 - u2;
        if (kind == SpanKind.Bezier)
        {
            double len = k1.Frame - k0.Frame;
            return h1 * k0.Value + h2 * k1.Value + h3 * (k0.OutSlope * len) + h4 * (k1.InSlope * len) + offset;
        }
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
    // by the ratio of this segment's length to the length of the two segments around the key. A TCB span's tangent
    // leaving a key depends on how the span before that key is drawn, as in LightWave: after a Bezier span it is the
    // key's out-slope (so a Bezier key's handle carries on smoothly), after a stepped span it is flat (the curve leaves
    // the jump level), and after a linear or TCB span it is the Kochanek–Bartels tangent (a linear key's with its
    // tension, continuity and bias ignored). The tangent arriving at a TCB key is always its own Kochanek–Bartels one,
    // from its neighbours' values whatever their spans.

    /// <summary>The tangent leaving key i, for the TCB segment from key i to key i + 1 (in value per segment).</summary>
    private double Outgoing(int i)
    {
        var k0 = _keys[i];
        var k1 = _keys[i + 1];
        switch (k0.Kind)
        {
            case SpanKind.Bezier: return k0.OutSlope * (k1.Frame - k0.Frame);
            case SpanKind.Stepped: return 0;
        }
        double d = k1.Value - k0.Value;
        double a = 1, b = 1;
        if (!k0.Linear && k0.Span != SpanKind.Linear)
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
        if (!k1.Linear && k1.Span != SpanKind.Linear)
        {
            a = (1 - k1.Tension) * (1 - k1.Continuity) * (1 + k1.Bias);
            b = (1 - k1.Tension) * (1 + k1.Continuity) * (1 - k1.Bias);
        }
        if (i + 2 >= _keys.Count) return a * d;
        var next = _keys[i + 2];
        double t = (k1.Frame - k0.Frame) / (next.Frame - k0.Frame);
        return t * (b * (next.Value - k1.Value) + a * d);
    }

    /// <summary>
    /// The curve's slopes at key <paramref name="index"/>, in value per frame: arriving from the span before it and
    /// leaving along the span after it (0 where there is no span; a stepped span is flat at both ends). Turning a key
    /// into a Bezier key with these slopes (and the previous key's out-slope set to its <c>Out</c>) keeps the curve.
    /// </summary>
    public (double In, double Out) Slopes(int index)
    {
        if (index < 0 || index >= _keys.Count) throw new ArgumentOutOfRangeException(nameof(index));
        double inSlope = index > 0 ? SpanSlopes(index - 1).End : 0;
        double outSlope = index < _keys.Count - 1 ? SpanSlopes(index).Start : 0;
        return (inSlope, outSlope);
    }

    /// <summary>The slopes (value per frame) at the start and end of the span from key i to key i + 1.</summary>
    private (double Start, double End) SpanSlopes(int i)
    {
        var k0 = _keys[i];
        var k1 = _keys[i + 1];
        double span = k1.Frame - k0.Frame;
        return k1.Kind switch
        {
            SpanKind.Linear => ((k1.Value - k0.Value) / span, (k1.Value - k0.Value) / span),
            SpanKind.Stepped => (0, 0),
            SpanKind.Bezier => (k0.OutSlope, k1.InSlope),
            _ => (Outgoing(i) / span, Incoming(i) / span),
        };
    }

    // The same arithmetic as before spans existed for TCB and linear ends, so a Linear end behaviour is unchanged.
    private double StartSlope() => SpanSlopes(0).Start;

    private double EndSlope() => SpanSlopes(_keys.Count - 2).End;

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
