using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>Where on its object a particle is born.</summary>
public enum EmitFrom
{
    /// <summary>Anywhere on the object's faces (by area), flying out along the face's normal.</summary>
    Surface,
    /// <summary>At the object's points, flying out along their faces' average normal.</summary>
    Points,
    /// <summary>At the object's pivot, flying out along its +Y.</summary>
    Origin,
}

/// <summary>How particles look.</summary>
public enum ParticleLook
{
    /// <summary>Small solid diamonds.</summary>
    Gem,
    /// <summary>Soft blobs that melt into each other where they meet (liquid, lava, goo): one smooth surface round them all.</summary>
    Blob,
    /// <summary>Soft puffs seen through (smoke, steam, cloud), lit by the scene's lights; the ray tracer draws them, the views show diamonds.</summary>
    Puff,
    /// <summary>Flat glowing spots that add light (sparks, fireflies, magic); the ray tracer draws them, the views show diamonds.</summary>
    Sprite,
    /// <summary>A copy of another object on each particle (debris, leaves, a flock), sized to the particle.</summary>
    Object,
}

/// <summary>What a particle does when it meets the floor or an object.</summary>
public enum ParticleHit
{
    /// <summary>Stops there, riding along with a moving object.</summary>
    Stick,
    /// <summary>Bounces off, losing speed by <see cref="ParticleSettings.Bounce"/> and <see cref="ParticleSettings.Friction"/>.</summary>
    Bounce,
    /// <summary>Is gone.</summary>
    Die,
}

/// <summary>
/// A particle emitter on an object: how many particles a second it sends out and when, from where, how fast, how long
/// they live, and what pulls on them. Times are in seconds, lengths in metres, angles in degrees; gravity pulls down
/// the world's −Y. Immutable: change it with <c>with</c> and <see cref="Checked"/> it.
/// </summary>
public sealed record ParticleSettings
{
    public EmitFrom From { get; init; } = EmitFrom.Surface;
    /// <summary>Particles a second while emitting.</summary>
    public double Rate { get; init; } = 50;
    /// <summary>The frames emission starts and stops at.</summary>
    public double StartFrame { get; init; }
    public double EndFrame { get; init; } = 60;
    /// <summary>How long each lives (s).</summary>
    public double Life { get; init; } = 2;
    /// <summary>How fast they leave (m/s), give or take <see cref="SpeedJitter"/> of it.</summary>
    public double Speed { get; init; } = 1;
    public double SpeedJitter { get; init; } = 0.3;
    /// <summary>How far from straight out they may leave (°): 0 a jet, 90 a hemisphere.</summary>
    public double Spread { get; init; } = 20;
    /// <summary>Downward pull (m/s²): 9.81 is Earth's.</summary>
    public double Gravity { get; init; } = 9.81;
    /// <summary>Air drag: how fast a particle's speed fades toward falling at its terminal speed (per second, 0 for none).</summary>
    public double Drag { get; init; }
    /// <summary>A particle's size (m), and what it's grown or shrunk to by the end of its life (× the size).</summary>
    public double Size { get; init; } = 0.02;
    public double SizeAtDeath { get; init; } = 1;
    /// <summary>A floor at this height (m) particles stop on, or null for none.</summary>
    public double? Floor { get; init; }
    /// <summary>The particles' colour, as 0–255 red, green and blue.</summary>
    public Rgb Color { get; init; } = new(255, 230, 160);
    /// <summary>Glow: their luminosity, 0 to 1 (sparks glow; dust doesn't).</summary>
    public double Glow { get; init; } = 0.5;
    /// <summary>Hide the emitter itself, so only its particles show.</summary>
    public bool HideEmitter { get; init; }
    /// <summary>Changes which particles come where, keeping the same settings.</summary>
    public int Seed { get; init; }
    /// <summary>How they look: diamonds, blobs, puffs, sprites or copies of an object.</summary>
    public ParticleLook Look { get; init; } = ParticleLook.Gem;
    /// <summary>For the Object look: the object (by name) copied onto each particle, its largest side the particle's size.</summary>
    public string InstanceOf { get; init; } = "";
    /// <summary>For the Object look: how fast each copy turns (°/s, about an axis of its own), from a turn of its own.</summary>
    public double Spin { get; init; }
    /// <summary>For the Object look: point each copy's +Z the way it's going (a flock), instead of turning at random.</summary>
    public bool AlignToMotion { get; init; }
    /// <summary>For puffs and sprites: how much a puff hides through its middle, or how bright a sprite is, 0 to 1.</summary>
    public double Opacity { get; init; } = 0.6;
    /// <summary>Meet the scene's other visible objects (as they move), as well as the floor.</summary>
    public bool Collide { get; init; }
    /// <summary>What a particle does when it meets the floor or an object.</summary>
    public ParticleHit OnHit { get; init; } = ParticleHit.Stick;
    /// <summary>For Bounce: how much of its speed into the surface it keeps, 0 (none) to 1 (all).</summary>
    public double Bounce { get; init; } = 0.5;
    /// <summary>For Bounce: how much of its speed along the surface it loses, 0 to 1.</summary>
    public double Friction { get; init; } = 0.2;

