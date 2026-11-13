using AVAModeler.Core;
using static AVAModeler.Scene.Tests.TestData;

namespace AVAModeler.Scene.Tests;

/// <summary>Scatter: copies of an object over another's faces, by count and seed, on a surface, thinned by a picture, kept in files.</summary>
public class ScatterTests
{
    private static Mesh Plane(double size, int surface = 0)
    {
        var m = new Mesh();
        m.Points.AddRange([new Vec3(-size, 0, -size), new Vec3(-size, 0, size), new Vec3(size, 0, size), new Vec3(size, 0, -size)]);
        m.Polygons.Add(new Polygon([0, 1, 2, 3], surface));
        m.Touch();
        return m;
    }

    private static (Scene, ObjectItem Ground, ObjectItem Tree) Stage()
    {
        var s = new Scene();
        var ground = s.AddObject("Ground", Doc(Plane(5)));
        var tree = s.AddObject("Tree", Doc(Sphere(new Vec3(0, 0.5, 0), 0.5, 8, 4)));
        tree.Visible = false;
        return (s, ground, tree);
    }

    [Fact]
    public void Copies_stand_on_the_surface_the_same_every_time()
    {
        var (s, ground, tree) = Stage();
        int treeFaces = tree.BaseMesh().Polygons.Count;
        ground.Scatter = new ScatterSettings { Of = "Tree", Count = 50, Seed = 3 }.Checked();
        var mesh = s.Evaluate(1).Objects.Single(x => x.Item == ground).Mesh;
        Assert.Equal(1 + 50 * treeFaces, mesh.Polygons.Count);
        // Every copy's points are above the ground, within the plane's square, at most a little over a metre high.
        Assert.All(mesh.Points.Skip(4), p => Assert.True(p.Y >= -1e-9 && p.Y <= 1.25 && Math.Abs(p.X) <= 5.7 && Math.Abs(p.Z) <= 5.7));
        var again = Scattering.Spots(ground.BaseMesh(), ground.Surfaces, ground.Scatter, null);
        Assert.Equal(again, Scattering.Spots(ground.BaseMesh(), ground.Surfaces, ground.Scatter, null));
        Assert.NotEqual(again, Scattering.Spots(ground.BaseMesh(), ground.Surfaces, ground.Scatter with { Seed = 4 }, null));
        Assert.All(again, sp => Assert.InRange(sp.Scale, 0.8, 1.2));
    }

    [Fact]
    public void Copies_keep_to_a_surface_follow_a_density_picture_and_tilt_with_the_slope()
    {
        // Two surfaces: copies only on the second.
        var m = Plane(5);
        m.Polygons[0] = new Polygon([0, 1, 2, 3], 0);
        var doc = Doc(m);
        doc.Surfaces.Add(new Surface("Grass"));
        var grass = new Mesh();
        grass.Points.AddRange(m.Points.Select(p => p + new Vec3(20, 0, 0)));
        grass.Polygons.Add(new Polygon([0, 1, 2, 3], 1));
        var both = new Mesh { Points = [.. m.Points, .. grass.Points], Polygons = [m.Polygons[0], new Polygon([4, 5, 6, 7], 1)] };
        both.Touch();
        var spots = Scattering.Spots(both, doc.Surfaces, new ScatterSettings { Of = "x", Count = 40, OnSurface = "Grass" }, null);
        Assert.All(spots, sp => Assert.True(sp.At.X > 14));

        // A picture black on its left half: no copies there.
        var rgba = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) { int i = (y * 16 + x) * 4; byte v = x < 8 ? (byte)0 : (byte)255; (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (v, v, v, 255); }
        var picture = new TextureImage(16, 16, rgba);
        var dense = Scattering.Spots(Plane(5), [new Surface("Default")], new ScatterSettings { Of = "x", Count = 60 }, picture);
        Assert.True(dense.Count >= 40);
        Assert.All(dense, sp => Assert.True(sp.At.X > -0.4, $"a copy at {sp.At}"));

        // A slope: along the surface, copies lean with it; upright, they don't.
        var slope = new Mesh();
        slope.Points.AddRange([new Vec3(-2, 0, -2), new Vec3(-2, 2, 2), new Vec3(2, 2, 2), new Vec3(2, 0, -2)]);
        slope.Polygons.Add(new Polygon([0, 1, 2, 3], 0));
        slope.Touch();
        var leaning = Scattering.Spots(slope, [new Surface("Default")], new ScatterSettings { Of = "x", Count = 5, AlongSurface = true }, null);
        // It rises 2 m over 4: its face points up and back, (0, 2, -1) / √5.
        Assert.All(leaning, sp => Assert.True(Math.Abs(sp.Up.Y - 0.894) < 0.01 && Math.Abs(sp.Up.Z + 0.447) < 0.01, $"up {sp.Up}"));
        Assert.All(Scattering.Spots(slope, [new Surface("Default")], new ScatterSettings { Of = "x", Count = 5 }, null), sp => Assert.Equal(Vec3.UnitY, sp.Up));
    }

    [Fact]
    public void Scatters_are_kept_in_files_copies_and_snapshots()
    {
        var (s, ground, _) = Stage();
        var sc = new ScatterSettings { Of = "Tree", Count = 7, Seed = 2, AlongSurface = true, OnSurface = "Default", Sink = 0.05 };
        ground.Scatter = sc;
        Assert.Equal(sc, SceneJson.Read(SceneJson.Write(s, embedMeshes: true)).Scene.Find("Ground") is ObjectItem g ? g.Scatter : null);
        Assert.Equal(sc, ((ObjectItem)s.Copy().Find("Ground")!).Scatter);
        Assert.Equal(sc, ((ObjectItem)LwsReader.Read(LwsWriter.Write(s), _ => Doc(Plane(5))).Scene.Find("Ground")!).Scatter);
        var snap = s.Snapshot();
        ground.Scatter = null;
        snap.Restore();
        Assert.Equal(sc, ground.Scatter);
        Assert.Throws<ArgumentException>(() => (sc with { Count = 0 }).Checked());
    }
}
