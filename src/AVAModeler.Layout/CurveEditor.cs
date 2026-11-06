using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>A number of a key the graph editor's key panel sets on every chosen key.</summary>
public enum KeyField { Frame, Value, Tension, Continuity, Bias, InSlope, OutSlope }

/// <summary>
/// Layout's graph editor without its control: the chosen item's curves (every channel <see cref="SceneChannels.Of"/>
/// lists, so a new animatable setting shows up by itself), which of them are shown (the chosen curves: one alone, or
/// several), which keys are chosen on them (click, Shift-click, a box), and what can be done to those keys: drag or
/// nudge them (values, or time in whole frames), set a number, a span or the slopes on all of them, set the curves'
/// behaviours before and after their keys, copy and paste them (onto the same channels, or a single copied curve onto
/// the one curve shown), delete them and scale their timing. Each change is one undo step through the session; keys
/// are <see cref="KeyId"/>s on channels, and moving, scaling and deleting go through <see cref="KeyTiming"/>, as the
/// dope sheet's do.
/// </summary>
public sealed class CurveEditor(LayoutSession session)
{
    /// <summary>The curves shown at first: the item's position.</summary>
    public static readonly string[] DefaultShown = ["position.x", "position.y", "position.z"];

    private const double Same = 1e-9;
    private readonly HashSet<KeyId> _selected = [];
    private readonly HashSet<string> _shown = [.. DefaultShown];
    private List<(string Channel, EnvelopeKey Key)> _clipboard = [];
    private int _busy;
    private (List<(KeyId Key, Envelope Envelope, double Value)> Keys, Dictionary<Envelope, Envelope> Before, double Frames, double Value)? _drag;

    public LayoutSession Session { get; } = session;

    /// <summary>Raised when the shown curves or the chosen keys change.</summary>
    public event Action? SelectionChanged;

    private Scene.Scene Scene => Session.Scene;

    /// <summary>The item whose curves are shown: Layout's chosen item.</summary>
    public SceneItem? Item => Session.Selected;

    /// <summary>Every curve the item has, in <see cref="SceneChannels.Of"/>'s order.</summary>
    public IReadOnlyList<(string Name, Envelope Envelope)> Channels() => Item == null ? [] : [.. SceneChannels.Of(Item)];

    /// <summary>
    /// The shown (chosen) curves, in the item's channel order. Curves chosen on another item that this one hasn't
    /// leave its position shown.
    /// </summary>
    public IReadOnlyList<(string Name, Envelope Envelope)> Shown()
    {
        var all = Channels();
        var shown = all.Where(c => _shown.Contains(c.Name)).ToList();
        if (shown.Count == 0 && _shown.Count > 0) shown = [.. all.Where(c => DefaultShown.Contains(c.Name))];
        return shown;
    }

    /// <summary>Whether a curve is shown.</summary>
    public bool IsShown(string channel) => Shown().Any(c => c.Name == channel);

    /// <summary>Shows one curve alone (a click on its name).</summary>
    public void ShowOnly(string channel) => SetShown([channel]);

    /// <summary>Shows a curve with the others, or hides it if shown (Ctrl- or Shift-click on its name).</summary>
    public void ToggleShown(string channel)
    {
        var now = Shown().Select(c => c.Name).ToHashSet();
        if (!now.Remove(channel)) now.Add(channel);
        SetShown(now);
    }

    /// <summary>A click on a curve's name: alone, or with <paramref name="add"/> added to the shown curves or taken away.</summary>
    public void ClickChannel(string channel, bool add)
    {
        if (add) ToggleShown(channel);
        else ShowOnly(channel);
    }

    /// <summary>Shows every curve the item has keyed (two keys or more, or a key in the scene's frames). Returns how many.</summary>
    public int ShowKeyed()
    {
        if (Item == null) return 0;
        var keyed = SceneChannels.Keyed(Item, Scene).Select(c => c.Name).ToList();
        if (keyed.Count == 0)
        {
            Session.SetStatus($"{Item.Name} has no keyed channels.");
            return 0;
        }
        SetShown(keyed);
        return keyed.Count;
    }

    /// <summary>Shows every curve the item has.</summary>
    public void ShowAll() => SetShown(Channels().Select(c => c.Name));

