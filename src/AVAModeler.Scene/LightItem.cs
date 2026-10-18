using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>The kinds of light, numbered as in LightWave scene files.</summary>
public enum LightType { Distant = 0, Point = 1, Spot = 2 }

/// <summary>
/// A light. A distant light shines along its +Z axis from infinitely far away (straight down +Z when unrotated);
/// a point light shines every way from its position; a spot light shines along its +Z axis inside a cone.
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
    /// <summary>Whether the light casts ray-traced shadows.</summary>
    public bool Shadows = true;

    public LightItem(string name, LightType type = LightType.Distant) : base(name)
    {
        Type = type;
        Intensity = Watch(new Envelope(1));
    }

    public override string Kind => "Light";

    /// <summary>The intensity (1 is LightWave's 100%).</summary>
    public Envelope Intensity { get; }

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
