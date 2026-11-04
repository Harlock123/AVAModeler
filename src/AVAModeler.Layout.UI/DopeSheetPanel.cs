using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Modeler.UI;

namespace AVAModeler.Layout.UI;

/// <summary>
/// Layout's dope sheet (D, or View › Dope Sheet): the scene's items as rows against the frames, every key a diamond.
/// Click a key to choose it, Shift-click to add or take it away, drag a box round several; drag chosen keys sideways
/// to move them in time (snapped to frames). Delete removes them, Ctrl+C and Ctrl+V copy them and paste them at the
/// current frame, Ctrl+A chooses all, and Scale stretches their timing about the earliest. Channels shows a row for each
/// keyed channel. Clicking the frame numbers moves to that frame. The work is <see cref="DopeSheet"/>'s; each change is
/// an undo step.
/// </summary>
public sealed class DopeSheetPanel : DockPanel
{
    public DopeSheet Sheet { get; }
    public DopeSheetView View { get; }

    /// <summary>Show each keyed channel's row.</summary>
    public CheckBox Channels { get; } = new() { Content = "Channels", Foreground = Look.Text, FontSize = 11, Margin = new Thickness(4, 0) };

    /// <summary>How much to stretch the chosen keys' timing.</summary>
    public TextBox ScaleFactor { get; } = new() { Text = "2", Width = 52, FontSize = 11 };

    private readonly TextBlock _info = new() { Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };

    public DopeSheetPanel(LayoutSession session)
    {
        Sheet = new DopeSheet(session);
        View = new DopeSheetView(Sheet);
        Background = Look.PanelDark;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
        bar.Children.Add(new TextBlock { Text = "Dope Sheet", FontWeight = FontWeight.Bold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) });
        bar.Children.Add(Channels);
        bar.Children.Add(Look.MakeButton("Copy", () => Sheet.Copy(), "Copy the chosen keys (Ctrl+C)"));
        bar.Children.Add(Look.MakeButton("Paste", () => Sheet.Paste(session.Frame), "Paste the copied keys at the current frame (Ctrl+V)"));
        bar.Children.Add(Look.MakeButton("Delete", () => Sheet.Delete(), "Delete the chosen keys (Delete)"));
        bar.Children.Add(Look.MakeButton("Scale ×", ApplyScale, "Stretch the chosen keys' timing by this factor, about the earliest (2 is twice as long)"));
        bar.Children.Add(ScaleFactor);
        bar.Children.Add(_info);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(new ScrollViewer { Content = View, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Channels.IsCheckedChanged += (_, _) =>
        {
            Sheet.ShowChannels = Channels.IsChecked == true;
            View.InvalidateMeasure();
            View.InvalidateVisual();
        };
        Sheet.SelectionChanged += () => { UpdateInfo(); View.InvalidateVisual(); };
        session.Changed += () =>
        {
            if (!IsVisible) return;
            Sheet.Prune();
            UpdateInfo();
            View.InvalidateMeasure();
            View.InvalidateVisual();
        };
        UpdateInfo();
    }

    private void UpdateInfo() => _info.Text = Sheet.Selected.Count == 0
        ? "Click or drag a box to choose keys; drag them to move them."
        : $"{Sheet.Selected.Count} chosen" + (Sheet.FirstSelectedFrame is { } f ? $", from frame {f:0}" : "");

    /// <summary>Scales the chosen keys by the factor typed (Scale ×).</summary>
    public void ApplyScale()
    {
        if (!double.TryParse(ScaleFactor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)) return;
        try { Sheet.Scale(factor); }
        catch (ArgumentException e) { Sheet.Session.SetStatus(e.Message); }
    }

    /// <summary>The dope sheet's keys: Delete, Ctrl+C, Ctrl+V, Ctrl+A, and the arrows nudge the chosen keys a frame. Returns whether it used the key.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Delete or Key.Back when !ctrl && Sheet.Selected.Count > 0: Sheet.Delete(); return true;
            case Key.C when ctrl: Sheet.Copy(); return true;
            case Key.V when ctrl: Sheet.Paste(Sheet.Session.Frame); return true;
            case Key.A when ctrl: Sheet.SelectAll(); return true;
            case Key.Left when !ctrl && Sheet.Selected.Count > 0: Sheet.Move(-1); return true;
            case Key.Right when !ctrl && Sheet.Selected.Count > 0: Sheet.Move(1); return true;
        }
        return false;
    }
}

