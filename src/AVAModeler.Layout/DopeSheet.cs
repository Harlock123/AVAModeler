using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// A key in the dope sheet: an item's keys at a frame on one channel (by its <see cref="SceneChannels"/> name), or on
/// every channel of the item that has one there (<see cref="Channel"/> null: the item's own row).
/// </summary>
public readonly record struct KeyId(SceneItem Item, string? Channel, double Frame);

/// <summary>A row of the dope sheet: an item (all its channels' keys together) or one of its channels.</summary>
public sealed record DopeRow(SceneItem Item, string? Channel)
{
    /// <summary>"Camera" for an item's row, "  heading" for a channel's.</summary>
    public string Label => Channel == null ? $"{Item.Kind}: {Item.Name}" : "  " + Channel;
}

/// <summary>
/// Moving keys in time: shifting a set of keys by whole frames and scaling them about a frame, snapped to whole frames,
/// with keys they land on replaced. Layout's dope sheet and agents' shift_keys and scale_keys share it. Each change is
/// one undo step through <see cref="LayoutSession.Edit"/>, and the keys keep their values, tension, continuity, bias,
/// linear flag and span; scaling divides their slopes (value per frame) by the factor so Bezier spans keep their shape.
/// </summary>
public static class KeyTiming
{
    private const double Same = 1e-9;

