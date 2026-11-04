using AVAModeler.Core;

namespace AVAModeler.Core.Tests;

public class ProceduralTests
{
    private static ProceduralTexture Of(ProceduralKind kind, Action<TextureLayer>? setup = null)
    {
        var t = TextureLayer.NewProcedural(kind);
        setup?.Invoke(t);
        return Procedurals.Prepare(t)!;
    }

    /// <summary>Points spread through a few texture units, the same every time.</summary>
    private static IEnumerable<Vec3> Samples(int n = 4000)
    {
        for (int i = 0; i < n; i++)
            yield return new Vec3(i * 0.1371 % 7.3 - 3.1, i * 0.0917 % 5.9 - 2.2, i * 0.1733 % 6.7 - 3.4);
    }

    [Fact]
    public void The_noise_table_is_a_permutation_and_the_noise_is_the_same_every_time()
    {
        Assert.Equal(Enumerable.Range(0, 256), Noise.Permutation.Select(b => (int)b).Order());
        // Perlin's reference implementation's values: fixed for good, so renders stay as they were.
        Assert.Equal(-0.4098787307739258, Noise.Perlin(0.5, 0.25, 0.75), 12);
        Assert.Equal(-0.07313213946708226, Noise.Perlin(-3.3, 12.7, 101.1), 12);
        Assert.Equal(0.12908926063150833, Noise.Fractal(new Vec3(1.1, 2.2, 3.3), 3, 0.5), 12);
        Assert.Equal(0, Noise.Perlin(3, -7, 12));
        Assert.Equal(Noise.Perlin(1.25, 2.5, 3.75), Noise.Perlin(1.25 + 256, 2.5, 3.75 - 512), 9);
        Assert.Equal(Samples().Select(p => Noise.Perlin(p)), Samples().Select(p => Noise.Perlin(p)));
    }

    [Fact]
    public void Noise_stays_in_range_and_centres_on_zero()
    {
        var values = Samples(20000).Select(p => Noise.Perlin(p * 3.7)).ToList();
        Assert.All(values, v => Assert.InRange(v, -1.05, 1.05));
        Assert.InRange(values.Average(), -0.03, 0.03);
        Assert.True(values.Max() > 0.5 && values.Min() < -0.5);
        var turbulence = Samples(5000).Select(p => Noise.Turbulence(p, 4, 0.5)).ToList();
        Assert.All(turbulence, v => Assert.InRange(v, 0, 1.05));
    }

    [Fact]
    public void Fractal_noise_is_between_0_and_1_with_a_mean_of_a_half()
    {
        var noise = Of(ProceduralKind.FractalNoise);
        var values = Samples(20000).Select(noise.Amount).ToList();
        Assert.All(values, v => Assert.InRange(v, 0, 1));
        Assert.InRange(values.Average(), 0.47, 0.53);
        Assert.True(values.Max() > 0.85 && values.Min() < 0.15);
        // More contrast spreads it further; none flattens it to a half.
        double Spread(double contrast) => Samples(4000).Select(Of(ProceduralKind.FractalNoise, t => Procedurals.Set(t, "Contrast", contrast)).Amount)
            .Select(v => Math.Abs(v - 0.5)).Average();
        Assert.True(Spread(3) > Spread(1) * 1.8);
        Assert.Equal(0, Spread(0), 12);
    }

    [Fact]
    public void A_checkerboard_alternates_one_texture_size_across_and_a_floor_on_a_cube_side_doesnt_speckle()
    {
        var checks = Of(ProceduralKind.Checkerboard, t => t.Size = new Vec3(0.5, 0.5, 0.5));
        Assert.Equal(0, checks.Amount(new Vec3(0.25, 0.25, 0.25)));
        Assert.Equal(1, checks.Amount(new Vec3(0.75, 0.25, 0.25)));
        Assert.Equal(0, checks.Amount(new Vec3(0.75, 0.75, 0.25)));
        Assert.Equal(1, checks.Amount(new Vec3(0.75, 0.75, 0.75)));
        Assert.Equal(1, checks.Amount(new Vec3(-0.25, 0.25, 0.25)));
        // Along a row of squares on the floor (y = 0, with a hair of rounding either side), every other one.
        for (int i = -6; i < 6; i++)
        {
            double expected = Math.Abs(i) % 2;
            Assert.Equal(expected, checks.Amount(new Vec3(i * 0.5 + 0.25, 1e-13, 0.1)));
            Assert.Equal(expected, checks.Amount(new Vec3(i * 0.5 + 0.25, -1e-13, 0.1)));
        }
        // The centre moves the board; Negative swaps the squares.
        Assert.Equal(1, Of(ProceduralKind.Checkerboard, t => t.Center = new Vec3(1, 0, 0)).Amount(new Vec3(0.5, 0.5, 0.5)));
        Assert.Equal(1, Of(ProceduralKind.Checkerboard, t => t.Negative = true).Amount(new Vec3(0.5, 0.5, 0.5)));
    }

