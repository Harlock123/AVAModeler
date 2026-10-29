using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// A scene as it was at one moment (<see cref="Scene.Snapshot"/>), to put back with <see cref="Restore"/>: its
/// settings and current camera, which items it held and in what order, and each item's name, parent, pivot,
/// visibility, channels and own settings (a light's, a camera's, an object's, a bone's). Items are kept by reference,
/// so restoring brings back the very items that were removed since, takes away those added since, and leaves anything
/// that holds an item (a selection) pointing at the right one. Objects' documents aren't copied (their geometry has the
/// Modeler's own undo), and their morph targets and displacements are kept as lists, not their settings.
/// </summary>
public sealed class SceneSnapshot
{
    private readonly double _fps;
    private readonly int _first, _last, _step;
    private readonly Vec3 _ambient, _background, _top, _bottom, _fogColor;
    private readonly double _ambientIntensity, _fogMin, _fogMax;
    private readonly bool _gradient;
    private readonly FogType _fog;
    private readonly CameraItem? _camera;
    private readonly List<SceneItem> _items;
    private readonly List<ItemState> _states;

    internal SceneSnapshot(Scene scene)
    {
        Scene = scene;
        Version = scene.Version;
        (_fps, _first, _last, _step) = (scene.FramesPerSecond, scene.FirstFrame, scene.LastFrame, scene.FrameStep);
        (_ambient, _ambientIntensity, _background, _gradient, _top, _bottom) =
            (scene.AmbientColor, scene.AmbientIntensity, scene.BackgroundColor, scene.GradientBackground, scene.GradientTop, scene.GradientBottom);
        (_fog, _fogMin, _fogMax, _fogColor) = (scene.Fog, scene.FogMinDistance, scene.FogMaxDistance, scene.FogColor);
        _camera = scene.CameraField;
        _items = [.. scene.ItemList];
        _states = _items.Select(i => new ItemState(i)).ToList();
    }

    /// <summary>The scene it was taken of.</summary>
    public Scene Scene { get; }

    /// <summary>The scene's <see cref="Scene.Version"/> when it was taken.</summary>
    public int Version { get; }

    /// <summary>Puts the scene back as it was, raising <see cref="Scene.Changed"/> once.</summary>
    public void Restore() => Scene.Batch(() =>
    {
        var list = Scene.ItemList;
        foreach (var gone in list.Where(i => !_items.Contains(i))) gone.Scene = null;
        list.Clear();
        list.AddRange(_items);
        foreach (var state in _states) state.Restore(Scene);
        Scene.CameraField = _camera;
        (Scene.FramesPerSecond, Scene.FirstFrame, Scene.LastFrame, Scene.FrameStep) = (_fps, _first, _last, _step);
        (Scene.AmbientColor, Scene.AmbientIntensity, Scene.BackgroundColor, Scene.GradientBackground, Scene.GradientTop, Scene.GradientBottom) =
            (_ambient, _ambientIntensity, _background, _gradient, _top, _bottom);
        (Scene.Fog, Scene.FogMinDistance, Scene.FogMaxDistance, Scene.FogColor) = (_fog, _fogMin, _fogMax, _fogColor);
        Scene.Notify();
    });

    /// <summary>An envelope's keys and behaviours, copied only back if it changed since.</summary>
    private readonly struct EnvelopeState(Envelope envelope)
    {
        private readonly int _version = envelope.Version;
        private readonly Envelope _copy = envelope.Clone();

        public void Restore()
        {
            if (envelope.Version != _version) envelope.CopyFrom(_copy);
        }
    }

    private sealed class ItemState
    {
        private readonly SceneItem _item;
        private readonly string _name;
        private readonly Vec3 _pivot;
        private readonly bool _visible;
        private readonly SceneItem? _parent;
        private readonly EnvelopeState[] _channels;
        private readonly Action _restoreOwn;

        public ItemState(SceneItem item)
        {
            _item = item;
            (_name, _pivot, _visible, _parent) = (item.Name, item.Pivot, item.Visible, item.Parent);
            _channels = item.Channels.Select(c => new EnvelopeState(c)).ToArray();
            _restoreOwn = item switch
            {
                LightItem l => Light(l),
                CameraItem c => Camera(c),
                ObjectItem o => Object(o),
                Bone b => Bone(b),
                _ => () => { },
            };
        }

        private static Action Light(LightItem l)
        {
            var (type, color, range, cone, edge, shadows) = (l.Type, l.Color, l.Range, l.ConeAngle, l.EdgeAngle, l.Shadows);
            var intensity = new EnvelopeState(l.Intensity);
            return () =>
            {
                (l.Type, l.Color, l.Range, l.ConeAngle, l.EdgeAngle, l.Shadows) = (type, color, range, cone, edge, shadows);
                intensity.Restore();
            };
        }

        private static Action Camera(CameraItem c)
        {
            var (w, h, aspect, action, title) = (c.Width, c.Height, c.PixelAspect, c.ActionSafe, c.TitleSafe);
            var zoom = new EnvelopeState(c.Zoom);
            return () =>
            {
                (c.Width, c.Height, c.PixelAspect, c.ActionSafe, c.TitleSafe) = (w, h, aspect, action, title);
                zoom.Restore();
            };
        }

        private static Action Object(ObjectItem o)
        {
            var (doc, layer, source, cast, receive, falloff) = (o.Document, o.Layer, o.SourcePath, o.CastShadows, o.ReceiveShadows, o.BoneFalloffExponent);
            List<Bone> bones = [.. o.BoneList];
            List<MorphTarget> morphs = [.. o.MorphList];
            List<WaveDisplacement> displacements = [.. o.DisplacementList];
            return () =>
            {
                (o.Document, o.Layer, o.SourcePath, o.CastShadows, o.ReceiveShadows, o.BoneFalloffExponent) = (doc, layer, source, cast, receive, falloff);
                Replace(o.BoneList, bones);
                Replace(o.MorphList, morphs);
                Replace(o.DisplacementList, displacements);
            };
        }

        private static Action Bone(Bone b)
        {
            var (position, rotation, length, strength, active) = (b.RestPosition, b.RestRotation, b.RestLength, b.Strength, b.Active);
            return () => (b.RestPosition, b.RestRotation, b.RestLength, b.Strength, b.Active) = (position, rotation, length, strength, active);
        }

        private static void Replace<T>(List<T> list, List<T> was)
        {
            list.Clear();
            list.AddRange(was);
        }

        public void Restore(Scene scene)
        {
            _item.Scene = scene;
            (_item.Name, _item.Pivot, _item.Visible) = (_name, _pivot, _visible);
            _item.RestoreParent(_parent);
            foreach (var c in _channels) c.Restore();
            _restoreOwn();
        }
    }
}
