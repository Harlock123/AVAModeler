using AVAModeler.Core;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// The hair brush: what's chosen in Layout's item panel (on or off, its size on screen and how strongly a stroke
/// combs), and the stroke itself, combing the chosen object's hair the way the mouse moves across a view.
/// </summary>
public sealed class HairBrush
{
    /// <summary>Whether dragging in a view combs the chosen object's hair (instead of moving it).</summary>
    public bool On { get; set; }

    /// <summary>The brush's radius on screen (pixels).</summary>
    public double Radius { get; set; } = 40;

    /// <summary>How far a stroke over a point combs it toward the stroke's way, 0 to 1 (less toward the brush's edge).</summary>
    public double Strength { get; set; } = 0.5;

    /// <summary>
    /// One stroke of the brush from <paramref name="from"/> to <paramref name="to"/> (screen pixels) in a view seen
    /// through <paramref name="camera"/>: the object's points under the brush (within its radius of the stroke, on the
    /// side facing the view) combed the way it moved, or with <paramref name="uncomb"/> their combing taken away. The
    /// hair as it would be, or null if the object has none or nothing was under the brush.
    /// </summary>
    public HairSettings? Stroke(ObjectItem o, double frame, ViewCamera camera, (double X, double Y) from, (double X, double Y) to, bool uncomb = false)
    {
        if (o.Hair is not { } hair) return null;
        double dx = to.X - from.X, dy = to.Y - from.Y;
        if (!uncomb && dx * dx + dy * dy < 1e-6) return null;
        var mesh = o.DeformedMesh(frame);
        var world = o.WorldMatrix(frame);
        var toLocal = world.Inverted();
        var normals = PointNormals(mesh);
        var combs = HairComb.Start(hair, mesh.Points.Count);
        bool touched = false;
        double r2 = Radius * Radius;
        for (int i = 0; i < mesh.Points.Count; i++)
        {
            var p = world.TransformPoint(mesh.Points[i]);
            var s = camera.Project(p);
            if (!s.Visible) continue;
            double d2 = SegmentDistance2(s.X, s.Y, from, to);
            if (d2 >= r2) continue;
            // Only the side of the object the view sees.
            var n = world.TransformVector(normals[i]);
            var toEye = camera.IsPerspective ? camera.Eye - p : -camera.Basis().Forward;
            if (n.LengthSquared > 0 && Vec3.Dot(n, toEye) < 0) continue;
            double f = 1 - Math.Sqrt(d2) / Radius;
            var way = toLocal.TransformVector(camera.DragDelta(dx, dy, p));
            combs[i] = HairComb.Blend(combs[i], way.LengthSquared > 1e-24 ? way.Normalized() : Vec3.Zero, Strength * f * f, uncomb);
            touched = true;
        }
        return touched ? hair with { Combing = CombMap.Of(combs) } : null;
    }

    private static Vec3[] PointNormals(Mesh mesh)
    {
        var n = new Vec3[mesh.Points.Count];
        foreach (var poly in mesh.Polygons)
        {
            if (poly.Count < 3) continue;
            var f = mesh.Normal(poly);
            foreach (int i in poly.Points) n[i] += f;
        }
        for (int i = 0; i < n.Length; i++) if (n[i].LengthSquared > 0) n[i] = n[i].Normalized();
        return n;
    }

    private static double SegmentDistance2(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, l2 = ux * ux + uy * uy;
        double t = l2 == 0 ? 0 : Math.Clamp(((x - a.X) * ux + (y - a.Y) * uy) / l2, 0, 1);
        double ex = a.X + ux * t - x, ey = a.Y + uy * t - y;
        return ex * ex + ey * ey;
    }
}
