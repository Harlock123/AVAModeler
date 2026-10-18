using AVAModeler.Core;

namespace AVAModeler.Geometry;

/// <summary>Distances below which two things count as the same, scaled to the size of the model.</summary>
internal static class Tolerance
{
    /// <summary>One part in a billion of the model's longest side (never less than 1e-12).</summary>
    public static double For(Box3 bounds) => Math.Max(bounds.IsEmpty ? 0 : bounds.Extent, 1e-3) * 1e-9;

    public static double For(Mesh mesh) => For(mesh.Bounds());

    public static double For(Mesh a, Mesh b) => For(a.Bounds().Include(b.Bounds()));
}
