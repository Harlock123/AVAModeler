using System.Globalization;
using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// Writes a scene as a LightWave version 3 scene file (.lws). Objects are written by their
/// <see cref="ObjectItem.SourcePath"/>, or else their document's file (an object with neither is written by its name,
/// with a warning), so the meshes must be saved separately. Morphs, displacements, visibility and the safe areas have no place in the format and are
/// left out. An area light is written as LightWave 6's (light type 4), and a camera's depth of field and motion blur
/// as LightWave's own settings; an area light's size, a light's radius, the effects' sample counts, ambient occlusion
/// and the environment have no place in the format either (the .avascene keeps them). A bone's IK goal is written as
/// <c>GoalItem</c> (with <c>GoalStrength 1</c> and <c>FullTimeIK</c>) and its joint limits as <c>HLimits</c>,
/// <c>PLimits</c> and <c>BLimits</c>; its IK anchor and locked channels aren't written (a warning says so). Path following
/// and motion modifiers have no place either: the keys are written, with a warning.
/// </summary>
public static class LwsWriter
{
    public static string Write(Scene scene, List<string>? warnings = null)
    {
        var w = new Writer(scene, warnings ?? []);
        w.Run();
        return w.Text.ToString();
    }

    public static void WriteFile(Scene scene, string path, List<string>? warnings = null) => File.WriteAllText(path, Write(scene, warnings));

    private sealed class Writer(Scene scene, List<string> warnings)
    {
        public readonly StringBuilder Text = new();
        private readonly List<SceneItem> _objects = scene.Items.Where(i => i is ObjectItem or NullItem).ToList();
        private readonly List<LightItem> _lights = scene.Lights.ToList();
        private readonly List<CameraItem> _cameras = scene.Cameras.ToList();

        private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string V(Vec3 v) => $"{N(v.X)} {N(v.Y)} {N(v.Z)}";
        private void L(string line = "") => Text.Append(line).Append('\n');

        public void Run()
        {
            L("LWSC");
            L("3");
            L();
            L($"FirstFrame {scene.FirstFrame}");
            L($"LastFrame {scene.LastFrame}");
            L($"FrameStep {scene.FrameStep}");
            L($"FramesPerSecond {N(scene.FramesPerSecond)}");
            L();

            foreach (var item in _objects)
            {
                if (item is ObjectItem o) WriteObject(o);
                else L($"AddNullObject {item.Name}");
                Motion("ObjectMotion", item);
                Common(item);
                if (item is ObjectItem obj)
                {
                    L($"ShadowOptions {(obj.CastShadows ? 2 : 0) | (obj.ReceiveShadows ? 4 : 0)}");
                    L($"BoneFalloffType {(int)Math.Round(Math.Log2(Math.Max(1, obj.BoneFalloffExponent)))}");
                    foreach (var b in obj.Bones) WriteBone(b);
                }
                L();
            }

            L($"AmbientColor {V(scene.AmbientColor)}");
            L($"AmbientIntensity {N(scene.AmbientIntensity)}");
            L();
            foreach (var l in _lights)
            {
                L("AddLight");
                L($"LightName {l.Name}");
                Motion("LightMotion", l);
                Common(l);
                L($"LightColor {V(l.Color)}");
                Value("LightIntensity", l.Intensity);
                L($"LightType {(int)l.Type}");
                L($"LightFalloffType {(l.Range > 0 ? 1 : 0)}");
                if (l.Range > 0) L($"LightRange {N(l.Range)}");
                L($"LightConeAngle {N(l.ConeAngle)}");
                L($"LightEdgeAngle {N(l.EdgeAngle)}");
                L($"ShadowType {(l.Shadows ? 1 : 0)}");
                L();
            }

            foreach (var c in _cameras)
            {
                L("AddCamera");
                L($"CameraName {c.Name}");
                Motion("CameraMotion", c);
                Common(c);
                Value("ZoomFactor", c.Zoom);
                L($"FrameSize {c.Width} {c.Height}");
                L($"PixelAspect {N(c.PixelAspect)}");
                // LightWave's own camera settings for these, written only when on so other scenes stay as they were.
                if (c.MotionBlur.On)
                {
                    L("MotionBlur 1");
                    L($"BlurLength {N(c.MotionBlur.Shutter)}");
                }
                if (c.DepthOfField.On)
                {
                    L("DepthOfField 1");
                    L($"FocalDistance {N(c.DepthOfField.FocalDistance)}");
                    L($"LensFStop {N(c.DepthOfField.FStop)}");
                }
                L();
            }
            if (scene.CurrentCamera is { } current) L($"CurrentCamera {_cameras.IndexOf(current)}");

            L($"BackdropColor {V(scene.BackgroundColor)}");
            L($"SolidBackdrop {(scene.GradientBackground ? 0 : 1)}");
            L($"ZenithColor {V(scene.GradientTop)}");
            L($"NadirColor {V(scene.GradientBottom)}");
            L($"FogType {(scene.Fog == FogType.Off ? 0 : 1)}");
            L($"FogMinDistance {N(scene.FogMinDistance)}");
            L($"FogMaxDistance {N(scene.FogMaxDistance)}");
            L($"FogColor {V(scene.FogColor)}");
        }

