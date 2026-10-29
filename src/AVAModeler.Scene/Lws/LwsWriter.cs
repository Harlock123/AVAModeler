using System.Globalization;
using System.Text;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>
/// Writes a scene as a LightWave version 3 scene file (.lws). Objects are written by their
/// <see cref="ObjectItem.SourcePath"/>, or else their document's file (an object with neither is written by its name,
/// with a warning), so the meshes must be saved separately. Morphs, displacements, visibility and the safe areas have no place in the format and are
/// left out.
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
        }

        private void Common(SceneItem item)
        {
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

        private void WriteEnvelope(Envelope e)
        {
            L("{ Envelope");
            L($"  {e.Keys.Count}");
            foreach (var k in e.Keys)
                L($"  Key {N(k.Value)} {N(k.Frame / scene.FramesPerSecond)} {(k.Linear ? 3 : 0)} {N(k.Tension)} {N(k.Continuity)} {N(k.Bias)} 0 0 0");
            L($"  Behaviors {(int)e.PreBehavior} {(int)e.PostBehavior}");
            L("}");
        }
    }
}
