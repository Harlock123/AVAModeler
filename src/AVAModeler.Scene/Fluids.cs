using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// Particles that flow as a liquid (<see cref="ParticleSettings.Fluid"/>): position-based fluids (Macklin and Müller,
/// 2013). Each step every particle moves under gravity and drag, then is pushed by its neighbours (within twice its
/// size) until none is packed tighter than particles a size apart are at rest, then kept above the floor and out of
/// the colliders; its speed is where that took it, smoothed toward its neighbours' by the viscosity. They only push
/// (no surface tension), so a fluid spreads into a puddle and fills a bowl; and however thin it spreads, no two come
/// closer than six-tenths of their size. Particles are born as the emitter's
/// others are (the same places and speeds, by a hash), at most <see cref="ParticleSettings.MaxFluid"/> alive at
/// once. Stepped from the start frame on a grid of steps, the state kept at whole frames, so playing forward costs a
/// frame's steps and any frame comes out the same however it's reached; any change to the scene starts it again.
/// </summary>
public static class Fluids
{
    /// <summary>How much state a fluid keeps (particles over kept frames): past it, it keeps every n-th frame.</summary>
    private const long Budget = 4_000_000;

    /// <summary>Pushing passes a step.</summary>
    private const int Iterations = 3;

    /// <summary>Steps a frame at 30 frames a second (fewer at higher rates, never under two): a step is at most a 150th of a second.</summary>
    public static int StepsPerFrame(double fps) => Math.Max(2, (int)Math.Ceiling(150 / Math.Max(1, fps)));

    /// <summary>The fluid's particles alive at a moment: which (by birth number), where and how fast (world).</summary>
    private sealed class Pool
    {
        public long Next;
        public List<long> K = [];
        public List<Vec3> X = [], V = [];

        public Pool Clone() => new() { Next = Next, K = [.. K], X = [.. X], V = [.. V] };
    }

    private sealed class State
    {
        public required object Key;
        public required int Stride;
        /// <summary>The pool at the start frame plus i × stride frames.</summary>
        public readonly List<Pool> Frames = [];
    }

    /// <summary>
    /// The fluid particles alive at <paramref name="frame"/>, worked out from the kept frame before it.
    /// <paramref name="emitterAt"/> gives the emitter's world transform at a frame; <paramref name="colliders"/> may
    /// be null.
    /// </summary>
    internal static List<ParticleSystem.Particle> At(Scene scene, ObjectItem o, ParticleSettings s, double frame, Mesh rest,
        Func<double, Mat4> emitterAt, IParticleColliders? colliders)
    {
        double fps = scene.FramesPerSecond;
        if (s.Rate <= 0 || fps <= 0 || frame < s.StartFrame) return [];
        var key = (s, scene.Version, fps, rest.Points.Count, rest.Version, colliders != null);
        Pool pool;
        double at;
        int steps = StepsPerFrame(fps);
        lock (o.FluidLock)
        {
            var state = o.FluidCache as State;
            if (state == null || !state.Key.Equals(key))
            {
                long frames = Math.Max(1, (long)Math.Ceiling(Math.Max(scene.LastFrame, s.EndFrame + s.Life * fps) - s.StartFrame) + 1);
                int stride = (int)Math.Max(1, Math.Ceiling((double)ParticleSettings.MaxFluid * frames / Budget));
                state = new State { Key = key, Stride = stride };
                state.Frames.Add(new Pool());
                o.FluidCache = state;
            }
            int i = Math.Min(state.Frames.Count - 1, (int)Math.Floor((frame - s.StartFrame) / state.Stride));
            pool = state.Frames[i].Clone();
            at = s.StartFrame + (long)i * state.Stride;
            var solver = new Solver(s, fps, steps, rest, emitterAt, colliders);
            // Whole frames up to the one asked for, keeping those on the stride the first time they're reached.
            while (at + 1 <= frame + 1e-9)
            {
                solver.Frame(pool, at);
                at += 1;
                long done = (long)Math.Round(at - s.StartFrame);
                if (done % state.Stride == 0 && done / state.Stride == state.Frames.Count) state.Frames.Add(pool.Clone());
            }
            // A part frame (motion blur, a fractional frame): its steps on a copy, not kept.
            if (frame - at > 1e-9) solver.Steps(pool, at, (int)Math.Floor((frame - at) * steps + 1e-9), (frame - at) * steps % 1);
        }
        var alive = new List<ParticleSystem.Particle>(pool.K.Count);
        double now = frame / fps;
        for (int j = 0; j < pool.K.Count; j++)
        {
            double born = s.StartFrame / fps + pool.K[j] / s.Rate;
            double f = Math.Clamp((now - born) / s.Life, 0, 1);
            alive.Add(new ParticleSystem.Particle(pool.X[j], s.Size * (1 + (s.SizeAtDeath - 1) * f), f, pool.V[j], pool.K[j]));
        }
        return alive;
    }

