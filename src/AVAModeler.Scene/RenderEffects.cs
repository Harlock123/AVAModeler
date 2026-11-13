namespace AVAModeler.Scene;

/// <summary>
/// The render effects' settings. Each is a record that never changes once made (set a new one: <c>scene.Occlusion =
/// scene.Occlusion with { On = true }</c>), so copies of a scene and undo snapshots can share them safely. All of them
/// are off by default, and a scene with them off renders exactly as it did before they existed.
/// <para>
/// Sample counts are per pixel. Depth of field and motion blur need a ray per lens point or moment, so they raise the
/// rays shot through each pixel to their count (when it's more than the antialiasing's). Ambient occlusion, environment
/// lighting and soft shadows are worked out where each of those rays hits, sharing their count among the pixel's rays:
/// each hit takes count ÷ rays a pixel (at least 1), spread so the pixel as a whole covers the count once.
/// </para>
/// </summary>
public static class RenderEffects
{
    /// <summary>The most samples any effect takes a pixel.</summary>
    public const int MaxSamples = 256;

    internal static void CheckSamples(int samples, string what)
    {
        if (samples < 1 || samples > MaxSamples) throw new ArgumentException($"{what} must be from 1 to {MaxSamples}.");
    }

    internal static void CheckPositive(double v, string what, string units)
    {
        if (!double.IsFinite(v) || v <= 0) throw new ArgumentException($"{what} must be more than 0{units}.");
    }
}

/// <summary>
/// Ambient occlusion: the ambient light is dimmed where nearby surfaces hide the sky from a point, so creases, corners
/// and the ground under things darken. Rays go out over the hemisphere above each hit (weighted toward its normal); one
/// that meets a surface within <see cref="Distance"/> metres hides some of the ambient light, more the nearer the
/// surface (1 − (d / distance)²). <see cref="Strength"/> scales how much is taken away (1: all of it).
/// </summary>
public sealed record AmbientOcclusion
{
    public bool On { get; init; }
    /// <summary>Rays a pixel (1 to 256, default 16).</summary>
    public int Samples { get; init; } = 16;
    /// <summary>How far, in metres, a surface still hides the ambient light (default 1).</summary>
    public double Distance { get; init; } = 1;
    /// <summary>How much of the hidden light is taken away, 0 to 1 (default 1).</summary>
    public double Strength { get; init; } = 1;

    /// <summary>Throws an <see cref="ArgumentException"/> saying what's wrong, if anything.</summary>
    public AmbientOcclusion Checked()
    {
        RenderEffects.CheckSamples(Samples, "Ambient occlusion samples");
        RenderEffects.CheckPositive(Distance, "The ambient occlusion distance", " metres");
        if (!(Strength >= 0 && Strength <= 1)) throw new ArgumentException("The ambient occlusion strength must be from 0 to 1.");
        return this;
    }
}

/// <summary>
/// An environment picture round the whole scene: a latitude-longitude (equirectangular) image, its middle looking
/// along +Z, +X a quarter of the way to its right, straight up along its top edge. When <see cref="Image"/> is set it
/// replaces the backdrop (seen behind everything, in reflections and through transparent surfaces), and with
/// <see cref="Lighting"/> it lights the scene too: each hit gathers the environment's light over the hemisphere above
/// it (<see cref="Samples"/> rays a pixel, shadowed by whatever is in the way), added to the lights' and the ambient.
/// <para>
/// The image is a Radiance .hdr (high dynamic range: brighter than white lights more), or a PNG, JPEG or BMP (255 is
/// 1). <c>builtin:sky</c> and <c>builtin:studio</c> are made-up environments that need no file. <see cref="Heading"/>
/// turns it about the Y axis, as an item's heading turns (positive turns what was ahead toward +X).
/// </para>
/// </summary>
public sealed record EnvironmentSettings
{
    /// <summary>The picture's file (or a <c>builtin:</c> name); empty for none, which leaves the backdrop as it is.</summary>
    public string Image { get; init; } = "";
    /// <summary>Its brightness, as a multiplier (default 1).</summary>
    public double Intensity { get; init; } = 1;
    /// <summary>Degrees it is turned about the Y axis (default 0).</summary>
    public double Heading { get; init; }
    /// <summary>Whether it lights the scene, as well as being seen.</summary>
    public bool Lighting { get; init; }
    /// <summary>Rays a pixel for its lighting (1 to 256, default 16).</summary>
    public int Samples { get; init; } = 16;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool On => !string.IsNullOrWhiteSpace(Image);

