using AVAModeler.Core;

namespace AVAModeler.Slicing;

/// <summary>
/// How long moves take on a printer that accelerates: each segment speeds up and slows down at the printer's
/// acceleration, and passes through a corner no faster than the corner allows (Klipper's square corner velocity: a 90°
/// corner at that speed, gentler ones faster, a straight run at full speed). A polyline starts and ends at rest; the
/// speeds at its corners are planned backward and forward so no segment has to stop or start harder than it can.
/// </summary>
public static class MotionTime
{
    /// <summary>The time (s) to run a polyline at <paramref name="speed"/> mm/s, from rest to rest.</summary>
    public static double Path(IReadOnlyList<Vec2> points, double speed, double accel, double cornerSpeed)
    {
        int n = points.Count - 1;
        if (n < 1 || speed <= 0) return 0;
        var length = new double[n];
        var dir = new Vec2[n];
        for (int i = 0; i < n; i++)
        {
            var d = points[i + 1] - points[i];
            length[i] = d.Length;
            dir[i] = length[i] > 1e-12 ? d / length[i] : Vec2.Zero;
        }
        // The fastest through each joint: 0 at the ends, the corner's limit between.
        double jd = cornerSpeed * cornerSpeed * (Math.Sqrt(2) - 1) / accel;
        var v = new double[n + 1];
        for (int i = 1; i < n; i++)
        {
            double cos = -Vec2.Dot(dir[i - 1], dir[i]);
            double sinHalf = Math.Sqrt(Math.Max(0, 0.5 * (1 - cos)));
            v[i] = sinHalf >= 1 - 1e-9 ? speed : Math.Min(speed, Math.Sqrt(accel * jd * sinHalf / (1 - sinHalf)));
        }
        // Backward then forward: no faster than can be slowed to the next joint, or reached from the last.
        for (int i = n - 1; i >= 0; i--) v[i] = Math.Min(v[i], Math.Sqrt(v[i + 1] * v[i + 1] + 2 * accel * length[i]));
        for (int i = 0; i < n; i++) v[i + 1] = Math.Min(v[i + 1], Math.Sqrt(v[i] * v[i] + 2 * accel * length[i]));
        double t = 0;
        for (int i = 0; i < n; i++) t += Segment(length[i], v[i], v[i + 1], speed, accel);
        return t;
    }

    /// <summary>A straight move from rest to rest.</summary>
    public static double Line(double length, double speed, double accel) => Segment(length, 0, 0, speed, accel);

    /// <summary>One segment: from <paramref name="u"/> to <paramref name="w"/> mm/s, cruising at <paramref name="speed"/> if there's room (a trapezoid), else a triangle.</summary>
    public static double Segment(double length, double u, double w, double speed, double accel)
    {
        if (length <= 0) return 0;
        double up = (speed * speed - u * u) / (2 * accel), down = (speed * speed - w * w) / (2 * accel);
        if (up + down <= length) return (speed - u) / accel + (speed - w) / accel + (length - up - down) / speed;
        double peak = Math.Sqrt((2 * accel * length + u * u + w * w) / 2);
        return (peak - u) / accel + (peak - w) / accel;
    }
}
