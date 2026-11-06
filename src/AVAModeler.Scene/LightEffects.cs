namespace AVAModeler.Scene;

/// <summary>
/// A light's lens flare, after LightWave 5's: drawn over the finished picture where the light is seen (added to the
/// colour and clamped; alpha is left as the geometry made it), in the light's colour. A central glow
/// <see cref="Size"/> of the picture's height in radius with a white-hot core, an optional halo ring, and an optional
/// star of <see cref="Streaks"/> streaks at angles that look random but are the same every render (a hash of the
/// light). How bright it is comes from <see cref="LightItem.FlareIntensity"/> (an envelope, 1 = 100%) less
/// <see cref="Dissolve"/>. It fades behind objects (seen through a transparent surface it is dimmed by its
/// transparency), as the light leaves the frame, and optionally with distance. A distant light flares where its
/// direction meets the picture (the sun); a light behind the camera has none.
/// </summary>
public sealed record LensFlare
{
    public bool On { get; init; }
    /// <summary>The central glow's radius as a fraction of the picture's height (default 0.1).</summary>
    public double Size { get; init; } = 0.1;
    /// <summary>Whether the central glow is drawn (default on).</summary>
    public bool Glow { get; init; } = true;
    /// <summary>Whether a halo ring is drawn round the glow (default off).</summary>
    public bool Ring { get; init; }
    /// <summary>The ring's radius as a fraction of the picture's height (default 0.15).</summary>
    public double RingSize { get; init; } = 0.15;
    /// <summary>How many streaks the star has, 0 (none, the default) to 64.</summary>
    public int Streaks { get; init; }
    /// <summary>The longest streak's length as a fraction of the picture's height (default 0.3).</summary>
    public double StreakLength { get; init; } = 0.3;
    /// <summary>Whether it fades behind objects (default on).</summary>
    public bool FadeBehind { get; init; } = true;
    /// <summary>Whether it fades out as the light leaves the frame (default on).</summary>
    public bool FadeOffScreen { get; init; } = true;
    /// <summary>Whether it fades with the light's distance beyond <see cref="NominalDistance"/> (default off).</summary>
    public bool FadeWithDistance { get; init; }
    /// <summary>With <see cref="FadeWithDistance"/>, the distance in metres at which it has its full intensity (default 5): at twice that, half.</summary>
    public double NominalDistance { get; init; } = 5;
    /// <summary>How much it is dissolved, 0 (not at all, the default) to 1 (gone).</summary>
    public double Dissolve { get; init; }

    /// <summary>Throws an <see cref="ArgumentException"/> saying what's wrong, if anything.</summary>
    public LensFlare Checked()
    {
        LightEffects.CheckRange(Size, 0.001, 2, "The lens flare's size", " of the picture's height");
        LightEffects.CheckRange(RingSize, 0.001, 2, "The lens flare's ring size", " of the picture's height");
        if (Streaks < 0 || Streaks > LightEffects.MaxStreaks) throw new ArgumentException($"The lens flare's streaks must be from 0 to {LightEffects.MaxStreaks}.");
        LightEffects.CheckRange(StreakLength, 0.001, 4, "The lens flare's streak length", " of the picture's height");
        RenderEffects.CheckPositive(NominalDistance, "The lens flare's nominal distance", " metres");
        if (!(Dissolve >= 0 && Dissolve <= 1)) throw new ArgumentException("The lens flare's dissolve must be from 0 to 1.");
        return this;
    }
}

/// <summary>
/// A volumetric light: the light's beam seen in the air, for spot and point lights (a spot's cone, a point light's
/// sphere) and distant lights (shafts of sunlight). Camera rays march through the part of the beam between the camera
/// and what they hit, adding the light scattered toward the camera at each step: the light's colour × intensity × the
/// spot's cone factor × its falloff × <see cref="Intensity"/> × 0.1 per metre (so at 1, ten metres of unfaded beam
/// add the light's full brightness), shadowed when <see cref="Shadows"/> is on. Only rays from the camera see beams,
/// not reflections or what's seen through transparent surfaces.
/// </summary>
public sealed record VolumetricLight
{
    public bool On { get; init; }
    /// <summary>The beam's brightness: 1 (100%, the default) adds a tenth of the light per metre of beam crossed.</summary>
    public double Intensity { get; init; } = 1;
    /// <summary>Steps along each camera ray through the beam, 1 to 256 (default 32): the quality, and the cost.</summary>
    public int Samples { get; init; } = 32;
    /// <summary>
    /// How far the beam reaches, in metres from the light (from the camera for a distant light); 0 (the default) takes
    /// the light's range, or, without one, reaches across the scene. A beam without a range fades linearly to nothing
    /// at its end.
    /// </summary>
    public double Distance { get; init; }
    /// <summary>Whether things in the beam cast shadows into it (a shadow ray, or a shadow map lookup, per step; default on).</summary>
    public bool Shadows { get; init; } = true;

    public VolumetricLight Checked()
    {
        if (!double.IsFinite(Intensity) || Intensity < 0) throw new ArgumentException("The volumetric light's intensity must be 0 or more (1 is 100%).");
        RenderEffects.CheckSamples(Samples, "Volumetric light samples");
        if (!double.IsFinite(Distance) || Distance < 0) throw new ArgumentException("The volumetric light's distance must be 0 (automatic) or more metres.");
        return this;
    }
}

/// <summary>
/// A light's shadow map: instead of a shadow ray per point lit, the light's view of the scene is drawn once per frame
/// (per moment of motion blur) into a depth picture <see cref="Size"/> pixels square, and shadows are looked up in it,
/// softened over <see cref="Fuzziness"/> map pixels (percentage-closer filtering). A spot light has one map over its
/// cone (spots wider than 60° use a cube, as point lights do); a point light has a cube of six 90° maps; a distant
/// light one map looking along its direction, fitted to the scene. Area lights keep ray-traced shadows. Every surface
/// that casts shadows is opaque to a shadow map (a transparent one casts a full shadow), and a point or spot light with
/// a radius is lit as a point, its softness then the fuzziness's.
/// </summary>
public sealed record ShadowMapSettings
{
    /// <summary>Whether the light uses a shadow map rather than ray-traced shadows (default off: ray traced).</summary>
    public bool On { get; init; }
    /// <summary>The map's size in pixels a side, 64 to 4096 (default 512); a cube's faces are at most 2048.</summary>
    public int Size { get; init; } = 512;
    /// <summary>The soft edge's width in map pixels, 0 (hard) to 32 (default 1).</summary>
    public double Fuzziness { get; init; } = 1;

    public ShadowMapSettings Checked()
    {
        if (Size < LightEffects.MinMapSize || Size > LightEffects.MaxMapSize)
            throw new ArgumentException($"The shadow map's size must be from {LightEffects.MinMapSize} to {LightEffects.MaxMapSize} pixels.");
        if (!(Fuzziness >= 0 && Fuzziness <= LightEffects.MaxFuzziness))
            throw new ArgumentException($"The shadow map's fuzziness must be from 0 to {LightEffects.MaxFuzziness} map pixels.");
        return this;
    }
}

/// <summary>Limits shared by the light effects' records.</summary>
public static class LightEffects
{
    public const int MaxStreaks = 64, MinMapSize = 64, MaxMapSize = 4096, MaxCubeFace = 2048;
    public const double MaxFuzziness = 32;

    internal static void CheckRange(double v, double min, double max, string what, string units)
    {
        if (!double.IsFinite(v) || v < min || v > max) throw new ArgumentException($"{what} must be from {min} to {max}{units}.");
    }
}
