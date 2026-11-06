using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// The lights' own effects: lens flares (drawn over the finished picture), volumetric lights (beams in the air, seen
/// by camera rays only) and shadow maps (a depth picture per light per moment, looked up instead of shadow rays).
/// Each is off until a light asks for it, and then has its own branch, so pictures without them are unchanged.
/// </summary>
public static partial class RayTracer
{
    private const int DimensionVolume = 5;

    /// <summary>The finished picture in full precision, kept while lens flares are on so they're added before rounding.</summary>
    private sealed class FlareCanvas(int width, int height)
    {
        public readonly int Width = width, Height = height;
        private readonly double[] _rgb = new double[width * height * 3];

        /// <summary>A canvas when any visible light has a lens flare, else null (and the picture is written as before).</summary>
        public static FlareCanvas? For(Scene scene, int width, int height) =>
            scene.Lights.Any(l => l.Visible && l.Flare.On) ? new FlareCanvas(width, height) : null;

        public void Keep(int x, int y, Vec3 c)
        {
            int i = (y * Width + x) * 3;
            (_rgb[i], _rgb[i + 1], _rgb[i + 2]) = (c.X, c.Y, c.Z);
        }

        public void Add(int x, int y, Vec3 c)
        {
            int i = (y * Width + x) * 3;
            _rgb[i] += c.X;
            _rgb[i + 1] += c.Y;
            _rgb[i + 2] += c.Z;
        }

        /// <summary>The colours into the picture's bytes (alpha is left as the geometry made it).</summary>
        public void WriteTo(byte[] rgba)
        {
            for (int p = 0; p < Width * Height; p++)
            {
                rgba[p * 4] = ToByte(_rgb[p * 3]);
                rgba[p * 4 + 1] = ToByte(_rgb[p * 3 + 1]);
                rgba[p * 4 + 2] = ToByte(_rgb[p * 3 + 2]);
            }
        }
    }

    /// <summary>
    /// Where a light's flare goes and how strong it is at a frame, or null when it has none there: off, dissolved, behind
    /// the camera, faded off screen, with distance or behind objects (through a transparent surface it is dimmed by
    /// the surface's transparency). (X, Y) is in pixels, from the picture's top left.
    /// </summary>
    private static (double X, double Y, double Strength)? FlareSpot(LightItem l, double frame, Mat4 toWorld, double tanH, double tanV,
        int width, int height, Tracer tracer)
    {
        var f = l.Flare;
        double strength = l.FlareIntensity.Evaluate(frame) * (1 - f.Dissolve);
        if (!(strength > 0)) return null;
        var toCamera = toWorld.Inverted();
        var eye = toWorld.TransformPoint(Vec3.Zero);
        Vec3 local, toLight;
        double distance;
        if (l.Type == LightType.Distant)
        {
            toLight = -l.Direction(frame);
            local = toCamera.TransformVector(toLight);
            distance = double.PositiveInfinity;
        }
        else
        {
            var at = l.WorldMatrix(frame).TransformPoint(Vec3.Zero);
            var d = at - eye;
            distance = d.Length;
            if (distance == 0) return null;
            toLight = d / distance;
            local = toCamera.TransformPoint(at);
        }
        if (local.Z <= 1e-9) return null;
        double x = (local.X / local.Z / tanH + 1) / 2 * width;
        double y = (1 - local.Y / local.Z / tanV) / 2 * height;
        if (f.FadeOffScreen)
        {
            double outside = Math.Max(Math.Max(-x, x - width), Math.Max(-y, y - height));
            double margin = Math.Max(1, f.Size * height);
            strength *= Math.Clamp(1 - outside / margin, 0, 1);
        }
        if (f.FadeWithDistance && double.IsFinite(distance)) strength *= Math.Min(1, f.NominalDistance / distance);
        if (strength > 0 && f.FadeBehind) strength *= tracer.Visibility(eye, toLight, distance);
        return strength > 0 ? (x, y, strength) : null;
    }

    /// <summary>Draws every visible light's lens flare onto the canvas, light after light (so the sums are the same on any number of threads).</summary>
    private static void DrawFlares(FlareCanvas canvas, Scene scene, CameraItem camera, double frame, double tanH, double tanV, Tracer tracer,
        ParallelOptions options)
    {
        var toWorld = camera.CameraToWorld(frame);
        int index = -1;
        foreach (var l in scene.Lights)
        {
            index++;
            if (!l.Visible || !l.Flare.On) continue;
            if (FlareSpot(l, frame, toWorld, tanH, tanV, canvas.Width, canvas.Height, tracer) is not var (x, y, strength)) continue;
            DrawFlare(canvas, x, y, l.Color, l.Flare, strength, FlareSeed(l.Name, index), options);
        }
    }