    /// <summary>The most particles alive at once that are drawn and rendered.</summary>
    public const int MaxAlive = 20000;

    public ParticleSettings Checked()
    {
        static void In(double v, double min, double max, string what)
        {
            if (!double.IsFinite(v) || v < min || v > max) throw new ArgumentException($"The particles' {what} must be from {min} to {max} (it's {v}).");
        }
        In(Rate, 0, 100000, "rate (a second)");
        In(StartFrame, -100000, 100000, "start frame");
        In(EndFrame, StartFrame, 100000, "end frame (from the start frame)");
        In(Life, 0.001, 3600, "life (s)");
        In(Speed, 0, 10000, "speed (m/s)");
        In(SpeedJitter, 0, 1, "speed jitter (a fraction)");
        In(Spread, 0, 180, "spread (°)");
        In(Gravity, -1000, 1000, "gravity (m/s²)");
        In(Drag, 0, 1000, "drag (a second)");
        In(Size, 0.00001, 100, "size (m)");
        In(SizeAtDeath, 0, 100, "size at death (× the size)");
        In(Glow, 0, 1, "glow");
        if (Floor is { } f) In(f, -1e6, 1e6, "floor (m)");
        In(Spin, -36000, 36000, "spin (°/s)");
        In(Opacity, 0, 1, "opacity");
        In(Bounce, 0, 1, "bounce");
        In(Friction, 0, 1, "friction");
        if (!Enum.IsDefined(Look)) throw new ArgumentException($"The particles' look must be one of {string.Join(", ", Enum.GetNames<ParticleLook>())}.");
        if (!Enum.IsDefined(OnHit)) throw new ArgumentException($"What particles do on a hit must be one of {string.Join(", ", Enum.GetNames<ParticleHit>())}.");
        if (Look == ParticleLook.Object && string.IsNullOrWhiteSpace(InstanceOf))
            throw new ArgumentException("Particles that look like an object need the object to copy (instance_of).");
        return this;
    }
}

/// <summary>What particles can meet as they fly: the scene's other objects, wherever they are at the time.</summary>
public interface IParticleColliders
{
    /// <summary>
    /// The first place the straight move from <paramref name="from"/> to <paramref name="to"/> (world) at
    /// <paramref name="time"/> (s) meets a collider: how far along it (0 to 1), the surface's normal there, and which collider.
    /// </summary>
    bool Hit(Vec3 from, Vec3 to, double time, out double fraction, out Vec3 normal, out int collider);

    /// <summary>Where a point stuck on a collider at <paramref name="from"/> (s) has been carried by <paramref name="to"/>.</summary>
    Vec3 Carry(int collider, Vec3 point, double from, double to);
}

/// <summary>
/// Particles worked out at a time, not stepped through it: each one's birth time, place and starting velocity come from
/// a hash of its number and the seed, and its path after that is the closed form of gravity and drag, so any frame is
/// the same however it's reached (scrubbing, rendering in parallel, from a copy of the scene).
/// </summary>
public static class ParticleSystem
{
    /// <summary>
    /// One particle alive at a time: where it is (world), its size, how far through its life (0 to 1), how fast it's
    /// going (world, m/s; zero once stopped), and its number (for anything it picks at random).
    /// </summary>
    public readonly record struct Particle(Vec3 Position, double Size, double Age, Vec3 Velocity = default, long Index = 0);

