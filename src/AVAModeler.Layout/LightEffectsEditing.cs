using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// A light's lens flare, volumetric beam and shadow settings, each set as one undo step on the session's history (as
/// a Lights panel's OK or an agent's call does). Each record is checked first (<c>Checked()</c>), so a bad one throws an
/// <see cref="ArgumentException"/> saying what's wrong and changes nothing.
/// </summary>
public static class LightEffectsEditing
{
    /// <summary>Sets a light's lens flare.</summary>
    public static void SetLensFlare(this LayoutSession session, LightItem light, LensFlare flare)
    {
        flare.Checked();
        session.Edit($"Set {light.Name}'s Lens Flare", () =>
        {
            light.Flare = flare;
            session.Scene.Notify();
        });
    }

    /// <summary>
    /// Sets a light's lens flare intensity (1 is 100%) at a frame by the rule keys follow (<see cref="Keying.Set"/>):
    /// with <paramref name="key"/> a key there, else the value throughout (or the key there on an animated channel).
    /// </summary>
    public static void SetFlareIntensity(this LayoutSession session, LightItem light, double frame, double value, bool key)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentException("The lens flare's intensity must be 0 or more (1 is 100%).");
        session.Edit($"Set {light.Name}'s Flare Intensity", () =>
        {
            Keying.Set(light.FlareIntensity, session.Scene, frame, value, key, $"{light.Name}'s flare intensity");
            session.Scene.Notify();
        });
    }

    /// <summary>Sets a light's volumetric beam.</summary>
    public static void SetVolumetric(this LayoutSession session, LightItem light, VolumetricLight volumetric)
    {
        volumetric.Checked();
        session.Edit($"Set {light.Name}'s Volumetric Light", () =>
        {
            light.Volumetric = volumetric;
            session.Scene.Notify();
        });
    }

    /// <summary>
    /// Sets how a light casts shadows: <paramref name="shadows"/> off for none, else ray traced, or from a shadow map
    /// when <paramref name="map"/> is on.
    /// </summary>
    public static void SetShadows(this LayoutSession session, LightItem light, bool shadows, ShadowMapSettings map)
    {
        map.Checked();
        session.Edit($"Set {light.Name}'s Shadows", () =>
        {
            light.Shadows = shadows;
            light.ShadowMap = map;
            session.Scene.Notify();
        });
    }
}
