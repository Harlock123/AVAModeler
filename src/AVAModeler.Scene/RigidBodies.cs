using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>Whether a rigid body moves by the simulation (dynamic) or by its keys, pushing dynamic ones (static).</summary>
public enum RigidBodyKind { Dynamic, Static }

/// <summary>The shape a rigid body collides as: its box, a ball round it, or a convex fit to its points (26 sides).</summary>
public enum CollisionShape { Box, Sphere, Hull }

/// <summary>
/// A rigid body on an object: it falls, bounces, slides, tumbles and stacks against the scene's other rigid bodies
/// (dynamic ones moved by the simulation, static ones by their keys) and an optional floor. Lengths in metres, times
/// in seconds, angles in degrees. Immutable: change it with <c>with</c> and <see cref="Checked"/> it.
/// </summary>
public sealed record RigidBodySettings
{
    public RigidBodyKind Kind { get; init; } = RigidBodyKind.Dynamic;
    public CollisionShape Shape { get; init; } = CollisionShape.Hull;
    /// <summary>Its mass in kilograms (dynamic bodies).</summary>
    public double Mass { get; init; } = 1;
    /// <summary>How much it bounces, 0 (a thud) to 1 (springy).</summary>
    public double Bounce { get; init; } = 0.3;
    /// <summary>How much it grips, 0 (ice) to 2 (rubber).</summary>
    public double Friction { get; init; } = 0.5;
    /// <summary>How fast its motion dies away in the air, a second (0 for none).</summary>
    public double Damping { get; init; } = 0.05;
    /// <summary>Downward pull (m/s²) down the world's −Y: 9.81 is Earth's.</summary>
    public double Gravity { get; init; } = 9.81;
    /// <summary>Its speed [x, y, z] (m/s, the world's axes) when it starts moving, added to the speed its keys give it then.</summary>
    public Vec3 Velocity { get; init; }
    /// <summary>Its spin [x, y, z] (degrees a second about the world's axes) when it starts moving.</summary>
    public Vec3 Spin { get; init; }
    /// <summary>Carry the motion its keys give it at the start frame (a throw keyed before it's let go), when that's after the scene's first frame.</summary>
    public bool InheritMotion { get; init; } = true;
    /// <summary>A floor at this height (m) it can't go below, or null for none (static bodies make floors too).</summary>
    public double? Floor { get; init; }
    /// <summary>The frame it starts moving from (as its keys have it then); null for the scene's first frame.</summary>
    public double? StartFrame { get; init; }
    /// <summary>Steps a frame: more is steadier and slower.</summary>
    public int Substeps { get; init; } = 10;

    public const int MaxBodies = 500;
    public const int MaxFrames = 20000;

    public RigidBodySettings Checked()
    {
        static void In(double v, double min, double max, string what)
        {
            if (!double.IsFinite(v) || v < min || v > max) throw new ArgumentException($"The rigid body's {what} must be from {min} to {max} (it's {v}).");
        }
        In(Mass, 0.001, 1e6, "mass (kg)");
        In(Bounce, 0, 1, "bounce");
        In(Friction, 0, 2, "friction");
        In(Damping, 0, 100, "damping (a second)");
        In(Gravity, -1000, 1000, "gravity (m/s²)");
        if (Substeps is < 1 or > 100) throw new ArgumentException($"The rigid body's substeps must be from 1 to 100 (it's {Substeps}).");
        foreach (var (v, what) in new[] { (Velocity, "velocity"), (Spin, "spin") })
            if (!double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Z) || v.Length > 1e5) throw new ArgumentException($"The rigid body's {what} must be three numbers.");
        if (Floor is { } f) In(f, -1e6, 1e6, "floor (m)");
        if (StartFrame is { } s) In(s, -1e6, 1e6, "start frame");
        return this;
    }
}

/// <summary>
/// The scene's rigid bodies, simulated together from the earliest start frame and remembered frame by frame until the
/// scene changes. <see cref="MotionSolver"/> asks it where a dynamic body is, so views, renders, particles and soft
/// bodies all see the same motion. Safe to use from several threads.
/// </summary>
public sealed class RigidBodyWorld
{
    private readonly Scene _scene;
    private readonly Lock _lock = new();
    private int _version = int.MinValue;
    private Sim? _sim;