/// <summary>The dope sheet's drawing and mouse: rows, frames and key diamonds.</summary>
public sealed class DopeSheetView : Control
{
    public const double LabelWidth = 150, HeaderHeight = 18, RowHeight = 18, Inset = 10;

    private readonly DopeSheet _sheet;
    private Point _press, _now;
    private enum Gesture { None, Drag, Box, Scrub }
    private Gesture _gesture;
    private bool _moved;
    private KeyId? _pressedKey;

    public DopeSheetView(DopeSheet sheet)
    {
        _sheet = sheet;
        Focusable = true;
        ClipToBounds = true;
    }

    private Scene.Scene Scene => _sheet.Session.Scene;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, HeaderHeight + Math.Max(1, _sheet.Rows().Count) * RowHeight + 4);

    private double First => Scene.FirstFrame;
    private double Last => Math.Max(Scene.LastFrame, Scene.FirstFrame + 1);
    private double Across => Math.Max(1, Bounds.Width - LabelWidth - 2 * Inset);

    /// <summary>Where a frame is across the view.</summary>
    public double XOf(double frame) => LabelWidth + Inset + (frame - First) / (Last - First) * Across;

    /// <summary>The frame at a point across the view (not snapped).</summary>
    public double FrameAt(double x) => First + (x - LabelWidth - Inset) / Across * (Last - First);

    /// <summary>The middle of a row, down the view.</summary>
    public static double YOf(int row) => HeaderHeight + (row + 0.5) * RowHeight;

    /// <summary>The row at a point down the view (-1 above the rows).</summary>
    public static int RowAt(double y) => y < HeaderHeight ? -1 : (int)((y - HeaderHeight) / RowHeight);

    /// <summary>Where a key's diamond is drawn (for tests and screenshots), or null if its row isn't shown.</summary>
    public Point? PointOf(KeyId key)
    {
        var rows = _sheet.Rows();
        int r = -1;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Item == key.Item && rows[i].Channel == key.Channel) { r = i; break; }
        return r < 0 ? null : new Point(XOf(key.Frame), YOf(r));
    }

    /// <summary>The key whose diamond is under a point, or null.</summary>
    public KeyId? KeyAt(Point p)
    {
        var rows = _sheet.Rows();
        int r = RowAt(p.Y);
        if (r < 0 || r >= rows.Count || p.X < LabelWidth) return null;
        KeyId? best = null;
        double bestD = 7;
        foreach (var f in _sheet.KeysOf(rows[r]))
        {
            double d = Math.Abs(XOf(f) - p.X);
            if (d < bestD) { bestD = d; best = DopeSheet.Key(rows[r], f); }
        }
        return best;
    }

    /// <summary>How far a drag has moved the chosen keys, in whole frames.</summary>
    private double DragFrames => _gesture == Gesture.Drag && _moved ? KeyTiming.Snap(FrameAt(_now.X) - FrameAt(_press.X)) : 0;

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        dc.FillRectangle(new SolidColorBrush(Color.FromRgb(46, 46, 50)), new Rect(Bounds.Size));
        var rows = _sheet.Rows();
        var typeface = new Typeface(FontFamily.Default);
        var text = Look.Text;
        // Rows, every other one shaded, and their names.
        for (int r = 0; r < rows.Count; r++)
        {
            double y = HeaderHeight + r * RowHeight;
            if (r % 2 == 1) dc.FillRectangle(new SolidColorBrush(Color.FromRgb(54, 54, 58)), new Rect(0, y, w, RowHeight));
            if (rows[r].Item == _sheet.Session.Selected && rows[r].Channel == null) dc.FillRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 214, 74)), new Rect(0, y, LabelWidth, RowHeight));
            var label = new FormattedText(rows[r].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, rows[r].Channel == null ? text : new SolidColorBrush(Color.FromRgb(190, 190, 196)))
            {
                MaxTextWidth = LabelWidth - 8, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(label, new Point(6, y + 2));
        }
        // Frame lines and numbers: a line every frame when there's room, stronger every 5 and 10.
        double perFrame = Across / (Last - First);
        int labelEvery = perFrame >= 18 ? 1 : perFrame >= 7 ? 5 : perFrame >= 3 ? 10 : 20;
        for (double f = Math.Ceiling(First); f <= Last; f++)
        {
            double x = XOf(f);
            bool major = (long)f % labelEvery == 0;
            if (perFrame >= 4 || major)
                dc.DrawLine(new Pen(new SolidColorBrush(major ? Color.FromRgb(86, 86, 92) : Color.FromRgb(62, 62, 68)), 1), new Point(x, HeaderHeight), new Point(x, h));
            if (major)
                dc.DrawText(new FormattedText(((long)f).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, text),
                    new Point(x - 6, 2));
        }
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 96)), 1), new Point(LabelWidth, 0), new Point(LabelWidth, h));
        // The current frame.
        double cx = XOf(_sheet.Session.Frame);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(255, 120, 60)), 1.5), new Point(cx, 0), new Point(cx, h));
        // The keys: chosen ones yellow (drawn where a drag is taking them), the others light grey.
        double shift = DragFrames;
        for (int r = 0; r < rows.Count; r++)
        {
            double y = YOf(r);
            foreach (var f in _sheet.KeysOf(rows[r]))
            {
                bool chosen = _sheet.IsSelected(DopeSheet.Key(rows[r], f));
                double x = XOf(chosen ? f + shift : f);
                Diamond(dc, x, y, rows[r].Channel == null ? 5 : 4, chosen ? Look.ButtonOn : new SolidColorBrush(Color.FromRgb(215, 215, 222)));
            }
        }
        if (_gesture == Gesture.Box && _moved)
        {
            var box = new Rect(_press, _now).Normalize();
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 214, 74)), new Pen(Look.ButtonOn, 1, new DashStyle([3, 3], 0)), box);
        }
    }

    private static void Diamond(DrawingContext dc, double x, double y, double s, IBrush fill)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, y - s), true);
            c.LineTo(new Point(x + s, y));
            c.LineTo(new Point(x, y + s));
            c.LineTo(new Point(x - s, y));
            c.EndFigure(true);
        }
        dc.DrawGeometry(fill, new Pen(Brushes.Black, 1), g);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        _press = _now = p;
        _moved = false;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (p.Y < HeaderHeight && p.X > LabelWidth)
        {
            _gesture = Gesture.Scrub;
            _sheet.Session.SetFrame(KeyTiming.Snap(FrameAt(p.X)));
        }
        else if (KeyAt(p) is { } key)
        {
            _pressedKey = key;
            // A key not chosen is chosen at once (Shift adds it); a chosen one waits to see if it's dragged.
            if (!_sheet.IsSelected(key)) _sheet.Click(key, shift);
            else if (shift) { _sheet.Click(key, toggle: true); _pressedKey = null; }
            _gesture = _sheet.Selected.Count > 0 && !shift ? Gesture.Drag : Gesture.None;
        }
        else
        {
            if (p.X > LabelWidth && RowAt(p.Y) is var row && row >= 0 && row < _sheet.Rows().Count && !shift) _sheet.Session.Select(_sheet.Rows()[row].Item);
            _gesture = Gesture.Box;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_gesture == Gesture.None) return;
        _now = e.GetPosition(this);
        if (Math.Abs(_now.X - _press.X) + Math.Abs(_now.Y - _press.Y) > 3) _moved = true;
        if (_gesture == Gesture.Scrub) _sheet.Session.SetFrame(KeyTiming.Snap(FrameAt(_now.X)));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _now = e.GetPosition(this);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (_gesture)
        {
            case Gesture.Drag when _moved:
                double d = DragFrames;
                if (d != 0) _sheet.Move(d);
                break;
            case Gesture.Drag when _pressedKey is { } key:
                // A click on a chosen key, not dragged: choose it alone.
                _sheet.Click(key, toggle: false);
                break;
            case Gesture.Box when _moved:
                _sheet.Box(RowAt(Math.Max(HeaderHeight, Math.Min(_press.Y, _now.Y))), RowAt(Math.Max(_press.Y, _now.Y)), FrameAt(_press.X), FrameAt(_now.X), add: shift);
                break;
            case Gesture.Box when !shift:
                _sheet.ClearSelection();
                break;
        }
        _gesture = Gesture.None;
        _pressedKey = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _gesture = Gesture.None;
        InvalidateVisual();
    }
}
