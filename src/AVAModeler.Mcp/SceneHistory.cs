using System.Text.Json.Nodes;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVAModeler.Mcp;

/// <summary>
/// An agent's own scene history for undo_scene: before each of its changes to the scene, a <see cref="SceneSnapshot"/>.
/// Layout has no undo of its own, so this covers only the agent's steps, and only while they're the latest thing to have
/// happened to the scene: once anyone else changes it (the person, in the live app) or it's replaced (new_scene,
/// open_scene, or the person loading one), the steps before are forgotten rather than undone over someone else's work.
/// </summary>
internal sealed class SceneHistory
{
    /// <summary>How many steps are kept.</summary>
    public const int Limit = 50;

    private sealed class Step(SceneSnapshot before, string label, int after)
    {
        public SceneSnapshot Before { get; } = before;
        public string Label { get; } = label;
        /// <summary>The scene's version just after the step (or after undoing the one above it).</summary>
        public int After { get; set; } = after;
    }

    private readonly List<Step> _steps = [];

    /// <summary>Records a step that just finished, given the snapshot taken before it.</summary>
    public void Done(SceneSnapshot before, LayoutSession l, string label)
    {
        var scene = l.Scene;
        if (!ReferenceEquals(scene, before.Scene))
        {
            // The scene was replaced: a fresh start.
            _steps.Clear();
            return;
        }
        if (scene.Version == before.Version) return;
        if (_steps.Count > 0 && (!ReferenceEquals(_steps[^1].Before.Scene, scene) || _steps[^1].After != before.Version)) _steps.Clear();
        _steps.Add(new Step(before, label, scene.Version));
        if (_steps.Count > Limit) _steps.RemoveAt(0);
    }

    /// <summary>
    /// After a failed call put the scene back as it was (<paramref name="before"/>), the steps carry on from there: the
    /// scene is as the last step left it, though its version moved on.
    /// </summary>
    public void Restored(SceneSnapshot before, LayoutSession l)
    {
        if (_steps.Count > 0 && ReferenceEquals(_steps[^1].Before.Scene, l.Scene) && _steps[^1].After == before.Version) _steps[^1].After = l.Scene.Version;
    }

    /// <summary>Forgets the steps if the scene has changed (or been replaced) since the last of them.</summary>
    private void Check(LayoutSession l)
    {
        if (_steps.Count > 0 && (!ReferenceEquals(_steps[^1].Before.Scene, l.Scene) || _steps[^1].After != l.Scene.Version)) _steps.Clear();
    }

    /// <summary>What undo_scene can do now, for get_scene.</summary>
    public JsonObject State(LayoutSession l)
    {
        Check(l);
        return new JsonObject { ["steps"] = _steps.Count, ["next"] = _steps.Count > 0 ? _steps[^1].Label : null };
    }

    /// <summary>Takes the scene back to before the agent's last change; returns what was undone.</summary>
    public string Undo(LayoutSession l)
    {
        bool had = _steps.Count > 0;
        Check(l);
        if (_steps.Count == 0)
            throw new InvalidOperationException(had
                ? "The scene has changed since your last change to it (the person edited it, or it was replaced), so undo_scene won't step back over that. Put things right with set_transform, set_item and the other tools instead."
                : "There's nothing of yours to undo in the scene. (undo_scene steps back through your own scene changes since the scene was made or opened; undo is for the object.)");
        var step = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);
        step.Before.Restore();
        l.Revalidate();
        if (_steps.Count > 0) _steps[^1].After = l.Scene.Version;
        return step.Label;
    }

    public int Count => _steps.Count;
}
