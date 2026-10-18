using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// A camera. Unrotated, it looks down +Z with +Y up and +X to the right of the image.
/// <para>
/// Its lens is LightWave's zoom factor, which is measured against the image height:
/// zoom = 1 / tan(vertical FOV / 2). LightWave's default zoom of 3.2 is a 34.71° vertical field of view, and with a
/// 4:3 frame a 45.24° horizontal one (the same as a 24 mm lens on its default 15 mm aperture height). The horizontal
/// field follows from the frame aspect: tan(hfov / 2) = aspect / zoom, where aspect = width · pixel aspect / height.
/// </para>
/// </summary>
public sealed class CameraItem : SceneItem
{
    public const double DefaultZoom = 3.2;

    public int Width = 640, Height = 480;
    /// <summary>The width of a pixel over its height (1 for square pixels).</summary>
    public double PixelAspect = 1;
    /// <summary>The action-safe and title-safe areas as fractions of the frame's width and height, centred.</summary>
    public double ActionSafe = 0.9, TitleSafe = 0.8;

    public CameraItem(string name) : base(name) => Zoom = Watch(new Envelope(DefaultZoom));

    public override string Kind => "Camera";

    /// <summary>The zoom factor over time.</summary>
    public Envelope Zoom { get; }

    /// <summary>The frame's shape as seen: width · pixel aspect / height.</summary>
    public double FrameAspect => Height > 0 ? Width * PixelAspect / Height : 1;

    public static double ZoomToVerticalFov(double zoom) => 2 * Rotations.ToDegrees(Math.Atan(1 / zoom));
    public static double VerticalFovToZoom(double degrees) => 1 / Math.Tan(Mat4.Deg(degrees) / 2);
    public static double ZoomToHorizontalFov(double zoom, double aspect) => 2 * Rotations.ToDegrees(Math.Atan(aspect / zoom));
    public static double HorizontalFovToZoom(double degrees, double aspect) => aspect / Math.Tan(Mat4.Deg(degrees) / 2);

    public double VerticalFov(double frame) => ZoomToVerticalFov(Zoom.Evaluate(frame));
    public double HorizontalFov(double frame) => ZoomToHorizontalFov(Zoom.Evaluate(frame), FrameAspect);

    /// <summary>A centred safe-area rectangle in pixels, for a fraction such as <see cref="ActionSafe"/>.</summary>
    public (double X, double Y, double Width, double Height) SafeArea(double fraction) =>
        (Width * (1 - fraction) / 2, Height * (1 - fraction) / 2, Width * fraction, Height * fraction);

    /// <summary>
    /// The camera's world placement without any scale: its axes as unit vectors and its position. Points in camera
    /// space map to the world through this.
    /// </summary>
    public Mat4 CameraToWorld(double frame)
    {
        var m = WorldMatrix(frame);
        var o = m.TransformPoint(Vec3.Zero);
        var x = m.TransformVector(Vec3.UnitX).Normalized();
        var y = m.TransformVector(Vec3.UnitY).Normalized();
        var z = m.TransformVector(Vec3.UnitZ).Normalized();
        return Mat4.FromRows(x.X, y.X, z.X, o.X, x.Y, y.Y, z.Y, o.Y, x.Z, y.Z, z.Z, o.Z, 0, 0, 0, 1);
    }

    /// <summary>
    /// An OpenGL-style view matrix. It takes LightWave world coordinates (left-handed, Z forward) straight to GL's
    /// right-handed eye space (looking down -Z) by flipping Z after the inverse camera transform, so meshes can be
    /// uploaded as they are. The flip mirrors the scene, so LightWave's clockwise front faces arrive anticlockwise:
    /// GL's default front face (GL_CCW) is the right one to cull with.
    /// </summary>
    public Mat4 ViewMatrix(double frame) => Mat4.Scaling(new Vec3(1, 1, -1)) * CameraToWorld(frame).Inverted();

    /// <summary>
    /// An OpenGL-style perspective projection matching the camera's vertical field of view. The aspect defaults to
    /// the frame aspect; pass the viewport's to fill a window of another shape (the vertical field is kept).
    /// </summary>
    public Mat4 ProjectionMatrix(double frame, double near, double far, double? aspect = null) =>
        Mat4.Perspective(Mat4.Deg(VerticalFov(frame)), aspect ?? FrameAspect, near, far);
}
