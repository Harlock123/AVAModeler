using AVAModeler.Core;

namespace AVAModeler.Core.Tests;

/// <summary>Weight and colour maps follow the mesh's edits; left/right names; skelegon names.</summary>
public class PointMapTests
{
    private static Mesh Quad()
    {
        var m = new Mesh();
        m.AddFace(0, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 1, 0), new Vec3(1, 0, 0));
        var w = m.AddPointMap("Arm", PointMapKind.Weight);
        w.SetWeight(0, 1);
        w.SetWeight(2, 0.5);
        var c = m.AddPointMap("Paint", PointMapKind.Rgb);
        c.SetColor(1, new Vec3(1, 0, 0));
        return m;
    }

    [Fact]
    public void Maps_follow_removing_appending_cloning_and_copying_points()
    {
        var m = Quad();
        var copy = m.Clone();
        copy.FindPointMap("Arm")!.SetWeight(0, 0.25);
        Assert.Equal(1, m.FindPointMap("Arm")!.Weight(0));

        m.RemovePoints([0]);
        var arm = m.FindPointMap("Arm")!;
        Assert.Equal(0.5, arm.Weight(1), 6); // point 2 is now 1
        Assert.Single(arm.Values);
        Assert.Equal(new Vec3(1, 0, 0), m.FindPointMap("Paint")!.Color(0));

        var other = Quad();
        int offset = m.Append(other);
        Assert.Equal(1, m.FindPointMap("Arm")!.Weight(offset));
        m.CopyPointUVs(offset, m.AddPoint(Vec3.One));
        Assert.Equal(1, m.FindPointMap("Arm")!.Weight(m.Points.Count - 1));

        // A point between two takes values between theirs; weight 0 isn't stored.
        var q = Quad();
        int mid = q.AddPointBetween(0, 2, 0.5);
        Assert.Equal(0.75, q.FindPointMap("Arm")!.Weight(mid), 6);
        int none = q.AddPointBetween(1, 3, 0.5);
        Assert.False(q.FindPointMap("Arm")!.Values.ContainsKey(none));
        Assert.Equal(new Vec3(1, 0, 0), q.FindPointMap("Paint")!.Color(none));

        // Merging points keeps the kept point's values and renumbers the rest.
        var mm = Quad();
        mm.Points[3] = mm.Points[2];
        mm.Touch();
        mm.MergePoints(1e-9);
        Assert.Equal(3, mm.Points.Count);
        Assert.Equal(0.5, mm.FindPointMap("Arm")!.Weight(2), 6);
    }

    [Fact]
    public void A_name_is_one_kind_of_map()
    {
        var m = Quad();
        Assert.Throws<ArgumentException>(() => m.AddPointMap("Arm", PointMapKind.Rgb));
        Assert.Same(m.FindPointMap("Paint"), m.AddPointMap("Paint", PointMapKind.Rgba));
        Assert.True(m.RemovePointMap("Paint"));
        Assert.Null(m.FindPointMap("Paint"));
    }

    [Theory]
    [InlineData("Left Arm", "Right Arm")]
    [InlineData("Right Leg", "Left Leg")]
    [InlineData("Arm.L", "Arm.R")]
    [InlineData("hand_r", "hand_l")]
    [InlineData("L_Thigh", "R_Thigh")]
    [InlineData("Spine", null)]
    public void Left_and_right_names_swap(string name, string? opposite) => Assert.Equal(opposite, MapNames.Opposite(name));

    [Fact]
    public void Skelegons_are_named_as_their_bones()
    {
        var m = new Mesh();
        var surfaces = new List<Surface> { new("Default"), new("Skelegon_Arm"), new("Skelegon"), new("Skelegon_Arm") };
        int a = m.AddPoint(Vec3.Zero), b = m.AddPoint(Vec3.UnitY), c = m.AddPoint(Vec3.One);
        m.AddPolygon([a, b], 1);
        m.AddPolygon([b, c], 2);
        m.AddPolygon([a, c], 3);
        m.AddPolygon([a, b, c], 1);
        Assert.Equal(["Arm", "Bone 2", "Arm (2)"], SkelegonNames.Of(m, surfaces).Select(s => s.Name));
    }
}
