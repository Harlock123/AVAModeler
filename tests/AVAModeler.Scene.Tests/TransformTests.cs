using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

public class TransformTests
{
    [Fact]
    public void HeadingNinetyLooksDownPlusX() => AssertNear(Vec3.UnitX, Rotations.Forward(new Vec3(90, 0, 0)));

    [Fact]
    public void PositivePitchTipsTheNoseDown() => AssertNear(new Vec3(0, -1, 0), Rotations.Forward(new Vec3(0, 90, 0)));

    [Fact]
    public void PositiveBankTurnsXTowardY() =>
        AssertNear(Vec3.UnitY, Rotations.Matrix(new Vec3(0, 0, 90)).TransformVector(Vec3.UnitX));

    [Fact]
    public void HeadingAppliesBeforePitch()
    {
        // Turned right 90°, then pitched down 45°: looking along +X and down.
        double s = Math.Sqrt(0.5);
        AssertNear(new Vec3(s, -s, 0), Rotations.Forward(new Vec3(90, 45, 0)));
        // Bank doesn't move the forward axis.
        AssertNear(new Vec3(s, -s, 0), Rotations.Forward(new Vec3(90, 45, 30)));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(30, 20, 10)]
    [InlineData(-120, 45, -170)]
    [InlineData(170, -80, 60)]
    public void MatrixDecomposesBackToHpb(double h, double p, double b)
    {
        var hpb = Rotations.FromMatrix(Rotations.Matrix(new Vec3(h, p, b)));
        AssertNear(new Vec3(h, p, b), hpb, 1e-9);
    }

    [Fact]
    public void StraightDownDecomposesToTheSameRotation()
    {
        var m = Rotations.Matrix(new Vec3(30, 90, 10));
        Assert.Equal(m, Rotations.Matrix(Rotations.FromMatrix(m)));
    }

    [Fact]
    public void DirectionGivesHeadingAndPitch()
    {
        var d = new Vec3(1, -1, 1);
        AssertNear(d.Normalized(), Rotations.Forward(Rotations.FromDirection(d)));
    }

    [Fact]
    public void ChildFollowsItsParent()
    {
        var scene = new Scene();
        var parent = scene.AddNull("Parent");
        var child = scene.AddNull("Child");
        child.Parent = parent;
        parent.SetPosition(0, new Vec3(0, 0, 5));
        parent.SetRotation(0, new Vec3(90, 0, 0));
        child.SetPosition(0, new Vec3(1, 0, 0));
        // Heading 90 turns +Z toward +X, so +X turns toward -Z.
        AssertNear(new Vec3(0, 0, 4), child.WorldPosition(0));
    }

    [Fact]
    public void ItemsRotateAndScaleAboutTheirPivot()
    {
        var scene = new Scene();
        var item = scene.AddNull("Item");
        item.Pivot = new Vec3(1, 0, 0);
        item.SetPosition(0, new Vec3(3, 0, 0));
        var m = item.WorldMatrix(0);
        AssertNear(new Vec3(3, 0, 0), m.TransformPoint(item.Pivot));
        AssertNear(new Vec3(4, 0, 0), m.TransformPoint(new Vec3(2, 0, 0)));

        item.SetRotation(0, new Vec3(180, 0, 0));
        AssertNear(new Vec3(2, 0, 0), item.WorldMatrix(0).TransformPoint(new Vec3(2, 0, 0)));

        item.SetRotation(0, Vec3.Zero);
        item.SetScale(0, new Vec3(2, 2, 2));
        AssertNear(new Vec3(5, 0, 0), item.WorldMatrix(0).TransformPoint(new Vec3(2, 0, 0)));
        AssertNear(new Vec3(3, 0, 0), item.WorldPosition(0));
    }

    [Fact]
    public void ParentPivotsAreRespectedDownTheChain()
    {
        var scene = new Scene();
        var parent = scene.AddNull("Parent");
        parent.Pivot = new Vec3(0, 1, 0);
        parent.SetRotation(0, new Vec3(0, 0, 90));
        var child = scene.AddNull("Child");
        child.Parent = parent;
        child.SetPosition(0, new Vec3(1, 1, 0));
        // The child sits one unit along the parent's +X from its pivot; bank 90 turns that to +Y.
        AssertNear(new Vec3(0, 1, 0), child.WorldPosition(0));
    }

    [Fact]
    public void CyclesAreRejected()
    {
        var scene = new Scene();
        var a = scene.AddNull("A");
        var b = scene.AddNull("B");
        var c = scene.AddNull("C");
        b.Parent = a;
        c.Parent = b;
        Assert.Throws<InvalidOperationException>(() => a.Parent = c);
        Assert.Throws<InvalidOperationException>(() => a.Parent = a);
        Assert.Null(a.Parent);
        Assert.Equal([b, a], c.Ancestors());
    }