    internal RigidBodyWorld(Scene scene) => _scene = scene;

    /// <summary>[ThreadStatic] true while the simulation itself is reading the scene, so a dynamic body there is placed by its keys.</summary>
    [ThreadStatic] private static bool t_inside;

    /// <summary>
    /// Where a dynamic body is at a frame (its position in its parent's space and its heading, pitch and bank), or null
    /// before it starts moving (its keys place it) or when it isn't a dynamic rigid body.
    /// </summary>
    public (Vec3 Position, Vec3 Rotation)? Pose(ObjectItem o, double frame)
    {
        if (t_inside || o.RigidBody is not { Kind: RigidBodyKind.Dynamic } || o.IsPlaceholder) return null;
        lock (_lock)
        {
            if (_version != _scene.Version || _sim == null)
            {
                _version = _scene.Version;
                t_inside = true;
                try { _sim = Sim.Build(_scene); }
                finally { t_inside = false; }
            }
            if (_sim == null || !_sim.Index.TryGetValue(o, out int i)) return null;
            var body = _sim.Bodies[i];
            if (frame <= body.Start) return null;
            t_inside = true;
            try { return _sim.PoseAt(i, frame); }
            finally { t_inside = false; }
        }
    }

    /// <summary>Whether the simulation is being run on this thread (scene reads then see keys, not simulated poses).</summary>
    public static bool Simulating => t_inside;

    // ───────────────────────────── the simulation ─────────────────────────────

    private sealed class Body
    {
        public required ObjectItem Item;
        public required RigidBodySettings S;
        public bool Dynamic;
        public double Start;
        public Vec3 Center;          // in the object's own coordinates
        public Vec3 Scale;
        public Vec3 Half;            // half extents, scaled
        public double Radius;        // ball radius, scaled
        public Vec3[] Samples = [];  // scaled, from the centre, in the body's frame
        public Vec3[] Normals = [];  // hull planes
        public double[] Offsets = [];
        public double InvMass;
        public Vec3 InvInertia;      // body frame, diagonal
        // State (world): centre, orientation, speed, spin.
        public Vec3 X, V, W;
        public Quat Q = Quat.Identity;
        public bool Moving;          // past its start frame
        public double Still;         // seconds nearly still
        public bool Asleep;
        // Kinematic frame (statics, and dynamics before their start).
        public Vec3 KX0, KX1;
        public Quat KQ0 = Quat.Identity, KQ1 = Quat.Identity;
    }

    private sealed class Sim
    {
        public readonly List<Body> Bodies = [];
        public readonly Dictionary<ObjectItem, int> Index = new(ReferenceEqualityComparer.Instance);
        public Scene Scene = null!;
        public double First;
        public int Substeps;
        public double Fps;
        public readonly List<(Vec3 X, Quat Q)[]> Frames = [];   // whole frames from First

        public static Sim? Build(Scene scene)
        {
            var sim = new Sim { Scene = scene, Fps = scene.FramesPerSecond > 0 ? scene.FramesPerSecond : 30 };
            foreach (var o in scene.Objects)
            {
                if (o.RigidBody is not { } s || o.IsPlaceholder || !o.Visible) continue;
                var mesh = o.BaseMesh();
                if (mesh.Points.Count == 0) continue;
                if (sim.Bodies.Count >= RigidBodySettings.MaxBodies) break;
                var b = new Body { Item = o, S = s, Dynamic = s.Kind == RigidBodyKind.Dynamic, Start = s.StartFrame ?? scene.FirstFrame };
                Shape(b, mesh, o.ScaleAt(b.Start));
                sim.Index[o] = sim.Bodies.Count;
                sim.Bodies.Add(b);
            }
            if (!sim.Bodies.Any(b => b.Dynamic)) return null;
            sim.First = sim.Bodies.Where(b => b.Dynamic).Min(b => b.Start);
            sim.Substeps = sim.Bodies.Where(b => b.Dynamic).Max(b => b.S.Substeps);
            foreach (var b in sim.Bodies) sim.Kinematic(b, sim.First, sim.First);
            sim.Frames.Add([.. sim.Bodies.Select(b => (b.X, b.Q))]);
            return sim;
        }

