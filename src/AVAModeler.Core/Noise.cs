namespace AVAModeler.Core;

/// <summary>
/// Ken Perlin's "improved noise" (2002) in 3D, with his reference permutation table fixed in the code, so the same
/// point always gives the same value on every machine and every run: renders with procedural textures are
/// repeatable. The OpenGL renderer's shader does the same sums with the same table (<see cref="Permutation"/>).
/// </summary>
public static class Noise
{
    /// <summary>The 256 numbers 0–255 in the order of Perlin's reference implementation.</summary>
    public static readonly byte[] Permutation =
    [
        151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69, 142, 8, 99, 37, 240,
        21, 10, 23, 190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32, 57, 177, 33, 88,
        237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175, 74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83,
        111, 229, 122, 60, 211, 133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1, 216,
        80, 73, 209, 76, 132, 187, 208, 89, 18, 169, 200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186,
        3, 64, 52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212, 207, 206, 59, 227, 47, 16, 58, 17,
        182, 189, 28, 42, 223, 183, 170, 213, 119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9, 129,
        22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104, 218, 246, 97, 228, 251, 34, 242, 193, 238,
        210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31, 181, 199, 106, 157, 184,
        84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93, 222, 114, 67, 29, 24, 72, 243, 141, 128, 195,
        78, 66, 215, 61, 156, 180,
    ];

    /// <summary>The permutation table read round and round (index 256 is index 0 again).</summary>
    public static int Perm(int i) => Permutation[i & 255];

    /// <summary>Noise at a point: smooth, about −1 to 1, 0 at every whole-number lattice point, repeating every 256.</summary>
    public static double Perlin(double x, double y, double z)
    {
        double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
        int X = (int)(long)fx & 255, Y = (int)(long)fy & 255, Z = (int)(long)fz & 255;
        x -= fx;
        y -= fy;
        z -= fz;
        double u = Fade(x), v = Fade(y), w = Fade(z);
        int a = Perm(X) + Y, aa = Perm(a) + Z, ab = Perm(a + 1) + Z;
        int b = Perm(X + 1) + Y, ba = Perm(b) + Z, bb = Perm(b + 1) + Z;
        return Lerp(w,
            Lerp(v, Lerp(u, Grad(Perm(aa), x, y, z), Grad(Perm(ba), x - 1, y, z)),
                    Lerp(u, Grad(Perm(ab), x, y - 1, z), Grad(Perm(bb), x - 1, y - 1, z))),
            Lerp(v, Lerp(u, Grad(Perm(aa + 1), x, y, z - 1), Grad(Perm(ba + 1), x - 1, y, z - 1)),
                    Lerp(u, Grad(Perm(ab + 1), x, y - 1, z - 1), Grad(Perm(bb + 1), x - 1, y - 1, z - 1))));
    }

    /// <inheritdoc cref="Perlin(double, double, double)"/>
    public static double Perlin(Vec3 p) => Perlin(p.X, p.Y, p.Z);

    /// <summary>
    /// Where each octave is shifted, so the octaves' lattices don't all meet at the origin (where noise is 0): octave
    /// i is sampled at p·2^i + i·this.
    /// </summary>
    public static readonly Vec3 OctaveShift = new(19.1, 33.4, 47.3);

    /// <summary>
    /// Fractal (fBm) noise: <paramref name="octaves"/> octaves, each twice the frequency of the one before and
    /// <paramref name="smallPower"/> times its weight, divided by the weights' sum (so about −1 to 1, mean 0).
    /// </summary>
    public static double Fractal(Vec3 p, int octaves, double smallPower)
    {
        double sum = 0, weights = 0, weight = 1, scale = 1;
        for (int i = 0; i < octaves; i++)
        {
            sum += weight * Perlin(p * scale + OctaveShift * i);
            weights += weight;
            weight *= smallPower;
            scale *= 2;
        }
        return weights > 0 ? sum / weights : 0;
    }

    /// <summary>
    /// Turbulence: like <see cref="Fractal"/> but of the noise's size (|noise|), so 0 to about 1, with sharp creases
    /// where the noise crosses zero.
    /// </summary>
    public static double Turbulence(Vec3 p, int octaves, double smallPower)
    {
        double sum = 0, weights = 0, weight = 1, scale = 1;
        for (int i = 0; i < octaves; i++)
        {
            sum += weight * Math.Abs(Perlin(p * scale + OctaveShift * i));
            weights += weight;
            weight *= smallPower;
            scale *= 2;
        }
        return weights > 0 ? sum / weights : 0;
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Lerp(double t, double a, double b) => a + t * (b - a);

    private static double Grad(int hash, double x, double y, double z)
    {
        int h = hash & 15;
        double u = h < 8 ? x : y;
        double v = h < 4 ? y : h == 12 || h == 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
