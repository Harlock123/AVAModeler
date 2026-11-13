using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Geometry.Tests;

/// <summary>Photo match: cameras solved from marks, and places triangulated from two photos.</summary>
public class PhotoMatchTests
{
    /// <summary>A pinhole camera at a place looking at the origin, focal f pixels, the picture w × h: its pixel for a point.</summary>
    private static Func<Vec3, Vec2> Camera(Vec3 eye, double f, int w, int h)
    {
        var forward = (-eye).Normalized();
        var right = Vec3.Cross(Vec3.UnitY, forward).Normalized();
        var up = Vec3.Cross(forward, right);
        return p =>
        {
            var d = p - eye;
            double z = Vec3.Dot(d, forward);
            return new Vec2(w / 2.0 + f * Vec3.Dot(d, right) / z, h / 2.0 - f * Vec3.Dot(d, up) / z);
        };
    }

    private static readonly Vec3[] Corners = [.. Enumerable.Range(0, 8).Select(i => new Vec3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -0.5 : 0.5, (i & 4) == 0 ? -0.75 : 0.75))];

    [Fact]
    public void A_camera_comes_back_from_eight_marks()
    {
        var eye = new Vec3(3, 2, -5);
        var see = Camera(eye, 800, 1024, 768);
        var match = PhotoMatch.Solve([.. Corners.Select(c => new PhotoMark(c, see(c)))], 1024, 768);
        Assert.True((match.Position - eye).Length < 1e-6, $"{match.Position}");
        Assert.Equal(800, match.FocalPixels, 4);
        Assert.Equal(2 * Math.Atan(384.0 / 800) * 180 / Math.PI, match.VerticalFov, 4);
        Assert.True(match.Error < 1e-6);
        Assert.True(Vec3.Dot(match.Forward, (-eye).Normalized()) > 0.9999);
        var probe = new Vec3(0.3, -0.2, 0.1);
        Assert.True((match.Project(probe)!.Value - see(probe)).Length < 1e-6);
        Assert.Throws<ArgumentException>(() => PhotoMatch.Solve([.. Corners.Take(5).Select(c => new PhotoMark(c, see(c)))], 1024, 768));
        var flat = Corners.Select(c => c with { Z = 0 }).Concat([new Vec3(0, 0, 0), new Vec3(0.2, 0.1, 0)]).ToArray();
        Assert.Throws<ArgumentException>(() => PhotoMatch.Solve([.. flat.Select(c => new PhotoMark(c, see(c)))], 1024, 768));
    }

    [Fact]
    public void Two_photos_find_a_new_place()
    {
        var seeA = Camera(new Vec3(3, 2, -5), 800, 1024, 768);
        var seeB = Camera(new Vec3(-4, 1, -3), 650, 1024, 768);
        var a = PhotoMatch.Solve([.. Corners.Select(c => new PhotoMark(c, seeA(c)))], 1024, 768);
        var b = PhotoMatch.Solve([.. Corners.Select(c => new PhotoMark(c, seeB(c)))], 1024, 768);
        var target = new Vec3(0.4, 0.9, -0.3);
        var (p, gap) = PhotoMatch.Triangulate(a, seeA(target), b, seeB(target));
        Assert.True((p - target).Length < 1e-6, $"{p}");
        Assert.True(gap < 1e-6);
        // A pixel a little off gives a nearby place and says how far the rays miss.
        var (q, miss) = PhotoMatch.Triangulate(a, seeA(target) + new Vec2(2, 0), b, seeB(target));
        Assert.True((q - target).Length < 0.05);
        Assert.True(miss > 1e-4);
    }
}
