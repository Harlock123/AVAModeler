using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVAModeler.Layout.UI;

/// <summary>
/// The chosen item's settings, as Layout's panels show them: its name, position, rotation, scale, pivot and parent, then
/// what its kind has (a light's, a camera's with its depth of field and motion blur, a bone's with its inverse
/// kinematics, an object's), its path, and Remove. Each change is one undo step on the session. The Layout workspace
/// shows it under the item list; a host can add its own controls for an item (<see cref="Extras"/>).
/// </summary>
public sealed class ItemPropertiesPanel : StackPanel
{
    public LayoutSession Session { get; }

    public ItemPropertiesPanel(LayoutSession session)
    {
        Session = session;
        Spacing = 3;
    }

    /// <summary>The item whose settings are showing, and the frame they were read at.</summary>
    public SceneItem? ShownItem { get; private set; }
    public double ShownFrame { get; private set; } = double.NaN;

    /// <summary>Raised when an item's name or parent changed here, so an item list can show it afresh.</summary>
    public event Action? ItemsChanged;

    /// <summary>A host's own controls for an item, added after its path settings and before Remove.</summary>
    public Action<SceneItem, ItemPropertiesPanel>? Extras { get; set; }

    /// <summary>Makes a change from the panel one undo step.</summary>
    private void Change(string label, Action change) => Session.Edit(label, change);

