using AVAModeler.Core;

namespace AVAModeler.Scene.Tests;

public class SunSkyTests
{
    private static EnvironmentMap Sky(double heading, double elevation, double turbidity = 3)
    {
        var img = EnvironmentMap.Load(EnvironmentMap.SunSky(heading, elevation, turbidity), null, out var problem);
        Assert.Null(problem);
        return new EnvironmentMap(img!);
    }

    private static double Lum(Vec3 c) => 0.2126 * c.X + 0.7152 * c.Y + 0.0722 * c.Z;

    private static Vec3 Dir(double heading, double elevation)
    {
        double h = heading * Math.PI / 180, e = elevation * Math.PI / 180;
        return new Vec3(Math.Sin(h) * Math.Cos(e), Math.Sin(e), Math.Cos(h) * Math.Cos(e));
    }

    [Fact]
    public void Day_is_blue_overhead_and_brightest_at_the_sun()
    {
        var sky = Sky(90, 40);
        var zenith = sky.Sample(Vec3.UnitY);
        Assert.True(zenith.Z > zenith.X, $"Zenith {zenith}.");
        double atSun = Lum(sky.Sample(Dir(90, 40)));
        Assert.True(atSun > 5, $"Sun {atSun}.");
        Assert.True(atSun > 10 * Lum(sky.Sample(Dir(270, 40))));
        // The light from above outshines the ground's.
        Assert.True(Lum(sky.Irradiance(Vec3.UnitY)) > Lum(sky.Irradiance(-Vec3.UnitY)));
    }

    [Fact]
    public void Night_is_dark_and_sunset_is_warm_on_the_suns_side()
    {
        var day = Sky(0, 50);
        var night = Sky(0, -30);
        Assert.True(Lum(night.Irradiance(Vec3.UnitY)) < 0.1 * Lum(day.Irradiance(Vec3.UnitY)));
        var sunset = Sky(0, 2);
        var toward = sunset.Sample(Dir(0, 5));
        var away = sunset.Sample(Dir(180, 5));
        Assert.True(toward.X > toward.Z, $"Toward the sun {toward}.");
        Assert.True(Lum(toward) > Lum(away));
    }

    [Fact]
    public void Haze_greys_the_sky_and_bad_names_are_refused()
    {
        var clear = Sky(0, 60, 2).Sample(Vec3.UnitY);
        var hazy = Sky(0, 60, 10).Sample(Vec3.UnitY);
        Assert.True(clear.Z / clear.X > hazy.Z / hazy.X);
        Assert.Null(EnvironmentMap.Load("builtin:sun?colour=3", null, out var problem));
        Assert.Contains("builtin:sun", problem);
        Assert.Null(EnvironmentMap.Load("builtin:sunny", null, out _));
        Assert.NotNull(EnvironmentMap.Load("builtin:sun", null, out _));
    }
}
