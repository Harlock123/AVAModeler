using AVAModeler.Core;

namespace AVAModeler.Scene.Tests;

/// <summary>The lights' lens flares, volumetric beams and shadow maps in copies, undo snapshots, .avascene and .lws files.</summary>
public class LightEffectsFilesTests
{
    private static (Scene Scene, LightItem Spot) Set()
    {
        var scene = Scene.CreateDefault();
        var spot = scene.AddLight("Spot", LightType.Spot);
        spot.Flare = new LensFlare { On = true, Size = 0.2, Ring = true, RingSize = 0.3, Streaks = 6, StreakLength = 0.5, FadeBehind = false, FadeWithDistance = true, NominalDistance = 8, Dissolve = 0.25 };
        spot.FlareIntensity.SetKey(0, new EnvelopeKey(1, 0.5));
        spot.FlareIntensity.AddKey(30, 2);
        spot.Volumetric = new VolumetricLight { On = true, Intensity = 1.5, Samples = 48, Distance = 7, Shadows = false };
        spot.ShadowMap = new ShadowMapSettings { On = true, Size = 1024, Fuzziness = 2.5 };
        return (scene, spot);
    }

    private static void AssertSame(LightItem expected, LightItem actual)
    {
        Assert.Equal(expected.Flare, actual.Flare);
        Assert.Equal(expected.Volumetric, actual.Volumetric);
        Assert.Equal(expected.ShadowMap, actual.ShadowMap);
        Assert.Equal(expected.FlareIntensity.Keys, actual.FlareIntensity.Keys);
    }

    [Fact]
    public void Copies_keep_the_light_effects_and_share_no_envelope()
    {
        var (scene, spot) = Set();
        var copy = (LightItem)scene.Copy().Find("Spot")!;
        AssertSame(spot, copy);
        copy.FlareIntensity.SetConstant(9);
        Assert.Equal(2, spot.FlareIntensity.Keys.Count);
        var clone = (LightItem)scene.CopyItems([spot])[spot];
        AssertSame(spot, clone);
    }

    [Fact]
    public void A_snapshot_puts_the_light_effects_back()
    {
        var (scene, spot) = Set();
        var before = (LightItem)scene.Copy().Find("Spot")!;
        var snap = scene.Snapshot();
        spot.Flare = new LensFlare();
        spot.Volumetric = new VolumetricLight();
        spot.ShadowMap = new ShadowMapSettings();
        spot.FlareIntensity.SetConstant(0.1);
        snap.Restore();
        AssertSame(before, spot);
    }

    [Fact]
    public void Avascene_keeps_them_and_writes_nothing_for_the_defaults()
    {
        var (scene, spot) = Set();
        var back = (LightItem)SceneJson.Read(SceneJson.Write(scene)).Scene.Find("Spot")!;
        AssertSame(spot, back);

        string plain = SceneJson.Write(Scene.CreateDefault());
        foreach (var name in new[] { "lensFlare", "flareIntensity", "volumetric", "shadowMap" }) Assert.DoesNotContain(name, plain);
        var light = SceneJson.Read(plain).Scene.Lights.Single();
        Assert.Equal(new LensFlare(), light.Flare);
        Assert.Equal(new VolumetricLight(), light.Volumetric);
        Assert.Equal(new ShadowMapSettings(), light.ShadowMap);
        Assert.Equal(1, light.FlareIntensity.Evaluate(0));
    }

    [Fact]
    public void Lws_keeps_shadow_maps_and_lightwaves_lens_flare_settings()
    {
        var (scene, spot) = Set();
        var warnings = new List<string>();
        string text = LwsWriter.Write(scene, warnings);
        Assert.Contains("ShadowType 2", text);
        Assert.Contains("ShadowMapSize 1024", text);
        Assert.Contains("ShadowFuzziness 2.5", text);
        Assert.Contains("LensFlare 1", text);
        Assert.Contains("FlareIntensity (envelope)", text);
        Assert.Contains("FlareNominalDistance 8", text);
        Assert.Contains("FlareDissolve 0.25", text);
        Assert.Contains($"LensFlareFade {LwsLightBits.FadeOffScreen | LwsLightBits.FadeWithDistance}", text);
        Assert.Contains($"LensFlareOptions {LwsLightBits.StarFilter | LwsLightBits.CentralGlow | LwsLightBits.CentralRing}", text);
        Assert.Contains(warnings, w => w.Contains("volumetric"));
        Assert.Contains(warnings, w => w.Contains("lens flare size"));

        var back = (LightItem)LwsReader.Read(text, _ => null).Scene.Find("Spot")!;
        Assert.Equal(spot.ShadowMap, back.ShadowMap);
        Assert.True(back.Shadows);
        Assert.Equal(spot.FlareIntensity.Keys, back.FlareIntensity.Keys);
        var f = back.Flare;
        Assert.True(f.On && f.Ring && f.Glow && f.FadeWithDistance && f.FadeOffScreen && !f.FadeBehind);
        Assert.Equal((8.0, 0.25, LwsLightBits.StarStreaks), (f.NominalDistance, f.Dissolve, f.Streaks));
        // Volumetrics aren't kept.
        Assert.False(back.Volumetric.On);

        // Without them, the lines are as they were.
        string plain = LwsWriter.Write(Scene.CreateDefault());
        Assert.Contains("ShadowType 1", plain);
        foreach (var key in new[] { "ShadowMapSize", "LensFlare", "FlareIntensity" }) Assert.DoesNotContain(key, plain);
    }

    [Fact]
    public void Lws_from_lightwave_5_reads_shadow_types_and_a_flare()
    {
        const string lw5 = "LWSC\n1\n\nAddLight\nLightName Key\nShadowType 2\nShadowMapSize 2048\nShadowFuzziness 4\nLensFlare 1\nFlareIntensity 0.75\n\nAddLight\nLightName Fill\nShadowType 0\nShadowMapSize 9000\n";
        var result = LwsReader.Read(lw5, _ => null);
        var key = (LightItem)result.Scene.Find("Key")!;
        Assert.True(key.UsesShadowMap);
        Assert.Equal(new ShadowMapSettings { On = true, Size = 2048, Fuzziness = 4 }, key.ShadowMap);
        Assert.True(key.Flare.On);
        Assert.Equal(0.75, key.FlareIntensity.Evaluate(0));
        var fill = (LightItem)result.Scene.Find("Fill")!;
        Assert.False(fill.Shadows);
        Assert.Equal(LightEffects.MaxMapSize, fill.ShadowMap.Size);
        Assert.Contains(result.Warnings, w => w.Contains("9000"));
    }
}
