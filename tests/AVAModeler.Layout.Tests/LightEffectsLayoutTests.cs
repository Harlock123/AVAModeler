using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Layout.Tests;

/// <summary>The lights' lens flares, volumetric beams and shadow maps in Layout: the session's undo steps, the flare intensity channel and the views' hints.</summary>
public class LightEffectsLayoutTests
{
    [Fact]
    public void Each_light_effect_is_one_undo_step_and_a_bad_one_changes_nothing()
    {
        var s = new LayoutSession();
        var light = s.Scene.Lights.First();
        s.SetLensFlare(light, new LensFlare { On = true, Streaks = 4 });
        Assert.Equal($"Set {light.Name}'s Lens Flare", s.History.UndoLabel);
        s.SetVolumetric(light, new VolumetricLight { On = true, Samples = 8 });
        s.SetShadows(light, true, new ShadowMapSettings { On = true, Size = 256 });
        s.SetFlareIntensity(light, s.Frame, 0.5, key: false);
        Assert.Equal(4, s.History.Count);
        Assert.True(light.UsesShadowMap);
        Assert.Equal(0.5, light.FlareIntensity.Evaluate(0));

        Assert.Throws<ArgumentException>(() => s.SetShadows(light, true, new ShadowMapSettings { Size = 10 }));
        Assert.Throws<ArgumentException>(() => s.SetLensFlare(light, new LensFlare { Dissolve = 3 }));
        Assert.Throws<ArgumentException>(() => s.SetVolumetric(light, new VolumetricLight { Samples = 999 }));
        Assert.Throws<ArgumentException>(() => s.SetFlareIntensity(light, 0, -1, key: false));
        Assert.Equal(4, s.History.Count);
        Assert.Equal(256, light.ShadowMap.Size);

        s.Undo();
        s.Undo();
        Assert.False(light.UsesShadowMap);
        Assert.True(light.Volumetric.On);
        s.Undo();
        s.Undo();
        Assert.False(light.Flare.On);
        Assert.False(light.Volumetric.On);
        Assert.Equal(1, light.FlareIntensity.Evaluate(0));
    }

    [Fact]
    public void Flare_intensity_is_a_channel_while_the_flare_is_on_or_keyed()
    {
        var s = new LayoutSession();
        var light = s.Scene.Lights.First();
        Assert.Null(SceneChannels.Find(light, SceneChannels.FlareIntensity));
        light.Flare = new LensFlare { On = true };
        Assert.Same(light.FlareIntensity, SceneChannels.Find(light, SceneChannels.FlareIntensity));
        s.SetFlareIntensity(light, 10, 2, key: true);
        s.SetFlareIntensity(light, 20, 0, key: true);
        Assert.Equal(2, light.FlareIntensity.Keys.Count);
        light.Flare = new LensFlare();
        // Still listed while it's animated, so its keys show in the dope sheet.
        Assert.Contains(SceneChannels.Keyed(light, s.Scene), c => c.Name == SceneChannels.FlareIntensity);
    }

    [Fact]
    public void The_view_hints_a_flare_and_a_volumetric_spots_cone()
    {
        var spot = new LightItem("Spot", LightType.Spot) { ConeAngle = 30 };
        int plain = SceneDrawing.Glyph(spot).Count();
        spot.Flare = new LensFlare { On = true };
        Assert.Equal(plain + 2, SceneDrawing.Glyph(spot).Count());
        spot.Volumetric = new VolumetricLight { On = true, Distance = 3 };
        var lines = SceneDrawing.Glyph(spot).ToList();
        Assert.Equal(plain + 2 + 24 + 4, lines.Count);
        // The cone's rim is 3 m out along +Z, tan 30° × 3 across.
        Assert.Contains(lines, l => Math.Abs(l.Item1.Z - 3) < 1e-9 && Math.Abs(new Vec3(l.Item1.X, l.Item1.Y, 0).Length - 3 * Math.Tan(Math.PI / 6)) < 1e-9);
        // A volumetric point light has no cone.
        var bulb = new LightItem("Bulb", LightType.Point) { Volumetric = new VolumetricLight { On = true } };
        Assert.Equal(SceneDrawing.Glyph(new LightItem("Other", LightType.Point)).Count(), SceneDrawing.Glyph(bulb).Count());
    }
}