    private void SetShown(IEnumerable<string> names)
    {
        _shown.Clear();
        foreach (var n in names) _shown.Add(n);
        _selected.RemoveWhere(k => !_shown.Contains(k.Channel!));
        SelectionChanged?.Invoke();
    }

    // ───────────────────────────── choosing keys ─────────────────────────────

    /// <summary>The chosen keys.</summary>
    public IReadOnlyCollection<KeyId> Selected => _selected;

    /// <summary>A key on one of the item's curves.</summary>
    public KeyId Key(string channel, double frame) =>
        new(Item ?? throw new InvalidOperationException("Choose an item first."), channel, frame);

    public bool IsSelected(KeyId key) => _selected.Contains(key);

    /// <summary>Chooses a key: alone, or with <paramref name="toggle"/> (Shift) added to the others or taken away.</summary>
    public void Click(KeyId key, bool toggle)
    {
        if (!toggle) _selected.Clear();
        if (!toggle || !_selected.Remove(key)) _selected.Add(key);
        SelectionChanged?.Invoke();
    }

    /// <summary>Chooses keys by their frames and channels at once (a test's or a host's shortcut), added to the others with <paramref name="add"/>.</summary>
    public void Choose(IEnumerable<KeyId> keys, bool add = false)
    {
        if (!add) _selected.Clear();
        foreach (var k in keys) _selected.Add(k);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Chooses every key on the shown curves inside a box of frames and values (dragged on the graph), added to the
    /// others with <paramref name="add"/>.
    /// </summary>
    public void Box(double fromFrame, double toFrame, double fromValue, double toValue, bool add)
    {
        if (!add) _selected.Clear();
        (fromFrame, toFrame) = (Math.Min(fromFrame, toFrame), Math.Max(fromFrame, toFrame));
        (fromValue, toValue) = (Math.Min(fromValue, toValue), Math.Max(fromValue, toValue));
        foreach (var (name, env) in Shown())
            foreach (var k in env.Keys)
                if (k.Frame >= fromFrame - Same && k.Frame <= toFrame + Same && k.Value >= fromValue - Same && k.Value <= toValue + Same)
                    _selected.Add(Key(name, k.Frame));
        SelectionChanged?.Invoke();
    }

    /// <summary>Chooses every key on the shown curves in the scene's frames (Ctrl+A).</summary>
    public void SelectAll() => Box(Scene.FirstFrame, Scene.LastFrame, double.NegativeInfinity, double.PositiveInfinity, add: false);

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>Forgets chosen keys that aren't there any more: another item chosen, a curve hidden, an undo.</summary>
    public void Prune()
    {
        if (_busy > 0 || _drag != null) return;
        var shown = Shown().Select(c => c.Name).ToHashSet();
        int n = _selected.RemoveWhere(k => k.Item != Item || k.Item.Scene != Scene || !shown.Contains(k.Channel!)
            || SceneChannels.Find(k.Item, k.Channel!) is not { } e || e.IndexOf(k.Frame) < 0);
        if (n > 0) SelectionChanged?.Invoke();
    }

    /// <summary>The chosen keys as they are: each with its channel and envelope, in channel then frame order.</summary>
    public IReadOnlyList<(string Channel, Envelope Envelope, EnvelopeKey Key)> ChosenKeys()
    {
        var result = new List<(string, Envelope, EnvelopeKey)>();
        if (Item == null) return result;
        foreach (var (name, env) in Channels())
            foreach (var k in env.Keys)
                if (_selected.Contains(new KeyId(Item, name, k.Frame))) result.Add((name, env, k));
        return result;
    }

    /// <summary>A number every chosen key shares (for the key panel), or null when they differ or none is chosen.</summary>
    public double? Common(KeyField field)
    {
        var keys = ChosenKeys();
        if (keys.Count == 0) return null;
        double first = Get(keys[0].Key, field);
        return keys.All(k => Get(k.Key, field) == first) ? first : null;
    }

    /// <summary>The span every chosen key shares, or null.</summary>
    public SpanKind? CommonSpan
    {
        get
        {
            var keys = ChosenKeys();
            if (keys.Count == 0) return null;
            var first = keys[0].Key.Kind;
            return keys.All(k => k.Key.Kind == first) ? first : null;
        }
    }

    private static double Get(EnvelopeKey k, KeyField f) => f switch
    {
        KeyField.Frame => k.Frame,
        KeyField.Value => k.Value,
        KeyField.Tension => k.Tension,
        KeyField.Continuity => k.Continuity,
        KeyField.Bias => k.Bias,
        KeyField.InSlope => k.InSlope,
        _ => k.OutSlope,
    };

    private static EnvelopeKey With(EnvelopeKey k, KeyField f, double v) => f switch
    {
        KeyField.Frame => k with { Frame = v },
        KeyField.Value => k with { Value = v },
        KeyField.Tension => k with { Tension = Math.Clamp(v, -1, 1) },
        KeyField.Continuity => k with { Continuity = Math.Clamp(v, -1, 1) },
        KeyField.Bias => k with { Bias = Math.Clamp(v, -1, 1) },
        KeyField.InSlope => k with { InSlope = v },
        _ => k with { OutSlope = v },
    };

    // ───────────────────────────── changing keys ─────────────────────────────

    /// <summary>
    /// Sets a number on every chosen key as one undo step (tension, continuity and bias kept from -1 to 1; slopes in
    /// value per frame). Setting the frame of several keys on one curve is refused, as they would land on each other.
    /// Returns how many keys changed.
    /// </summary>
    public int Set(KeyField field, double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException($"The {Label(field)} must be a number.");
        var keys = ChosenKeys();
        if (keys.Count == 0) return 0;
        if (field == KeyField.Frame && keys.GroupBy(k => k.Envelope).Any(g => g.Count() > 1))
            throw new ArgumentException("Several chosen keys are on one curve, so they can't all go to one frame; move them with a drag or the arrow keys instead.");
        if (keys.All(k => Get(With(k.Key, field, value), field) == Get(k.Key, field))) return 0;
        var moved = new List<KeyId>();
        Busy(() => Session.Edit($"Set Key {Label(field)}", () => Scene.Batch(() =>
        {
            foreach (var (name, env, key) in keys)
            {
                int i = env.IndexOf(key.Frame);
                var k = With(key, field, value);
                env.SetKey(i, k);
                moved.Add(new KeyId(Item!, name, k.Frame));
            }
            Scene.Notify();
        })));
        if (field == KeyField.Frame) Choose(moved);
        else SelectionChanged?.Invoke();
        return keys.Count;
    }

    private static string Label(KeyField f) => f switch
    {
        KeyField.InSlope => "In Slope",
        KeyField.OutSlope => "Out Slope",
        _ => f.ToString(),
    };

    /// <summary>
    /// Sets how the span into every chosen key is drawn (TCB, linear, stepped or Bezier) as one undo step, through
    /// <see cref="Keying.SetSpan"/>: keys becoming Bezier take the curve's slopes there, so its shape holds until the
    /// slopes are changed. Returns how many keys changed.
    /// </summary>
    public int SetSpan(SpanKind kind)
    {
        var keys = ChosenKeys();
        if (keys.Count == 0 || keys.All(k => k.Key.Kind == kind)) return 0;
        Busy(() => Session.Edit("Set Key Span", () => Scene.Batch(() =>
        {
            foreach (var (_, env, key) in keys.OrderBy(k => k.Key.Frame))
                Keying.SetSpan(env, env.IndexOf(key.Frame), kind);
            Scene.Notify();
        })));
        SelectionChanged?.Invoke();
        return keys.Count;
    }

    /// <summary>The curves behaviours apply to: those with chosen keys, else every shown curve.</summary>
    public IReadOnlyList<(string Name, Envelope Envelope)> TargetCurves()
    {
        var chosen = ChosenKeys().Select(k => k.Channel).ToHashSet();
        var shown = Shown();
        return chosen.Count > 0 ? [.. shown.Where(c => chosen.Contains(c.Name))] : shown;
    }

    /// <summary>
    /// Sets what the target curves (<see cref="TargetCurves"/>) do before their first key and after their last as one
    /// undo step. Returns how many curves changed.
    /// </summary>
    public int SetBehavior(EndBehavior? pre, EndBehavior? post)
    {
        var curves = TargetCurves().Where(c => pre is { } p && c.Envelope.PreBehavior != p || post is { } q && c.Envelope.PostBehavior != q).ToList();
        if (curves.Count == 0) return 0;
        string label = pre != null && post != null ? "End Behaviors" : pre != null ? "Pre Behavior" : "Post Behavior";
        Session.Edit(label, () => Scene.Batch(() =>
        {
            foreach (var (_, env) in curves)
            {
                if (pre is { } p) env.PreBehavior = p;
                if (post is { } q) env.PostBehavior = q;
            }
            Scene.Notify();
        }));
        return curves.Count;
    }

    /// <summary>Whether a drag of the chosen keys is under way (<see cref="BeginDrag"/>).</summary>
    public bool Dragging => _drag != null;

    /// <summary>
    /// Starts dragging the chosen keys: every change until <see cref="EndDrag"/> is one undo step named
    /// <paramref name="label"/>. Returns false with no key chosen.
    /// </summary>
    public bool BeginDrag(string label = "Drag Keys")
    {
        if (_drag != null) EndDrag();
        var keys = ChosenKeys();
        if (keys.Count == 0) return false;
        Session.BeginEdit(label);
        var before = new Dictionary<Envelope, Envelope>(ReferenceEqualityComparer.Instance);
        foreach (var (_, env, _) in keys) before.TryAdd(env, env.Clone());
        _drag = ([.. keys.Select(k => (new KeyId(Item!, k.Channel, k.Key.Frame), k.Envelope, k.Key.Value))], before, 0, 0);
        return true;
    }

    /// <summary>
    /// Moves the dragged keys from where they started: <paramref name="frames"/> later (snapped to whole frames; keys
    /// they land on are replaced) and <paramref name="value"/> up.
    /// </summary>
    public void DragBy(double frames, double value)
    {
        if (_drag is not { } d || !double.IsFinite(frames) || !double.IsFinite(value)) return;
        double df = KeyTiming.Snap(frames);
        if (df == d.Frames && value == d.Value) return;
        _drag = d with { Frames = df, Value = value };
        Busy(() => Scene.Batch(() =>
        {
            foreach (var (env, before) in d.Before)
            {
                env.CopyFrom(before);
                var mine = d.Keys.Where(k => ReferenceEquals(k.Envelope, env)).ToList();
                if (df != 0) KeyTiming.Retime(env, [.. mine.Select(k => k.Key.Frame)], f => f + df);
                foreach (var (key, _, v) in mine)
                    if (env.IndexOf(Moved(key.Frame, df)) is var i and >= 0) env.SetKey(i, env.Keys[i] with { Value = v + value });
            }
            Scene.Notify();
        }));
        _selected.Clear();
        foreach (var (key, _, _) in d.Keys) _selected.Add(key with { Frame = Moved(key.Frame, df) });
        SelectionChanged?.Invoke();
    }

    /// <summary>Where a key at a frame goes when dragged by whole frames: snapped, as <see cref="KeyTiming.Retime"/> puts it.</summary>
    private static double Moved(double frame, double frames) => frames == 0 ? frame : KeyTiming.Snap(frame + frames);

    /// <summary>Ends the drag, recording it as one undo step if anything moved.</summary>
    public void EndDrag()
    {
        if (_drag == null) return;
        _drag = null;
        Session.EndEdit();
        Prune();
    }

    /// <summary>Moves the chosen keys by whole frames and a value at once, as one undo step (the arrow keys). Returns how many keys.</summary>
    public int Nudge(double frames, double value)
    {
        if (!BeginDrag("Nudge Keys")) return 0;
        int n = _drag!.Value.Keys.Count;
        DragBy(frames, value);
        EndDrag();
        return n;
    }

    // ───────────────────────────── copy, paste, delete, scale ─────────────────────────────

    /// <summary>Whether there are copied keys to paste.</summary>
    public bool CanPaste => _clipboard.Count > 0;

    /// <summary>Whether the copied keys all came from one curve (they can then be pasted onto another).</summary>
    public bool CopiedOneCurve => _clipboard.Count > 0 && _clipboard.All(c => c.Channel == _clipboard[0].Channel);

    /// <summary>Copies the chosen keys (Ctrl+C), their frames kept relative to the earliest. Returns how many.</summary>
    public int Copy()
    {
        var keys = ChosenKeys();
        if (keys.Count == 0) return 0;
        double first = keys.Min(k => k.Key.Frame);
        _clipboard = [.. keys.Select(k => (k.Channel, k.Key with { Frame = k.Key.Frame - first }))];
        Session.SetStatus($"Copied {_clipboard.Count} key(s).");
        return _clipboard.Count;
    }

    /// <summary>
    /// Pastes the copied keys (Ctrl+V), the earliest at <paramref name="frame"/> (snapped), as one undo step, replacing
    /// keys there, with their spans and slopes. Keys copied from one curve go onto the one curve shown when only one
    /// is (copying a curve to another channel); otherwise each goes onto the channel of the same name on the chosen
    /// item. The pasted keys become the chosen ones. Returns how many.
    /// </summary>
    public int Paste(double frame)
    {
        if (Item == null || _clipboard.Count == 0) return 0;
        double at = KeyTiming.Snap(frame);
        var shown = Shown();
        string? onto = CopiedOneCurve && shown.Count == 1 ? shown[0].Name : null;
        var targets = _clipboard.Select(c => (Channel: onto ?? c.Channel, c.Key))
            .Where(c => SceneChannels.Find(Item, c.Channel) != null).ToList();
        if (targets.Count == 0)
        {
            Session.SetStatus($"{Item.Name} has none of the copied keys' channels; show one curve to paste them onto it.");
            return 0;
        }
        var item = Item;
        Busy(() => Session.Edit("Paste Keys", () => Scene.Batch(() =>
        {
            foreach (var (name, key) in targets)
                Keying.Key(SceneChannels.Find(item, name)!, Scene, key with { Frame = at + key.Frame }, keepShape: false);
            Scene.Notify();
        })));
        foreach (var (name, _) in targets) _shown.Add(name);
        Choose(targets.Select(t => new KeyId(item, t.Channel, at + t.Key.Frame)));
        Session.SetStatus($"Pasted {targets.Count} key(s) at frame {at:0}" + (onto != null && _clipboard[0].Channel != onto ? $" onto {onto}." : "."));
        return targets.Count;
    }

    /// <summary>Deletes the chosen keys as one undo step (a curve's last key stays). Returns how many went.</summary>
    public int Delete()
    {
        if (_selected.Count == 0) return 0;
        var (deleted, kept) = Busy(() => KeyTiming.Delete(Session, _selected));
        _selected.Clear();
        SelectionChanged?.Invoke();
        Session.SetStatus($"Deleted {deleted} key(s)." + (kept > 0 ? $" {kept} kept: each is its curve's only key, which holds its value." : ""));
        return deleted;
    }

    /// <summary>The earliest chosen key's frame (null with none chosen).</summary>
    public double? FirstSelectedFrame => _selected.Count == 0 ? null : _selected.Min(k => k.Frame);

    /// <summary>
    /// Scales the chosen keys' timing about a frame (default: the earliest chosen key), snapped to whole frames, as one
    /// undo step; their slopes are divided by the factor so Bezier spans keep their shape. Returns the keys moved.
    /// </summary>
    public int Scale(double factor, double? pivot = null)
    {
        if (_selected.Count == 0) return 0;
        double p = pivot ?? FirstSelectedFrame!.Value;
        var (moved, replaced) = Busy(() => KeyTiming.Scale(Session, _selected, factor, p));
        if (moved == 0) return 0;
        Choose([.. _selected.Select(k => k with { Frame = KeyTiming.Snap(p + (k.Frame - p) * factor) })]);
        Session.SetStatus($"Scaled {moved} key(s) by {factor:0.###} about frame {p:0}" + (replaced > 0 ? $"; {replaced} key(s) merged where they met." : "."));
        return moved;
    }

    private T Busy<T>(Func<T> work)
    {
        _busy++;
        try { return work(); }
        finally { _busy--; }
    }

    private void Busy(Action work) => Busy(() => { work(); return 0; });
}
