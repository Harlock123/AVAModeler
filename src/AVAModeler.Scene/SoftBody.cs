using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// Cloth or a soft body on an object: its points move under gravity, held together by its edges (and, more weakly,
/// across its faces so it doesn't fold flat), drawn back toward its animated shape by <see cref="Goal"/> (0 for cloth,
/// more for a jiggly soft body), held fast where <see cref="Pin"/>'s faces are, and kept above the floor and out of
/// the scene's other objects. Lengths in metres, times in seconds. Immutable: change it with <c>with</c> and
/// <see cref="Checked"/> it.
/// </summary>
public sealed record SoftBodySettings
{
    /// <summary>How firmly its edges keep their length, 0 (stretchy) to 1 (not at all).</summary>
    public double Stiffness { get; init; } = 0.9;
    /// <summary>How firmly it resists folding, 0 (a sheet that drapes) to 1 (stiff card).</summary>
    public double Bend { get; init; } = 0.1;
    /// <summary>How strongly it's drawn back to its animated shape, 0 (cloth: none) to 1 (it follows exactly).</summary>
    public double Goal { get; init; }
    /// <summary>How fast its motion dies away (a second): 0 swings on, more settles sooner.</summary>
    public double Damping { get; init; } = 0.5;
    /// <summary>Downward pull (m/s²) down the world's −Y: 9.81 is Earth's.</summary>
    public double Gravity { get; init; } = 9.81;
    /// <summary>A floor at this height (m) it can't go below, or null for none.</summary>
    public double? Floor { get; init; }
    /// <summary>Kept out of the scene's other visible objects (as they move).</summary>
    public bool Collide { get; init; }
    /// <summary>How far it stays off the floor and objects (m).</summary>
    public double Thickness { get; init; } = 0.005;
    /// <summary>Hold fast the points of this surface's faces (by name), so they move only as the object is animated; empty for none.</summary>
    public string Pin { get; init; } = "";
    /// <summary>The frame it starts moving from (as the object is then); null for the scene's first frame.</summary>
    public double? StartFrame { get; init; }
    /// <summary>Steps a frame: more is steadier and slower.</summary>
    public int Substeps { get; init; } = 8;
    /// <summary>Times a step its edges are put right: more is firmer and slower.</summary>
    public int Iterations { get; init; } = 8;

    /// <summary>The most points simulated on one object.</summary>
    public const int MaxPoints = 20000;

    public SoftBodySettings Checked()
    {
        static void In(double v, double min, double max, string what)
        {
            if (!double.IsFinite(v) || v < min || v > max) throw new ArgumentException($"The soft body's {what} must be from {min} to {max} (it's {v}).");
        }
        In(Stiffness, 0, 1, "stiffness");
        In(Bend, 0, 1, "bend");
        In(Goal, 0, 1, "goal");
        In(Damping, 0, 100, "damping (a second)");
        In(Gravity, -1000, 1000, "gravity (m/s²)");
        In(Thickness, 0, 1, "thickness (m)");
        In(Substeps, 1, 64, "substeps");
        In(Iterations, 1, 64, "iterations");
        if (Floor is { } f) In(f, -1e6, 1e6, "floor (m)");
        if (StartFrame is { } s) In(s, -100000, 100000, "start frame");
        return this;
    }
}

/// <summary>
/// Soft bodies worked out by stepping (position-based dynamics: move, then put the edges right), from the start frame
/// up to the one asked for. Each object keeps the state it reached at whole frames, so playing forward costs one frame
/// a frame, and any frame comes out the same however it's reached; any change to the scene starts it again.
/// </summary>
public static class SoftBodies
{
    /// <summary>How much state a body keeps (Vec3s, places and speeds together): past it, it keeps every n-th frame.</summary>
    private const long Budget = 20_000_000;

    internal sealed class State
    {
        public required object Key;
        public required int[] A, B;
        public required double[] Rest, Weight;
        public required bool[] Pinned;
        public required double Start;
        public required int Stride;
        /// <summary>Places and speeds (world) at the start frame plus i × stride.</summary>
        public readonly List<(Vec3[] X, Vec3[] V)> Frames = [];
        public (double Frame, Mesh Mesh)? Last;
    }

