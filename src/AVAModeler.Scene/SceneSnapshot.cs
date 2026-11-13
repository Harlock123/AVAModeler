using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// A scene as it was at one moment (<see cref="Scene.Snapshot"/>), to put back with <see cref="Restore"/>: its
/// settings (the render effects', the image list, compositing and the extras among them) and current camera, which items it held and in what order, and each item's name, parent, pivot,
/// visibility, lock, path following (with its path.position channel), motion modifiers, channels and own settings (a light's, a camera's, an object's (its dissolve envelope among them), a bone's, its IK goal, flags and limits included). Items are kept by reference,
/// so restoring brings back the very items that were removed since, takes away those added since, and leaves anything
/// that holds an item (a selection) pointing at the right one. An object's morph targets and displacements are kept by
/// reference too, with their settings: a morph's name, target and weight envelope, a displacement's shape, axes,
/// centre, wavelength, speed, phase and amplitude envelope. Objects' documents aren't copied (their geometry has the
/// Modeler's own undo), and nor are morph target meshes: replace a target's mesh rather than editing it in place.
/// </summary>
public sealed class SceneSnapshot
{
    private readonly double _fps;
    private readonly int _first, _last, _step;
    private readonly Vec3 _ambient, _background, _top, _bottom, _fogColor;
    private readonly double _ambientIntensity, _fogMin, _fogMax;
    private readonly bool _gradient;
    private readonly FogType _fog;
    private readonly AmbientOcclusion _occlusion;
    private readonly EnvironmentSettings _environment;
    private readonly CompositingSettings _compositing;
    private readonly IReadOnlyList<SceneImage> _images;
    private readonly IReadOnlyDictionary<string, string> _extras;
    private readonly SceneAudio _audio;
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
        (_occlusion, _environment) = (scene.Occlusion, scene.Environment);
        (_compositing, _images) = (scene.Compositing, scene.Images);
        _extras = scene.Extras;
        _audio = scene.Audio;
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
        (Scene.Occlusion, Scene.Environment) = (_occlusion, _environment);
        (Scene.Compositing, Scene.Images) = (_compositing, _images);
        Scene.Extras = _extras;
        Scene.Audio = _audio;
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
        private readonly bool _visible, _locked;
        private readonly SceneItem? _parent;
        private readonly FollowPath? _path;
        private readonly IReadOnlyList<MotionModifier> _modifiers;
        private readonly EnvelopeState _pathPosition;
        private readonly EnvelopeState[] _channels;
        private readonly Action _restoreOwn;

        public ItemState(SceneItem item)
        {
            _item = item;
            (_name, _pivot, _visible, _locked, _parent, _path) = (item.Name, item.Pivot, item.Visible, item.Locked, item.Parent, item.Path);
            _modifiers = item.Modifiers;
            _channels = item.Channels.Select(c => new EnvelopeState(c)).ToArray();
            _pathPosition = new EnvelopeState(item.PathPosition);
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
            var (width, height, radius, samples) = (l.AreaWidth, l.AreaHeight, l.Radius, l.ShadowSamples);
            var (flare, volumetric, map) = (l.Flare, l.Volumetric, l.ShadowMap);
            var (linkMode, linked) = (l.LinkMode, l.LinkedObjects);
            var intensity = new EnvelopeState(l.Intensity);
            var flareIntensity = new EnvelopeState(l.FlareIntensity);
            return () =>
            {
                (l.Type, l.Color, l.Range, l.ConeAngle, l.EdgeAngle, l.Shadows) = (type, color, range, cone, edge, shadows);
                (l.AreaWidth, l.AreaHeight, l.Radius, l.ShadowSamples) = (width, height, radius, samples);
                (l.Flare, l.Volumetric, l.ShadowMap) = (flare, volumetric, map);
                (l.LinkMode, l.LinkedObjects) = (linkMode, linked);
                intensity.Restore();
                flareIntensity.Restore();
            };
        }