    /// <summary>Shows the chosen item's settings afresh (at the current frame).</summary>
    public void Show()
    {
        ShownItem = Session.Selected;
        ShownFrame = Session.Frame;
        Children.Clear();
        if (Session.Selected is not { } item) return;
        Children.Add(new TextBlock { Text = $"{item.Kind}: {item.Name}", FontWeight = FontWeight.Bold, Foreground = Look.Text });
        var name = new TextBox { Text = item.Name };
        void Rename()
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text == item.Name) return;
            Change($"Rename {item.Name}", () => { item.Name = name.Text; Session.Scene.Notify(); });
            ItemsChanged?.Invoke();
        }
        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Rename(); };
        Children.Add(name);
        Vector3Row("Position", item.PositionAt(Session.Frame), v => Session.SetVector(item, 0, v));
        Vector3Row("Rotation (H P B)", item.RotationAt(Session.Frame), v => Session.SetVector(item, 3, v));
        Vector3Row("Scale", item.ScaleAt(Session.Frame), v => Session.SetVector(item, 6, v));
        Vector3Row("Pivot", item.Pivot, v => Change($"Move {item.Name}'s Pivot", () => { item.Pivot = v; Session.Scene.Notify(); }));

        var parents = new List<SceneItem?> { null };
        parents.AddRange(Session.Scene.Items.Where(i => i != item && !i.Ancestors().Contains(item)));
        var parent = new ComboBox { ItemsSource = parents.Select(p => p == null ? "(none)" : $"{p.Kind}: {p.Name}").ToList(), SelectedIndex = parents.IndexOf(item.Parent), HorizontalAlignment = HorizontalAlignment.Stretch };
        parent.SelectionChanged += (_, _) =>
        {
            if (parent.SelectedIndex < 0) return;
            try { Change($"Parent {item.Name}", () => item.Parent = parents[parent.SelectedIndex]); ItemsChanged?.Invoke(); }
            catch (InvalidOperationException e) { Session.SetStatus(e.Message); }
        };
        Children.Add(Label("Parent"));
        Children.Add(parent);

        switch (item)
        {
            case LightItem light:
                Children.Add(Label("Light type"));
                var type = new ComboBox { ItemsSource = Enum.GetNames<LightType>(), SelectedItem = light.Type.ToString() };
                type.SelectionChanged += (_, _) =>
                {
                    if (!Enum.TryParse<LightType>(type.SelectedItem as string, out var t) || t == light.Type) return;
                    Change("Light Type", () => { light.Type = t; Session.Scene.Notify(); });
                    // An area light has a size, the others a radius: show the settings this type has.
                    Dispatcher.UIThread.Post(Show);
                };
                Children.Add(type);
                // 0 to 255 like surface colours (and LightWave's light panel); kept as 0 to 1.
                Vector3Row("Colour (0–255)", light.Color * 255, v => Change("Light Colour", () => { light.Color = Vec3.Min(Vec3.Max(v / 255, Vec3.Zero), Vec3.One); Session.Scene.Notify(); }));
                NumberRow("Intensity", light.Intensity.Evaluate(Session.Frame), v => Change("Light Intensity", () => { light.Intensity.AddKey(Session.Frame, v); Session.Scene.Notify(); }));
                NumberRow("Cone angle °", light.ConeAngle, v => Change("Cone Angle", () => { light.ConeAngle = v; Session.Scene.Notify(); }));
                NumberRow("Soft edge °", light.EdgeAngle, v => Change("Soft Edge", () => { light.EdgeAngle = v; Session.Scene.Notify(); }));
                NumberRow("Range (0: none)", light.Range, v => Change("Light Range", () => { light.Range = v; Session.Scene.Notify(); }));
                var shadows = new CheckBox { Content = "Shadows", IsChecked = light.Shadows, Foreground = Look.Text };
                shadows.IsCheckedChanged += (_, _) => Change("Shadows", () => { light.Shadows = shadows.IsChecked == true; Session.Scene.Notify(); });
                Children.Add(shadows);
                // Soft shadows: an area light's size, or a point or spot light's radius, and the rays a pixel they take.
                if (light.Type == LightType.Area)
                {
                    NumberRow("Area width (m)", light.AreaWidth, v => Change("Area Width", () => { light.AreaWidth = Math.Max(1e-3, v); Session.Scene.Notify(); }));
                    NumberRow("Area height (m)", light.AreaHeight, v => Change("Area Height", () => { light.AreaHeight = Math.Max(1e-3, v); Session.Scene.Notify(); }));
                }
                else if (light.Type != LightType.Distant)
                    NumberRow("Radius (m, 0: hard shadows)", light.Radius, v => Change("Light Radius", () => { light.Radius = Math.Max(0, v); Session.Scene.Notify(); }));
                if (light.Type != LightType.Distant)
                    NumberRow("Shadow samples", light.ShadowSamples, v => Change("Shadow Samples", () => { light.ShadowSamples = Math.Clamp((int)v, 1, RenderEffects.MaxSamples); Session.Scene.Notify(); }));
                break;
            case CameraItem cam:
                NumberRow("Zoom factor", cam.Zoom.Evaluate(Session.Frame), v => Change("Zoom", () => { cam.Zoom.AddKey(Session.Frame, Math.Max(0.01, v)); Session.Scene.Notify(); }));
                Children.Add(new TextBlock { Text = $"{cam.HorizontalFov(Session.Frame):0.#}° across, {cam.VerticalFov(Session.Frame):0.#}° high", Foreground = Look.Text, FontSize = 11 });
                NumberRow("Width (pixels)", cam.Width, v => Change("Camera Width", () => { cam.Width = Math.Max(1, (int)v); Session.Scene.Notify(); }));
                NumberRow("Height (pixels)", cam.Height, v => Change("Camera Height", () => { cam.Height = Math.Max(1, (int)v); Session.Scene.Notify(); }));
                Children.Add(Look.MakeButton("Use this camera", () => Change("Use Camera", () => Session.Scene.CurrentCamera = cam)));
                CameraEffects(cam);
                break;
            case Bone bone:
                NumberRow("Rest length", bone.RestLength, v => Change("Rest Length", () => { bone.RestLength = Math.Max(1e-3, v); Session.Scene.Notify(); }));
                NumberRow("Strength", bone.Strength, v => Change("Bone Strength", () => { bone.Strength = v; Session.Scene.Notify(); }));
                var active = new CheckBox { Content = "Active", IsChecked = bone.Active, Foreground = Look.Text };
                active.IsCheckedChanged += (_, _) => Change("Bone Active", () => { bone.Active = active.IsChecked == true; Session.Scene.Notify(); });
                Children.Add(active);
                Children.Add(Look.MakeButton("Rest here", () => Change("Rest Here", () => { bone.RestFromPose(Session.Frame); Session.Scene.Notify(); }), "Make the current pose the rest pose"));
                IKSettings(bone);
                break;
            case ObjectItem obj:
                Children.Add(new TextBlock { Text = obj.IsPlaceholder ? "(couldn't load: a placeholder)" : $"{obj.BaseMesh().Points.Count} points, {obj.BaseMesh().Polygons.Count} polygons", Foreground = Look.Text, FontSize = 11 });
                Children.Add(Look.MakeButton("Add Bone", () => Session.Select(Session.Edit("Add Bone", () => Session.Scene.AddBone(obj, $"Bone {obj.Bones.Count + 1}")))));
                Children.Add(Look.MakeButton("Skelegons → Bones", () =>
                {
                    var made = Session.Edit("Skelegons → Bones", () => Skelegons.CreateBones(Session.Scene, obj));
                    Session.SetStatus(made.Count > 0 ? $"Made {made.Count} bones from skelegons." : "No skelegons (2-point polygons on a \"Skelegon…\" surface) in this object.");
                }));
                break;
        }
        if (item is not Bone) PathSettings(item);
        Extras?.Invoke(item, this);
        Children.Add(Look.MakeButton("Remove item", () => Session.Remove(item)));
    }

    /// <summary>The items an item's path can run through (in the item list's order).</summary>
    public IReadOnlyList<SceneItem> PathChoices(SceneItem item) =>
        Session.Scene.Items.Where(i => i is not Bone && FollowPath.Problem(item, [i]) == null).ToList();

    /// <summary>
    /// Following a path: Follow a path on or off, the points it runs through (chosen in a list, taken in the item list's
    /// order), Closed, Align to path with its look-ahead, Banking, where along it the item is at this frame (keyed by
    /// Auto key, as positions are), Even speed (keys the position from the first frame to the last) and Bake path.
    /// </summary>
    private void PathSettings(SceneItem item)
    {
        void Set(string label, Func<FollowPath, FollowPath> change)
        {
            if (item.Path is not { } p) return;
            try { var next = change(p).Checked(); Change(label, () => { item.Path = next; Session.Scene.Notify(); }); }
            catch (ArgumentException e) { Session.SetStatus(e.Message); }
        }
        Children.Add(new TextBlock { Text = "Path", FontWeight = FontWeight.Bold, Foreground = Look.Text, Margin = new Thickness(0, 8, 0, 0) });
        var choices = PathChoices(item);
        var follow = new CheckBox { Name = "FollowPath", Content = "Follow a path", IsChecked = item.Path != null, Foreground = Look.Text };
        var entries = choices.Select(i => new ItemChoice(i)).ToList();
        var list = new ListBox { Name = "PathPoints", SelectionMode = Avalonia.Controls.SelectionMode.Multiple, MaxHeight = 120, FontSize = 11, ItemsSource = entries };
        ToolTip.SetTip(list, "The items the path runs through (nulls, usually), in the item list's order");
        bool filling = true;
        if (item.Path is { } current)
            foreach (var e in entries.Where(e => current.Points.Contains(e.Item))) list.SelectedItems!.Add(e);
        filling = false;
        List<SceneItem> Chosen() => list.SelectedItems!.Cast<ItemChoice>().Select(e => e.Item).OrderBy(i => Session.Scene.Items.ToList().IndexOf(i)).ToList();
        follow.IsCheckedChanged += (_, _) =>
        {
            if (follow.IsChecked == true && item.Path == null)
            {
                var points = Chosen();
                if (points.Count < 2)
                {
                    Session.SetStatus("Choose at least two items in the list for the path to run through (add nulls for its points).");
                    follow.IsChecked = false;
                    return;
                }
                Change($"Follow Path", () => { item.Path = new FollowPath { Points = points }; Session.Scene.Notify(); });
            }
            else if (follow.IsChecked != true && item.Path != null) Change("Stop Following Path", () => { item.Path = null; Session.Scene.Notify(); });
            Dispatcher.UIThread.Post(Show);
        };
        list.SelectionChanged += (_, _) =>
        {
            if (filling || item.Path == null) return;
            var points = Chosen();
            if (points.Count >= item.Path.Fewest) Set("Path Points", p => p with { Points = points });
            else Session.SetStatus($"A{(item.Path.Closed ? " closed" : "n open")} path needs at least {item.Path.Fewest} points.");
        };
        Children.Add(follow);
        Children.Add(list);
        if (item.Path is not { } path) return;
        var closed = new CheckBox { Name = "PathClosed", Content = "Closed (a loop)", IsChecked = path.Closed, Foreground = Look.Text };
        closed.IsCheckedChanged += (_, _) => Set("Close Path", p => p with { Closed = closed.IsChecked == true });
        var align = new CheckBox { Name = "AlignToPath", Content = "Align to path", IsChecked = path.AlignToPath, Foreground = Look.Text };
        align.IsCheckedChanged += (_, _) => Set("Align to Path", p => p with { AlignToPath = align.IsChecked == true });
        Children.Add(closed);
        Children.Add(align);
        NumberRow("Look-ahead (m)", path.LookAhead, v => Set("Path Look-ahead", p => p with { LookAhead = v }));
        NumberRow("Banking (° on a 1 m turn)", path.Banking, v => Set("Path Banking", p => p with { Banking = v }));
        NumberRow("Position along path (0–1)", item.PathPosition.Evaluate(Session.Frame), v => Session.SetPathPosition(item, v));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        buttons.Children.Add(Look.MakeButton("Even speed", () =>
        {
            try { Session.TimePath(item, Session.Scene.FirstFrame, Session.Scene.LastFrame); Show(); }
            catch (ArgumentException e) { Session.SetStatus(e.Message); }
        }, "Key the position along the path from 0 at the first frame to 1 at the last, at an even speed"));
        buttons.Children.Add(Look.MakeButton("Bake path", () =>
        {
            try { Session.BakePath(item, Session.Scene.FirstFrame, Session.Scene.LastFrame); Show(); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Session.SetStatus(e.Message); }
        }, "Key the item's motion along the path at every frame, and stop following it"));
        Children.Add(buttons);
    }

    /// <summary>The IK goal's choices for a bone (the items it can reach for), shown after "(none)".</summary>
    public IReadOnlyList<SceneItem> GoalChoices(Bone bone) =>
        Session.Scene.Items.Where(i => InverseKinematics.GoalProblem(bone, i) == null).ToList();

    /// <summary>
    /// A bone's inverse kinematics, as LightWave 5's motion options had them: the goal its tip reaches for, Full-time
    /// IK, the anchor that stops chains below it, and per channel a joint limit and a lock (keyed only); Key IK at this
    /// frame and Bake IK over the scene's frames.
    /// </summary>
    private void IKSettings(Bone bone)
    {
        void Set(string label, Action change) => Change(label, () => { change(); Session.Scene.Notify(); });
        Children.Add(new TextBlock { Text = "Inverse kinematics", FontWeight = FontWeight.Bold, Foreground = Look.Text, Margin = new Thickness(0, 8, 0, 0) });
        var choices = GoalChoices(bone);
        var goal = new ComboBox
        {
            Name = "IKGoal", ItemsSource = new[] { "(none)" }.Concat(choices.Select(i => $"{i.Kind}: {i.Name}")).ToList(),
            SelectedIndex = bone.IKGoal == null ? 0 : Math.Max(0, choices.ToList().IndexOf(bone.IKGoal) + 1), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11,
        };
        ToolTip.SetTip(goal, "The item this bone's tip reaches for (a null, usually); the chain is this bone and its parent bones, up to an anchor");
        goal.SelectionChanged += (_, _) =>
        {
            if (goal.SelectedIndex < 0) return;
            var g = goal.SelectedIndex == 0 ? null : choices[goal.SelectedIndex - 1];
            if (g == bone.IKGoal) return;
            Set(g == null ? "Remove IK Goal" : "IK Goal", () => bone.IKGoal = g);
            Dispatcher.UIThread.Post(Show);
        };
        Children.Add(Label("Goal"));
        Children.Add(goal);
        var fullTime = new CheckBox { Name = "FullTimeIK", Content = "Full-time IK", IsChecked = bone.FullTimeIK, Foreground = Look.Text };
        ToolTip.SetTip(fullTime, "Follow the goal at every frame; off, the chain plays its keys (Key IK or Bake IK keys it)");
        fullTime.IsCheckedChanged += (_, _) => Set("Full-time IK", () => bone.FullTimeIK = fullTime.IsChecked == true);
        var anchor = new CheckBox { Name = "IKAnchor", Content = "Anchor (unaffected by IK below)", IsChecked = bone.IKAnchor, Foreground = Look.Text };
        anchor.IsCheckedChanged += (_, _) => Set("IK Anchor", () => bone.IKAnchor = anchor.IsChecked == true);
        Children.Add(fullTime);
        Children.Add(anchor);
        if (bone.IKGoal != null)
        {
            var chain = InverseKinematics.Chain(bone);
            Children.Add(new TextBlock
            {
                Text = $"Chain: {string.Join(" → ", chain.Select(b => b.Name))} (reach {InverseKinematics.Reach(bone):0.###} m)",
                TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 11,
            });
        }
        else if (bone.IKEnd is { } end)
            Children.Add(new TextBlock { Text = $"Turned by {end.Name}'s IK.", Foreground = Look.Text, FontSize = 11 });

        // Per channel: a limit (min and max, degrees) and a lock (IK leaves it at its keys).
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("62,Auto,*,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        void Cell(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
        Cell(new TextBlock { Text = "limit", FontSize = 10, Foreground = Look.Text }, 0, 1);
        Cell(new TextBlock { Text = "min °", FontSize = 10, Foreground = Look.Text, Margin = new Thickness(3, 0) }, 0, 2);
        Cell(new TextBlock { Text = "max °", FontSize = 10, Foreground = Look.Text, Margin = new Thickness(3, 0) }, 0, 3);
        Cell(new TextBlock { Text = "lock", FontSize = 10, Foreground = Look.Text }, 0, 4);
        string[] names = ["Heading", "Pitch", "Bank"];
        for (int c = 0; c < 3; c++)
        {
            int channel = c;
            var limit = bone.IKLimits[c];
            var on = new CheckBox { Name = $"IKLimit{names[c]}", IsChecked = limit != null, MinWidth = 0, Padding = new Thickness(0) };
            var min = new TextBox { Name = $"IKMin{names[c]}", Text = F(limit?.Min ?? -90), FontSize = 11, Margin = new Thickness(1, 0), IsEnabled = limit != null };
            var max = new TextBox { Name = $"IKMax{names[c]}", Text = F(limit?.Max ?? 90), FontSize = 11, Margin = new Thickness(1, 0), IsEnabled = limit != null };
            var locked = new CheckBox { Name = $"IKLock{names[c]}", IsChecked = (bone.IKLocked & (IKChannels)(1 << c)) != 0, MinWidth = 0, Padding = new Thickness(0) };
            ToolTip.SetTip(on, $"Limit how far IK turns the {names[c].ToLowerInvariant()}");
            ToolTip.SetTip(locked, $"IK leaves the {names[c].ToLowerInvariant()} at its keys");
            void ApplyLimit()
            {
                IKLimit? l = null;
                if (on.IsChecked == true)
                {
                    if (!double.TryParse(min.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) ||
                        !double.TryParse(max.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi)) return;
                    try { l = new IKLimit(lo, hi).Checked(names[channel]); }
                    catch (ArgumentException e) { Session.SetStatus(e.Message); return; }
                }
                if (bone.IKLimits[channel] == l) return;
                Set($"IK {names[channel]} Limit", () => bone.IKLimits = bone.IKLimits.With(channel, l));
            }
            on.IsCheckedChanged += (_, _) => { min.IsEnabled = max.IsEnabled = on.IsChecked == true; ApplyLimit(); };
            foreach (var box in new[] { min, max })
            {
                box.LostFocus += (_, _) => ApplyLimit();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyLimit(); };
            }
            locked.IsCheckedChanged += (_, _) =>
            {
                var flag = (IKChannels)(1 << channel);
                Set($"IK {names[channel]} Lock", () => bone.IKLocked = locked.IsChecked == true ? bone.IKLocked | flag : bone.IKLocked & ~flag);
            };
            Cell(new TextBlock { Text = names[c], FontSize = 11, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center }, c + 1, 0);
            Cell(on, c + 1, 1);
            Cell(min, c + 1, 2);
            Cell(max, c + 1, 3);
            Cell(locked, c + 1, 4);
        }
        Children.Add(grid);
        if (bone.IKGoal != null)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            buttons.Children.Add(Look.MakeButton("Key IK here", () =>
            {
                try { Session.KeyIK(bone); Show(); }
                catch (InvalidOperationException e) { Session.SetStatus(e.Message); }
            }, "Key the chain's IK pose at this frame"));
            buttons.Children.Add(Look.MakeButton("Bake IK", () =>
            {
                try { Session.BakeIK(bone, Session.Scene.FirstFrame, Session.Scene.LastFrame); Show(); }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Session.SetStatus(e.Message); }
            }, "Key the chain's IK pose at every frame of the scene, and turn full-time IK off"));
            Children.Add(buttons);
        }
    }

    /// <summary>An item in a list of choices, shown by its kind and name.</summary>
    public sealed record ItemChoice(SceneItem Item)
    {
        public override string ToString() => $"{Item.Kind}: {Item.Name}";
    }

    /// <summary>The camera's depth of field (with Focus on an item) and motion blur.</summary>
    private void CameraEffects(CameraItem cam)
    {
        void Dof(string label, Func<DepthOfField, DepthOfField> change) => Change(label, () => { cam.DepthOfField = change(cam.DepthOfField); Session.Scene.Notify(); });
        void Blur(string label, Func<MotionBlur, MotionBlur> change) => Change(label, () => { cam.MotionBlur = change(cam.MotionBlur); Session.Scene.Notify(); });
        var dof = new CheckBox { Content = "Depth of field", IsChecked = cam.DepthOfField.On, Foreground = Look.Text, Margin = new Thickness(0, 6, 0, 0) };
        dof.IsCheckedChanged += (_, _) => Dof("Depth of Field", d => d with { On = dof.IsChecked == true });
        Children.Add(dof);
        NumberRow("Focal distance (m)", cam.DepthOfField.FocalDistance, v => { if (v > 0) Dof("Focal Distance", d => d with { FocalDistance = v }); });
        NumberRow("Lens f-stop", cam.DepthOfField.FStop, v => { if (v > 0) Dof("Lens F-Stop", d => d with { FStop = v }); });
        NumberRow("Depth of field samples", cam.DepthOfField.Samples, v => Dof("Depth of Field Samples", d => d with { Samples = Math.Clamp((int)v, 1, RenderEffects.MaxSamples) }));
        var others = Session.Scene.Items.Where(i => i != cam).ToList();
        if (others.Count > 0)
        {
            var target = new ComboBox { ItemsSource = others.Select(i => $"{i.Kind}: {i.Name}").ToList(), SelectedIndex = Math.Max(0, others.FindIndex(i => i is ObjectItem)), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
            var focus = Look.MakeButton("Focus on", () =>
            {
                if (target.SelectedIndex < 0) return;
                try
                {
                    double d = Session.FocusOn(cam, others[target.SelectedIndex]);
                    Session.SetStatus($"{cam.Name} focused {d:0.###} m away, on {others[target.SelectedIndex].Name}.");
                    Show();
                }
                catch (ArgumentException e) { Session.SetStatus(e.Message); }
            }, "Set the focal distance so the chosen item is sharp at this frame");
            Children.Add(new DockPanel { Children = { Docked(focus, Dock.Right), target } });
        }
        var blur = new CheckBox { Content = "Motion blur", IsChecked = cam.MotionBlur.On, Foreground = Look.Text, Margin = new Thickness(0, 6, 0, 0) };
        blur.IsCheckedChanged += (_, _) => Blur("Motion Blur", b => b with { On = blur.IsChecked == true });
        Children.Add(blur);
        NumberRow("Shutter (frames, 0–1)", cam.MotionBlur.Shutter, v => { if (v > 0 && v <= 1) Blur("Shutter", b => b with { Shutter = v }); });
        NumberRow("Motion blur samples", cam.MotionBlur.Samples, v => Blur("Motion Blur Samples", b => b with { Samples = Math.Clamp((int)v, 1, RenderEffects.MaxSamples) }));
    }

    private static Control Docked(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }

    public static TextBlock Label(string t) => new() { Text = t, Foreground = Look.Text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public void NumberRow(string label, double value, Action<double> set)
    {
        var box = new TextBox { Text = F(value), FontSize = 11 };
        var applied = box.Text;
        // Only a value changed is a change (and an undo step): Enter, then leaving the box, is one.
        void Apply()
        {
            if (box.Text == applied || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return;
            applied = box.Text;
            set(v);
        }
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Apply(); };
        Children.Add(Label(label));
        Children.Add(box);
    }

    public void Vector3Row(string label, Vec3 value, Action<Vec3> set)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var boxes = new TextBox[3];
        for (int i = 0; i < 3; i++)
        {
            boxes[i] = new TextBox { Text = F(value[i]), FontSize = 11, Margin = new Thickness(1, 0) };
            Grid.SetColumn(boxes[i], i);
            grid.Children.Add(boxes[i]);
        }
        string Texts() => string.Join(" ", boxes.Select(b => b.Text));
        var applied = Texts();
        void Apply()
        {
            if (Texts() == applied) return;
            var v = new double[3];
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return;
            applied = Texts();
            set(new Vec3(v[0], v[1], v[2]));
        }
        foreach (var b in boxes)
        {
            b.LostFocus += (_, _) => Apply();
            b.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Apply(); };
        }
        Children.Add(Label(label));
        Children.Add(grid);
    }
}
