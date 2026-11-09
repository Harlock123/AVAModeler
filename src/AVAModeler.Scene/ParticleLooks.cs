using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// A particle the ray tracer draws itself rather than as faces (world coordinates): a puff seen through (smoke) or a
/// sprite that adds light. <see cref="Color"/> is 0 to 1.
/// </summary>
public readonly record struct SoftParticle(Vec3 Position, double Size, Vec3 Color, double Opacity, double Glow, bool Additive);

/// <summary>The geometry particles are drawn with, for each look but the plain diamonds.</summary>
public static class ParticleLooks
{
    /// <summary>The most faces copies of an object add, all particles together: past it, fewer particles carry copies.</summary>
    public const int MaxInstanceFaces = 2_000_000;

    /// <summary>The most grid points a blob surface is worked out on: past it, the grid gets coarser.</summary>
    public const int MaxBlobGrid = 3_000_000;

    /// <summary>
    /// One smooth surface round all the particles as blobs (metaballs): each particle's field falls from 1 at its centre
    /// to 0 at its reach, and the surface is where the fields add to a quarter, so a lone particle is a ball of its size
    /// and close ones melt together. Made by marching tetrahedra over a grid, welded, in the coordinates
    /// <paramref name="toLocal"/> takes world points to. Returns how many faces it added.
    /// </summary>
    public static int AddBlobs(Mesh mesh, IReadOnlyList<ParticleSystem.Particle> particles, Mat4 toLocal, int surface)
    {
        const double iso = 0.25;
        // A lone particle's surface is at d = R·√(1 − √iso) = 0.707 R from its centre: R = 0.707 × size puts it at size/2.
        const double reach = 0.7071;
        var live = particles.Where(p => p.Size > 1e-9).ToList();
        if (live.Count == 0) return 0;
        var box = Box3.Empty;
        foreach (var p in live)
        {
            var r = new Vec3(1, 1, 1) * (p.Size * reach);
            box = box.Include(p.Position - r).Include(p.Position + r);
        }
        var sizes = live.Select(p => p.Size).Order().ToList();
        double cell = sizes[sizes.Count / 2] * reach / 5;
        var span = box.Size;
        // Coarser if the grid would be too big (particles spread far apart).
        double points = (span.X / cell + 3) * (span.Y / cell + 3) * (span.Z / cell + 3);
        if (points > MaxBlobGrid) cell *= Math.Cbrt(points / MaxBlobGrid);
        int nx = (int)Math.Ceiling(span.X / cell) + 3, ny = (int)Math.Ceiling(span.Y / cell) + 3, nz = (int)Math.Ceiling(span.Z / cell) + 3;
        var origin = box.Min - new Vec3(cell, cell, cell);
        var field = new float[nx * ny * nz];
        int Index(int x, int y, int z) => (z * ny + y) * nx + x;
        Vec3 At(int x, int y, int z) => origin + new Vec3(x * cell, y * cell, z * cell);
        foreach (var p in live)
        {
            double R = p.Size * reach, R2 = R * R;
            var lo = (p.Position - new Vec3(R, R, R) - origin) / cell;
            var hi = (p.Position + new Vec3(R, R, R) - origin) / cell;
            int x0 = Math.Max(0, (int)Math.Floor(lo.X)), y0 = Math.Max(0, (int)Math.Floor(lo.Y)), z0 = Math.Max(0, (int)Math.Floor(lo.Z));
            int x1 = Math.Min(nx - 1, (int)Math.Ceiling(hi.X)), y1 = Math.Min(ny - 1, (int)Math.Ceiling(hi.Y)), z1 = Math.Min(nz - 1, (int)Math.Ceiling(hi.Z));
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double d2 = (At(x, y, z) - p.Position).LengthSquared;
                        if (d2 >= R2) continue;
                        double f = 1 - d2 / R2;
                        field[Index(x, y, z)] += (float)(f * f);
                    }
        }
        // Six tetrahedra a cube round its 0–6 diagonal (the same split in every cube, so neighbours' faces match).
        int[][] cube = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0], [0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1]];
        int[][] tets = [[0, 5, 1, 6], [0, 1, 2, 6], [0, 2, 3, 6], [0, 3, 7, 6], [0, 7, 4, 6], [0, 4, 5, 6]];
        var welded = new Dictionary<(int, int), int>();
        int before = mesh.Polygons.Count;
        int Cross(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (welded.TryGetValue(key, out int i)) return i;
            int ax = a % nx, ay = a / nx % ny, az = a / (nx * ny), bx = b % nx, by = b / nx % ny, bz = b / (nx * ny);
            double va = field[a], vb = field[b], t = Math.Abs(vb - va) < 1e-12 ? 0.5 : (iso - va) / (vb - va);
            var p = At(ax, ay, az) + (At(bx, by, bz) - At(ax, ay, az)) * Math.Clamp(t, 0, 1);
            welded[key] = i = mesh.Points.Count;
            mesh.Points.Add(toLocal.TransformPoint(p));
            return i;
        }
        void Face(int[] corners, int inside, int outside)
        {
            if (corners.Distinct().Count() < 3) return;
            var poly = new Polygon([.. corners], surface);
            // Facing out of the blob: from the inside corner toward the outside one.
            int x = inside % nx, y = inside / nx % ny, z = inside / (nx * ny), ox = outside % nx, oy = outside / nx % ny, oz = outside / (nx * ny);
            var outward = toLocal.TransformVector(At(ox, oy, oz) - At(x, y, z));
            if (Vec3.Dot(mesh.Normal(poly), outward) < 0) poly.Points.Reverse();
            mesh.Polygons.Add(poly);
        }
        Span<int> v = stackalloc int[4];
        for (int z = 0; z + 1 < nz; z++)
            for (int y = 0; y + 1 < ny; y++)
                for (int x = 0; x + 1 < nx; x++)
                    foreach (var tet in tets)
                    {
                        int count = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            var c = cube[tet[i]];
                            v[i] = Index(x + c[0], y + c[1], z + c[2]);
                            if (field[v[i]] > iso) count++;
                        }
                        if (count is 0 or 4) continue;
                        var ins = new List<int>();
                        var outs = new List<int>();
                        for (int i = 0; i < 4; i++) (field[v[i]] > iso ? ins : outs).Add(v[i]);
                        if (ins.Count == 1) Face([Cross(ins[0], outs[0]), Cross(ins[0], outs[1]), Cross(ins[0], outs[2])], ins[0], outs[0]);
                        else if (ins.Count == 3) Face([Cross(outs[0], ins[0]), Cross(outs[0], ins[1]), Cross(outs[0], ins[2])], ins[0], outs[0]);
                        else
                        {
                            // Two in, two out: a quad across the four edges between them, round in order.
                            Face([Cross(ins[0], outs[0]), Cross(ins[0], outs[1]), Cross(ins[1], outs[1]), Cross(ins[1], outs[0])], ins[0], outs[0]);
                        }
                    }
        return mesh.Polygons.Count - before;
    }

    /// <summary>
    /// A copy of <paramref name="instance"/> (its own coordinates, its faces' surfaces moved up by
    /// <paramref name="firstSurface"/>) on each particle: centred on it, scaled so its largest side is the particle's
    /// size, and turned at random and by <see cref="ParticleSettings.Spin"/>, or with its +Z the way the particle goes.
    /// In the coordinates <paramref name="toLocal"/> takes world points to. Returns how many copies it made.
    /// </summary>
    public static int AddInstances(Mesh mesh, IReadOnlyList<ParticleSystem.Particle> particles, ParticleSettings s, Mesh instance, int firstSurface, Mat4 toLocal)
    {
        var faces = instance.Polygons.Where(p => p.Count >= 1).ToList();
        if (faces.Count == 0 || particles.Count == 0) return 0;
        var bounds = instance.Bounds();
        if (bounds.IsEmpty) return 0;
        var centre = (bounds.Min + bounds.Max) / 2;
        double side = Math.Max(bounds.Size.X, Math.Max(bounds.Size.Y, bounds.Size.Z));
        if (side <= 0) side = 1;
        // Past the face budget, every n-th particle carries a copy.
        int every = Math.Max(1, (int)Math.Ceiling((double)particles.Count * faces.Count / MaxInstanceFaces));
        int made = 0;
        for (int i = 0; i < particles.Count; i += every)
        {
            var q = particles[i];
            Mat4 turn;
            if (s.AlignToMotion && q.Velocity.LengthSquared > 1e-12)
            {
                var z = q.Velocity.Normalized();
                var x = Vec3.Cross(Math.Abs(z.Y) < 0.99 ? Vec3.UnitY : Vec3.UnitX, z).Normalized();
                var y = Vec3.Cross(z, x);
                turn = Mat4.FromRows(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0, 0, 0, 0, 1);
            }
            else
            {
                // An axis and a starting turn of its own, then the spin about that axis over its life so far.
                double u = ParticleSystem.Unit(s.Seed, q.Index, 20), w = ParticleSystem.Unit(s.Seed, q.Index, 21);
                double cz = 2 * u - 1, r = Math.Sqrt(Math.Max(0, 1 - cz * cz)), phi = 2 * Math.PI * w;
                var axis = new Vec3(r * Math.Cos(phi), r * Math.Sin(phi), cz);
                double angle = 2 * Math.PI * ParticleSystem.Unit(s.Seed, q.Index, 22) + s.Spin * Math.PI / 180 * q.Age * s.Life;
                turn = Mat4.Rotation(axis, angle);
            }
            var place = toLocal * Mat4.Translation(q.Position) * turn * Mat4.Scaling(q.Size / side) * Mat4.Translation(-centre);
            int b = mesh.Points.Count;
            foreach (var p in instance.Points) mesh.Points.Add(place.TransformPoint(p));
            foreach (var f in faces) mesh.Polygons.Add(new Polygon([.. f.Points.Select(k => k + b)], f.Surface + firstSurface));
            made++;
        }
        return made;
    }

    /// <summary>The particles as the ray tracer's puffs or sprites, in their colour (0 to 1).</summary>
    public static List<SoftParticle> Soft(IEnumerable<ParticleSystem.Particle> particles, ParticleSettings s) =>
        particles.Where(p => p.Size > 0).Select(p => new SoftParticle(p.Position, p.Size, s.Color.ToVec(), s.Opacity, s.Glow, s.Look == ParticleLook.Sprite)).ToList();
}