        private static Action Camera(CameraItem c)
        {
            var (w, h, aspect, action, title) = (c.Width, c.Height, c.PixelAspect, c.ActionSafe, c.TitleSafe);
            var (dof, blur) = (c.DepthOfField, c.MotionBlur);
            var zoom = new EnvelopeState(c.Zoom);
            return () =>
            {
                (c.Width, c.Height, c.PixelAspect, c.ActionSafe, c.TitleSafe) = (w, h, aspect, action, title);
                (c.DepthOfField, c.MotionBlur) = (dof, blur);
                zoom.Restore();
            };
        }

        private static Action Object(ObjectItem o)
        {
            var (doc, layer, source, cast, receive, falloff) = (o.Document, o.Layer, o.SourcePath, o.CastShadows, o.ReceiveShadows, o.BoneFalloffExponent);
            var (particles, hair, soft, rigid, scatter) = (o.Particles, o.Hair, o.SoftBody, o.RigidBody, o.Scatter);
            List<Bone> bones = [.. o.BoneList];
            List<MorphTarget> morphs = [.. o.MorphList];
            List<WaveDisplacement> displacements = [.. o.DisplacementList];
            var morphStates = morphs.Select(Morph).ToArray();
            var dissolve = new EnvelopeState(o.Dissolve);
            var displacementStates = displacements.Select(Displacement).ToArray();
            return () =>
            {
                (o.Document, o.Layer, o.SourcePath, o.CastShadows, o.ReceiveShadows, o.BoneFalloffExponent) = (doc, layer, source, cast, receive, falloff);
                (o.Particles, o.Hair, o.SoftBody, o.RigidBody, o.Scatter) = (particles, hair, soft, rigid, scatter);
                Replace(o.BoneList, bones);
                Replace(o.MorphList, morphs);
                Replace(o.DisplacementList, displacements);
                dissolve.Restore();
                foreach (var m in morphStates) m();
                foreach (var d in displacementStates) d();
            };
        }

        private static Action Morph(MorphTarget m)
        {
            var (name, target) = (m.Name, m.Target);
            var weight = new EnvelopeState(m.Weight);
            return () =>
            {
                (m.Name, m.Target) = (name, target);
                weight.Restore();
            };
        }

        private static Action Displacement(WaveDisplacement d)
        {
            var (shape, direction, along, center, wavelength, speed, phase) = (d.Shape, d.Direction, d.Along, d.Center, d.Wavelength, d.Speed, d.Phase);
            var amplitude = new EnvelopeState(d.Amplitude);
            return () =>
            {
                (d.Shape, d.Direction, d.Along, d.Center, d.Wavelength, d.Speed, d.Phase) = (shape, direction, along, center, wavelength, speed, phase);
                amplitude.Restore();
            };
        }

        private static Action Bone(Bone b)
        {
            var (position, rotation, length, strength, active) = (b.RestPosition, b.RestRotation, b.RestLength, b.Strength, b.Active);
            var (goal, fullTime, anchor, locked, limits) = (b.IKGoal, b.FullTimeIK, b.IKAnchor, b.IKLocked, b.IKLimits);
            var (weightMap, mapOnly) = (b.WeightMap, b.WeightMapOnly);
            return () =>
            {
                (b.RestPosition, b.RestRotation, b.RestLength, b.Strength, b.Active) = (position, rotation, length, strength, active);
                (b.IKGoal, b.FullTimeIK, b.IKAnchor, b.IKLocked, b.IKLimits) = (goal, fullTime, anchor, locked, limits);
                (b.WeightMap, b.WeightMapOnly) = (weightMap, mapOnly);
            };
        }

        private static void Replace<T>(List<T> list, List<T> was)
        {
            list.Clear();
            list.AddRange(was);
        }

        public void Restore(Scene scene)
        {
            _item.Scene = scene;
            (_item.Name, _item.Pivot, _item.Visible, _item.Locked, _item.Path) = (_name, _pivot, _visible, _locked, _path);
            _item.RestoreParent(_parent);
            _item.RestoreModifiers(_modifiers);
            foreach (var c in _channels) c.Restore();
            _pathPosition.Restore();
            _restoreOwn();
        }
    }
}
