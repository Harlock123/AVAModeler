using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVAModeler.Scene.Tests;

/// <summary>Live deformers: each kind's sums, the range, the order, keying, and keeping them in files and undo.</summary>
public class DeformerTests
{
    /// <summary>A rod of points up Y from 0 to 2, every 0.25 m, at x = x0.</summary>
    private static Vec3[] Rod(double x0 = 0) => [.. Enumerable.Range(0, 9).Select(i => new Vec3(x0, i * 0.25, 0))];

    private static Deformer Along(DeformerKind kind, double amount, double low = 0, double high = 2)
    {
        var d = new Deformer(kind) { Axis = Axis.Y, Toward = Axis.X, Low = low, High = high };
        d.Amount.SetConstant(amount);
        return d;
    }

    [Fact]
    public void A_ninety_degree_bend_curls_a_rod_into_a_quarter_circle()
    {
        var pts = Rod();
        Along(DeformerKind.Bend, 90).Apply(pts, 1);
        double r = 2 / (Math.PI / 2);
        TestData.AssertNear(new Vec3(r, r, 0), pts[^1], 1e-9);
        TestData.AssertNear(Vec3.Zero, pts[0], 1e-12);
        // Every point stays on the arc round (r, 0), the same length along it as it was.
        foreach (var p in pts) Assert.Equal(r, (p - new Vec3(r, 0, 0)).Length, 9);
        // Beyond the range it carries on straight along the tip's tangent (+X there).
        var past = new[] { new Vec3(0, 3, 0) };
        Along(DeformerKind.Bend, 90).Apply(past, 1);
        TestData.AssertNear(new Vec3(r + 1, r, 0), past[0], 1e-9);
    }

    [Fact]
    public void Twist_taper_shear_and_squash_do_their_sums_and_neutral_amounts_change_nothing()
    {
        var twist = new[] { new Vec3(1, 2, 0) };
        Along(DeformerKind.Twist, 90).Apply(twist, 1);
        TestData.AssertNear(new Vec3(0, 2, -1), twist[0], 1e-9);   // +X turned a right angle about +Y goes to -Z

        var taper = new[] { new Vec3(1, 2, 1), new Vec3(1, 0, 1) };
        Along(DeformerKind.Taper, 0.5).Apply(taper, 1);
        TestData.AssertNear(new Vec3(0.5, 2, 0.5), taper[0]);
        TestData.AssertNear(new Vec3(1, 0, 1), taper[1]);

        var shear = new[] { new Vec3(0, 1, 0) };
        Along(DeformerKind.Shear, 0.4).Apply(shear, 1);
        TestData.AssertNear(new Vec3(0.2, 1, 0), shear[0]);

        // Squash keeps a box's volume: half as tall, √2 as wide each way.
        var box = new[] { new Vec3(1, 1, 1), new Vec3(-1, -1, -1) };
        Along(DeformerKind.Squash, 0.5, -1, 1).Apply(box, 1);
        double w = box[0].X - box[1].X, h = box[0].Y - box[1].Y, dz = box[0].Z - box[1].Z;
        Assert.Equal(8, w * h * dz, 9);
        Assert.Equal(1, h, 9);

        foreach (var kind in Enum.GetValues<DeformerKind>())
        {
            var pts = Rod(0.3);
            var before = pts.ToArray();
            new Deformer(kind) { Axis = Axis.Y, Low = 0, High = 2 }.Apply(pts, 1);
            Assert.Equal(before, pts);
        }
    }

    [Fact]
    public void A_lattice_moves_what_is_inside_smoothly_and_leaves_the_rest()
    {
        var d = new Deformer(DeformerKind.Lattice) { LatticeMin = new Vec3(-1, -1, -1), LatticeMax = new Vec3(1, 1, 1), Divisions = (2, 2, 2) };
        d.Amount.SetConstant(1);
        // Lift the top four corners 0.5: a point at the top moves 0.5, one halfway up 0.25, the bottom none.
        d.Offsets = new Vec3[8];
        for (int x = 0; x < 2; x++) for (int z = 0; z < 2; z++) d.Offsets[x + 2 * (1 + 2 * z)] = new Vec3(0, 0.5, 0);
        var pts = new[] { new Vec3(0, 1, 0), new Vec3(0.3, 0, -0.2), new Vec3(0, -1, 0), new Vec3(5, 0, 0) };
        d.Apply(pts, 1);
        TestData.AssertNear(new Vec3(0, 1.5, 0), pts[0]);
        TestData.AssertNear(new Vec3(0.3, 0.25, -0.2), pts[1]);
        TestData.AssertNear(new Vec3(0, -1, 0), pts[2]);
        TestData.AssertNear(new Vec3(5, 0, 0), pts[3]);
        Assert.Contains("8 control points", new Deformer(DeformerKind.Lattice) { Offsets = new Vec3[9] }.Problem());
    }