    public EnvironmentSettings Checked()
    {
        RenderEffects.CheckSamples(Samples, "Environment samples");
        if (!double.IsFinite(Intensity) || Intensity < 0) throw new ArgumentException("The environment's intensity must be 0 or more.");
        if (!double.IsFinite(Heading)) throw new ArgumentException("The environment's heading must be a number of degrees.");
        return this;
    }
}

/// <summary>
/// A camera's depth of field, through a thin lens as LightWave 5's was: things at <see cref="FocalDistance"/> metres
/// in front of the camera (measured along its view axis) are sharp, nearer and further ones blur more the further
/// they are from it. The lens opening follows LightWave: the focal length is the zoom factor × 7.5 mm (3.2 is a 24 mm
/// lens) and the aperture's diameter is that over <see cref="FStop"/>, so a smaller f-stop blurs more. It takes
/// <see cref="Samples"/> rays a pixel, raising the antialiasing to that.
/// </summary>
public sealed record DepthOfField
{
    public bool On { get; init; }
    /// <summary>The distance in focus, metres along the camera's view (default 5).</summary>
    public double FocalDistance { get; init; } = 5;
    /// <summary>LightWave's lens f-stop: the focal length over the aperture's diameter (default 4).</summary>
    public double FStop { get; init; } = 4;
    /// <summary>Rays a pixel (1 to 256, default 16).</summary>
    public int Samples { get; init; } = 16;

    /// <summary>The focal length in metres for a zoom factor: zoom × 7.5 mm.</summary>
    public static double FocalLength(double zoom) => zoom * 0.0075;

    /// <summary>The lens opening's radius in metres at a zoom factor.</summary>
    public double ApertureRadius(double zoom) => FocalLength(zoom) / FStop / 2;

    /// <summary>The f-stop that gives an aperture radius (metres) at a zoom factor.</summary>
    public static double FStopFor(double apertureRadius, double zoom) => FocalLength(zoom) / (2 * apertureRadius);

    public DepthOfField Checked()
    {
        RenderEffects.CheckSamples(Samples, "Depth of field samples");
        RenderEffects.CheckPositive(FocalDistance, "The focal distance", " metres");
        RenderEffects.CheckPositive(FStop, "The f-stop", "");
        return this;
    }
}

/// <summary>
/// A camera's motion blur: each pixel's rays are spread over the time the shutter is open, <see cref="Shutter"/>
/// of a frame centred on the frame (0.5, the default, is a 180° shutter), so whatever moves (items, the camera,
/// deformed objects, lights) smears along its path. It takes <see cref="Samples"/> rays a pixel, raising the
/// antialiasing to that; the scene is worked out at up to 32 moments across the shutter.
/// </summary>
public sealed record MotionBlur
{
    public bool On { get; init; }
    /// <summary>How long the shutter is open, as a fraction of a frame, more than 0 and at most 1 (default 0.5).</summary>
    public double Shutter { get; init; } = 0.5;
    /// <summary>Rays a pixel (1 to 256, default 16).</summary>
    public int Samples { get; init; } = 16;

    public MotionBlur Checked()
    {
        RenderEffects.CheckSamples(Samples, "Motion blur samples");
        if (!(Shutter > 0 && Shutter <= 1)) throw new ArgumentException("The shutter must be more than 0 and at most 1 frame.");
        return this;
    }
}

/// <summary>
/// The scene's sound track: a WAV file, the scene frame its start plays at (null: the scene's first frame), and its
/// volume. Movies made from the scene's frames carry it; AVALightWave shows its waveform and plays it. Immutable:
/// change it with <c>with</c> and <see cref="Checked"/> it.
/// </summary>
public sealed record SceneAudio
{
    /// <summary>The sound file (full path in memory; relative to the scene in .avascene), or empty for none.</summary>
    public string Path { get; init; } = "";
    /// <summary>The frame the sound's start plays at, or null for the scene's first frame.</summary>
    public double? StartFrame { get; init; }
    /// <summary>Its loudness, 0 (silent) to 2 (twice as loud).</summary>
    public double Volume { get; init; } = 1;

    public bool On => !string.IsNullOrWhiteSpace(Path);

    public SceneAudio Checked()
    {
        if (!double.IsFinite(Volume) || Volume < 0 || Volume > 2) throw new ArgumentException($"The sound's volume must be from 0 to 2 (it's {Volume}).");
        if (StartFrame is { } s && !double.IsFinite(s)) throw new ArgumentException("The sound's start frame must be a number.");
        return this;
    }

    /// <summary>Seconds into the sound at a scene frame (negative before it starts).</summary>
    public double SecondsAt(double frame, double firstFrame, double fps) => (frame - (StartFrame ?? firstFrame)) / (fps > 0 ? fps : 30);
}
