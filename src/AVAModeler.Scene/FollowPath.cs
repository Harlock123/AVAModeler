using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// How an item follows a path (<see cref="SceneItem.Path"/>): a smooth curve through other items (nulls, usually) at
/// their positions in the world, in the order given, open or closed into a loop. Where along it the item is comes from
/// its keyable <see cref="SceneItem.PathPosition"/> channel ("path.position": 0 the start, 1 the end; a closed path's
/// positions wrap, so 2 is twice round). With <see cref="AlignToPath"/> its heading and pitch turn to face along the
/// path (looking <see cref="LookAhead"/> metres ahead, or along the tangent with 0), as LightWave's Align to Path
/// did; <see cref="Banking"/> leans it into turns. Its position channels and (when aligned) heading and pitch are
/// ignored while it follows; its bank channel adds to the banking. Immutable: change it with <c>with</c>.
/// </summary>
public sealed record FollowPath
{
    /// <summary>The items the path runs through, in order.</summary>
    public IReadOnlyList<SceneItem> Points { get; init; } = [];

    /// <summary>Whether the path closes back to its first point (a loop).</summary>
    public bool Closed { get; init; }

    /// <summary>Turn the item's +Z axis (heading and pitch) along the path.</summary>
    public bool AlignToPath { get; init; } = true;

    /// <summary>How far ahead along the path an aligned item looks, in metres (0: along the tangent).</summary>
    public double LookAhead { get; init; }

    /// <summary>
    /// How much an aligned item leans into turns: its bank in degrees on a turn of 1 m radius, proportionally less on
    /// wider ones (bank = banking ÷ radius, at most 90°), the inside of the turn going down. 0: no banking.
    /// </summary>
    public double Banking { get; init; }

    /// <summary>The fewest points a path needs: two open, three closed.</summary>
    public int Fewest => Closed ? 3 : 2;

    /// <summary>These settings, or an <see cref="ArgumentException"/> saying what's wrong with them (points aside: see <see cref="Problem"/>).</summary>
    public FollowPath Checked()
    {
        if (!double.IsFinite(LookAhead) || LookAhead < 0) throw new ArgumentException("The look-ahead must be 0 metres or more.");
        if (!double.IsFinite(Banking)) throw new ArgumentException("Banking must be a number of degrees (0 for none).");
        if (Points.Count < Fewest) throw new ArgumentException($"A{(Closed ? " closed" : "n open")} path needs at least {Fewest} points, not {Points.Count}.");
        return this;
    }

    /// <summary>
    /// Why these points can't make a path for <paramref name="item"/>, or null if they can: each must be in the
    /// item's scene, once, and neither the item nor anything that moves with it (it would chase itself).
    /// </summary>
    public static string? Problem(SceneItem item, IReadOnlyList<SceneItem> points)
    {
        var seen = new HashSet<SceneItem>();
        foreach (var p in points)
        {
            if (p == item) return $"{item.Name} can't be a point of its own path.";
            if (!seen.Add(p)) return $"{p.Name} is in the path twice: give each point once (close the path to come back to the start).";
            if (item.Scene != null && p.Scene != item.Scene) return $"{p.Name} isn't in {item.Name}'s scene.";
            for (var a = p.Parent; a != null; a = a.Parent)
                if (a == item) return $"{p.Name} moves with {item.Name}, so the path would move as {item.Name} follows it: parent it to something else.";
            if (p is Bone b && (b.Owner == item || b.Owner.Ancestors().Contains(item))) return $"{p.Name} is a bone of {b.Owner.Name}, which moves with {item.Name}.";
        }
        return null;
    }