    /// <summary>
    /// The object's mesh at <paramref name="frame"/> moved by its soft body (in its own coordinates), from
    /// <paramref name="deformed"/>, its mesh as animated then. Before the start frame, or with more points than
    /// <see cref="SoftBodySettings.MaxPoints"/>, it's the animated mesh.
    /// </summary>
    internal static Mesh At(Scene scene, ObjectItem o, SoftBodySettings s, Mesh deformed, double frame)
    {
        double start = s.StartFrame ?? scene.FirstFrame;
        if (frame <= start || deformed.Points.Count == 0 || deformed.Points.Count > SoftBodySettings.MaxPoints) return deformed;
        var baseMesh = o.BaseMesh();
        var key = (s, scene.Version, scene.FramesPerSecond, start, baseMesh, baseMesh.Version, deformed.Points.Count);
        lock (o.SoftLock)
        {
            var state = o.SoftCache as State;
            if (state == null || !state.Key.Equals(key))
            {
                state = Build(scene, o, s, start, key);
                o.SoftCache = state;
            }
            if (state.Last is { } last && last.Frame == frame) return last.Mesh;
            var (x, _) = Run(scene, o, s, state, frame);
            var toLocal = o.WorldMatrix(frame).Inverted();
            var mesh = new Mesh { Points = [.. x.Select(toLocal.TransformPoint)], Polygons = deformed.Polygons, UVMaps = deformed.UVMaps };
            mesh.Touch();
            state.Last = (frame, mesh);
            return mesh;
        }
    }

    /// <summary>The object's animated mesh in the world at a frame.</summary>
    private static Vec3[] Target(ObjectItem o, double frame)
    {
        var m = o.WorldMatrix(frame);
        return [.. o.DeformedMesh(frame).Points.Select(m.TransformPoint)];
    }

    private static State Build(Scene scene, ObjectItem o, SoftBodySettings s, double start, object key)
    {
        var x0 = Target(o, start);
        var mesh = o.DeformedMesh(start);
        int n = x0.Length;
        var pinned = new bool[n];
        if (s.Pin.Length > 0)
        {
            int surface = o.Surfaces.ToList().FindIndex(x => x.Name == s.Pin);
            if (surface >= 0)
                foreach (var poly in mesh.Polygons.Where(p => p.Surface == surface))
                    foreach (int i in poly.Points) pinned[i] = true;
        }
        // Edges (stiffness), and across faces and folds (bend): pairs of points kept at their starting distance.
        var pairs = new Dictionary<(int, int), double>();
        void Pair(int a, int b, double weight)
        {
            if (a == b) return;
            var k = a < b ? (a, b) : (b, a);
            if (!pairs.TryGetValue(k, out double w) || w < weight) pairs[k] = weight;
        }
        var faces = new Dictionary<(int, int), List<int>>();
        foreach (var poly in mesh.Polygons)
        {
            var p = poly.Points;
            int c = p.Count;
            if (c < 2) continue;
            for (int i = 0; i < (c == 2 ? 1 : c); i++)
            {
                int a = p[i], b = p[(i + 1) % c];
                Pair(a, b, s.Stiffness);
                if (c < 3) continue;
                var k = a < b ? (a, b) : (b, a);
                // The point after the edge in this face: across the fold from the next face's.
                (faces.TryGetValue(k, out var l) ? l : faces[k] = []).Add(p[(i + 2) % c]);
            }
            // Shear: a quad's diagonals.
            if (c == 4)
            {
                Pair(p[0], p[2], s.Stiffness * 0.5);
                Pair(p[1], p[3], s.Stiffness * 0.5);
            }
        }
        foreach (var (_, opposite) in faces)
            if (opposite.Count == 2) Pair(opposite[0], opposite[1], s.Bend);
        var list = pairs.Where(kv => kv.Value > 0).ToList();
        // A share each iteration that comes to the setting's over all of them.
        double Share(double k) => k >= 1 ? 1 : 1 - Math.Pow(1 - k, 1.0 / s.Iterations);
        long frames = Math.Max(1, scene.LastFrame - (long)start + 1);
        int stride = (int)Math.Max(1, Math.Ceiling(2.0 * n * frames / Budget));
        var state = new State
        {
            Key = key, Start = start, Stride = stride, Pinned = pinned,
            A = [.. list.Select(kv => kv.Key.Item1)], B = [.. list.Select(kv => kv.Key.Item2)],
            Rest = [.. list.Select(kv => (x0[kv.Key.Item1] - x0[kv.Key.Item2]).Length)], Weight = [.. list.Select(kv => Share(kv.Value))],
        };
        state.Frames.Add((x0, new Vec3[n]));
        return state;
    }

