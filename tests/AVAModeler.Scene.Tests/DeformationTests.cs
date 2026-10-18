using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class DeformationTests
{
    /// <summary>An object of single points (1-point polygons) at the given places.</summary>
    private static (Scene Scene, ObjectItem Object) PointsObject(params Vec3[] points)
    {
        var mesh = new Mesh();
        foreach (var p in points) mesh.AddPolygon([mesh.AddPoint(p)]);
        var scene = new Scene();
        return (scene, scene.AddObject("Points", Doc(mesh)));
    }

    [Fact]
    public void BonesAtRestChangeNothing()
    {
        var (scene, o) = PointsObject(new Vec3(0.3, 0.2, 0.5), new Vec3(-2, 1, 4));
        scene.AddBone(o, "Bone", new Vec3(0, 0, 0), new Vec3(10, 20, 0), 1);
        var mesh = o.DeformedMesh(0);
        AssertNear(new Vec3(0.3, 0.2, 0.5), mesh.Points[0], 1e-9);
        AssertNear(new Vec3(-2, 1, 4), mesh.Points[1], 1e-9);
    }

    [Fact]
    public void APointFollowsASingleBone()
    {
        var (scene, o) = PointsObject(new Vec3(0, 0, 0.5));
        var bone = scene.AddBone(o, "Bone");
        bone.SetRotation(10, new Vec3(90, 0, 0));
        AssertNear(new Vec3(0, 0, 0.5), o.DeformedMesh(0).Points[0], 1e-9);
        AssertNear(new Vec3(0.5, 0, 0), o.DeformedMesh(10).Points[0], 1e-9);
    }

    [Fact]
    public void ThePointNearestABoneFollowsItMost()
    {
        var (scene, o) = PointsObject(new Vec3(4.9, 0, 0.5), new Vec3(-4.9, 0, 0.5), new Vec3(0, 0, 0.5));
        scene.AddBone(o, "Left", new Vec3(-5, 0, 0));
        var right = scene.AddBone(o, "Right", new Vec3(5, 0, 0));
        right.SetPosition(10, new Vec3(5, 1, 0));
        var m = o.DeformedMesh(10);
        Assert.True(m.Points[0].Y > 0.99);
        Assert.True(m.Points[1].Y < 0.01);
        AssertNear(0.5, m.Points[2].Y, 1e-9);
    }

    [Fact]
    public void StrengthAndFalloffShiftTheBlend()
    {
        var (scene, o) = PointsObject(new Vec3(2, 0, 0.5));
        var left = scene.AddBone(o, "Left", new Vec3(-5, 0, 0));
        var right = scene.AddBone(o, "Right", new Vec3(5, 0, 0));
        right.SetPosition(10, new Vec3(5, 1, 0));

        double Moved() => o.DeformedMesh(10).Points[0].Y;

        o.BoneFalloffExponent = 1;
        scene.Notify();
        // Distances 7 and 3: weights 1/7 and 1/3.
        AssertNear((1.0 / 3) / (1.0 / 3 + 1.0 / 7), Moved(), 1e-9);
        double inverse = Moved();

        o.BoneFalloffExponent = 4;
        scene.Notify();
        Assert.True(Moved() > inverse);

        left.Strength = 100;
        scene.Notify();
        Assert.True(Moved() < 0.5);

        left.Active = false;
        scene.Notify();
        AssertNear(1, Moved(), 1e-9);
    }

    [Fact]
    public void ChildBonesFollowTheirParents()
    {
        var (scene, o) = PointsObject(new Vec3(0, 0, 1.5));
        var root = scene.AddBone(o, "Root", Vec3.Zero, Vec3.Zero, 1);
        var tip = scene.AddBone(o, "Tip", new Vec3(0, 0, 1), Vec3.Zero, 1);
        tip.Parent = root;
        AssertNear(new Vec3(0, 0, 1), tip.RestInObject().TransformPoint(Vec3.Zero));
        AssertNear(new Vec3(0, 0, 2), tip.RestSegment().End);
        // Only the tip bone is near enough to matter much; turning the root swings it.
        o.BoneFalloffExponent = 16;
        root.SetRotation(10, new Vec3(90, 0, 0));
        AssertNear(new Vec3(1.5, 0, 0), o.DeformedMesh(10).Points[0], 1e-3);
        AssertNear(new Vec3(1, 0, 0), tip.WorldPosition(10), 1e-9);
    }

    [Fact]
    public void BoneWorldPositionsIncludeTheObject()
    {
        var (scene, o) = PointsObject(Vec3.Zero);
        o.SetPosition(0, new Vec3(10, 0, 0));
        var b = scene.AddBone(o, "Bone", new Vec3(0, 1, 0));
        AssertNear(new Vec3(10, 1, 0), b.WorldPosition(0));
    }

    [Fact]
    public void RestFromPoseMakesThePoseTheRest()
    {
        var (scene, o) = PointsObject(new Vec3(0, 0, 0.5));
        var b = scene.AddBone(o, "Bone");
        b.SetRotation(0, new Vec3(90, 0, 0));
        b.RestFromPose(0);
        AssertNear(new Vec3(0, 0, 0.5), o.DeformedMesh(0).Points[0], 1e-9);
        b.ResetPose();
        Assert.Single(b[Channel.Heading].Keys);
        Assert.Equal(90, b[Channel.Heading].Evaluate(50));
    }

    [Fact]
    public void SkelegonsBecomeABoneChain()
    {
        var doc = new Document();
        int skel = doc.SurfaceIndex("Skelegon");
        int arm = doc.SurfaceIndex("Skelegon_Arm");
        int plain = doc.SurfaceIndex("Wire");
        var m = doc.Layers[0].Mesh;
        int a = m.AddPoint(Vec3.Zero), b = m.AddPoint(new Vec3(0, 0, 1)), c = m.AddPoint(new Vec3(0, 1, 1)), d = m.AddPoint(new Vec3(5, 5, 5));
        m.AddPolygon([b, c], arm);
        m.AddPolygon([a, b], skel);
        m.AddPolygon([c, d], plain);
        var scene = new Scene();
        var o = scene.AddObject("Figure", doc);

        var bones = Skelegons.CreateBones(scene, o);

        Assert.Equal(2, bones.Count);
        var (armBone, rootBone) = (bones[0], bones[1]);
        Assert.Equal("Arm", armBone.Name);
        Assert.Equal("Bone 2", rootBone.Name);
        Assert.Equal(rootBone, armBone.Parent);
        Assert.Null(rootBone.Parent);
        AssertNear(1, armBone.RestLength);
        var (start, end) = armBone.RestSegment();
        AssertNear(new Vec3(0, 0, 1), start, 1e-9);
        AssertNear(new Vec3(0, 1, 1), end, 1e-9);
        // At the parent's tip, in the parent's terms.
        AssertNear(new Vec3(0, 0, 1), armBone.RestPosition, 1e-9);
        // At rest, nothing moves.
        for (int i = 0; i < 3; i++) AssertNear(m.Points[i], o.DeformedMesh(0).Points[i], 1e-9);
        Assert.Equal(2, o.Bones.Count);
    }

    [Fact]
    public void MorphsBlendByTheirEnvelope()
    {
        var mesh = new Mesh();
        for (int i = 0; i < 3; i++) mesh.AddPoint(new Vec3(i, 0, 0));
        mesh.AddPolygon([0, 1, 2]);
        var scene = new Scene();
        var o = scene.AddObject("Morphing", Doc(mesh));
        var target = mesh.Clone();
        target.Transform(Mat4.Translation(new Vec3(0, 2, 0)));
        var weight = new Envelope();
        weight.AddKey(new EnvelopeKey(0, 0));
        weight.AddKey(new EnvelopeKey(10, 1, Linear: true));
        o.AddMorph(new MorphTarget("Up", target, weight));

        AssertNear(0, o.DeformedMesh(0).Points[1].Y);
        AssertNear(1, o.DeformedMesh(5).Points[1].Y);
        AssertNear(2, o.DeformedMesh(10).Points[2].Y);

        var second = target.Clone();
        second.Transform(Mat4.Translation(new Vec3(1, -2, 0)));
        o.AddMorph(new MorphTarget("Right", second, new Envelope(0.5)));
        AssertNear(new Vec3(1.5, 2, 0), o.DeformedMesh(10).Points[1], 1e-12);
    }

    [Fact]
    public void MismatchedMorphsAreReportedAndIgnored()
    {
        var mesh = Square(Vec3.Zero, 1, Vec3.UnitZ);
        var scene = new Scene();
        var o = scene.AddObject("O", Doc(mesh));
        var bad = new Mesh();
        bad.AddPoint(new Vec3(9, 9, 9));
        o.AddMorph(new MorphTarget("Bad", bad));
        Assert.Single(o.MorphProblems());
        var frame = scene.Evaluate(0);
        Assert.Contains(frame.Warnings, w => w.Contains("Bad"));
        AssertNear(mesh.Points[0], frame.Objects[0].Mesh.Points[0]);
    }

    [Fact]
    public void WaveDisplacementMovesPointsAlongItsAxis()
    {
        var w = new WaveDisplacement { Direction = Axis.Y, Along = Axis.X, Wavelength = 4, Speed = 1 };
        w.Amplitude.SetConstant(2);
        AssertNear(new Vec3(1, 2, 0), w.Displace(new Vec3(1, 0, 0), 0));
        AssertNear(new Vec3(1, 0, 0), w.Displace(new Vec3(1, 0, 0), 1), 1e-12);
        AssertNear(new Vec3(3, -2, 5), w.Displace(new Vec3(3, 0, 5), 0), 1e-12);
        w.Amplitude.SetConstant(0);
        Assert.Equal(new Vec3(1, 0, 0), w.Displace(new Vec3(1, 0, 0), 0));
    }

    [Fact]
    public void RippleSpreadsFromItsCentre()
    {
        var w = new WaveDisplacement { Shape = WaveShape.Ripple, Direction = Axis.Y, Wavelength = 4, Center = new Vec3(1, 0, 1) };
        w.Amplitude.SetConstant(1);
        AssertNear(1, w.Displace(new Vec3(2, 0, 1), 0).Y);
        AssertNear(1, w.Displace(new Vec3(1, 5, 0), 0).Y - 5);
    }

    [Fact]
    public void DisplacementAppliesToTheObject()
    {
        var mesh = new Mesh();
        mesh.AddPolygon([mesh.AddPoint(new Vec3(1, 0, 0))]);
        var scene = new Scene();
        var o = scene.AddObject("Wavy", Doc(mesh));
        var w = o.AddDisplacement(new WaveDisplacement { Wavelength = 4 });
        w.Amplitude.AddKey(10, 3);
        w.Amplitude.AddKey(0, 0);
        AssertNear(0, o.DeformedMesh(0).Points[0].Y, 1e-12);
        AssertNear(3, o.DeformedMesh(10).Points[0].Y, 1e-12);
    }

    [Fact]
    public void DeformedMeshesAreCachedUntilSomethingChanges()
    {
        var (scene, o) = PointsObject(new Vec3(0, 0, 0.5));
        var bone = scene.AddBone(o, "Bone");
        var first = o.DeformedMesh(3);
        Assert.Same(first, o.DeformedMesh(3));
        bone.SetRotation(3, new Vec3(90, 0, 0));
        var second = o.DeformedMesh(3);
        Assert.NotSame(first, second);
        AssertNear(new Vec3(0.5, 0, 0), second.Points[0], 1e-9);
        o.Document!.Layers[0].Mesh.Points[0] = new Vec3(0, 0, 0.25);
        o.Document.Layers[0].Mesh.Touch();
        AssertNear(new Vec3(0.25, 0, 0), o.DeformedMesh(3).Points[0], 1e-9);
    }
}
