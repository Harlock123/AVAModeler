using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVAModeler.UI.Tests;

/// <summary>
/// The README's pictures of Layout's motion features: the dope sheet with keys chosen and a car following a path drawn
/// in the view, and an arm reaching for a ball by IK seen through a banked camera. Set AVAMODELER_SHOTS to save them.
/// </summary>
public class MotionScreenshotTests
{
    private static string? Folder => Environment.GetEnvironmentVariable("AVAMODELER_SHOTS") is { Length: > 0 } d ? d : null;

    private static void Shot(Window w, string name)
    {
        Ui.Pump();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Folder is not { } dir) return;
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, $"AVAModeler-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void Box(MainWindow w, Vec3 center, Vec3 size, string surface, int segments = 1) =>
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Center"] = center, ["Size"] = size, ["Surface"] = surface, ["Segments X"] = segments });

    private static void Paint(MainWindow w, string name, Rgb color, double specular = 0.3)
    {
        var doc = w.Session.Document;
        int i = doc.SurfaceIndex(name);
        doc.Surfaces[i].Color = color;
        doc.Surfaces[i].Specular = specular;
        doc.Surfaces[i].Smoothing = true;
    }

    [AvaloniaFact]
    public void Screenshot_the_dope_sheet_and_a_car_following_a_path()
    {
        var w = Ui.Open(1400, 860);
        // A toy car (layer 1), the ground (layer 2) and a ball (layer 3).
        Box(w, new Vec3(0, 0.2, 0), new Vec3(0.5, 0.25, 1), "Car");
        Box(w, new Vec3(0, 0.42, -0.1), new Vec3(0.42, 0.2, 0.5), "Car");
        w.Session.Commands.SetLayers([1]);
        Box(w, new Vec3(0, -0.01, 0), new Vec3(11, 0.02, 7), "Ground");
        w.Session.Commands.SetLayers([2]);
        w.Session.Commands.Run("Ball", new Dictionary<string, object?> { ["Center"] = new Vec3(0, 0.25, 0), ["Radius"] = new Vec3(0.25, 0.25, 0.25), ["Surface"] = "Ball" });
        w.Session.Commands.SetLayers([0]);
        Paint(w, "Car", new Rgb(200, 40, 40), 0.6);
        Paint(w, "Ground", new Rgb(110, 125, 110), 0.1);
        Paint(w, "Ball", new Rgb(70, 120, 230), 0.5);
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var doc = w.Session.Document;
        foreach (var o in s.Scene.Objects.ToList()) s.Remove(o);
        var ball = s.Add(new ObjectItem("Ball", doc, 2));
        var car = s.Add(new ObjectItem("Car", doc, 0));
        s.Add(new ObjectItem("Ground", doc, 1));
        var points = new List<NullItem>();
        for (int i = 0; i < 6; i++)
        {
            double a = 2 * Math.PI * i / 6;
            var p = s.Add(new NullItem($"Road {i + 1}"));
            s.SetVector(p, 0, new Vec3(4 * Math.Cos(a), 0, 2.5 * Math.Sin(a)));
            points.Add(p);
        }
        s.Edit("Follow Path", () => { car.Path = new FollowPath { Points = points, Closed = true, LookAhead = 0.3 }; s.Scene.Notify(); });
        s.TimePath(car, 1, 60);
        // The ball bounces across the middle of the loop.
        foreach (var (f, x, y) in new[] { (1.0, -2.0, 1.4), (12, -1.0, 0), (24, 0.0, 1.2), (36, 1.0, 0), (48, 2.0, 0.9), (60, 2.5, 0) })
        {
            s.SetFrame(f);
            s.SetVector(ball, 0, new Vec3(x, y, 0));
        }
        s.SetFrame(20);
        // Keys chosen in the dope sheet: the ball's from frame 12 to 36, with its channels shown.
        layout.DopeSheetShown = true;
        layout.DopeSheet.Channels.IsChecked = true;
        s.Select(ball);
        var sheet = layout.DopeSheet.Sheet;
        var rows = sheet.Rows().ToList();
        int first = rows.FindIndex(r => r.Item == ball);
        sheet.Box(first, first + 2, 12, 36, add: false);
        Assert.Equal(9, sheet.Selected.Count);
        layout.View.Mode = LayoutViewMode.Perspective;
        layout.View.Fit();
        layout.View.Camera.Heading = 15;
        layout.View.Camera.Pitch = 34;
        layout.View.Camera.Distance *= 0.36;
        Ui.Pump();
        // The car is on its path, facing along it.
        var forward = car.WorldMatrix(20).TransformVector(Vec3.UnitZ);
        var ahead = car.WorldPosition(20.5) - car.WorldPosition(20);
        Assert.True(Vec3.Dot(forward.Normalized(), ahead.Normalized()) > 0.98);
        Shot(w, "dope-sheet");
    }