    private sealed class Solver
    {
        private readonly ParticleSettings _s;
        private readonly double _fps, _dt, _h, _h2, _rest, _epsilon, _poly6, _spiky;
        private readonly int _steps;
        private readonly Mesh _mesh;
        private readonly Func<double, Mat4> _emitterAt;
        private readonly IParticleColliders? _colliders;

        public Solver(ParticleSettings s, double fps, int steps, Mesh mesh, Func<double, Mat4> emitterAt, IParticleColliders? colliders)
        {
            (_s, _fps, _steps, _mesh, _emitterAt, _colliders) = (s, fps, steps, mesh, emitterAt, colliders);
            _dt = 1 / (fps * steps);
            // The kernels reach twice a particle's size; at rest particles sit a size apart.
            _h = 2 * s.Size;
            _h2 = _h * _h;
            _poly6 = 315 / (64 * Math.PI * Math.Pow(_h, 9));
            _spiky = -45 / (Math.PI * Math.Pow(_h, 6));
            // The density particles a size apart on a cubic lattice have, and the push such a particle feels: its
            // rest density, and the constraint's softening (a tenth of that push, which keeps it steady).
            double rho = 0;
            var grad = Vec3.Zero;
            double gradSum = 0;
            int reach = (int)Math.Ceiling(_h / s.Size);
            for (int x = -reach; x <= reach; x++)
                for (int y = -reach; y <= reach; y++)
                    for (int z = -reach; z <= reach; z++)
                    {
                        var r = new Vec3(x, y, z) * s.Size;
                        rho += W(r.LengthSquared);
                        if (x == 0 && y == 0 && z == 0) continue;
                        var g = GradW(r);
                        grad += g;
                        gradSum += g.LengthSquared;
                    }
            _rest = rho;
            _epsilon = 0.1 * (gradSum + grad.LengthSquared) / (rho * rho);
        }

        private double W(double r2) => r2 >= _h2 ? 0 : _poly6 * Math.Pow(_h2 - r2, 3);

        private Vec3 GradW(Vec3 r)
        {
            double l = r.Length;
            if (l >= _h || l < 1e-12) return Vec3.Zero;
            double k = _h - l;
            return r * (_spiky * k * k / l);
        }

        /// <summary>One whole frame from <paramref name="frame"/>.</summary>
        public void Frame(Pool pool, double frame) => Steps(pool, frame, _steps, 0);

        /// <summary>Whole steps from a frame, then a part of one.</summary>
        public void Steps(Pool pool, double frame, int whole, double part)
        {
            double t = frame / _fps;
            for (int k = 0; k < whole; k++, t += _dt) Step(pool, t, _dt);
            if (part > 1e-9) Step(pool, t, _dt * part);
        }

