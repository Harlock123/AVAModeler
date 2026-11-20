using Avalonia.Headless.XUnit;
using AVAModeler.Core;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;

namespace AVAModeler.UI.Tests;

/// <summary>Scene › Load Motion Capture in Layout: a .bvh onto the chosen object's bones, one undo step.</summary>
public class MotionCaptureUITests
{
    private const string Capture = """
        HIERARCHY
        ROOT mixamorig:Hips
        {
          OFFSET 0 100 0
          CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation
          JOINT mixamorig:Spine
          {
            OFFSET 0 20 0
            CHANNELS 3 Zrotation Xrotation Yrotation
            JOINT mixamorig:LeftArm
            {
              OFFSET 5 10 0
              CHANNELS 3 Zrotation Xrotation Yrotation
              End Site
              {
                OFFSET 30 0 0
              }
            }
          }
        }
        MOTION
        Frames: 2
        Frame Time: 0.0333333
        0 100 0 0 0 0   0 0 0   0 0 0
        10 100 20 0 0 0   0 0 0   90 0 0
        """;

    [AvaloniaFact]
    public void Load_motion_capture_puts_a_bvh_on_the_chosen_objects_bones_as_one_undo_step()
    {
        var w = Ui.Open();
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var dir = Directory.CreateTempSubdirectory("ava-bvh-");
        try
        {
            var (figure, hips, arm) = s.Edit("Rig", () =>
            {
                var o = s.Scene.AddObject("Figure");
                var h = s.Scene.AddBone(o, "Hips", new Vec3(0, 1, 0), new Vec3(0, -90, 0), 0.2);
                var sp = s.Scene.AddBone(o, "Spine", new Vec3(0, 0, 0.2), Vec3.Zero, 0.1);
                sp.Parent = h;
                var a = s.Scene.AddBone(o, "LeftArm", new Vec3(0, 0.05, 0.1), new Vec3(90, 0, 0), 0.3);
                a.Parent = sp;
                return (o, h, a);
            });
            var path = Path.Combine(dir.FullName, "lift.bvh");
            File.WriteAllText(path, Capture);

            // A chosen bone stands for its object; nothing chosen, no target.
            s.Select(null);
            Assert.Null(layout.MotionCaptureTarget());
            s.Select(arm);
            Assert.Same(figure, layout.MotionCaptureTarget());

            var rest = arm.RotationAt(2);
            Assert.True(layout.LoadMotionCapture(figure, path));
            Assert.Contains("Loaded lift.bvh onto Figure: 3 bone(s) keyed", s.Status);
            Assert.NotEqual(rest, arm.RotationAt(2));
            Assert.NotEqual(hips.PositionAt(1), hips.PositionAt(2));
            layout.Undo();
            Assert.Equal(rest, arm.RotationAt(2));

            // Not a BVH, or an object without bones: said, and nothing changes.
            var junk = Path.Combine(dir.FullName, "junk.bvh");
            File.WriteAllText(junk, "not a capture");
            Assert.False(layout.LoadMotionCapture(figure, junk));
            Assert.Contains("Can't load junk.bvh", s.Status);
            var bare = s.Edit("Bare", () => s.Scene.AddObject("Bare"));
            Assert.False(layout.LoadMotionCapture(bare, path));
            Assert.Contains("no bones", s.Status);
        }
        finally { dir.Delete(true); }
    }
}