    [Fact]
    public void Grid_lines_are_one_size_apart_and_their_thickness_wide_seen_down_the_axis()
    {
        var grid = Of(ProceduralKind.Grid, t => { t.Size = new Vec3(0.5, 0.5, 0.5); t.Axis = Axis.Y; });
        // Thickness 0.1 of the 0.5 m spacing: lines 5 cm wide, centred on every half metre of X and Z.
        foreach (double x in new[] { -1.0, -0.5, 0, 0.5, 1, 1.5 })
        {
            Assert.Equal(1, grid.Amount(new Vec3(x, 0.37, 0.25)));
            Assert.Equal(1, grid.Amount(new Vec3(x + 0.024, 3.1, 0.25)));
            Assert.Equal(0, grid.Amount(new Vec3(x + 0.026, 0.37, 0.25)));
            Assert.Equal(0, grid.Amount(new Vec3(x + 0.25, -2, 0.25)));
        }
        Assert.Equal(1, grid.Amount(new Vec3(0.25, 0, 0.5)));
        // Thicker lines cover more.
        var thick = Of(ProceduralKind.Grid, t => { t.Size = new Vec3(0.5, 0.5, 0.5); t.Axis = Axis.Y; Procedurals.Set(t, "Line Thickness", 0.5); });
        Assert.Equal(1, thick.Amount(new Vec3(0.1, 0, 0.25)));
    }

    [Fact]
    public void Dots_are_round_and_fuzziness_softens_their_edges()
    {
        var dots = Of(ProceduralKind.Dots, t => t.Axis = Axis.Z);
        Assert.Equal(1, dots.Amount(new Vec3(2, 3, 0.7)));
        Assert.Equal(1, dots.Amount(new Vec3(2.24, 3, 0)));
        Assert.Equal(0, dots.Amount(new Vec3(2.26, 3, 0)));
        Assert.Equal(0, dots.Amount(new Vec3(2.5, 3.5, 0)));
        Assert.InRange(Samples(20000).Average(dots.Amount), Math.PI / 16 - 0.02, Math.PI / 16 + 0.02);
        var soft = Of(ProceduralKind.Dots, t => { t.Axis = Axis.Z; Procedurals.Set(t, "Fuzziness", 1); });
        Assert.Equal(1, soft.Amount(new Vec3(2, 3, 0)));
        Assert.InRange(soft.Amount(new Vec3(2.125, 3, 0)), 0.3, 0.7);
        Assert.Equal(0, soft.Amount(new Vec3(2.26, 3, 0)));
    }

    [Fact]
    public void Every_procedural_gives_amounts_from_0_to_1_that_vary_and_repeat()
    {
        foreach (var type in Procedurals.All)
        {
            var p = Of(type.Kind, t => t.Size = new Vec3(0.7, 0.7, 0.7));
            var values = Samples().Select(p.Amount).ToList();
            Assert.All(values, v => Assert.InRange(v, 0, 1));
            Assert.Equal(values, Samples().Select(Of(type.Kind, t => t.Size = new Vec3(0.7, 0.7, 0.7)).Amount));
            if (type.Kind == ProceduralKind.Value) Assert.All(values, v => Assert.Equal(1, v));
            else Assert.True(values.Max() - values.Min() > 0.5, $"{type.Name} hardly varies: {values.Min()} to {values.Max()}");
        }
    }

    [Fact]
    public void Marble_veins_are_layered_along_the_axis_and_wood_rings_round_it()
    {
        // Without turbulence the veins are flat sheets across the axis, one vein spacing apart.
        var marble = Of(ProceduralKind.Marble, t => { t.Axis = Axis.X; Procedurals.Set(t, "Turbulence", 0); });
        Assert.Equal(1, marble.Amount(new Vec3(0, 0.3, 0.7)), 9);
        Assert.Equal(1, marble.Amount(new Vec3(0.5, -2, 1)), 9);
        Assert.Equal(0, marble.Amount(new Vec3(0.25, 0.3, 0.7)), 9);
        Assert.Equal(marble.Amount(new Vec3(0.1, 0, 0)), marble.Amount(new Vec3(0.1, 5, -3)), 9);
        // Wood's rings are circles round the axis: the same all the way round, and along it.
        var wood = Of(ProceduralKind.Wood, t => { t.Axis = Axis.Y; Procedurals.Set(t, "Turbulence", 0); });
        double ring = wood.Amount(new Vec3(0.33, 0, 0));
        Assert.Equal(ring, wood.Amount(new Vec3(0, 2, 0.33)), 9);
        Assert.Equal(ring, wood.Amount(new Vec3(-0.33 * Math.Sqrt(0.5), -1, 0.33 * Math.Sqrt(0.5))), 9);
        Assert.Equal(0.25, wood.Amount(new Vec3(0.35, 0, 0)), 9);
        Assert.True(wood.Amount(new Vec3(0.399, 0, 0)) > 0.9);
    }

