using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>What a <see cref="Deformer"/> does.</summary>
public enum DeformerKind
{
    /// <summary>Bends the range along <see cref="Deformer.Axis"/> into an arc of Amount degrees, toward <see cref="Deformer.Toward"/>.</summary>
    Bend,
    /// <summary>Turns each slice along the axis about it, by up to Amount degrees across the range.</summary>
    Twist,
    /// <summary>Scales each slice across the axis, from 1 at the range's low end to Amount at its high end.</summary>
    Taper,
    /// <summary>Slides each slice toward <see cref="Deformer.Toward"/>, by up to Amount metres across the range.</summary>
    Shear,
    /// <summary>Squash and stretch: Amount times as long along the axis about the centre, thinner or fatter across to keep the volume.</summary>
    Squash,
    /// <summary>A free-form deformation: a box of control points (<see cref="Deformer.Offsets"/>) bending what's inside it, Amount of the way.</summary>
    Lattice,
}

/// <summary>
/// A live deformer on an object (Layout's Bend, Twist, Taper, Shear, Squash and Lattice), applied in the object's own
/// coordinates after its morphs, bones and displacements, in the order the object lists them. Each has a keyable
/// <see cref="Amount"/>, so it animates: a bend's or a twist's degrees, a taper's or a squash's scale, a shear's
/// metres, a lattice's share of its offsets. The others work along <see cref="Axis"/> through <see cref="Center"/>,
/// over the range <see cref="Low"/> to <see cref="High"/> (metres from the centre along the axis): points below the
/// range are left alone and points beyond it carry on as the range's end leaves them, so a bent rod's tip stays
/// straight. An amount of zero (one for taper and squash) changes nothing.
/// </summary>
public sealed class Deformer
{
    public DeformerKind Kind;
    /// <summary>The axis it works along.</summary>
    public Axis Axis = Axis.Y;
    /// <summary>The axis a bend curls and a shear slides toward (not <see cref="Axis"/>).</summary>
    public Axis Toward = Axis.X;
    /// <summary>The centre, in object coordinates: the axis runs through it and the range is measured from it.</summary>
    public Vec3 Center;
    /// <summary>The range along the axis, metres from the centre; <see cref="High"/> is above <see cref="Low"/>.</summary>
    public double Low = -1, High = 1;
    /// <summary>Whether it's applied (a switched-off deformer keeps its settings).</summary>
    public bool Enabled = true;
    /// <summary>A lattice's box, in object coordinates.</summary>
    public Vec3 LatticeMin = -Vec3.One, LatticeMax = Vec3.One;
    /// <summary>A lattice's control points along X, Y and Z (2 to 8 each).</summary>
    public (int X, int Y, int Z) Divisions = (2, 2, 2);
    /// <summary>
    /// How far each lattice control point is moved from its place in the box (metres), X fastest then Y then Z
    /// (<c>x + nx·(y + ny·z)</c>); a missing one is not moved.
    /// </summary>
    public Vec3[] Offsets = [];

    /// <summary>The amount: degrees (bend, twist), scale (taper, squash), metres (shear) or share (lattice).</summary>
    public Envelope Amount { get; }

    public Deformer(DeformerKind kind)
    {
        Kind = kind;
        Amount = new Envelope(Neutral(kind));
    }

    /// <summary>The amount that changes nothing: 1 for taper and squash, 0 for the rest.</summary>
    public static double Neutral(DeformerKind kind) => kind is DeformerKind.Taper or DeformerKind.Squash ? 1 : 0;

    /// <summary>The lattice's control point count.</summary>
    public int ControlPoints => Divisions.X * Divisions.Y * Divisions.Z;

    /// <summary>What's wrong with the settings, or null.</summary>
    public string? Problem()
    {
        if (Kind != DeformerKind.Lattice)
        {
            if (!(High > Low)) return "The range's high end must be above its low end.";
            if (Kind is DeformerKind.Bend or DeformerKind.Shear && Toward == Axis) return "A bend or shear's toward axis must differ from its axis.";
        }
        else
        {
            if (Divisions.X is < 2 or > 8 || Divisions.Y is < 2 or > 8 || Divisions.Z is < 2 or > 8) return "A lattice has 2 to 8 control points along each axis.";
            var size = LatticeMax - LatticeMin;
            if (!(size.X > 0 && size.Y > 0 && size.Z > 0)) return "A lattice's box needs some size along every axis (its max above its min).";
            if (Offsets.Length > ControlPoints) return $"A lattice of {Divisions.X} × {Divisions.Y} × {Divisions.Z} has {ControlPoints} control points, not {Offsets.Length}.";
        }
        return null;
    }

