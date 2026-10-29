using AVAModeler.Scene;

namespace AVAModeler.Layout;

/// <summary>
/// Layout's undo history: before each step, a <see cref="SceneSnapshot"/> of the scene; undoing puts it back (and keeps
/// the scene as it was for redo). The person's steps and AI agents' share it, as they share the Modeler's
/// <c>UndoStack</c>. <see cref="LayoutSession"/> owns it and records the steps (<see cref="LayoutSession.Edit"/>,
/// <see cref="LayoutSession.BeginEdit"/>); a new or loaded scene clears it. The object's geometry isn't in it: that is
/// the Modeler's own undo.
/// </summary>
public sealed class SceneUndo
{
    /// <summary>A step: the scene on one side of it, and which known state that is and leads to.</summary>
    private sealed record Step(SceneSnapshot State, string Label, int From, int To);

    private readonly List<Step> _undo = [], _redo = [];
    private int _state, _stateVersion = -1, _lastId;
    private Scene.Scene? _stateScene;

    /// <summary>How many steps are kept (the oldest go first).</summary>
    public int Limit { get; set; } = 100;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>What Undo would take back ("Move Camera"), or null.</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    /// <summary>What Redo would do again, or null.</summary>
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;
    /// <summary>How many steps can be undone.</summary>
    public int Count => _undo.Count;
    /// <summary>How many steps can be redone.</summary>
    public int RedoCount => _redo.Count;

    /// <summary>Raised when a step is recorded, undone or redone, or the history is cleared.</summary>
    public event Action? Changed;

    /// <summary>
    /// Which state the scene is in, as a number: the same after undoing back to it (or redoing forward to it), and a
    /// new one after any change. <see cref="LayoutSession"/> uses it to know when undo has come back to the saved scene.
    /// </summary>
    public int StateOf(Scene.Scene scene)
    {
        if (!ReferenceEquals(scene, _stateScene) || scene.Version != _stateVersion) SetState(scene, ++_lastId);
        return _state;
    }

    private void SetState(Scene.Scene scene, int id)
    {
        (_state, _stateScene, _stateVersion) = (id, scene, scene.Version);
    }

    /// <summary>
    /// Records a step that has just been made, given the snapshot taken before it. Nothing is recorded if the scene
    /// hasn't changed since. Redo is cleared.
    /// </summary>
    public void Record(SceneSnapshot before, string label)
    {
        var scene = before.Scene;
        if (scene.Version == before.Version) return;
        int from = ReferenceEquals(scene, _stateScene) && before.Version == _stateVersion ? _state : ++_lastId;
        int to = ++_lastId;
        SetState(scene, to);
        _undo.Add(new Step(before, label, from, to));
        if (_undo.Count > Math.Max(1, Limit)) _undo.RemoveRange(0, _undo.Count - Math.Max(1, Limit));
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// After a step that failed was put back (<paramref name="before"/> restored), the scene is in the state it was in
    /// before, though its version moved on.
    /// </summary>
    public void Restored(SceneSnapshot before)
    {
        if (ReferenceEquals(before.Scene, _stateScene) && before.Version == _stateVersion) _stateVersion = before.Scene.Version;
    }

    /// <summary>Takes the scene back to before the last step; returns the step's name, or null if there was none.</summary>
    public string? Undo(Scene.Scene scene) => Move(scene, _undo, _redo, undo: true);

    /// <summary>Makes the last step undone again; returns its name, or null if there was none.</summary>
    public string? Redo(Scene.Scene scene) => Move(scene, _redo, _undo, undo: false);

    private string? Move(Scene.Scene scene, List<Step> from, List<Step> to, bool undo)
    {
        if (from.Count == 0) return null;
        var step = from[^1];
        if (!ReferenceEquals(step.State.Scene, scene))
        {
            // A history for another scene (it should have been cleared when the scene was replaced).
            Clear();
            return null;
        }
        int now = StateOf(scene);
        from.RemoveAt(from.Count - 1);
        var here = scene.Snapshot();
        step.State.Restore();
        // The step to go back the other way: from where the scene is now, to where it was.
        to.Add(undo ? step with { State = here, To = now } : step with { State = here, From = now });
        SetState(scene, undo ? step.From : step.To);
        Changed?.Invoke();
        return step.Label;
    }

    /// <summary>Forgets every step.</summary>
    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