    /// <summary>A frame snapped to the nearest whole frame, as the dope sheet moves keys.</summary>
    public static double Snap(double frame) => Math.Round(frame, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The envelopes and key frames a set of dope sheet keys stands for: an item's row is every channel of the item
    /// with a key at that frame (channels never keyed, holding a constant, are left out).
    /// </summary>
    public static Dictionary<Envelope, List<double>> Resolve(Scene.Scene scene, IEnumerable<KeyId> keys)
    {
        var result = new Dictionary<Envelope, List<double>>(ReferenceEqualityComparer.Instance);
        void Add(Envelope e, double frame)
        {
            if (e.IndexOf(frame) < 0) return;
            if (!result.TryGetValue(e, out var list)) result[e] = list = [];
            if (!list.Any(f => Math.Abs(f - frame) < Same)) list.Add(frame);
        }
        foreach (var k in keys)
        {
            if (k.Item.Scene != scene) continue;
            foreach (var (name, env) in SceneChannels.Keyed(k.Item, scene))
                if (k.Channel == null || k.Channel == name) Add(env, k.Frame);
        }
        return result;
    }

    /// <summary>
    /// Moves some of an envelope's keys to new frames (<paramref name="map"/> from each old frame to its new one,
    /// snapped): keys not moved that are where a moved key lands are replaced, and when two moved keys land on one frame
    /// the one that started later wins. Returns how many keys were replaced or merged. With
    /// <paramref name="timeFactor"/> (a scale's factor), the moved keys' slopes, in value per frame, are divided by it.
    /// </summary>
    public static int Retime(Envelope e, IReadOnlyCollection<double> frames, Func<double, double> map, double timeFactor = 1)
    {
        var moving = e.Keys.Where(k => frames.Any(f => Math.Abs(f - k.Frame) < Same)).ToList();
        if (moving.Count == 0) return 0;
        var moved = moving.OrderBy(k => k.Frame).Select(k => timeFactor == 1 || k.InSlope == 0 && k.OutSlope == 0
            ? k with { Frame = Snap(map(k.Frame)) }
            : k with { Frame = Snap(map(k.Frame)), InSlope = k.InSlope / timeFactor, OutSlope = k.OutSlope / timeFactor }).ToList();
        if (moving.Zip(moved).All(p => Math.Abs(p.First.Frame - p.Second.Frame) < Same)) return 0;
        var staying = e.Keys.Except(moving).ToList();
        var result = new Envelope { PreBehavior = e.PreBehavior, PostBehavior = e.PostBehavior };
        int lost = 0;
        foreach (var k in staying)
        {
            if (moved.Any(m => Math.Abs(m.Frame - k.Frame) < Same)) { lost++; continue; }
            result.AddKey(k);
        }
        foreach (var k in moved)
        {
            if (result.IndexOf(k.Frame) >= 0 && !staying.Any(s => Math.Abs(s.Frame - k.Frame) < Same)) lost++;
            result.AddKey(k);
        }
        e.CopyFrom(result);
        return lost;
    }

    /// <summary>
    /// Shifts keys by <paramref name="delta"/> frames (snapped to whole frames) as one undo step named
    /// <paramref name="label"/>. Returns how many keys moved and how many others they replaced.
    /// </summary>
    public static (int Moved, int Replaced) Shift(LayoutSession session, IEnumerable<KeyId> keys, double delta, string label = "Move Keys")
    {
        if (!double.IsFinite(delta)) throw new ArgumentException("The shift must be a number of frames.");
        double d = Snap(delta);
        return Apply(session, keys, f => f + d, label);
    }

    /// <summary>
    /// Scales keys' times about <paramref name="pivot"/> by <paramref name="factor"/> (2 makes their spacing twice as
    /// long, 0.5 half), snapped to whole frames, as one undo step. Returns how many keys moved and how many merged.
    /// </summary>
    public static (int Moved, int Replaced) Scale(LayoutSession session, IEnumerable<KeyId> keys, double factor, double pivot, string label = "Scale Keys")
    {
        if (!(factor > 0) || !double.IsFinite(factor)) throw new ArgumentException("The scale must be more than 0 (1 leaves the keys where they are, 2 spaces them twice as far apart).");
        if (!double.IsFinite(pivot)) throw new ArgumentException("The pivot must be a frame.");
        return Apply(session, keys, f => pivot + (f - pivot) * factor, label, factor);
    }

    private static (int Moved, int Replaced) Apply(LayoutSession session, IEnumerable<KeyId> keys, Func<double, double> map, string label, double timeFactor = 1)
    {
        var resolved = Resolve(session.Scene, keys);
        int moved = resolved.Sum(r => r.Value.Count(f => Math.Abs(Snap(map(f)) - f) > Same));
        int replaced = 0;
        if (moved == 0) return (0, 0);
        session.Edit(label, () => session.Scene.Batch(() =>
        {
            foreach (var (env, frames) in resolved) replaced += Retime(env, frames, map, timeFactor);
        }));
        return (moved, replaced);
    }

    /// <summary>
    /// Removes keys as one undo step, but not a channel's last key, which holds its value (as Delete Key does). Returns
    /// how many went and how many were kept for that reason.
    /// </summary>
    public static (int Deleted, int Kept) Delete(LayoutSession session, IEnumerable<KeyId> keys, string label = "Delete Keys")
    {
        var resolved = Resolve(session.Scene, keys);
        int deleted = 0, kept = 0;
        if (resolved.Count == 0) return (0, 0);
        session.Edit(label, () => session.Scene.Batch(() =>
        {
            foreach (var (env, frames) in resolved)
                foreach (var f in frames.OrderByDescending(f => f))
                {
                    if (env.Keys.Count <= 1) { kept++; continue; }
                    env.RemoveKeyAt(f);
                    deleted++;
                }
        }));
        return (deleted, kept);
    }
}

/// <summary>
/// Layout's dope sheet without its control: the rows (each item, and with <see cref="ShowChannels"/> each of its keyed
/// channels), the keys on them, which keys are chosen (click, Shift-click, a box), and what can be done to them: move
/// them in time (snapped to frames), scale their timing, delete them, and copy and paste them at another frame, each one
/// undo step through <see cref="KeyTiming"/> and the session.
/// </summary>
public sealed class DopeSheet(LayoutSession session)
{
    private readonly HashSet<KeyId> _selected = [];
    private int _busy;
    private List<(SceneItem Item, string Channel, EnvelopeKey Key)> _clipboard = [];

    public LayoutSession Session { get; } = session;

    /// <summary>Show a row for each keyed channel under its item's row.</summary>
    public bool ShowChannels { get; set; }