        private void Step(Pool pool, double t0, double dt)
        {
            var s = _s;
            double t1 = t0 + dt, start = s.StartFrame / _fps, end = s.EndFrame / _fps;
            // The old ones die; new ones are born (as many as were due up to now, while there's room).
            for (int i = pool.K.Count - 1; i >= 0; i--)
                if (t1 - (start + pool.K[i] / s.Rate) > s.Life)
                {
                    pool.K.RemoveAt(i);
                    pool.X.RemoveAt(i);
                    pool.V.RemoveAt(i);
                }
            while (true)
            {
                double born = start + pool.Next / s.Rate;
                if (born > t1 + 1e-12 || born > end + 1e-12) break;
                long k = pool.Next++;
                if (pool.K.Count >= ParticleSettings.MaxFluid) continue;
                var (p, v) = ParticleSystem.Birth(s, k, _mesh, _emitterAt(born * _fps));
                // A nudge of a fiftieth of its size, so particles born on one spot don't stack in a perfect column.
                p += new Vec3(ParticleSystem.Unit(s.Seed, k, 30) - 0.5, ParticleSystem.Unit(s.Seed, k, 31) - 0.5, ParticleSystem.Unit(s.Seed, k, 32) - 0.5) * (s.Size * 0.04);
                // Carried on from its birth to the end of this step.
                double rest = Math.Max(0, t1 - born);
                pool.K.Add(k);
                pool.X.Add(p + v * rest);
                pool.V.Add(v);
            }
            int n = pool.K.Count;
            if (n == 0) return;
            var x = pool.X;
            var v0 = pool.V;
            var g = new Vec3(0, -s.Gravity, 0);
            double fade = s.Drag > 0 ? Math.Exp(-s.Drag * dt) : 1;
            var p0 = new Vec3[n];
            var vel = new Vec3[n];
            for (int i = 0; i < n; i++)
            {
                vel[i] = (v0[i] + g * dt) * fade;
                p0[i] = x[i] + vel[i] * dt;
            }
            var near = Neighbours(p0);
            var lambda = new double[n];
            var delta = new Vec3[n];
            for (int it = 0; it < Iterations; it++)
            {
                Parallel.For(0, n, i =>
                {
                    double rho = W(0);
                    var gi = Vec3.Zero;
                    double sum = 0;
                    foreach (int j in near[i])
                    {
                        var r = p0[i] - p0[j];
                        rho += W(r.LengthSquared);
                        var gw = GradW(r) / _rest;
                        gi += gw;
                        sum += gw.LengthSquared;
                    }
                    // Only packed too tight pushes (no pull): a free surface doesn't clump.
                    double c = Math.Max(0, rho / _rest - 1);
                    lambda[i] = -c / (sum + gi.LengthSquared + _epsilon);
                });
                Parallel.For(0, n, i =>
                {
                    var d = Vec3.Zero;
                    foreach (int j in near[i]) d += GradW(p0[i] - p0[j]) * (lambda[i] + lambda[j]);
                    // At most a quarter of its size a pass, so particles born packed tight spread out rather than burst apart.
                    d /= _rest;
                    double l = d.Length, most = s.Size * 0.25;
                    delta[i] = l > most ? d * (most / l) : d;
                });
                for (int i = 0; i < n; i++) p0[i] += delta[i];
            }
            // However thin the fluid (a puddle one particle deep is less dense than at rest, so the pressure doesn't
            // push), no two particles come closer than six-tenths of their size.
            double gap = 0.6 * s.Size;
            Parallel.For(0, n, i =>
            {
                var d = Vec3.Zero;
                foreach (int j in near[i])
                {
                    var r = p0[i] - p0[j];
                    double l = r.Length;
                    if (l >= gap) continue;
                    // Two on the same spot part along a direction of their own.
                    var dir = l > 1e-9 ? r / l : new Vec3(ParticleSystem.Unit(i, j, 33) - 0.5, ParticleSystem.Unit(i, j, 34) - 0.5, ParticleSystem.Unit(i, j, 35) - 0.5).Normalized() * (i < j ? 1 : -1);
                    d += dir * ((gap - l) * 0.5);
                }
                delta[i] = d;
            });
            for (int i = 0; i < n; i++) p0[i] += delta[i];
            // The floor and the colliders, then the speeds from where each went, smoothed by the viscosity.
            // Kept a fiftieth of their size off what they meet, so the next step starts clear of it.
            double lift = Math.Max(1e-6, s.Size * 0.02);
            Parallel.For(0, n, i =>
            {
                if (s.Floor is { } floor && p0[i].Y < floor) p0[i] = new Vec3(p0[i].X, floor, p0[i].Z);
                // Stopped at a surface, keeping what's left of the move along it (and stopped again if that meets another).
                var from = x[i];
                for (int pass = 0; pass < 4 && _colliders != null && _colliders.Hit(from, p0[i], t1, out double f, out var nrm, out _); pass++)
                {
                    if (Vec3.Dot(p0[i] - from, nrm) > 0) nrm = -nrm;
                    var hit = from + (p0[i] - from) * f + nrm * lift;
                    var rest = p0[i] - hit;
                    p0[i] = pass == 3 ? hit : hit + (rest - nrm * Vec3.Dot(rest, nrm));
                    from = hit;
                }
                vel[i] = (p0[i] - x[i]) / dt;
            });
            var smooth = new Vec3[n];
            Parallel.For(0, n, i =>
            {
                var sum = Vec3.Zero;
                foreach (int j in near[i]) sum += (vel[j] - vel[i]) * W((p0[i] - p0[j]).LengthSquared);
                smooth[i] = vel[i] + sum * (s.Viscosity / _rest);
            });
            for (int i = 0; i < n; i++)
            {
                x[i] = p0[i];
                v0[i] = smooth[i];
            }
        }

        /// <summary>Each particle's neighbours within the kernel's reach, sorted, by cubes of that size.</summary>
        private int[][] Neighbours(Vec3[] p)
        {
            int n = p.Length;
            var cells = new (long X, long Y, long Z)[n];
            var keys = new long[n];
            var order = new int[n];
            static long Key(long x, long y, long z) => (x & 0x1FFFFF) | (y & 0x1FFFFF) << 21 | (z & 0x1FFFFF) << 42;
            for (int i = 0; i < n; i++)
            {
                cells[i] = ((long)Math.Floor(p[i].X / _h), (long)Math.Floor(p[i].Y / _h), (long)Math.Floor(p[i].Z / _h));
                keys[i] = Key(cells[i].X, cells[i].Y, cells[i].Z);
                order[i] = i;
            }
            Array.Sort(keys, order);
            var near = new int[n][];
            Parallel.For(0, n, i =>
            {
                var list = new List<int>();
                var (cx, cy, cz) = cells[i];
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            long key = Key(cx + dx, cy + dy, cz + dz);
                            int at = Array.BinarySearch(keys, key);
                            if (at < 0) continue;
                            while (at > 0 && keys[at - 1] == key) at--;
                            for (; at < n && keys[at] == key; at++)
                            {
                                int j = order[at];
                                if (j != i && (p[i] - p[j]).LengthSquared < _h2) list.Add(j);
                            }
                        }
                list.Sort();
                near[i] = [.. list];
            });
            return near;
        }
    }
}