    /// <summary>
    /// Bakes path following into keys: the item's position (and, when aligned, its heading, pitch and bank) at every
    /// <paramref name="step"/> frames from <paramref name="first"/> to <paramref name="last"/> (and at last), as its
    /// motion makes them, keyed by <paramref name="key"/> (which applies the keying rule); then it stops following, so
    /// the keys play. Every frame is worked out before a key is written. Returns the frames keyed.
    /// </summary>
    public static IReadOnlyList<double> Bake(SceneItem item, double first, double last, double step, Action<Envelope, double, double> key)
    {
        if (item.Path is not { } path) throw new InvalidOperationException($"{item.Name} doesn't follow a path, so there's nothing to bake.");
        if (!(step > 0) || !double.IsFinite(step)) throw new ArgumentException("The step must be more than 0 frames.");
        if (last < first) throw new ArgumentException($"The last frame ({last:0.###}) is before the first ({first:0.###}).");
        var frames = new List<double>();
        for (double f = first; f < last - 1e-9; f += step) frames.Add(f);
        frames.Add(last);
        var poses = frames.Select(f => (item.MotionPositionAt(f), item.MotionRotationAt(f))).ToList();
        for (int i = 0; i < frames.Count; i++)
        {
            var (p, r) = poses[i];
            for (int c = 0; c < 3; c++) key(item.Channels[c], frames[i], p[c]);
            if (path.AlignToPath)
            {
                // Heading unwrapped frame to frame, so a loop's keys don't spin back the long way between them.
                var h = r.X;
                if (i > 0) h += 360 * Math.Round((poses[i - 1].Item2.X - h) / 360);
                poses[i] = (p, r with { X = h });
                key(item.Channels[3], frames[i], h);
                key(item.Channels[4], frames[i], r.Y);
                key(item.Channels[5], frames[i], r.Z);
            }
        }
        item.Path = null;
        item.Scene?.Notify();
        return frames;
    }
}

/// <summary>
/// A smooth curve through points (a centripetal Catmull–Rom spline, which neither overshoots into loops nor cusps
/// between close points), open or closed, measured along its length so a position from 0 to 1 moves along it evenly.
/// </summary>
public sealed class MotionPath
{
    private const int SamplesPerSegment = 48;
    private readonly Vec3[] _points;
    private readonly bool _closed;
    private readonly double[] _lengths;     // arc length at each sample
    private readonly int _segments;

    public MotionPath(IReadOnlyList<Vec3> points, bool closed)
    {
        if (points.Count < 2) throw new ArgumentException("A path needs at least two points.");
        _points = [.. points];
        _closed = closed && points.Count >= 3;
        _segments = _closed ? _points.Length : _points.Length - 1;
        _lengths = new double[_segments * SamplesPerSegment + 1];
        var last = Evaluate(0, 0);
        for (int i = 1; i < _lengths.Length; i++)
        {
            var p = Evaluate((i - 1) / SamplesPerSegment, (double)((i - 1) % SamplesPerSegment + 1) / SamplesPerSegment);
            _lengths[i] = _lengths[i - 1] + (p - last).Length;
            last = p;
        }
    }

    /// <summary>The path's length in metres.</summary>
    public double Length => _lengths[^1];

    public bool Closed => _closed;

    /// <summary>A control point by index, wrapping round a closed path and reflected past the ends of an open one.</summary>
    private Vec3 P(int i)
    {
        int n = _points.Length;
        if (_closed) return _points[((i % n) + n) % n];
        if (i < 0) return 2 * _points[0] - _points[1];
        if (i >= n) return 2 * _points[n - 1] - _points[n - 2];
        return _points[i];
    }

    /// <summary>The point on segment <paramref name="segment"/> (from point i to point i + 1) at <paramref name="u"/> from 0 to 1.</summary>
    private Vec3 Evaluate(int segment, double u)
    {
        var p0 = P(segment - 1);
        var p1 = P(segment);
        var p2 = P(segment + 1);
        var p3 = P(segment + 2);
        static double Knot(Vec3 a, Vec3 b) => Math.Max(Math.Sqrt((b - a).Length), 1e-6);
        double t0 = 0, t1 = t0 + Knot(p0, p1), t2 = t1 + Knot(p1, p2), t3 = t2 + Knot(p2, p3);
        double t = t1 + (t2 - t1) * u;
        // Barry and Goldman's pyramid.
        var a1 = p0 * ((t1 - t) / (t1 - t0)) + p1 * ((t - t0) / (t1 - t0));
        var a2 = p1 * ((t2 - t) / (t2 - t1)) + p2 * ((t - t1) / (t2 - t1));
        var a3 = p2 * ((t3 - t) / (t3 - t2)) + p3 * ((t - t2) / (t3 - t2));
        var b1 = a1 * ((t2 - t) / (t2 - t0)) + a2 * ((t - t0) / (t2 - t0));
        var b2 = a2 * ((t3 - t) / (t3 - t1)) + a3 * ((t - t1) / (t3 - t1));
        return b1 * ((t2 - t) / (t2 - t1)) + b2 * ((t - t1) / (t2 - t1));
    }

