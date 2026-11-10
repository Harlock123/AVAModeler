using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>Puffs and sprites: particles the ray tracer draws itself, along each camera ray, in front of what it hits.</summary>
public static partial class RayTracer
{
    private sealed partial class Tracer
    {
        private List<SoftParticle>? _softList;
        [ThreadStatic] private static double _softSkip;
        private SoftParticle[]? _soft;
        /// <summary>Each puff's colour as lit (or a sprite's light), worked out once at its centre.</summary>
        private Vec3[] _softLit = [];
        private SphereTree? _softTree;

        [ThreadStatic] private static List<(double T, int I, double Amount)>? _softHits;

        /// <summary>Whether any particle is drawn as a puff or a sprite.</summary>
        public bool HasSoft => _soft != null;

        /// <summary>
        /// Lights each puff where it is (the ambient light, and each light's colour by its falloff and spot cone, if
        /// nothing casting shadows is in the way, dimmed by the other puffs between it and the light), plus its glow; a
        /// sprite is its colour by its opacity.
        /// </summary>
        private void PrepareSoft()
        {
            if (_softList is not { Count: > 0 } list) return;
            _soft = [.. list];
            _softLit = new Vec3[_soft.Length];
            _softTree = new SphereTree([.. _soft.Select(q => (q.Position, Reach(q)))]);
            var lit = _softLit;
            Parallel.For(0, _soft.Length, i =>
            {
                var q = _soft[i];
                if (q.Additive)
                {
                    lit[i] = q.Color * q.Opacity;
                    return;
                }
                var light = _ambient;
                foreach (var l in _lights)
                {
                    Vec3 toLight;
                    double distance, strength = 1;
                    if (l.Type == LightType.Distant)
                    {
                        toLight = -l.Direction;
                        distance = double.PositiveInfinity;
                    }
                    else
                    {
                        var d = l.Position - q.Position;
                        distance = d.Length;
                        if (distance == 0) continue;
                        toLight = d / distance;
                        if (l.Item.Range > 0) strength = l.Item.Falloff(distance);
                        if (l.Type == LightType.Spot)
                            strength *= l.Item.SpotFactor(Math.Acos(Math.Clamp(Vec3.Dot(l.Direction, -toLight), -1, 1)) * 180 / Math.PI);
                    }
                    if (strength <= 0) continue;
                    if (_settings.Shadows && l.Item.Shadows)
                    {
                        if (_bvh.Intersect(q.Position, toLight, _bias * 10, distance, out var hit) && _materials[_bvh[hit.Triangle].Material].CastShadows) continue;
                        // The smoke between it and the light (not itself) shades it, so a thick cloud is dark on its far side.
                        strength *= PuffsThrough(q.Position, toLight, distance, skip: i);
                    }
                    light += l.Color * strength;
                }
                lit[i] = new Vec3(q.Color.X * light.X, q.Color.Y * light.Y, q.Color.Z * light.Z) * 0.8 + q.Color * q.Glow;
            });
        }

        /// <summary>
        /// How much light gets through the puffs (not sprites, which only add light) along a ray from
        /// <paramref name="origin"/> for <paramref name="distance"/>, 1 for none in the way: for shadows, and for the
        /// puffs' own lighting (leaving out puff <paramref name="skip"/>).
        /// </summary>
        private double PuffsThrough(Vec3 origin, Vec3 dir, double distance, int skip = -1)
        {
            if (_softTree == null) return 1;
            double tau = 0;
            double end = double.IsFinite(distance) ? distance : 1e30;
            _softSkip = skip;
            _softTree.Along(origin, dir, end, i =>
            {
                if (i == (int)_softSkip) return;
                var q = _soft![i];
                if (q.Additive) return;
                tau += PuffDepth(q, origin, dir, end);
            });
            return Math.Exp(-tau);
        }

        /// <summary>A Gaussian puff's optical depth along a ray from 0 to <paramref name="end"/>, in closed form.</summary>
        private static double PuffDepth(in SoftParticle q, Vec3 origin, Vec3 dir, double end)
        {
            var to = q.Position - origin;
            double tc = Vec3.Dot(to, dir), d2 = Math.Max(0, to.LengthSquared - tc * tc);
            double sigma = q.Size / 3;
            if (d2 >= 9 * sigma * sigma) return 0;
            double tau0 = -Math.Log(1 - Math.Min(0.999, q.Opacity));
            double part = (Erf((Math.Min(end, 1e30) - tc) / sigma) - Erf(-tc / sigma)) / 2;
            return Math.Max(0, tau0 * Math.Exp(-d2 / (sigma * sigma)) * part);
        }

        /// <summary>How far from its centre a puff (three spreads) or a sprite (half its size) reaches.</summary>
        private static double Reach(SoftParticle q) => q.Additive ? q.Size / 2 : q.Size;