    /// <summary>The chosen keys.</summary>
    public IReadOnlyCollection<KeyId> Selected => _selected;

    /// <summary>Raised when the chosen keys change.</summary>
    public event Action? SelectionChanged;

    private Scene.Scene Scene => Session.Scene;

    /// <summary>The rows, in the item list's order.</summary>
    public IReadOnlyList<DopeRow> Rows()
    {
        var rows = new List<DopeRow>();
        foreach (var item in Scene.Items)
        {
            rows.Add(new DopeRow(item, null));
            if (ShowChannels)
                foreach (var (name, _) in SceneChannels.Keyed(item, Scene)) rows.Add(new DopeRow(item, name));
        }
        return rows;
    }

    /// <summary>The frames a row has keys at, in order (for an item, any keyed channel's).</summary>
    public IReadOnlyList<double> KeysOf(DopeRow row)
    {
        var frames = new SortedSet<double>();
        foreach (var (name, env) in SceneChannels.Keyed(row.Item, Scene))
            if (row.Channel == null || row.Channel == name)
                foreach (var k in env.Keys) frames.Add(k.Frame);
        return [.. frames];
    }

    /// <summary>The key on a row at a frame.</summary>
    public static KeyId Key(DopeRow row, double frame) => new(row.Item, row.Channel, frame);

    /// <summary>Whether a key is chosen: itself, or (for a channel's key) its item's key at that frame.</summary>
    public bool IsSelected(KeyId key) =>
        _selected.Contains(key) || key.Channel != null && _selected.Contains(key with { Channel = null })
        || key.Channel == null && _selected.Any(k => k.Item == key.Item && k.Frame == key.Frame);

    /// <summary>Chooses a key: alone, or with <paramref name="toggle"/> (Shift) added to the others or taken away.</summary>
    public void Click(KeyId key, bool toggle)
    {
        if (!toggle) _selected.Clear();
        if (!toggle || !_selected.Remove(key)) _selected.Add(key);
        SelectionChanged?.Invoke();
    }

    /// <summary>Chooses every key in rows <paramref name="firstRow"/> to <paramref name="lastRow"/> between two frames (a box), added to the others with <paramref name="add"/>.</summary>
    public void Box(int firstRow, int lastRow, double fromFrame, double toFrame, bool add)
    {
        if (!add) _selected.Clear();
        var rows = Rows();
        (firstRow, lastRow) = (Math.Max(0, Math.Min(firstRow, lastRow)), Math.Min(rows.Count - 1, Math.Max(firstRow, lastRow)));
        (fromFrame, toFrame) = (Math.Min(fromFrame, toFrame), Math.Max(fromFrame, toFrame));
        for (int r = firstRow; r <= lastRow; r++)
            foreach (var f in KeysOf(rows[r]))
                if (f >= fromFrame - 1e-9 && f <= toFrame + 1e-9) _selected.Add(Key(rows[r], f));
        SelectionChanged?.Invoke();
    }

    /// <summary>Chooses every key in the scene's frames (Ctrl+A).</summary>
    public void SelectAll() => Box(0, int.MaxValue, Scene.FirstFrame, Scene.LastFrame, add: false);

    public void ClearSelection()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>Forgets chosen keys that aren't there any more (after an undo, say).</summary>
    public void Prune()
    {
        // Not in the middle of moving keys: the selection follows them once they're moved.
        if (_busy > 0) return;
        int n = _selected.RemoveWhere(k => k.Item.Scene != Scene || KeyTiming.Resolve(Scene, [k]).Count == 0);
        if (n > 0) SelectionChanged?.Invoke();
    }

    /// <summary>The earliest chosen key's frame (null with none chosen).</summary>
    public double? FirstSelectedFrame => _selected.Count == 0 ? null : _selected.Min(k => k.Frame);

