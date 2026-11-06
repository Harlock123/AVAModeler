namespace AVAModeler.Scene;

/// <summary>
/// The lens flare's flags in a LightWave scene file. LightWave 5 writes them as bit sets, <c>LensFlareFade</c> and
/// <c>LensFlareOptions</c>; which bit is which here is a best reading of LightWave's panel order, not taken from
/// LightWave's own documentation, so a flare written by LightWave may come back with its options shuffled (its on/off,
/// intensity, nominal distance and dissolve are LightWave's own keywords and come back as they were).
/// <list type="bullet">
/// <item><c>LensFlareFade</c>: 1 off screen, 2 behind objects, 4 in fog (not used: fog dims nothing here), 8 with distance.</item>
/// <item><c>LensFlareOptions</c>: 1 star filter (streaks), 2 central glow, 4 red outer glow, 8 glow behind objects
/// (neither used), 16 central ring.</item>
/// </list>
/// A star filter read without a streak count has <see cref="StarStreaks"/> streaks.
/// </summary>
public static class LwsLightBits
{
    public const int FadeOffScreen = 1, FadeBehind = 2, FadeInFog = 4, FadeWithDistance = 8;
    public const int StarFilter = 1, CentralGlow = 2, RedOuterGlow = 4, GlowBehindObjects = 8, CentralRing = 16;

    /// <summary>The streaks a star filter has when read from a scene file.</summary>
    public const int StarStreaks = 8;

    public static int Fade(LensFlare f) =>
        (f.FadeOffScreen ? FadeOffScreen : 0) | (f.FadeBehind ? FadeBehind : 0) | (f.FadeWithDistance ? FadeWithDistance : 0);

    public static int Options(LensFlare f) => (f.Streaks > 0 ? StarFilter : 0) | (f.Glow ? CentralGlow : 0) | (f.Ring ? CentralRing : 0);

    public static LensFlare ReadFade(LensFlare f, int bits) => f with
    {
        FadeOffScreen = (bits & FadeOffScreen) != 0, FadeBehind = (bits & FadeBehind) != 0, FadeWithDistance = (bits & FadeWithDistance) != 0,
    };

    public static LensFlare ReadOptions(LensFlare f, int bits) => f with
    {
        Streaks = (bits & StarFilter) == 0 ? 0 : f.Streaks > 0 ? f.Streaks : StarStreaks, Glow = (bits & CentralGlow) != 0, Ring = (bits & CentralRing) != 0,
    };

    /// <summary>Whether a flare comes back from a scene file as it is: its sizes the defaults, and no streaks or the star filter's count.</summary>
    public static bool Kept(LensFlare f)
    {
        var d = new LensFlare();
        return f.Size == d.Size && f.RingSize == d.RingSize && f.StreakLength == d.StreakLength && f.Streaks is 0 or StarStreaks;
    }
}