    [Fact]
    public void ObjectsCantBeParentedToTheirOwnBones()
    {
        var scene = new Scene();
        var o = scene.AddObject("Object");
        var bone = scene.AddBone(o, "Bone");
        Assert.Throws<InvalidOperationException>(() => o.Parent = bone);
        var other = scene.AddObject("Other");
        Assert.Throws<InvalidOperationException>(() => bone.Parent = other);
        bone.Parent = o;
        Assert.Equal(o, bone.Parent);
    }

    [Fact]
    public void ItemsInDifferentScenesCantBeParented()
    {
        var a = new Scene().AddNull("A");
        var b = new Scene().AddNull("B");
        Assert.Throws<InvalidOperationException>(() => a.Parent = b);
    }

    [Fact]
    public void AnimatedChannelsMoveTheItem()
    {
        var scene = new Scene();
        var n = scene.AddNull("N");
        n.SetPosition(0, Vec3.Zero);
        n.SetPosition(10, new Vec3(10, 0, 0));
        AssertNear(new Vec3(5, 0, 0), n.WorldPosition(5));
        Assert.Equal([0.0, 10], n.KeyFrames());
        n.CreateKey(5);
        Assert.Equal([0.0, 5, 10], n.KeyFrames());
        Assert.Equal(2, n[Channel.ScaleZ].Keys.Count); // its starting key at 0, and the new one
        n.DeleteKey(5);
        Assert.Equal([0.0, 10], n.KeyFrames());
        Assert.Equal(5, n.Curve(Channel.PositionX, 0, 10, 5).Length);
    }

    [Fact]
    public void ChangesBumpTheSceneVersion()
    {
        var scene = new Scene();
        int raised = 0;
        scene.Changed += () => raised++;
        var n = scene.AddNull("N");
        int v = scene.Version;
        n[Channel.PositionY].AddKey(3, 1);
        Assert.True(scene.Version > v);
        v = scene.Version;
        n.Parent = scene.AddNull("P");
        Assert.True(scene.Version > v);
        Assert.Equal(scene.Version, raised);
    }

    [Fact]
    public void RemovingAnItemMovesItsChildrenUp()
    {
        var scene = new Scene();
        var a = scene.AddNull("A");
        var b = scene.AddNull("B");
        var c = scene.AddNull("C");
        b.Parent = a;
        c.Parent = b;
        scene.Remove(b);
        Assert.Equal(a, c.Parent);
        Assert.Null(b.Scene);
        Assert.Equal(2, scene.Items.Count);
    }

    [Fact]
    public void RemovingAnObjectTakesItsBones()
    {
        var scene = new Scene();
        var o = scene.AddObject("O");
        var b1 = scene.AddBone(o, "B1");
        var b2 = scene.AddBone(o, "B2");
        b2.Parent = b1;
        var n = scene.AddNull("N");
        n.Parent = b2;
        scene.Remove(o);
        Assert.Empty(scene.Bones);
        Assert.Null(n.Parent);
        Assert.Single(scene.Items);
    }

    [Fact]
    public void EvaluateGivesEveryWorldMatrixAndMesh()
    {
        var scene = new Scene();
        var mesh = Square(Vec3.Zero, 2, -Vec3.UnitZ);
        var o = scene.AddObject("Square", Doc(mesh));
        o.SetPosition(0, new Vec3(0, 0, 10));
        var cam = scene.AddCamera("Camera");
        var frame = scene.Evaluate(0);
        Assert.Equal(2, frame.World.Count);
        AssertNear(new Vec3(0, 0, 10), frame.WorldOf(o).TransformPoint(Vec3.Zero));
        var of = Assert.Single(frame.Objects);
        Assert.Equal(4, of.Mesh.Points.Count);
        Assert.All(of.WorldMesh.Points, p => AssertNear(10, p.Z));
        Assert.Equal(cam, scene.CurrentCamera);
        Assert.Empty(frame.Warnings);
    }

    [Fact]
    public void ObjectsUseOneLayerOrAllNonEmptyOnes()
    {
        var doc = new Document();
        doc.Layers[0].Mesh = Square(Vec3.Zero, 1, Vec3.UnitZ);
        doc.Layers[3].Mesh = Square(Vec3.One, 1, Vec3.UnitZ);
        var scene = new Scene();
        Assert.Equal(8, scene.AddObject("All", doc).BaseMesh().Points.Count);
        var one = scene.AddObject("One", doc, 3);
        AssertNear(new Vec3(1, 1, 1), one.BaseMesh().Bounds().Center);
        Assert.True(scene.AddObject("Placeholder").IsPlaceholder);
    }

    [Fact]
    public void DefaultSceneHasACameraAndALight()
    {
        var scene = Scene.CreateDefault();
        Assert.Single(scene.Cameras);
        var light = Assert.Single(scene.Lights);
        Assert.Equal(LightType.Distant, light.Type);
        // Shining from the upper left behind the camera toward the origin.
        var d = light.Direction(0);
        Assert.True(d.X > 0 && d.Y < 0 && d.Z > 0);
    }
}
