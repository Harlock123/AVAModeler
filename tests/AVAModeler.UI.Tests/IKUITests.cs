using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVAModeler.Core;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;

namespace AVAModeler.UI.Tests;

/// <summary>A bone's IK settings in Layout's item panel: goal, full-time IK, anchor, limits, locks, Key IK and Bake IK.</summary>
public class IKUITests
{
    internal static (LayoutWorkspace Layout, MainWindow Window, Bone Upper, Bone Lower, NullItem Goal) Arm()
    {
        var w = Ui.Open();
        w.Session.Commands.Run("Box", new Dictionary<string, object?> { ["Center"] = new Vec3(1, 0, 0), ["Size"] = new Vec3(2, 0.2, 0.2), ["Segments X"] = 8 });
        w.ShowLayout();
        Ui.Pump();
        var layout = (LayoutWorkspace)w.Content!;
        var s = layout.Session;
        var arm = s.Scene.Objects.Single();
        var upper = s.Edit("Add Bone", () => s.Scene.AddBone(arm, "Upper", Vec3.Zero, new Vec3(90, 0, 0), 1));
        var lower = s.Edit("Add Bone", () => s.Scene.AddBone(arm, "Lower", new Vec3(0, 0, 1), Vec3.Zero, 1));
        s.Edit("Parent", () => lower.Parent = upper);
        var goal = s.Add(new NullItem("Goal"));
        s.SetVector(goal, 0, new Vec3(1.2, 0.9, 0.3));
        s.Select(lower);
        Ui.Pump();
        return (layout, w, upper, lower, goal);
    }

    internal static T Named<T>(LayoutWorkspace layout, string name) where T : Control =>
        layout.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void The_bone_panel_gives_a_bone_a_goal_and_its_settings_each_as_an_undo_step()
    {
        var (layout, w, upper, lower, goal) = Arm();
        var s = layout.Session;
        var choice = Named<ComboBox>(layout, "IKGoal");
        var names = ((IEnumerable<string>)choice.ItemsSource!).ToList();
        // Its own bones can't be its goal; the null and the object can.
        Assert.Contains("Null: Goal", names);
        Assert.DoesNotContain("Bone: Upper", names);
        Assert.DoesNotContain("Bone: Lower", names);
        choice.SelectedIndex = names.IndexOf("Null: Goal");
        Ui.Pump();
        Assert.Same(goal, lower.IKGoal);
        Assert.Equal("IK Goal", s.History.UndoLabel);
        var tip = lower.WorldMatrix(1).TransformPoint(new Vec3(0, 0, 1));
        Assert.True((tip - goal.WorldPosition(1)).Length < 1e-4);
        Assert.Contains(layout.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Chain: Upper → Lower (reach 2 m)");

        // A limit on the pitch, typed and entered.
        Named<CheckBox>(layout, "IKLimitPitch").IsChecked = true;
        var min = Named<TextBox>(layout, "IKMinPitch");
        min.Focus();
        min.Text = "-15";
        Ui.Key(w, Avalonia.Input.Key.Enter);
        var max = Named<TextBox>(layout, "IKMaxPitch");
        max.Focus();
        max.Text = "15";
        Ui.Key(w, Avalonia.Input.Key.Enter);
        Assert.Equal(new IKLimit(-15, 15), lower.IKLimits.Pitch);
        Assert.InRange(lower.MotionRotationAt(1).Y, -15 - 1e-9, 15 + 1e-9);
        Named<CheckBox>(layout, "IKLockBank").IsChecked = true;
        Assert.Equal(IKChannels.Bank, lower.IKLocked);

        // The upper bone shows it's turned by the lower one's IK; as an anchor it's left alone.
        s.Select(upper);
        Ui.Pump();
        Assert.Contains(layout.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Turned by Lower's IK.");
        Named<CheckBox>(layout, "IKAnchor").IsChecked = true;
        Assert.True(upper.IKAnchor);
        Assert.Null(upper.IKEnd);
        s.Undo();
        Assert.False(upper.IKAnchor);

        // Bake IK keys the chain over the scene's frames and turns full-time IK off; Ctrl+Z takes it back.
        s.Select(lower);
        Ui.Pump();
        var bake = layout.GetLogicalDescendants().OfType<Button>().Single(b => (b.Content as TextBlock)?.Text == "Bake IK" || (string?)b.Content == "Bake IK");
        bake.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
        Assert.False(lower.FullTimeIK);
        Assert.Equal(s.Scene.LastFrame - s.Scene.FirstFrame + 1, upper[Channel.Heading].Keys.Count);
        Assert.False(Named<CheckBox>(layout, "FullTimeIK").IsChecked);
        layout.Undo();
        Assert.True(lower.FullTimeIK);
        w.Close();
    }
}
