using System.Text.Json.Nodes;
using static AVAModeler.Mcp.Tests.Calls;

namespace AVAModeler.Mcp.Tests;

/// <summary>load_bvh: a capture from the working folder onto a rig, as one undo step, and its mistakes.</summary>
public class MotionCaptureHandlerTests
{
    private const string Capture = """
        HIERARCHY
        ROOT Hips
        {
          OFFSET 0 100 0
          CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation
          JOINT Arm
          {
            OFFSET 0 20 0
            CHANNELS 3 Zrotation Xrotation Yrotation
            End Site
            {
              OFFSET 30 0 0
            }
          }
        }
        MOTION
        Frames: 3
        Frame Time: 0.0333333
        0 100 0 0 0 0 0 0 0
        5 100 0 0 0 0 45 0 0
        10 100 0 0 0 0 90 0 0
        """;

    [Fact]
    public async Task A_capture_drives_a_rig_and_undo_takes_its_keys_back()
    {
        using var b = new Bench();
        await b.Tools.RunTool("Box", null, Cancel);
        await b.Tools.AddObject(name: "Figure", cancel: Cancel);
        await Parse(b.Tools.AddBone("Figure", "Hips", [0, 1, 0], [0, -90, 0], 0.2, cancel: Cancel));
        await Parse(b.Tools.AddBone("Figure", "UpperArm", [0, 0, 0.2], [90, 0, 0], 0.3, parent_bone: "Hips", cancel: Cancel));
        File.WriteAllText(Path.Combine(b.Folder.Path, "wave.bvh"), Capture);

        var r = await Parse(b.Tools.LoadBvh("Figure", "wave.bvh", map: new() { ["UpperArm"] = "Arm" }, cancel: Cancel));
        Assert.Equal("Hips", (string)r["matched"]!["Hips"]!);
        Assert.Equal("Arm", (string)r["matched"]!["UpperArm"]!);
        Assert.Equal(3 * (6 + 3), (int)r["keys"]!);   // three frames: the hips turn and move, the arm turns
        var arm = b.Scene.Objects.First().Bones.Single(x => x.Name == "UpperArm");
        Assert.Equal(3, arm[AVAModeler.Scene.Channel.Heading].Keys.Count);
        await b.Tools.UndoScene(Cancel);
        Assert.Single(arm[AVAModeler.Scene.Channel.Heading].Keys);

        Assert.Contains("outside the working folder", await Error(b.Tools.LoadBvh("Figure", "../wave.bvh", cancel: Cancel)));
        Assert.Contains("no file", await Error(b.Tools.LoadBvh("Figure", "missing.bvh", cancel: Cancel)));
        Assert.Contains(".bvh", await Error(b.Tools.LoadBvh("Figure", "wave.txt", cancel: Cancel)));
        File.WriteAllText(Path.Combine(b.Folder.Path, "bad.bvh"), "HIERARCHY ROOT");
        Assert.Contains("BVH", await Error(b.Tools.LoadBvh("Figure", "bad.bvh", cancel: Cancel)));
        Assert.Contains("no joint", await Error(b.Tools.LoadBvh("Figure", "wave.bvh", map: new() { ["UpperArm"] = "Wing" }, cancel: Cancel)));
    }
}