    /// <summary>The places and speeds at a frame (from the nearest kept frame before it, keeping those it passes).</summary>
    private static (Vec3[] X, Vec3[] V) Run(Scene scene, ObjectItem o, SoftBodySettings s, State state, double frame)
    {
        int i = Math.Min(state.Frames.Count - 1, (int)Math.Floor((frame - state.Start) / state.Stride));
        var (x, v) = state.Frames[i];
        x = (Vec3[])x.Clone();
        v = (Vec3[])v.Clone();
        double at = state.Start + (long)i * state.Stride;
        double fps = scene.FramesPerSecond;
        var colliders = s.Collide ? Colliders.Of(scene, [o], frame, 1 / (fps * s.Substeps)) : null;
        var from = Target(o, at);
        while (at < frame - 1e-9)
        {
            double next = Math.Min(Math.Floor(at + 1e-9) + 1, frame);
            var to = Target(o, next);
            Step(s, state, x, v, from, to, at, next, fps, colliders);
            at = next;
            from = to;
            // Kept at every stride-th whole frame reached for the first time.
            if (Math.Abs(at - Math.Round(at)) < 1e-9 && ((long)Math.Round(at - state.Start)) % state.Stride == 0)
            {
                int index = (int)((long)Math.Round(at - state.Start) / state.Stride);
                if (index == state.Frames.Count) state.Frames.Add(((Vec3[])x.Clone(), (Vec3[])v.Clone()));
            }
        }
        return (x, v);
    }

    /// <summary>One frame's (or part frame's) substeps, the animated shape going from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static void Step(SoftBodySettings s, State state, Vec3[] x, Vec3[] v, Vec3[] from, Vec3[] to, double f0, double f1, double fps,
        Colliders? colliders)
    {
        int n = x.Length;
        int steps = Math.Max(1, (int)Math.Ceiling((f1 - f0) * s.Substeps - 1e-9));
        double h = (f1 - f0) / fps / steps;
        var g = new Vec3(0, -s.Gravity, 0);
        double fade = Math.Exp(-s.Damping * h);
        // The goal's pull a step, so that over a 30th of a second it closes the setting's share of the gap.
        double goal = s.Goal >= 1 ? 1 : 1 - Math.Pow(1 - s.Goal, h * 30);
        var p = new Vec3[n];
        for (int k = 1; k <= steps; k++)
        {
            double w = (double)k / steps;
            double time = (f0 + (f1 - f0) * w) / fps;
            for (int i = 0; i < n; i++)
            {
                var target = from[i] + (to[i] - from[i]) * w;
                if (state.Pinned[i]) { p[i] = target; continue; }
                v[i] = (v[i] + g * h) * fade;
                p[i] = x[i] + v[i] * h;
                if (goal > 0) p[i] += (target - p[i]) * goal;
            }
            for (int it = 0; it < s.Iterations; it++)
                for (int c = 0; c < state.A.Length; c++)
                {
                    int a = state.A[c], b = state.B[c];
                    bool pa = state.Pinned[a], pb = state.Pinned[b];
                    if (pa && pb) continue;
                    var d = p[b] - p[a];
                    double len = d.Length;
                    if (len < 1e-12) continue;
                    var corr = d * ((len - state.Rest[c]) / len * state.Weight[c]);
                    if (pa) p[b] -= corr;
                    else if (pb) p[a] += corr;
                    else
                    {
                        p[a] += corr * 0.5;
                        p[b] -= corr * 0.5;
                    }
                }
            for (int i = 0; i < n; i++)
            {
                if (state.Pinned[i]) { x[i] = p[i]; v[i] = Vec3.Zero; continue; }
                if (s.Floor is { } floor && p[i].Y < floor + s.Thickness) p[i] = new Vec3(p[i].X, floor + s.Thickness, p[i].Z);
                if (colliders != null && colliders.Hit(x[i], p[i], time, out double f, out var nrm, out _))
                {
                    if (Vec3.Dot(p[i] - x[i], nrm) > 0) nrm = -nrm;
                    p[i] = x[i] + (p[i] - x[i]) * f + nrm * s.Thickness;
                }
                v[i] = (p[i] - x[i]) / h;
                x[i] = p[i];
            }
        }
    }
}