        /// <summary>
        /// What a ray (from the camera, or reflected or refracted) sees through the puffs and sprites in front of what it hits (<paramref name="seen"/>), nearest
        /// first: each puff hides its share of what's behind it and shows its lit colour in its place, each sprite adds its
        /// light; the coverage grows by what the puffs hide.
        /// </summary>
        public Vec3 ThroughSoft(Vec3 origin, Vec3 dir, Vec3 seen, ref double coverage)
        {
            double hitAt = _bvh.Intersect(origin, dir, _bias, double.PositiveInfinity, out var hit) ? hit.T : double.PositiveInfinity;
            var hits = _softHits ??= [];
            hits.Clear();
            _softTree!.Along(origin, dir, hitAt, i =>
            {
                var q = _soft![i];
                var to = q.Position - origin;
                double tc = Vec3.Dot(to, dir), d2 = Math.Max(0, to.LengthSquared - tc * tc);
                if (q.Additive)
                {
                    double r = q.Size / 2;
                    if (tc <= 0 || tc >= hitAt || d2 >= r * r) return;
                    double f = 1 - d2 / (r * r);
                    hits.Add((tc, i, f * f));
                }
                else
                {
                    // A Gaussian puff: its density along the ray, from the eye to what's hit, in closed form.
                    double tau = PuffDepth(q, origin, dir, hitAt);
                    if (tau > 1e-6) hits.Add((tc, i, 1 - Math.Exp(-tau)));
                }
            });
            if (hits.Count == 0) return seen;
            hits.Sort((a, b) => a.T.CompareTo(b.T));
            double through = 1;
            var sum = Vec3.Zero;
            foreach (var (_, i, amount) in hits)
            {
                if (_soft![i].Additive) sum += _softLit[i] * (amount * through);
                else
                {
                    sum += _softLit[i] * (amount * through);
                    through *= 1 - amount;
                }
            }
            coverage = 1 - (1 - coverage) * through;
            return sum + seen * through;
        }

        /// <summary>The error function (Abramowitz and Stegun 7.1.26, good to 1.5e-7).</summary>
        private static double Erf(double x)
        {
            double sign = x < 0 ? -1 : 1;
            x = Math.Abs(x);
            double t = 1 / (1 + 0.3275911 * x);
            double y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
            return sign * y;
        }
    }

    /// <summary>A bounding volume hierarchy over spheres, to find those a ray passes through.</summary>
    private sealed class SphereTree
    {
        private readonly (Vec3 C, double R)[] _spheres;
        private readonly int[] _order;
        private readonly List<(Vec3 Min, Vec3 Max, int Left, int Right, int Count)> _nodes = [];

        public SphereTree((Vec3 C, double R)[] spheres)
        {
            _spheres = spheres;
            _order = Enumerable.Range(0, spheres.Length).ToArray();
            if (spheres.Length > 0) Build(0, spheres.Length);
        }

        private int Build(int start, int count)
        {
            var box = Box3.Empty;
            var centres = Box3.Empty;
            for (int i = start; i < start + count; i++)
            {
                var (c, r) = _spheres[_order[i]];
                box = box.Include(c - new Vec3(r, r, r)).Include(c + new Vec3(r, r, r));
                centres = centres.Include(c);
            }
            int index = _nodes.Count;
            _nodes.Add((box.Min, box.Max, start, -1, count));
            var size = centres.Size;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            if (count <= 4 || size[axis] <= 0) return index;
            var keys = new double[count];
            for (int i = 0; i < count; i++) keys[i] = _spheres[_order[start + i]].C[axis];
            keys.AsSpan().Sort(_order.AsSpan(start, count));
            int half = count / 2;
            int left = Build(start, half), right = Build(start + half, count - half);
            _nodes[index] = (box.Min, box.Max, left, right, 0);
            return index;
        }

        /// <summary>Calls <paramref name="each"/> for every sphere whose box the ray meets between 0 and <paramref name="tMax"/>.</summary>
        public void Along(Vec3 o, Vec3 d, double tMax, Action<int> each)
        {
            if (_nodes.Count == 0) return;
            var inv = new Vec3(1 / d.X, 1 / d.Y, 1 / d.Z);
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var n = _nodes[stack.Pop()];
                if (!Meets(n.Min, n.Max, o, inv, tMax)) continue;
                if (n.Count > 0)
                    for (int i = n.Left; i < n.Left + n.Count; i++) each(_order[i]);
                else
                {
                    stack.Push(n.Left);
                    stack.Push(n.Right);
                }
            }
        }

        private static bool Meets(Vec3 min, Vec3 max, Vec3 o, Vec3 inv, double tMax)
        {
            double lo = 0, hi = tMax;
            for (int a = 0; a < 3; a++)
            {
                double t1 = (min[a] - o[a]) * inv[a], t2 = (max[a] - o[a]) * inv[a];
                if (double.IsNaN(t1) || double.IsNaN(t2)) continue;
                lo = Math.Max(lo, Math.Min(t1, t2));
                hi = Math.Min(hi, Math.Max(t1, t2));
            }
            return lo <= hi;
        }
    }
}