    /// <summary>A light's flare's seed: a hash of its name and place in the scene (not string.GetHashCode, which changes between runs).</summary>
    private static ulong FlareSeed(string name, int index)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in name) h = (h ^ c) * 1099511628211UL;
        return Mix(h ^ (ulong)(uint)index * 0x9E3779B97F4A7C15UL);
    }

    private static double Unit(ulong seed, int i) => (Mix(seed ^ (ulong)(uint)(i + 1) * 0xD1B54A32D192ED03UL) >> 11) * (1.0 / (1UL << 53));

    /// <summary>
    /// One flare: a glow (light colour, fading as the square of the distance to its rim) with a white-hot core, a halo
    /// ring, and streaks out from the centre, each a thin line tapering to its end, at angles spread round the circle
    /// with a repeatable random wobble and lengths from half to all of the streak length.
    /// </summary>
    private static void DrawFlare(FlareCanvas canvas, double cx, double cy, Vec3 color, LensFlare f, double strength, ulong seed, ParallelOptions options)
    {
        int width = canvas.Width, height = canvas.Height;
        double glow = f.Size * height, ring = f.RingSize * height, streak = f.StreakLength * height;
        double ringWidth = Math.Max(1, 0.05 * ring), streakWidth = Math.Max(0.75, 0.003 * height);
        double reach = 0;
        if (f.Glow) reach = glow;
        if (f.Ring) reach = Math.Max(reach, ring + 4 * ringWidth);
        if (f.Streaks > 0) reach = Math.Max(reach, streak);
        int x0 = Math.Max(0, (int)Math.Floor(cx - reach)), x1 = Math.Min(width - 1, (int)Math.Ceiling(cx + reach));
        int y0 = Math.Max(0, (int)Math.Floor(cy - reach)), y1 = Math.Min(height - 1, (int)Math.Ceiling(cy + reach));
        if (x0 > x1 || y0 > y1) return;
        int n = f.Streaks;
        var cos = new double[n];
        var sin = new double[n];
        var length = new double[n];
        double turn = Unit(seed, 0) * 2 * Math.PI;
        for (int i = 0; i < n; i++)
        {
            double angle = turn + 2 * Math.PI * (i + 0.4 * (Unit(seed, 2 * i + 1) - 0.5)) / n;
            (cos[i], sin[i]) = (Math.Cos(angle), Math.Sin(angle));
            length[i] = streak * (0.5 + 0.5 * Unit(seed, 2 * i + 2));
        }
        var hot = (Vec3.One + color) / 2;
        Parallel.For(y0, y1 + 1, options, y =>
        {
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                var add = Vec3.Zero;
                if (f.Glow)
                {
                    double g = 1 - d / glow;
                    if (g > 0) add += color * (0.6 * g * g);
                    double core = d / (0.15 * glow);
                    add += hot * (0.6 * Math.Exp(-core * core));
                }
                if (f.Ring)
                {
                    double r = (d - ring) / ringWidth;
                    if (r * r < 16) add += color * (0.3 * Math.Exp(-r * r));
                }
                for (int i = 0; i < n; i++)
                {
                    double along = dx * cos[i] + dy * sin[i];
                    if (along <= 0 || along >= length[i]) continue;
                    double across = (-dx * sin[i] + dy * cos[i]) / streakWidth;
                    if (across * across >= 16) continue;
                    double fade = 1 - along / length[i];
                    add += hot * (0.5 * fade * fade * Math.Exp(-across * across));
                }
                if (add != Vec3.Zero) canvas.Add(x, y, add * strength);
            }
        });
    }

    private sealed partial class Tracer
    {
        /// <summary>Each light's shadow map (null for one with ray-traced shadows), or null when no light has one.</summary>
        private LightShadowMap?[]? _shadowMaps;
        /// <summary>Each light's beam length in metres (0 for one without a volumetric beam), or null when no light has one.</summary>
        private double[]? _beams;

        /// <summary>Whether any light's beam is seen in the air.</summary>
        public bool HasVolumetrics => _beams != null;

        /// <summary>Builds the shadow maps and works out the beams' lengths, for the lights that have them.</summary>
        private void PrepareLightEffects(List<Triangle> tris, Box3 bounds)
        {
            Triangle[]? casters = null;
            for (int li = 0; li < _lights.Count; li++)
            {
                var light = _lights[li];
                var item = light.Item;
                if (_settings.Shadows && item.UsesShadowMap)
                {
                    casters ??= tris.Where(t => _materials[t.Material].CastShadows).ToArray();
                    _shadowMaps ??= new LightShadowMap?[_lights.Count];
                    _shadowMaps[li] = LightShadowMap.Build(light.Type, light.Position, light.Direction, item.ConeAngle, item.ShadowMap, casters, bounds);
                }
                var v = item.Volumetric;
                if (v.On && v.Intensity > 0 && light.Type != LightType.Area)
                {
                    _beams ??= new double[_lights.Count];
                    _beams[li] = BeamLength(light, bounds);
                }
            }
        }

        /// <summary>
        /// How far a beam reaches: its own distance (no further than the light's range), else the light's range, else
        /// across the scene (to the far corner of everything's bounds from the light; a distant light's twice the
        /// scene's size). At least a millimetre.
        /// </summary>
        private static double BeamLength(LightInfo light, Box3 bounds)
        {
            var item = light.Item;
            double distance = item.Volumetric.Distance;
            bool distant = light.Type == LightType.Distant;
            if (!distant && item.Range > 0) distance = distance > 0 ? Math.Min(distance, item.Range) : item.Range;
            if (distance <= 0)
            {
                if (bounds.IsEmpty) distance = 10;
                else if (distant) distance = 2 * Math.Max(1, bounds.Size.Length);
                else
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vec3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
                        distance = Math.Max(distance, (corner - light.Position).Length);
                    }
            }
            return Math.Max(1e-3, distance);
        }

        /// <summary>
        /// The light the beams scatter toward the camera along a camera ray, from its origin to what it hits (or each
        /// beam's end on a miss): for each beam the ray's part inside it (a spot's cone, a point light's sphere, a distant
        /// light's reach), marched in even steps from a start jittered per pixel and ray.
        /// </summary>
        public Vec3 Volumetric(Vec3 origin, Vec3 dir, in PixelSamples pixel)
        {
            double hitAt = _bvh.Intersect(origin, dir, _bias, double.PositiveInfinity, out var hit) ? hit.T : double.PositiveInfinity;
            var sum = Vec3.Zero;
            for (int li = 0; li < _lights.Count; li++)
                if (_beams![li] > 0) sum += Beam(li, origin, dir, hitAt, pixel);
            return sum;
        }

        private Vec3 Beam(int li, Vec3 origin, Vec3 dir, double hitAt, in PixelSamples pixel)
        {
            var light = _lights[li];
            var item = light.Item;
            var v = item.Volumetric;
            double length = _beams![li];
            bool distant = light.Type == LightType.Distant;
            double a = 0, b = hitAt;
            if (distant) b = Math.Min(b, length);
            else
            {
                if (!Sphere(origin, dir, light.Position, length, out double s0, out double s1)) return Vec3.Zero;
                a = Math.Max(a, s0);
                b = Math.Min(b, s1);
                if (light.Type == LightType.Spot && b > a && !Cone(origin, dir, light.Position, light.Direction, item.ConeAngle, ref a, ref b)) return Vec3.Zero;
            }
            if (!(b > a)) return Vec3.Zero;
            int k = pixel.PerHit(v.Samples);
            double step = (b - a) / k;
            double jitter = Random01(pixel.X, pixel.Y, pixel.Ray, DimensionVolume, li);
            bool shadows = v.Shadows && _settings.Shadows && item.Shadows;
            var map = _shadowMaps?[li];
            double total = 0;
            for (int i = 0; i < k; i++)
            {
                double t = a + (i + jitter) * step;
                var p = origin + dir * t;
                Vec3 toLight;
                double distance, strength;
                if (distant)
                {
                    toLight = -light.Direction;
                    distance = double.PositiveInfinity;
                    strength = v.Distance > 0 ? 1 - t / length : 1;
                }
                else
                {
                    var d = light.Position - p;
                    distance = d.Length;
                    if (distance == 0) continue;
                    toLight = d / distance;
                    strength = item.Range > 0 ? item.Falloff(distance) : 1 - distance / length;
                    if (light.Type == LightType.Spot)
                        strength *= item.SpotFactor(Math.Acos(Math.Clamp(Vec3.Dot(light.Direction, -toLight), -1, 1)) * 180 / Math.PI);
                }
                if (strength <= 0) continue;
                strength *= FogClear(t);
                if (shadows && strength > 0)
                    strength *= map != null ? map.Lit(p, Vec3.Zero, toLight, _settings.Counters) : Transmission(p, toLight, distance);
                total += strength;
            }
            return light.Color * (total * step * 0.1 * v.Intensity);
        }

        /// <summary>How much of what's at a distance from the camera the scene's fog lets through (1 without fog).</summary>
        private double FogClear(double distance)
        {
            if (_scene.Fog != FogType.Linear) return 1;
            double span = _scene.FogMaxDistance - _scene.FogMinDistance;
            double f = span <= 0 ? (distance >= _scene.FogMaxDistance ? 1 : 0) : Math.Clamp((distance - _scene.FogMinDistance) / span, 0, 1);
            return 1 - f;
        }

        /// <summary>Where a ray (unit direction) is inside a sphere: from t0 to t1, false if it misses or it's all behind.</summary>
        private static bool Sphere(Vec3 origin, Vec3 dir, Vec3 centre, double radius, out double t0, out double t1)
        {
            var oc = origin - centre;
            double b = Vec3.Dot(oc, dir), c = oc.LengthSquared - radius * radius;
            double disc = b * b - c;
            t0 = t1 = 0;
            if (disc < 0) return false;
            double s = Math.Sqrt(disc);
            (t0, t1) = (-b - s, -b + s);
            return t1 > 0;
        }

        /// <summary>
        /// Narrows [a, b] along a ray to where it is inside a spot light's cone (its forward half, which is convex, so
        /// that's one stretch): the cone's surface and the plane through its apex split the stretch, and the pieces
        /// whose middles are inside are kept. False when none is.
        /// </summary>
        private static bool Cone(Vec3 origin, Vec3 dir, Vec3 apex, Vec3 axis, double angle, ref double a, ref double b)
        {
            double cos = Math.Cos(Math.Min(angle, 90) * Math.PI / 180), cos2 = cos * cos;
            var co = origin - apex;
            double da = Vec3.Dot(dir, axis), ca = Vec3.Dot(co, axis), dc = Vec3.Dot(dir, co), cc = co.LengthSquared;
            double qa = da * da - cos2, qb = 2 * (da * ca - dc * cos2), qc = ca * ca - cc * cos2;
            // The stretch's ends and where the cone's surface and the apex's plane cross it (NaN where they don't).
            double r1 = double.NaN, r2 = double.NaN, r3 = double.NaN;
            if (Math.Abs(qa) > 1e-12)
            {
                double disc = qb * qb - 4 * qa * qc;
                if (disc >= 0)
                {
                    double s = Math.Sqrt(disc);
                    (r1, r2) = ((-qb - s) / (2 * qa), (-qb + s) / (2 * qa));
                }
            }
            else if (Math.Abs(qb) > 1e-12) r1 = -qc / qb;
            if (Math.Abs(da) > 1e-12) r3 = -ca / da;
            Span<double> ts = stackalloc double[5];
            int n = 0;
            ts[n++] = a;
            ts[n++] = b;
            foreach (double t in (ReadOnlySpan<double>)[r1, r2, r3])
                if (t > a && t < b) ts[n++] = t;
            var cut = ts[..n];
            cut.Sort();
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int i = 0; i + 1 < n; i++)
            {
                if (!(cut[i + 1] > cut[i])) continue;
                double m = (cut[i] + cut[i + 1]) / 2;
                var v = co + dir * m;
                double s = Vec3.Dot(v, axis);
                if (s > 0 && s * s >= v.LengthSquared * cos2)
                {
                    lo = Math.Min(lo, cut[i]);
                    hi = Math.Max(hi, cut[i + 1]);
                }
            }
            if (!(hi > lo)) return false;
            (a, b) = (lo, hi);
            return true;
        }

        /// <summary>
        /// How much of a light the camera sees along a ray, for its flare: 1 clear, less through transparent surfaces
        /// (by their transparency), 0 behind something opaque. Unlike a shadow ray, every surface counts, whether or not
        /// its object casts shadows.
        /// </summary>
        public double Visibility(Vec3 from, Vec3 dir, double distance)
        {
            double share = 1, reach = double.IsInfinity(distance) ? distance : distance * (1 - 1e-9);
            for (int i = 0; i < 64 && share > 1e-4; i++)
            {
                if (!_bvh.Intersect(from, dir, _bias, reach, out var hit)) return share;
                var tri = _bvh[hit.Triangle];
                var m = _materials[tri.Material];
                double clear = m.TexturedTransparency ? LookAt(m, tri, hit, from + dir * hit.T, tri.Normal, 0).Transparency : m.Transparency;
                share *= _settings.Transparency ? clear : 0;
                from += dir * hit.T;
                reach -= hit.T;
            }
            return 0;
        }
    }
}