    /// <summary>A copy with its own amount envelope.</summary>
    public Deformer Clone()
    {
        var d = new Deformer(Kind)
        {
            Axis = Axis, Toward = Toward, Center = Center, Low = Low, High = High, Enabled = Enabled,
            LatticeMin = LatticeMin, LatticeMax = LatticeMax, Divisions = Divisions, Offsets = [.. Offsets],
        };
        d.Amount.CopyFrom(Amount);
        return d;
    }

    /// <summary>Deforms points in place at a frame (nothing when off, wrong, or at its neutral amount).</summary>
    public void Apply(Vec3[] points, double frame)
    {
        if (!Enabled || Problem() != null) return;
        double amount = Amount.Evaluate(frame);
        if (!double.IsFinite(amount) || amount == Neutral(Kind)) return;
        if (Kind == DeformerKind.Lattice) { ApplyLattice(points, amount); return; }
        double span = High - Low;
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i] - Center;
            double s = p[Axis];
            double clamped = Math.Clamp(s, Low, High), share = (clamped - Low) / span;
            switch (Kind)
            {
                case DeformerKind.Twist:
                {
                    double angle = amount * share * Math.PI / 180;
                    p = Turn(p, Axis, angle);
                    break;
                }
                case DeformerKind.Taper:
                {
                    double f = 1 + (amount - 1) * share;
                    p = Vec3.Scale(p, Vec3.One + (Vec3.One - Vec3.Unit(Axis)) * (f - 1));
                    break;
                }
                case DeformerKind.Shear:
                    p = p.With(Toward, p[Toward] + amount * share);
                    break;
                case DeformerKind.Squash:
                {
                    double across = 1 / Math.Sqrt(Math.Max(Math.Abs(amount), 1e-9));
                    var f = Vec3.One * across;
                    p = Vec3.Scale(p, f.With(Axis, amount));
                    break;
                }
                case DeformerKind.Bend:
                {
                    double phi = amount * Math.PI / 180;
                    if (Math.Abs(phi) < 1e-12) break;
                    double r = span / phi;
                    double theta = phi * share;
                    double d = p[Toward];
                    double along = Low + (r - d) * Math.Sin(theta);
                    double toward = r - (r - d) * Math.Cos(theta);
                    // Beyond either end the point carries on straight, along the arc's tangent there.
                    double extra = s - clamped;
                    along += extra * Math.Cos(theta);
                    toward += extra * Math.Sin(theta);
                    p = p.With(Axis, along).With(Toward, toward);
                    break;
                }
            }
            points[i] = p + Center;
        }
    }

    private static Vec3 Turn(Vec3 p, Axis axis, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        // Right-handed about the axis, as the Modeler's Rotate is: a positive angle about Y turns +Z toward +X.
        return axis switch
        {
            Axis.X => new Vec3(p.X, p.Y * c - p.Z * s, p.Y * s + p.Z * c),
            Axis.Y => new Vec3(p.X * c + p.Z * s, p.Y, -p.X * s + p.Z * c),
            _ => new Vec3(p.X * c - p.Y * s, p.X * s + p.Y * c, p.Z),
        };
    }

    /// <summary>Sederberg and Parry's free-form deformation: Bernstein weights over the box, inside it only.</summary>
    private void ApplyLattice(Vec3[] points, double amount)
    {
        if (Offsets.Length == 0) return;
        var (nx, ny, nz) = Divisions;
        var size = LatticeMax - LatticeMin;
        var bx = new double[nx];
        var by = new double[ny];
        var bz = new double[nz];
        for (int i = 0; i < points.Length; i++)
        {
            var q = points[i] - LatticeMin;
            double u = q.X / size.X, v = q.Y / size.Y, w = q.Z / size.Z;
            if (u < 0 || u > 1 || v < 0 || v > 1 || w < 0 || w > 1) continue;
            Bernstein(nx - 1, u, bx);
            Bernstein(ny - 1, v, by);
            Bernstein(nz - 1, w, bz);
            var move = Vec3.Zero;
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        int k = x + nx * (y + ny * z);
                        if (k >= Offsets.Length) continue;
                        move += Offsets[k] * (bx[x] * by[y] * bz[z]);
                    }
            points[i] += move * amount;
        }
    }

    private static void Bernstein(int n, double t, double[] into)
    {
        for (int i = 0; i <= n; i++) into[i] = Choose(n, i) * Math.Pow(t, i) * Math.Pow(1 - t, n - i);
    }

    private static double Choose(int n, int k)
    {
        double r = 1;
        for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }
}