    /// <summary>Moves the chosen keys by whole frames as one undo step ("Move Keys"); they stay chosen. Returns the keys moved.</summary>
    public int Move(double delta)
    {
        double d = KeyTiming.Snap(delta);
        var (moved, replaced) = Busy(() => KeyTiming.Shift(Session, _selected, d));
        if (moved == 0) return 0;
        Reselect(f => f + d);
        Session.SetStatus($"Moved {moved} key(s) {(d > 0 ? "later" : "earlier")} by {Math.Abs(d):0} frame(s)" + (replaced > 0 ? $"; {replaced} key(s) they landed on were replaced." : "."));
        return moved;
    }

    /// <summary>Scales the chosen keys' timing about a frame (default: the earliest chosen key) as one undo step. Returns the keys moved.</summary>
    public int Scale(double factor, double? pivot = null)
    {
        if (_selected.Count == 0) return 0;
        double p = pivot ?? FirstSelectedFrame!.Value;
        var (moved, replaced) = Busy(() => KeyTiming.Scale(Session, _selected, factor, p));
        if (moved == 0) return 0;
        Reselect(f => KeyTiming.Snap(p + (f - p) * factor));
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

    private void Reselect(Func<double, double> map)
    {
        var moved = _selected.Select(k => k with { Frame = KeyTiming.Snap(map(k.Frame)) }).ToList();
        _selected.Clear();
        foreach (var k in moved) _selected.Add(k);
        SelectionChanged?.Invoke();
    }

    /// <summary>Deletes the chosen keys as one undo step (a channel's last key stays). Returns how many went.</summary>
    public int Delete()
    {
        var (deleted, kept) = Busy(() => KeyTiming.Delete(Session, _selected));
        _selected.Clear();
        SelectionChanged?.Invoke();
        Session.SetStatus($"Deleted {deleted} key(s)." + (kept > 0 ? $" {kept} kept: each is its channel's only key, which holds its value." : ""));
        return deleted;
    }

    /// <summary>Whether there are copied keys to paste.</summary>
    public bool CanPaste => _clipboard.Count > 0;

    /// <summary>Copies the chosen keys (Ctrl+C), their frames kept relative to the earliest. Returns how many.</summary>
    public int Copy()
    {
        var resolved = KeyTiming.Resolve(Scene, _selected);
        if (resolved.Count == 0) return 0;
        double first = resolved.SelectMany(r => r.Value).Min();
        var names = new Dictionary<Envelope, (SceneItem, string)>(ReferenceEqualityComparer.Instance);
        foreach (var item in Scene.Items)
            foreach (var (name, env) in SceneChannels.Of(item)) names[env] = (item, name);
        _clipboard = [];
        foreach (var (env, frames) in resolved)
        {
            var (item, name) = names[env];
            foreach (var f in frames)
                if (env.IndexOf(f) is var i and >= 0) _clipboard.Add((item, name, env.Keys[i] with { Frame = f - first }));
        }
        Session.SetStatus($"Copied {_clipboard.Count} key(s).");
        return _clipboard.Count;
    }

    /// <summary>
    /// Pastes the copied keys (Ctrl+V) onto the same items' channels, the earliest at <paramref name="frame"/> (snapped),
    /// as one undo step, replacing keys there; the pasted keys become the chosen ones. Returns how many.
    /// </summary>
    public int Paste(double frame)
    {
        double at = KeyTiming.Snap(frame);
        var targets = _clipboard.Where(c => c.Item.Scene == Scene && SceneChannels.Find(c.Item, c.Channel) != null).ToList();
        if (targets.Count == 0) return 0;
        Busy(() => Session.Edit("Paste Keys", () => Scene.Batch(() =>
        {
            foreach (var (item, name, key) in targets)
                Keying.Key(SceneChannels.Find(item, name)!, Scene, key with { Frame = at + key.Frame }, keepShape: false);
        })));
        _selected.Clear();
        foreach (var (item, name, key) in targets) _selected.Add(new KeyId(item, name, at + key.Frame));
        SelectionChanged?.Invoke();
        Session.SetStatus($"Pasted {targets.Count} key(s) at frame {at:0}.");
        return targets.Count;
    }
}