    [Fact]
    public void Ripples_spread_in_circles_from_their_sources()
    {
        var ripples = Of(ProceduralKind.Ripples, t => Procedurals.Set(t, "Wave Sources", 1));
        var source = ProceduralTexture.Source(0);
        // One source: crests every wavelength from it, troughs between, the same every way.
        Assert.Equal(1, ripples.Amount(source), 9);
        Assert.Equal(1, ripples.Amount(source + new Vec3(0.3, 0, 0)), 9);
        Assert.Equal(1, ripples.Amount(source + new Vec3(0, 0, -0.6)), 9);
        Assert.Equal(0, ripples.Amount(source + new Vec3(0, 0.15, 0)), 9);
        Assert.All(Enumerable.Range(0, 16), i => Assert.InRange(ProceduralTexture.Source(i).Length, 0, Math.Sqrt(3)));
    }

    [Fact]
    public void Falloff_fades_a_procedural_away_from_its_centre()
    {
        var value = Of(ProceduralKind.Value, t => { t.Center = new Vec3(1, 0, 0); t.Falloff = new Vec3(0.5, 0, 0); });
        Assert.Equal(1, value.Amount(new Vec3(1, 7, 3)));
        Assert.Equal(0.5, value.Amount(new Vec3(2, 0, 0)), 12);
        Assert.Equal(0.5, value.Amount(new Vec3(0, 0, 0)), 12);
        Assert.Equal(0, value.Amount(new Vec3(4, 0, 0)));
    }

    [Fact]
    public void Settings_are_found_by_name_and_checked()
    {
        var t = TextureLayer.NewProcedural(ProceduralKind.Marble);
        Assert.Equal("Marble", t.Procedural);
        Assert.Equal(4, Procedurals.Get(t, "Frequencies"));
        Assert.Equal(0.5, Procedurals.Get(t, "vein_spacing"));
        Procedurals.Set(t, "VeinSharpness", 8);
        Procedurals.Set(t, "frequencies", 5.4);
        Assert.Equal(8, t.Parameters[Procedurals.SlotIndex("TFP2")]);
        Assert.Equal(5, t.Parameters[Procedurals.SlotIndex("TIP0")]);
        Assert.Contains("must be from 1 to 8", Assert.Throws<ArgumentException>(() => Procedurals.Set(t, "Frequencies", 20)).Message);
        Assert.Contains("vein_spacing", Assert.Throws<ArgumentException>(() => Procedurals.Set(t, "Wavelength", 1)).Message);
        Assert.Throws<InvalidOperationException>(() => Procedurals.Get(new TextureLayer(), "Frequencies"));
        Assert.Equal(ProceduralKind.FractalNoise, Procedurals.Find("fractalnoise")!.Kind);
        Assert.Equal(ProceduralKind.BumpArray, Procedurals.Find("Bump Array")!.Kind);
        Assert.Null(Procedurals.Find("Brick"));
        // Every setting lives in one of LWOB's sub-chunks, none twice in a kind.
        Assert.All(Procedurals.All, k => Assert.Equal(k.Parameters.Count, k.Parameters.Select(p => p.Slot).Distinct().Count()));
        Assert.All(Procedurals.All.SelectMany(k => k.Parameters), p => Assert.Contains(p.Slot, Procedurals.Slots));
    }

    [Fact]
    public void A_layer_changes_type_between_image_maps_and_procedurals()
    {
        var t = new TextureLayer { Channel = TextureChannel.Diffuse, Size = new Vec3(2, 2, 2), Image = "a.png" };
        Assert.Equal("Planar Image Map", Procedurals.TypeOf(t));
        Procedurals.SetType(t, "Wood");
        Assert.True(t.IsProcedural);
        Assert.Equal((TextureChannel.Diffuse, new Vec3(2, 2, 2)), (t.Channel, t.Size));
        Assert.Equal(0.1, Procedurals.Get(t, "Ring Spacing"));
        Procedurals.Set(t, "Ring Spacing", 0.3);
        Procedurals.SetType(t, "Wood");
        Assert.Equal(0.3, Procedurals.Get(t, "Ring Spacing"));
        Procedurals.SetType(t, "Cubic Image Map");
        Assert.True(t.IsImage);
        Assert.Equal(TextureProjection.Cubic, t.Projection);
        Assert.Throws<ArgumentException>(() => Procedurals.SetType(t, "Plaid"));
        Assert.Equal(5 + Procedurals.All.Count, Procedurals.TextureTypes.Count);

        // A copy shares nothing that changes, and the signature notices a setting.
        var a = TextureLayer.NewProcedural(ProceduralKind.Dots);
        var b = a.Clone();
        Procedurals.Set(b, "Dot Diameter", 0.9);
        Assert.Equal(0.5, Procedurals.Get(a, "Dot Diameter"));
        Assert.NotEqual(a.Signature(), b.Signature());
        b = a.Clone();
        b.TextureColor = new Rgb(1, 2, 3);
        Assert.NotEqual(a.Signature(), b.Signature());
    }
}