    [AvaloniaFact]
    public void Screenshot_an_IK_arm_through_a_banked_camera()
    {
        var w = Ui.Open(1400, 860);
        Box(w, new Vec3(0.5, 0, 0), new Vec3(1, 0.16, 0.16), "Arm", 8);
        Box(w, new Vec3(1.5, 0, 0), new Vec3(1, 0.12, 0.12), "Arm", 8);
        w.Session.Commands.SetLayers([1]);
        w.Session.Commands.Run("Ball", new Dictionary<string, object?> { ["Center"] = Vec3.Zero, ["Radius"] = new Vec3(0.12, 0.12, 0.12), ["Surface"] = "Ball" });
        w.Session.Commands.SetLayers([2]);
        Box(w, new Vec3(0.8, -0.7, 0.5), new Vec3(7, 0.02, 5), "Floor", 14);
        w.Session.Commands.SetLayers([0]);
        Paint(w, "Arm", new Rgb(215, 175, 125), 0.3);
        Paint(w, "Floor", new Rgb(95, 110, 140), 0.1);
        Paint(w, "Ball", new Rgb(230, 50, 40), 0.6);
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var doc = w.Session.Document;
        foreach (var o in s.Scene.Objects.ToList()) s.Remove(o);
        var arm = s.Add(new ObjectItem("Arm", doc, 0));
        var ball = s.Add(new ObjectItem("Ball", doc, 1));
        s.Add(new ObjectItem("Floor", doc, 2));
        var upper = s.Edit("Add Bone", () => s.Scene.AddBone(arm, "Upper", Vec3.Zero, new Vec3(90, 0, 0), 1));
        var lower = s.Edit("Add Bone", () => s.Scene.AddBone(arm, "Lower", new Vec3(0, 0, 1), Vec3.Zero, 1));
        s.Edit("Parent", () => lower.Parent = upper);
        s.SetFrame(1);
        s.SetVector(ball, 0, new Vec3(1.5, 0.7, -0.3));
        s.SetFrame(60);
        s.SetVector(ball, 0, new Vec3(0.9, 1.3, -0.6));
        s.SetFrame(24);
        s.Edit("IK Goal", () =>
        {
            lower.IKGoal = ball;
            lower.IKLimits = new IKLimits(new IKLimit(-150, 0));
            s.Scene.Notify();
        });
        var tip = lower.WorldMatrix(24).TransformPoint(new Vec3(0, 0, 1));
        Assert.True((tip - ball.WorldPosition(24)).Length < 1e-3);
        // The camera, banked 12° to the right, looking at the arm; the view tilts with it as its render does.
        var cam = s.Scene.CurrentCamera!;
        s.SetVector(cam, 0, new Vec3(0.6, 0.6, -3.6));
        var aim = cam.RotationToward(new Vec3(0.8, 0.45, 0), 24);
        s.SetVector(cam, 3, new Vec3(aim.X, aim.Y, -12));
        s.Scene.Lights.First().SetRotation(0, new Vec3(30, 35, 0));
        s.Select(lower);
        layout.View.Mode = LayoutViewMode.Camera;
        Ui.Pump();
        Assert.Equal(-12, layout.View.Camera.Bank, 6);
        // The bone's IK settings in view in the item panel.
        var panel = layout.GetLogicalDescendants().OfType<ScrollViewer>().First();
        panel.Offset = new Avalonia.Vector(0, panel.Extent.Height);
        Ui.Pump();
        Shot(w, "ik");
    }
}