    /// <summary>
    /// The time step particles that meet things move in (s): half a frame, or longer for long lives, so no particle
    /// takes more than 2000 steps. Colliders are looked up on the same steps.
    /// </summary>
    public static double StepOf(ParticleSettings s, double fps) => Math.Max(1 / (2 * fps), s.Life / 2000);

    /// <summary>
    /// The particles alive at <paramref name="frame"/>. <paramref name="emitterAt"/> gives the emitter's world
    /// transform at a frame; <paramref name="rest"/> is the emitter's mesh (object coordinates) to emit from.
    /// </summary>
    public static List<Particle> At(ParticleSettings s, double frame, double fps, Mesh rest, Func<double, Mat4> emitterAt, IParticleColliders? colliders = null)
    {
        var alive = new List<Particle>();
        if (s.Rate <= 0 || fps <= 0) return alive;
        double t = frame / fps, start = s.StartFrame / fps, end = s.EndFrame / fps;
        double first = Math.Max(start, t - s.Life), last = Math.Min(end, t);
        if (last < first) return alive;
        long k0 = Math.Max(0, (long)Math.Ceiling((first - start) * s.Rate - 1e-9)), k1 = (long)Math.Floor((last - start) * s.Rate + 1e-9);
        if (k1 - k0 + 1 > ParticleSettings.MaxAlive) k0 = k1 - ParticleSettings.MaxAlive + 1;
        var source = Source.Of(rest, s.From);
        var g = new Vec3(0, -s.Gravity, 0);
        // Stepped through time (and so slower) only when particles can meet objects or do more than stop on the floor.
        bool stepped = colliders != null || (s.Floor != null && s.OnHit != ParticleHit.Stick);
        double dt = StepOf(s, fps);
        int n = (int)(k1 - k0 + 1);
        var found = new Particle?[n];
        // The emitter where each was born, worked out first (in order: an item's motion isn't worked out from many threads).
        var births = new Mat4[n];
        for (int i = 0; i < n; i++) births[i] = emitterAt((start + (k0 + i) / s.Rate) * fps);
        void One(int i)
        {
            long k = k0 + i;
            double born = start + k / s.Rate, age = t - born;
            if (age < 0 || age > s.Life) return;
            var m = births[i];
            var (local, normal) = source.Pick(s.Seed, k);
            var p0 = m.TransformPoint(local);
            var nrm = m.TransformVector(normal).Normalized();
            if (nrm.LengthSquared < 0.5) nrm = Vec3.UnitY;
            var dir = Cone(nrm, s.Spread * Math.PI / 180, Unit(s.Seed, k, 3), Unit(s.Seed, k, 4));
            double speed = s.Speed * (1 + s.SpeedJitter * (2 * Unit(s.Seed, k, 5) - 1));
            Vec3 p, v;
            if (stepped)
            {
                if (Stepped(s, p0, dir * speed, g, born, t, dt, colliders) is not { } q) return;
                (p, v) = q;
            }
            else
            {
                p = Path(p0, dir * speed, g, s.Drag, age);
                v = Velocity(dir * speed, g, s.Drag, age);
                if (s.Floor is { } floor && p.Y < floor)
                {
                    // Stopped where it reached the floor: the first time its height crosses it.
                    double lo = 0, hi = age;
                    if (p0.Y < floor) hi = 0;
                    for (int j = 0; j < 40 && hi > 0; j++)
                    {
                        double mid = (lo + hi) / 2;
                        if (Path(p0, dir * speed, g, s.Drag, mid).Y < floor) hi = mid; else lo = mid;
                    }
                    p = Path(p0, dir * speed, g, s.Drag, hi);
                    p = new Vec3(p.X, floor, p.Z);
                    v = Vec3.Zero;
                }
            }
            double f = age / s.Life;
            found[i] = new Particle(p, s.Size * (1 + (s.SizeAtDeath - 1) * f), f, v, k);
        }
        if (stepped && n > 64) Parallel.For(0, n, One);
        else for (int i = 0; i < n; i++) One(i);
        foreach (var q in found)
            if (q is { } x) alive.Add(x);
        return alive;
    }