        private void WriteObject(ObjectItem o)
        {
            string path = o.SourcePath ?? o.Document?.FilePath ?? "";
            if (path.Length == 0)
            {
                path = o.Name + ".lwo";
                warnings.Add($"{o.Name} has no file; it is written as {path}, which must be saved separately.");
            }
            L(o.Layer is int layer ? $"LoadObjectLayer {layer + 1} {path}" : $"LoadObject {path}");
        }

        private void WriteBone(Bone b)
        {
            L();
            L("AddBone");
            L($"BoneName {b.Name}");
            L($"BoneRestPosition {V(b.RestPosition)}");
            L($"BoneRestDirection {V(b.RestRotation)}");
            L($"BoneRestLength {N(b.RestLength)}");
            L($"BoneStrength {N(b.Strength)}");
            L($"BoneActive {(b.Active ? 1 : 0)}");
            Motion("BoneMotion", b);
            if (b.Parent is Bone parent) L($"ParentItem {Id(parent)}");
            if (b.IKGoal is { } goal && (goal is not Bone || goal.Scene == scene))
            {
                L($"GoalItem {Id(goal)}");
                L("GoalStrength 1");
                L($"FullTimeIK {(b.FullTimeIK ? 1 : 0)}");
            }
            string[] limitKeys = ["HLimits", "PLimits", "BLimits"];
            for (int c = 0; c < 3; c++)
                if (b.IKLimits[c] is { } limit) L($"{limitKeys[c]} {N(limit.Min)} {N(limit.Max)}");
            if (b.IKAnchor || b.IKLocked != IKChannels.None)
                warnings.Add($"{b.Name}'s IK anchor and locked channels have no place in a LightWave scene: the .avascene keeps them.");
        }

        private void Common(SceneItem item)
        {
            if (item.Path != null)
                warnings.Add($"{item.Name} follows a path, which a LightWave scene has no place for: the .avascene keeps it, and the .lws has only its keys (bake the path into keys first to keep the motion).");
            if (item.Modifiers.Count > 0)
                warnings.Add($"{item.Name} has motion modifiers ({string.Join(", ", item.Modifiers.Select(m => m.Kind))}), which a LightWave scene has no place for: the .avascene keeps them, and the .lws has only its keys.");
            if (item.Pivot != Vec3.Zero) L($"PivotPosition {V(item.Pivot)}");
            if (item.Parent != null && item is not Bone) L($"ParentItem {Id(item.Parent)}");
        }

        private string Id(SceneItem item) => item switch
        {
            LightItem l => $"2{_lights.IndexOf(l):X7}",
            CameraItem c => $"3{_cameras.IndexOf(c):X7}",
            Bone b => $"4{b.Owner.Bones.ToList().IndexOf(b):X3}{_objects.IndexOf(b.Owner):X4}",
            _ => $"1{_objects.IndexOf(item):X7}",
        };

        private void Motion(string key, SceneItem item)
        {
            L(key);
            L("NumChannels 9");
            for (int c = 0; c < 9; c++)
            {
                L($"Channel {c}");
                WriteEnvelope(item.Channels[c]);
            }
        }

        private void Value(string key, Envelope e)
        {
            if (e.IsConstant && e.PreBehavior == EndBehavior.Constant && e.PostBehavior == EndBehavior.Constant)
            {
                L($"{key} {N(e.Evaluate(0))}");
                return;
            }
            L($"{key} (envelope)");
            WriteEnvelope(e);
        }

        private void WriteEnvelope(Envelope e) => V3Envelope(Text, e, scene.FramesPerSecond);
    }

    /// <summary>
    /// A version 3 envelope block (<c>{ Envelope</c>, the key count, <c>Key</c> lines with times in seconds at
    /// <paramref name="framesPerSecond"/>, <c>Behaviors</c>, <c>}</c>), as scenes and motion files hold them.
    /// </summary>
    internal static void V3Envelope(StringBuilder text, Envelope e, double framesPerSecond)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        void L(string line) => text.Append(line).Append('\n');
        L("{ Envelope");
        L($"  {e.Keys.Count}");
        // Span types: 0 TCB, 1 Hermite (AVA Modeler's Bezier), 3 linear, 4 stepped. p4 and p5 are the incoming and
        // outgoing tangents, written as slopes in value per second since .lws times are seconds.
        double fps = framesPerSecond;
        foreach (var k in e.Keys)
        {
            int shape = k.Kind switch { SpanKind.Linear => 3, SpanKind.Stepped => 4, SpanKind.Bezier => 1, _ => 0 };
            L($"  Key {N(k.Value)} {N(k.Frame / fps)} {shape} {N(k.Tension)} {N(k.Continuity)} {N(k.Bias)} {N(k.InSlope * fps)} {N(k.OutSlope * fps)} 0");
        }
        L($"  Behaviors {(int)e.PreBehavior} {(int)e.PostBehavior}");
        L("}");
    }
}