        private static readonly Vec3[] Directions = MakeDirections();

        private static Vec3[] MakeDirections()
        {
            var list = new List<Vec3>();
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (x != 0 || y != 0 || z != 0) list.Add(new Vec3(x, y, z).Normalized());
            return [.. list];
        }

        private static void Shape(Body b, Mesh mesh, Vec3 scale)
        {
            b.Scale = scale;
            var pts = mesh.Points.Select(p => new Vec3(p.X * scale.X, p.Y * scale.Y, p.Z * scale.Z)).ToList();
            var box = Box3.Of(pts);
            var c = (box.Min + box.Max) * 0.5;
            b.Center = new Vec3(scale.X != 0 ? c.X / scale.X : 0, scale.Y != 0 ? c.Y / scale.Y : 0, scale.Z != 0 ? c.Z / scale.Z : 0);
            var rel = pts.Select(p => p - c).ToList();
            b.Half = (box.Max - box.Min) * 0.5;
            b.Radius = Math.Max(1e-4, rel.Max(p => p.Length));
            switch (b.S.Shape)
            {
                case CollisionShape.Sphere:
                    b.Samples = [];
                    break;
                case CollisionShape.Box:
                {
                    var h = b.Half;
                    var s = new List<Vec3>();
                    for (int i = 0; i < 8; i++) s.Add(new Vec3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z));
                    foreach (var (a, d) in new[] { (0, 1), (0, 2), (1, 3), (2, 3), (4, 5), (4, 6), (5, 7), (6, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                        s.Add((s[a] + s[d]) * 0.5);
                    b.Samples = [.. s];
                    break;
                }
                default:
                {
                    b.Normals = Directions;
                    b.Offsets = [.. Directions.Select(n => rel.Max(p => Vec3.Dot(n, p)))];
                    // The points furthest out every which way: the corners it lands on.
                    var chosen = new HashSet<int>();
                    var more = Fibonacci(48);
                    foreach (var n in Directions.Concat(more))
                    {
                        int best = 0;
                        double far = double.NegativeInfinity;
                        for (int i = 0; i < rel.Count; i++)
                        {
                            double d = Vec3.Dot(n, rel[i]);
                            if (d > far) { far = d; best = i; }
                        }
                        chosen.Add(best);
                    }
                    b.Samples = [.. chosen.Select(i => rel[i])];
                    break;
                }
            }
            double m = b.S.Mass;
            b.InvMass = b.Dynamic ? 1 / m : 0;
            Vec3 inertia;
            if (b.S.Shape == CollisionShape.Sphere)
            {
                double i = 0.4 * m * b.Radius * b.Radius;
                inertia = new Vec3(i, i, i);
            }
            else
            {
                var e = b.Half * 2;
                inertia = new Vec3(m / 12 * (e.Y * e.Y + e.Z * e.Z), m / 12 * (e.X * e.X + e.Z * e.Z), m / 12 * (e.X * e.X + e.Y * e.Y));
            }
            static double Inv(double v) => v > 1e-12 ? 1 / v : 0;
            b.InvInertia = b.Dynamic ? new Vec3(Inv(inertia.X), Inv(inertia.Y), Inv(inertia.Z)) : Vec3.Zero;
        }

        private static Vec3[] Fibonacci(int n)
        {
            var list = new Vec3[n];
            double golden = Math.PI * (3 - Math.Sqrt(5));
            for (int i = 0; i < n; i++)
            {
                double y = 1 - 2 * (i + 0.5) / n, r = Math.Sqrt(1 - y * y), a = golden * i;
                list[i] = new Vec3(r * Math.Cos(a), y, r * Math.Sin(a));
            }
            return list;
        }

        /// <summary>A body's centre and orientation as its keys (and parents) place it at a frame.</summary>
        private (Vec3 X, Quat Q) Keyed(Body b, double frame)
        {
            var m = b.Item.WorldMatrix(frame);
            return (m.TransformPoint(b.Center), Quat.FromMatrix(m));
        }

        /// <summary>Sets a kinematic body's frame for the substep from frame f0 to f1.</summary>
        private void Kinematic(Body b, double f0, double f1)
        {
            (b.KX0, b.KQ0) = Keyed(b, f0);
            (b.KX1, b.KQ1) = f1 == f0 ? (b.KX0, b.KQ0) : Keyed(b, f1);
            b.X = b.KX0;
            b.Q = b.KQ0;
        }

        public (Vec3 Position, Vec3 Rotation) PoseAt(int i, double frame)
        {
            double k = Math.Min(frame - First, RigidBodySettings.MaxFrames);
            int whole = (int)Math.Ceiling(k);
            while (Frames.Count <= whole) Step();
            int lo = Math.Max(0, (int)Math.Floor(k));
            double t = k - lo;
            var (x0, q0) = Frames[lo][i];
            var (x1, q1) = Frames[Math.Min(Frames.Count - 1, lo + 1)][i];
            var x = x0 + (x1 - x0) * t;
            var q = Quat.Slerp(q0, q1, t);
            return ToChannels(Bodies[i], frame, x, q);
        }

        /// <summary>A body's world centre and orientation as its channels: position in its parent's space, heading, pitch and bank.</summary>
        private static (Vec3, Vec3) ToChannels(Body b, double frame, Vec3 x, Quat q)
        {
            var o = b.Item;
            var parent = o.Parent;
            var toParent = parent != null ? parent.WorldMatrix(frame).Inverted() : Mat4.Identity;
            var rot = q.ToMatrix();
            var local = parent != null ? Unscaled(toParent) * rot : rot;
            var hpb = Rotations.FromMatrix(local);
            var s = b.Scale;
            var offset = new Vec3((b.Center.X - o.Pivot.X) * s.X, (b.Center.Y - o.Pivot.Y) * s.Y, (b.Center.Z - o.Pivot.Z) * s.Z);
            var position = toParent.TransformPoint(x) - local.TransformVector(offset);
            return (position, hpb);
        }

        private static Mat4 Unscaled(Mat4 m)
        {
            var x = new Vec3(m[0, 0], m[1, 0], m[2, 0]).Normalized();
            var y = new Vec3(m[0, 1], m[1, 1], m[2, 1]).Normalized();
            var z = new Vec3(m[0, 2], m[1, 2], m[2, 2]).Normalized();
            return Mat4.FromRows(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0, 0, 0, 0, 1);
        }

        private readonly record struct Contact(Body A, Body B, Vec3 Point, Vec3 Normal, double Depth, double Bounce, double Friction)
        {
            public double Normal0 { get; init; }
            public double AccN { get; init; }
            public double AccT { get; init; }
        }

        /// <summary>Simulates one more frame.</summary>
        private void Step()
        {
            int k = Frames.Count;
            double frame0 = First + k - 1, dt = 1 / (Fps * Substeps);
            for (int sub = 0; sub < Substeps; sub++)
            {
                double f0 = frame0 + (double)sub / Substeps, f1 = frame0 + (double)(sub + 1) / Substeps;
                foreach (var b in Bodies)
                {
                    if (b.Dynamic && b.Moving) continue;
                    if (b.Dynamic && f0 >= b.Start)
                    {
                        // Let go: from where its keys have it, with the speed they give it (and its own).
                        var (xa, qa) = Keyed(b, b.Start);
                        b.X = xa;
                        b.Q = qa;
                        b.V = b.S.Velocity;
                        b.W = b.S.Spin * (Math.PI / 180);
                        // Only motion keyed inside the scene's frames counts: a body placed at the first frame starts still.
                        if (b.S.InheritMotion && b.Start - 1 >= Scene.FirstFrame)
                        {
                            var (xb, qb) = Keyed(b, b.Start - 1.0 / Substeps);
                            b.V += (xa - xb) * (Fps * Substeps);
                            b.W += AngularVelocity(qb, qa, 1 / (Fps * Substeps));
                        }
                        b.Moving = true;
                        continue;
                    }
                    Kinematic(b, f0, f1);
                }
                Advance(dt);
            }
            Frames.Add([.. Bodies.Select(b => (b.X, b.Q))]);
        }

        private static Vec3 AngularVelocity(Quat from, Quat to, double dt)
        {
            var d = to * from.Conjugate();
            if (d.W < 0) d = new Quat(-d.W, -d.X, -d.Y, -d.Z);
            double angle = 2 * Math.Acos(Math.Clamp(d.W, -1, 1));
            double s = Math.Sqrt(Math.Max(0, 1 - d.W * d.W));
            if (s < 1e-9 || dt <= 0) return Vec3.Zero;
            return new Vec3(d.X / s, d.Y / s, d.Z / s) * (angle / dt);
        }

        private bool Active(Body b) => b.Dynamic && b.Moving;

        /// <summary>Whether impulses move a body now: a dynamic one that has started and isn't asleep (a sleeper holds like a static one).</summary>
        private static bool Free(Body b) => b.Dynamic && b.Moving && !b.Asleep;

        private void Advance(double dt)
        {
            // Forces.
            foreach (var b in Bodies)
            {
                if (!Active(b) || b.Asleep) continue;
                b.V += new Vec3(0, -b.S.Gravity, 0) * dt;
                double keep = Math.Exp(-b.S.Damping * dt);
                b.V *= keep;
                b.W *= keep;
            }
            var contacts = Contacts();
            // A sleeper wakes when something awake and moving touches it, or when nothing holds it up any more.
            var touched = new HashSet<Body>(ReferenceEqualityComparer.Instance);
            foreach (var c in contacts)
            {
                touched.Add(c.A);
                touched.Add(c.B);
                if (c.A.Asleep && Moving(c.B)) Wake(c.A);
                if (c.B.Asleep && Moving(c.A)) Wake(c.B);
            }
            foreach (var b in Bodies)
                if (b.Asleep && !touched.Contains(b)) Wake(b);
            // Speeds into each contact before solving, for bounces.
            for (int i = 0; i < contacts.Count; i++)
                contacts[i] = contacts[i] with { Normal0 = Vec3.Dot(Relative(contacts[i]), contacts[i].Normal) };
            for (int it = 0; it < 10; it++)
                for (int i = 0; i < contacts.Count; i++) contacts[i] = Solve(contacts[i], dt);
            // Move.
            foreach (var b in Bodies)
            {
                if (!b.Dynamic) { b.X = b.KX1; b.Q = b.KQ1; continue; }
                if (!b.Moving) { b.X = b.KX1; b.Q = b.KQ1; continue; }
                if (b.Asleep) continue;
                b.X += b.V * dt;
                b.Q = (b.Q + new Quat(0, b.W.X, b.W.Y, b.W.Z) * b.Q * (0.5 * dt)).Normalized();
                // Settling: a body that has stayed nearly still goes to sleep until something knocks it.
                if (b.V.Length < 0.05 && b.W.Length < 0.1) b.Still += dt; else b.Still = 0;
                if (b.Still > 0.3) { b.Asleep = true; b.V = b.W = Vec3.Zero; }
            }
            // Push apart what still overlaps: once a touching pair, by its deepest point, along the pair's average way out
            // (many points of one face resting on another mustn't add up to many pushes).
            foreach (var pair in contacts.GroupBy(c => (c.A, c.B)))
            {
                var deepest = pair.MaxBy(c => c.Depth);
                double fix = Math.Max(0, deepest.Depth - 0.002) * 0.4;
                if (fix <= 0) continue;
                var n = pair.Aggregate(Vec3.Zero, (s, c) => s + c.Normal * Math.Max(c.Depth, 1e-6)).Normalized();
                if (n.LengthSquared == 0) n = deepest.Normal;
                double wa = Free(deepest.A) ? deepest.A.InvMass : 0, wb = Free(deepest.B) ? deepest.B.InvMass : 0;
                if (wa + wb <= 0) continue;
                if (wa > 0) deepest.A.X += n * (fix * wa / (wa + wb));
                if (wb > 0) deepest.B.X -= n * (fix * wb / (wa + wb));
            }
        }

        private bool Moving(Body b) =>
            !b.Asleep && (Active(b) ? b.V.Length > 0.15 || b.W.Length > 0.3 : !b.Dynamic && ((b.KX1 - b.KX0).Length > 1e-7 || Math.Abs(Quat.Dot(b.KQ0, b.KQ1)) < 1 - 1e-10));

        private static void Wake(Body b)
        {
            b.Asleep = false;
            b.Still = 0;
        }

        private static Vec3 VelocityAt(Body b, Vec3 p, double dt)
        {
            if (b.Dynamic && b.Moving) return b.Asleep ? Vec3.Zero : b.V + Vec3.Cross(b.W, p - b.X);
            // Kinematic: from where its keys take the point over the substep.
            var local = b.KQ0.Conjugate().Rotate(p - b.KX0);
            var next = b.KX1 + b.KQ1.Rotate(local);
            return (next - p) / Math.Max(dt, 1e-9);
        }

        private double _dt => 1 / (Fps * Substeps);

        private Vec3 Relative(Contact c) => VelocityAt(c.A, c.Point, _dt) - VelocityAt(c.B, c.Point, _dt);

        private static Vec3 InvInertiaTimes(Body b, Vec3 v)
        {
            if (!Free(b)) return Vec3.Zero;
            var l = b.Q.Conjugate().Rotate(v);
            return b.Q.Rotate(new Vec3(l.X * b.InvInertia.X, l.Y * b.InvInertia.Y, l.Z * b.InvInertia.Z));
        }

        private static double EffectiveMass(Body a, Body b, Vec3 p, Vec3 n)
        {
            double k = (Free(a) ? a.InvMass : 0) + (Free(b) ? b.InvMass : 0);
            var ra = Vec3.Cross(p - a.X, n);
            var rb = Vec3.Cross(p - b.X, n);
            k += Vec3.Dot(Vec3.Cross(InvInertiaTimes(a, ra), p - a.X), n);
            k += Vec3.Dot(Vec3.Cross(InvInertiaTimes(b, rb), p - b.X), n);
            return k;
        }

        private static void Apply(Body b, Vec3 p, Vec3 impulse)
        {
            if (!Free(b)) return;
            b.V += impulse * b.InvMass;
            b.W += InvInertiaTimes(b, Vec3.Cross(p - b.X, impulse));
        }

        private Contact Solve(Contact c, double dt)
        {
            var n = c.Normal;
            var rel = Relative(c);
            double vn = Vec3.Dot(rel, n);
            double kn = EffectiveMass(c.A, c.B, c.Point, n);
            if (kn <= 1e-12) return c;
            double target = c.Normal0 < -0.8 ? -c.Bounce * c.Normal0 : 0;
            double lambda = (target - vn) / kn;
            double acc = Math.Max(0, c.AccN + lambda);
            lambda = acc - c.AccN;
            Apply(c.A, c.Point, n * lambda);
            Apply(c.B, c.Point, n * -lambda);
            c = c with { AccN = acc };
            // Friction, along the way it slides.
            rel = Relative(c);
            var slide = rel - n * Vec3.Dot(rel, n);
            double speed = slide.Length;
            if (speed > 1e-9)
            {
                var t = slide / speed;
                double kt = EffectiveMass(c.A, c.B, c.Point, t);
                if (kt > 1e-12)
                {
                    double lt = -speed / kt;
                    double accT = Math.Clamp(c.AccT + lt, -c.Friction * c.AccN, c.Friction * c.AccN);
                    lt = accT - c.AccT;
                    Apply(c.A, c.Point, t * lt);
                    Apply(c.B, c.Point, t * -lt);
                    c = c with { AccT = accT };
                }
            }
            return c;
        }

        // ───────────────────────────── contacts ─────────────────────────────

        private static readonly Body Ground = new()
        {
            Item = new ObjectItem("(floor)"), S = new RigidBodySettings { Kind = RigidBodyKind.Static }, Dynamic = false,
        };

        private List<Contact> Contacts()
        {
            var list = new List<Contact>();
            var boxes = Bodies.Select(Bounds).ToList();
            for (int i = 0; i < Bodies.Count; i++)
            {
                var a = Bodies[i];
                if (Active(a))
                {
                    // Its floor.
                    if (a.S.Floor is { } floor)
                    {
                        var ground = new Body { Item = Ground.Item, S = Ground.S, X = new Vec3(a.X.X, floor, a.X.Z), KX0 = new Vec3(a.X.X, floor, a.X.Z), KX1 = new Vec3(a.X.X, floor, a.X.Z) };
                        foreach (var p in WorldSamples(a))
                        {
                            double d = p.Point.Y - p.Reach - floor;
                            if (d < 0.003) list.Add(new Contact(a, ground, p.Point - new Vec3(0, p.Reach, 0), Vec3.UnitY, Math.Max(0, -d), a.S.Bounce, a.S.Friction));
                        }
                    }
                }
                for (int j = i + 1; j < Bodies.Count; j++)
                {
                    var b = Bodies[j];
                    if (!Active(a) && !Active(b)) continue;
                    if (!Overlap(boxes[i], boxes[j])) continue;
                    double bounce = (a.S.Bounce + b.S.Bounce) / 2, friction = Math.Sqrt(a.S.Friction * b.S.Friction);
                    // a's points in b, and b's in a.
                    foreach (var p in WorldSamples(a))
                    {
                        var (d, n) = Distance(b, p.Point, a.X - b.X);
                        d -= p.Reach;
                        if (d < 0.003) list.Add(new Contact(a, b, p.Point - n * p.Reach, n, Math.Max(0, -d), bounce, friction));
                    }
                    if (b.S.Shape == CollisionShape.Sphere && a.S.Shape == CollisionShape.Sphere) continue;
                    foreach (var p in WorldSamples(b))
                    {
                        var (d, n) = Distance(a, p.Point, b.X - a.X);
                        d -= p.Reach;
                        if (d < 0.003) list.Add(new Contact(a, b, p.Point - (-n) * p.Reach, -n, Math.Max(0, -d), bounce, friction));
                    }
                }
            }
            return list;
        }

        /// <summary>A body's collision points in the world, each with how far its surface reaches past it (a ball's radius).</summary>
        private static IEnumerable<(Vec3 Point, double Reach)> WorldSamples(Body b)
        {
            if (b.S.Shape == CollisionShape.Sphere) { yield return (b.X, b.Radius); yield break; }
            foreach (var s in b.Samples) yield return (b.X + b.Q.Rotate(s), 0);
        }

        /// <summary>
        /// How far a world point is outside a body's shape (negative inside), and the way out. Where faces tie (a point
        /// on an edge or corner, as when equal boxes are stacked), the one facing <paramref name="toward"/> (the other
        /// body) wins, so a resting contact pushes straight apart rather than sideways.
        /// </summary>
        private static (double Distance, Vec3 Normal) Distance(Body b, Vec3 world, Vec3 toward)
        {
            var p = b.Q.Conjugate().Rotate(world - b.X);
            var hint = b.Q.Conjugate().Rotate(toward);
            const double tie = 1e-3;
            double d;
            Vec3 n;
            switch (b.S.Shape)
            {
                case CollisionShape.Sphere:
                {
                    double l = p.Length;
                    d = l - b.Radius;
                    n = l > 1e-12 ? p / l : Vec3.UnitY;
                    break;
                }
                case CollisionShape.Box:
                {
                    var h = b.Half;
                    var q = new Vec3(Math.Abs(p.X) - h.X, Math.Abs(p.Y) - h.Y, Math.Abs(p.Z) - h.Z);
                    var outside = new Vec3(Math.Max(q.X, 0), Math.Max(q.Y, 0), Math.Max(q.Z, 0));
                    if (outside.LengthSquared > 0 && outside.Length > tie)
                    {
                        d = outside.Length;
                        n = new Vec3(outside.X * Math.Sign(p.X), outside.Y * Math.Sign(p.Y), outside.Z * Math.Sign(p.Z)).Normalized();
                    }
                    else
                    {
                        // Inside (or touching): out through the nearest face, the one facing the other body among ties.
                        double most = Math.Max(q.X, Math.Max(q.Y, q.Z));
                        d = outside.LengthSquared > 0 ? outside.Length : most;
                        n = Vec3.UnitY;
                        double best = double.NegativeInfinity;
                        for (int axis = 0; axis < 3; axis++)
                        {
                            if (q[axis] < most - tie) continue;
                            var face = axis == 0 ? new Vec3(p.X >= 0 ? 1 : -1, 0, 0) : axis == 1 ? new Vec3(0, p.Y >= 0 ? 1 : -1, 0) : new Vec3(0, 0, p.Z >= 0 ? 1 : -1);
                            double score = Vec3.Dot(face, hint);
                            if (score > best) { best = score; n = face; }
                        }
                    }
                    break;
                }
                default:
                {
                    d = double.NegativeInfinity;
                    n = Vec3.UnitY;
                    for (int i = 0; i < b.Normals.Length; i++) d = Math.Max(d, Vec3.Dot(b.Normals[i], p) - b.Offsets[i]);
                    double best = double.NegativeInfinity;
                    for (int i = 0; i < b.Normals.Length; i++)
                    {
                        if (Vec3.Dot(b.Normals[i], p) - b.Offsets[i] < d - tie) continue;
                        double score = Vec3.Dot(b.Normals[i], hint);
                        if (score > best) { best = score; n = b.Normals[i]; }
                    }
                    break;
                }
            }
            return (d, b.Q.Rotate(n));
        }

        private static Box3 Bounds(Body b)
        {
            double r = b.S.Shape == CollisionShape.Sphere ? b.Radius : b.Half.Length;
            var e = new Vec3(r + 0.01, r + 0.01, r + 0.01);
            return new Box3(b.X - e, b.X + e);
        }

        private static bool Overlap(Box3 a, Box3 b) =>
            a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
    }

    /// <summary>A rotation as a unit quaternion.</summary>
    private readonly record struct Quat(double W, double X, double Y, double Z)
    {
        public static readonly Quat Identity = new(1, 0, 0, 0);

        public static Quat operator *(Quat a, Quat b) => new(
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

        public static Quat operator *(Quat a, double s) => new(a.W * s, a.X * s, a.Y * s, a.Z * s);
        public static Quat operator +(Quat a, Quat b) => new(a.W + b.W, a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public Quat Conjugate() => new(W, -X, -Y, -Z);

        public static double Dot(Quat a, Quat b) => a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public Quat Normalized()
        {
            double l = Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
            return l > 1e-12 ? new Quat(W / l, X / l, Y / l, Z / l) : Identity;
        }

        public Vec3 Rotate(Vec3 v)
        {
            var r = this * new Quat(0, v.X, v.Y, v.Z) * Conjugate();
            return new Vec3(r.X, r.Y, r.Z);
        }

        public Mat4 ToMatrix()
        {
            var x = Rotate(Vec3.UnitX);
            var y = Rotate(Vec3.UnitY);
            var z = Rotate(Vec3.UnitZ);
            return Mat4.FromRows(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0, 0, 0, 0, 1);
        }

        /// <summary>The rotation in a matrix (its scale taken out).</summary>
        public static Quat FromMatrix(Mat4 m)
        {
            var x = new Vec3(m[0, 0], m[1, 0], m[2, 0]).Normalized();
            var y = new Vec3(m[0, 1], m[1, 1], m[2, 1]).Normalized();
            var z = new Vec3(m[0, 2], m[1, 2], m[2, 2]).Normalized();
            double m00 = x.X, m11 = y.Y, m22 = z.Z, trace = m00 + m11 + m22;
            Quat q;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1) * 2;
                q = new Quat(0.25 * s, (y.Z - z.Y) / s, (z.X - x.Z) / s, (x.Y - y.X) / s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
                q = new Quat((y.Z - z.Y) / s, 0.25 * s, (y.X + x.Y) / s, (z.X + x.Z) / s);
            }
            else if (m11 > m22)
            {
                double s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
                q = new Quat((z.X - x.Z) / s, (y.X + x.Y) / s, 0.25 * s, (z.Y + y.Z) / s);
            }
            else
            {
                double s = Math.Sqrt(1 + m22 - m00 - m11) * 2;
                q = new Quat((x.Y - y.X) / s, (z.X + x.Z) / s, (z.Y + y.Z) / s, 0.25 * s);
            }
            return q.Normalized();
        }

        public static Quat Slerp(Quat a, Quat b, double t)
        {
            double dot = a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            if (dot < 0) { b = b * -1; dot = -dot; }
            if (dot > 0.9995) return (a * (1 - t) + b * t).Normalized();
            double theta = Math.Acos(dot), s = Math.Sin(theta);
            return (a * (Math.Sin((1 - t) * theta) / s) + b * (Math.Sin(t * theta) / s)).Normalized();
        }
    }
}
