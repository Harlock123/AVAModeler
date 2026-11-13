using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>The kinds of light, numbered as in LightWave scene files (LightWave 6 on numbers area lights 4).</summary>
public enum LightType { Distant = 0, Point = 1, Spot = 2, Area = 4 }

/// <summary>Which objects a light lights: all, only those listed, or every one but those listed.</summary>
public enum LightLinkMode { All, Only, Except }

/// <summary>
/// A light. A distant light shines along its +Z axis from infinitely far away (straight down +Z when unrotated);
/// a point light shines every way from its position; a spot light shines along its +Z axis inside a cone; an area light
/// is a rectangle <see cref="AreaWidth"/> × <see cref="AreaHeight"/> in its own XY plane (scaled with the item, as
/// LightWave sizes area lights by scale), shining from its front (+Z) side, brightest straight ahead, and casting soft
/// shadows. A point or spot light with a <see cref="Radius"/> is a little ball of light rather than a point, and casts
/// soft shadows too. Soft lights take <see cref="ShadowSamples"/> rays a pixel to work out their light and shadow.
/// </summary>
public sealed class LightItem : SceneItem
{
    public LightType Type;
    /// <summary>The colour, each component from 0 to 1.</summary>
    public Vec3 Color = Vec3.One;
    /// <summary>
    /// The distance at which a point or spot light's intensity has fallen (linearly) to nothing; 0 for no falloff.
    /// Distant lights never fall off.
    /// </summary>
    public double Range;
    /// <summary>A spot light's cone half-angle in degrees.</summary>
    public double ConeAngle = 30;
    /// <summary>The soft edge in degrees inside the cone's rim, over which a spot light fades out.</summary>
    public double EdgeAngle = 5;
    /// <summary>Whether the light casts shadows: ray traced, or from a shadow map when <see cref="ShadowMap"/> is on.</summary>
    public bool Shadows = true;
    /// <summary>An area light's size in metres along its X and Y axes (default 1 × 1).</summary>
    public double AreaWidth = 1, AreaHeight = 1;
    /// <summary>A point or spot light's radius in metres, for soft shadows; 0 (the default) is a point, with hard shadows.</summary>
    public double Radius;
    /// <summary>Rays a pixel a soft light (area, or with a radius) takes for its light and shadows (1 to 256, default 16).</summary>
    public int ShadowSamples = 16;
    /// <summary>The lens flare (off by default); its intensity is <see cref="FlareIntensity"/>.</summary>
    public LensFlare Flare = new();
    /// <summary>The beam seen in the air (off by default).</summary>
    public VolumetricLight Volumetric = new();
    /// <summary>Shadow-map shadows instead of ray-traced ones (off by default: ray traced, when <see cref="Shadows"/> is on).</summary>
    public ShadowMapSettings ShadowMap = new();

    /// <summary>Whether the light has a size, so its shadows are soft: an area light, or a point or spot light with a radius.</summary>
    public bool IsSoft => Type == LightType.Area || Radius > 0 && Type is LightType.Point or LightType.Spot;

    public LightItem(string name, LightType type = LightType.Distant) : base(name)
    {
        Type = type;
        Intensity = Watch(new Envelope(1));
        FlareIntensity = Watch(new Envelope(1));
    }

    public override string Kind => "Light";

    /// <summary>The intensity (1 is LightWave's 100%).</summary>
    /// <summary>
    /// Light linking: which objects this light lights (<see cref="LinkedObjects"/> are the ones only lit, or the ones
    /// left unlit). Shadows, flares and beams are as without it. All (the default) changes nothing.
    /// </summary>
    public LightLinkMode LinkMode;

    /// <summary>The objects <see cref="LinkMode"/> names (set a new list to change it).</summary>
    public IReadOnlyList<ObjectItem> LinkedObjects = [];

    /// <summary>Whether this light lights an object (null: something that isn't one).</summary>
    public bool Lights(ObjectItem? o) => LinkMode switch
    {
        LightLinkMode.Only => o != null && LinkedObjects.Contains(o),
        LightLinkMode.Except => o == null || !LinkedObjects.Contains(o),
        _ => true,
    };

    public Envelope Intensity { get; }

    /// <summary>The lens flare's intensity (1 is 100%), keyable as the channel "flare.intensity".</summary>
    public Envelope FlareIntensity { get; }

    /// <summary>Whether the light's shadows come from a shadow map: shadows on, a map asked for, and not an area light (which keeps ray-traced shadows).</summary>
    public bool UsesShadowMap => Shadows && ShadowMap.On && Type != LightType.Area;

    /// <summary>The way the light shines, in world space.</summary>
    public Vec3 Direction(double frame) => WorldMatrix(frame).TransformVector(Vec3.UnitZ).Normalized();

    /// <summary>How much of the light reaches a distance (1 with no range set).</summary>
    public double Falloff(double distance) =>
        Type == LightType.Distant || Range <= 0 ? 1 : Math.Max(0, 1 - distance / Range);

    /// <summary>
    /// How much of a spot light reaches a direction an angle (degrees) off its axis: 1 inside the cone less its soft
    /// edge, fading smoothly to 0 at the rim. Other lights give 1.
    /// </summary>
    public double SpotFactor(double angle)
    {
        if (Type != LightType.Spot) return 1;
        if (angle >= ConeAngle) return 0;
        double inner = ConeAngle - Math.Max(EdgeAngle, 0);
        if (angle <= inner) return 1;
        double t = (ConeAngle - angle) / (ConeAngle - inner);
        return t * t * (3 - 2 * t);
    }
}