    /// <summary>A distance along the path, wrapped round a closed one and kept to the ends of an open one.</summary>
    public double Wrap(double distance)
    {
        double l = Length;
        if (l <= 0) return 0;
        return _closed ? distance - l * Math.Floor(distance / l) : Math.Clamp(distance, 0, l);
    }

    /// <summary>The point a distance along the path (metres from its start).</summary>
    public Vec3 PointAt(double distance)
    {
        double s = Wrap(distance);
        int hi = Array.BinarySearch(_lengths, s);
        if (hi >= 0) return Sample(hi);
        hi = Math.Clamp(~hi, 1, _lengths.Length - 1);
        int lo = hi - 1;
        double span = _lengths[hi] - _lengths[lo];
        double f = span > 0 ? (s - _lengths[lo]) / span : 0;
        int segment = lo / SamplesPerSegment;
        double u = (lo % SamplesPerSegment + f) / SamplesPerSegment;
        return Evaluate(Math.Min(segment, _segments - 1), segment >= _segments ? 1 : u);
    }

    private Vec3 Sample(int i) => i >= _lengths.Length - 1 ? Evaluate(_segments - 1, 1) : Evaluate(i / SamplesPerSegment, (double)(i % SamplesPerSegment) / SamplesPerSegment);

    /// <summary>The point at a position from 0 (the start) to 1 (the end; a closed path's positions wrap).</summary>
    public Vec3 At(double position) => PointAt(position * Length);

    /// <summary>The direction along the path at a distance (a unit vector; the first segment's at an open path's start).</summary>
    public Vec3 TangentAt(double distance)
    {
        double d = Math.Max(Length * 1e-4, 1e-6);
        double a = distance - d, b = distance + d;
        if (!_closed) { a = Math.Max(0, Math.Min(a, Length - 2 * d)); b = Math.Min(Length, Math.Max(b, 2 * d)); }
        var t = PointAt(b) - PointAt(a);
        return t.Length > 1e-12 ? t.Normalized() : Vec3.UnitZ;
    }

    /// <summary>
    /// How fast the path turns at a distance, seen from above: the change in its heading per metre along it, in radians
    /// (positive turning right, toward +X from +Z; 1 over the turn's radius).
    /// </summary>
    public double TurnAt(double distance)
    {
        // Measured over a stretch of the path either side, so banking eases in and out rather than following every
        // wobble of the spline between its points.
        double d = Math.Max(Length * 0.02, 1e-4);
        if (!_closed) distance = Math.Clamp(distance, d, Math.Max(d, Length - d));
        var a = TangentAt(distance - d);
        var b = TangentAt(distance + d);
        if (new Vec3(a.X, 0, a.Z).Length < 1e-6 || new Vec3(b.X, 0, b.Z).Length < 1e-6) return 0;
        double turn = Math.Atan2(b.X, b.Z) - Math.Atan2(a.X, a.Z);
        turn -= 2 * Math.PI * Math.Round(turn / (2 * Math.PI));
        return turn / (2 * d);
    }

    /// <summary>Points along the whole path, evenly by length, for drawing it.</summary>
    public IReadOnlyList<Vec3> Polyline(int count)
    {
        count = Math.Max(2, count);
        var r = new Vec3[count];
        for (int i = 0; i < count; i++) r[i] = PointAt(Length * i / (count - 1));
        return r;
    }

    /// <summary>
    /// The curve an item following <paramref name="path"/> is on at a frame (through its points' world positions), the
    /// world point it's at and how far along the curve that is; null if the points can't make a path (too few, or gone).
    /// </summary>
    internal static (MotionPath Path, Vec3 Point, double Distance)? Of(SceneItem item, FollowPath path, double frame)
    {
        if (path.Points.Count < path.Fewest || path.Points.Any(p => p.Scene != item.Scene)) return null;
        var points = path.Points.Select(p => p.WorldPosition(frame)).ToList();
        var curve = new MotionPath(points, path.Closed);
        double s = curve.Wrap(item.PathPosition.Evaluate(frame) * curve.Length);
        return (curve, curve.PointAt(s), s);
    }
}