    [Fact]
    public void An_objects_deformers_animate_and_are_kept_in_files_snapshots_and_copies()
    {
        var mesh = new Mesh();
        foreach (var p in Rod()) mesh.AddPoint(p);
        for (int i = 0; i + 1 < mesh.Points.Count; i++) mesh.AddPolygon([i, i + 1]);
        var scene = new Scene();
        var o = scene.AddObject("Reed", TestData.Doc(mesh));
        var bend = o.AddDeformer(Along(DeformerKind.Bend, 0));
        bend.Amount.SetConstant(0);
        bend.Amount.AddKey(new EnvelopeKey(1, 0));
        bend.Amount.AddKey(new EnvelopeKey(30, 90));
        Assert.Equal(2, scene.Evaluate(1).Objects[0].Mesh.Points[^1].Y, 9);
        Assert.True(scene.Evaluate(30).Objects[0].Mesh.Points[^1].X > 1);

        var (back, _) = SceneJson.Read(SceneJson.Write(scene, embedMeshes: true));
        var d = back.Objects.First().Deformers.Single();
        Assert.Equal((DeformerKind.Bend, 90.0), (d.Kind, d.Amount.Evaluate(30)));
        Assert.DoesNotContain("Deformers", SceneJson.Write(new Scene()));

        // In a LightWave scene: AVA's own plugin section, which other readers skip; a lattice's offsets come too.
        var lattice = o.AddDeformer(new Deformer(DeformerKind.Lattice) { Divisions = (2, 3, 2), Offsets = [Vec3.UnitX, new Vec3(0, 0.5, 0)] });
        lattice.Amount.SetConstant(0.75);
        var lws = LwsWriter.Write(scene);
        Assert.Contains("Plugin CustomObjHandler 1 AVA_Deformers", lws);
        var fromLws = ((ObjectItem)LwsReader.Read(lws, _ => TestData.Doc(mesh)).Scene.Find("Reed")!).Deformers;
        Assert.Equal([DeformerKind.Bend, DeformerKind.Lattice], fromLws.Select(x => x.Kind));
        Assert.Equal(bend.Amount.Evaluate(15.5), fromLws[0].Amount.Evaluate(15.5), 9);
        Assert.Equal((2, 3, 2), fromLws[1].Divisions);
        Assert.Equal(new Vec3(0, 0.5, 0), fromLws[1].Offsets[1]);
        Assert.Equal(0.75, fromLws[1].Amount.Evaluate(1));
        Assert.DoesNotContain("AVA_Deformers", LwsWriter.Write(new Scene()));
        // A bad section is said and left out, the rest of the scene read.
        var bad = LwsReader.Read(lws.Replace("\"kind\":\"Bend\"", "\"kind\":\"Wobble\""), _ => TestData.Doc(mesh));
        Assert.Empty(((ObjectItem)bad.Scene.Find("Reed")!).Deformers);
        Assert.Contains(bad.Warnings, w => w.Contains("AVA_Deformers"));
        o.RemoveDeformer(lattice);

        var snap = scene.Snapshot();
        o.RemoveDeformer(bend);
        bend.Axis = Axis.Z;
        snap.Restore();
        Assert.Same(bend, o.Deformers.Single());
        Assert.Equal(Axis.Y, bend.Axis);
        var copy = scene.Copy().Objects.First().Deformers.Single();
        Assert.NotSame(bend, copy);
        Assert.Equal(90, copy.Amount.Evaluate(30), 9);

        bend.Enabled = false;
        scene.Notify();
        Assert.Equal(0, scene.Evaluate(30).Objects[0].Mesh.Points[^1].X, 12);
    }
}