    /// <summary>
    /// A particle moved through time in steps of <paramref name="dt"/> (on a grid shared by every particle), from its
    /// birth to <paramref name="now"/>, meeting the floor and the colliders on the way: its place and speed, or null if it
    /// died on a hit. Between hits it follows the closed form, so a step's length only changes where hits are found.
    /// </summary>
    private static (Vec3 P, Vec3 V)? Stepped(ParticleSettings s, Vec3 p0, Vec3 v0, Vec3 g, double born, double now, double dt, IParticleColliders? colliders)
    {
        Vec3 origin = p0, vel = v0, prev = p0;
        double segment = born, before = born;
        double lift = Math.Max(1e-6, s.Size * 1e-3), settle = Math.Abs(s.Gravity) * dt * 2 + 1e-4;
        int bounces = 0;
        long k = (long)Math.Floor(born / dt) + 1;
        while (true)
        {
            double end = Math.Min(k * dt, now);
            var to = Path(origin, vel, g, s.Drag, end - segment);
            double best = double.PositiveInfinity;
            Vec3 normal = default;
            int which = -1;
            if (s.Floor is { } floor && prev.Y >= floor - 1e-9 && to.Y < floor)
            {
                best = (prev.Y - floor) / Math.Max(1e-12, prev.Y - to.Y);
                normal = Vec3.UnitY;
            }
            if (colliders != null && colliders.Hit(prev, to, end, out double frac, out var cn, out int ci) && frac < best)
                (best, normal, which) = (frac, cn, ci);
            if (best <= 1)
            {
                double th = before + best * (end - before);
                var at = prev + (to - prev) * best;
                var v = Velocity(vel, g, s.Drag, th - segment);
                if (Vec3.Dot(v, normal) > 0) normal = -normal;
                if (s.OnHit == ParticleHit.Die) return null;
                var vn = normal * Vec3.Dot(v, normal);
                var bounced = (v - vn) * (1 - s.Friction) - vn * s.Bounce;
                if (s.OnHit == ParticleHit.Stick || ++bounces > 64 || Vec3.Dot(bounced, normal) < settle)
                    // Stuck (or come to rest): where it met, carried along by what it met.
                    return (which >= 0 ? colliders!.Carry(which, at + normal * lift, th, now) : at + normal * lift, Vec3.Zero);
                origin = prev = at + normal * lift;
                vel = bounced;
                segment = before = th;
                continue;
            }
            if (end >= now) return (to, Velocity(vel, g, s.Drag, end - segment));
            prev = to;
            before = end;
            k++;
        }
    }

    /// <summary>How fast a particle is going after <paramref name="t"/> s (the derivative of <see cref="Path"/>).</summary>
    public static Vec3 Velocity(Vec3 v0, Vec3 g, double drag, double t)
    {
        if (drag <= 1e-9) return v0 + g * t;
        var terminal = g / drag;
        return terminal + (v0 - terminal) * Math.Exp(-drag * t);
    }

    /// <summary>Where a particle is after <paramref name="t"/> s: gravity and (if any) drag toward terminal speed, in closed form.</summary>
    public static Vec3 Path(Vec3 p0, Vec3 v0, Vec3 g, double drag, double t)
    {
        if (drag <= 1e-9) return p0 + v0 * t + g * (0.5 * t * t);
        var terminal = g / drag;
        return p0 + terminal * t + (v0 - terminal) * ((1 - Math.Exp(-drag * t)) / drag);
    }

