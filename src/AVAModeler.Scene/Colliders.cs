using System.Collections.Concurrent;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// The objects particles and soft bodies meet: each one's mesh as it is at the frame worked out (in its own
/// coordinates), placed where the object is at each moment asked about. Moments are looked up on a grid of
/// <c>step</c> seconds, each place worked out once; safe to use from many threads.
/// </summary>
internal sealed class Colliders : IParticleColliders
{
    private readonly List<(ObjectItem Item, Bvh Bvh)> _objects = [];
    private readonly ConcurrentDictionary<(int, long), (Mat4 World, Mat4 Local)> _places = new();
    private readonly double _step, _fps;

    private Colliders(double step, double fps) => (_step, _fps) = (step, fps);

    /// <summary>
    /// The scene's visible objects with faces other than those left out, at <paramref name="frame"/>, or null if there
    /// are none. Their meshes are as deformed at that frame; their motion is followed through time.
    /// </summary>
    public static Colliders? Of(Scene scene, IEnumerable<ObjectItem> leaveOut, double frame, double step)
    {
        var skip = leaveOut.ToHashSet();
        var c = new Colliders(step, scene.FramesPerSecond);
        foreach (var o in scene.Objects)
        {
            if (skip.Contains(o) || !o.Visible || o.IsPlaceholder || o.SoftBody != null) continue;
            var mesh = o.DeformedMesh(frame);
            var tris = new List<Triangle>();
            foreach (var poly in mesh.Polygons)
            {
                if (poly.Count < 3) continue;
                var n = mesh.Normal(poly);
                if (n.LengthSquared == 0) continue;
                foreach (var (a, b, cc) in Triangulator.Triangulate(mesh, poly))
                    tris.Add(new Triangle(mesh.Points[a], mesh.Points[b], mesh.Points[cc], n, n, n, n, 0, true));
            }
            if (tris.Count > 0) c._objects.Add((o, new Bvh([.. tris])));
        }
        return c._objects.Count > 0 ? c : null;
    }

    private (Mat4 World, Mat4 Local) Place(int i, double time)
    {
        long k = (long)Math.Round(time / _step);
        return _places.GetOrAdd((i, k), key =>
        {
            Mat4 w;
            // An item's motion isn't worked out from many threads at once.
            lock (_objects[key.Item1].Item) w = _objects[key.Item1].Item.WorldMatrix(key.Item2 * _step * _fps);
            return (w, w.Inverted());
        });
    }

    public bool Hit(Vec3 from, Vec3 to, double time, out double fraction, out Vec3 normal, out int collider)
    {
        fraction = double.PositiveInfinity;
        normal = default;
        collider = -1;
        for (int i = 0; i < _objects.Count; i++)
        {
            var (world, local) = Place(i, time);
            var a = local.TransformPoint(from);
            var d = local.TransformPoint(to) - a;
            if (d.LengthSquared == 0) continue;
            if (_objects[i].Bvh.Intersect(a, d, 0, Math.Min(1, fraction), out var hit) && hit.T < fraction)
            {
                fraction = hit.T;
                // A normal goes through the inverse transpose, so it stays square to a stretched face.
                var n = _objects[i].Bvh[hit.Triangle].Normal;
                normal = new Vec3(local[0, 0] * n.X + local[1, 0] * n.Y + local[2, 0] * n.Z,
                    local[0, 1] * n.X + local[1, 1] * n.Y + local[2, 1] * n.Z, local[0, 2] * n.X + local[1, 2] * n.Y + local[2, 2] * n.Z).Normalized();
                collider = i;
            }
        }
        return collider >= 0;
    }

    public Vec3 Carry(int collider, Vec3 point, double from, double to) =>
        Place(collider, to).World.TransformPoint(Place(collider, from).Local.TransformPoint(point));
}