    /// <summary>A direction within <paramref name="angle"/> (radians) of <paramref name="axis"/>, evenly over the cap.</summary>
    private static Vec3 Cone(Vec3 axis, double angle, double u, double v)
    {
        double cosA = Math.Cos(angle), z = 1 - u * (1 - cosA), r = Math.Sqrt(Math.Max(0, 1 - z * z)), phi = 2 * Math.PI * v;
        var a = Math.Abs(axis.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
        var x = Vec3.Cross(axis, a).Normalized();
        var y = Vec3.Cross(axis, x);
        return (axis * z + x * (r * Math.Cos(phi)) + y * (r * Math.Sin(phi))).Normalized();
    }

    /// <summary>A number from 0 to 1, the same every time for the same seed, particle and use.</summary>
    internal static double Unit(int seed, long index, int use)
    {
        ulong h = (ulong)index * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)seed * 0xBF58476D1CE4E5B9UL ^ (ulong)use * 0x94D049BB133111EBUL;
        h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 27; h *= 0x94D049BB133111EBUL; h ^= h >> 31;
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>What a mesh emits from: its faces' triangles by area, its points, or its pivot.</summary>
    private sealed class Source
    {
        private readonly List<(Vec3 A, Vec3 B, Vec3 C, Vec3 N)> _tris = [];
        private readonly double[] _cumulative;
        private readonly List<(Vec3 P, Vec3 N)> _points = [];
        private readonly EmitFrom _from;

        private Source(Mesh mesh, EmitFrom from)
        {
            _from = from;
            if (from == EmitFrom.Surface)
            {
                var area = new List<double>();
                double total = 0;
                foreach (var poly in mesh.Polygons)
                {
                    if (poly.Count < 3) continue;
                    var n = mesh.Normal(poly);
                    for (int i = 1; i + 1 < poly.Count; i++)
                    {
                        Vec3 a = mesh.Points[poly.Points[0]], b = mesh.Points[poly.Points[i]], c = mesh.Points[poly.Points[i + 1]];
                        double ar = Vec3.Cross(b - a, c - a).Length / 2;
                        if (ar <= 0) continue;
                        _tris.Add((a, b, c, n));
                        total += ar;
                        area.Add(total);
                    }
                }
                _cumulative = [.. area];
            }
            else
            {
                _cumulative = [];
                if (from == EmitFrom.Points)
                {
                    var normals = new Vec3[mesh.Points.Count];
                    foreach (var poly in mesh.Polygons)
                        if (poly.Count >= 3)
                        {
                            var n = mesh.Normal(poly);
                            foreach (int i in poly.Points) normals[i] += n;
                        }
                    for (int i = 0; i < mesh.Points.Count; i++)
                        _points.Add((mesh.Points[i], normals[i].LengthSquared > 0 ? normals[i].Normalized() : Vec3.UnitY));
                }
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, Dictionary<EmitFrom, Source>> Cache = new();

        public static Source Of(Mesh mesh, EmitFrom from)
        {
            var byKind = Cache.GetOrCreateValue(mesh);
            lock (byKind)
            {
                if (!byKind.TryGetValue(from, out var s)) byKind[from] = s = new Source(mesh, from);
                return s;
            }
        }

        public (Vec3 Point, Vec3 Normal) Pick(int seed, long k)
        {
            if (_from == EmitFrom.Surface && _tris.Count > 0)
            {
                double target = Unit(seed, k, 0) * _cumulative[^1];
                int i = Array.BinarySearch(_cumulative, target);
                if (i < 0) i = ~i;
                i = Math.Min(i, _tris.Count - 1);
                var (a, b, c, n) = _tris[i];
                double u = Unit(seed, k, 1), v = Unit(seed, k, 2);
                if (u + v > 1) (u, v) = (1 - u, 1 - v);
                return (a + (b - a) * u + (c - a) * v, n);
            }
            if (_from == EmitFrom.Points && _points.Count > 0)
            {
                var (p, n) = _points[(int)(Unit(seed, k, 0) * _points.Count) % _points.Count];
                return (p, n);
            }
            return (Vec3.Zero, Vec3.UnitY);
        }
    }

    /// <summary>
    /// The particles as little diamonds (octahedra, eight triangles each) added to <paramref name="mesh"/>, in the
    /// coordinates <paramref name="toLocal"/> takes world points to, on the surface index given.
    /// </summary>
    public static void AddGeometry(Mesh mesh, IEnumerable<Particle> particles, Mat4 toLocal, int surface)
    {
        int[][] faces = [[0, 2, 4], [2, 1, 4], [1, 3, 4], [3, 0, 4], [2, 0, 5], [1, 2, 5], [3, 1, 5], [0, 3, 5]];
        foreach (var q in particles)
        {
            double r = q.Size / 2;
            int b = mesh.Points.Count;
            foreach (var d in (Vec3[])[new(r, 0, 0), new(-r, 0, 0), new(0, 0, r), new(0, 0, -r), new(0, r, 0), new(0, -r, 0)])
                mesh.Points.Add(toLocal.TransformPoint(q.Position + d));
            foreach (var f in faces) mesh.Polygons.Add(new Polygon([b + f[0], b + f[1], b + f[2]], surface));
        }
    }
}
