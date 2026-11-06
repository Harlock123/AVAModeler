using System.Globalization;
using AVAModeler.Core;

namespace AVAModeler.Scene;

/// <summary>A scene read from a file, and anything that couldn't be read as it should.</summary>
public sealed record SceneLoadResult(Scene Scene, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads LightWave scene files (.lws, text): version 1 (LightWave 4–5) and version 3 (LightWave 6 on). Objects are
/// loaded through a callback given the resolved file path; one that can't be loaded becomes a placeholder (an object
/// with no document) and a warning. Lines it doesn't know are skipped, along with any <c>{ … }</c> block or
/// <c>Plugin … EndPlugin</c> section they open.
/// <para>
/// Relative object paths are tried against the content directory, then the scene's folder (LightWave stores them
/// relative to its content directory), and failing that ever shorter tails of the path are tried, so a scene from
/// another machine still finds its objects; backslashes are read as slashes. The first file that exists is used. In version 1 files colours are 0–255 and key
/// times are frames; in version 3 colours are 0–1 and key times are seconds. In a bone's section of a version 1
/// file, <c>ParentObject n</c> names the object's nth bone.
/// </para>
/// <para>
/// A bone's IK: <c>GoalObject n</c> (version 1, the nth object or null, as <c>ParentObject</c> counts them) or
/// <c>GoalItem id</c> (version 3) names its goal, <c>FullTimeIK</c> its Full-time IK flag (off when a goal comes without
/// it, as in LightWave), and <c>HLimits</c>, <c>PLimits</c> and <c>BLimits</c> its joint limits (min and max degrees;
/// 0 0 is none). IK on objects, nulls, lights and cameras isn't read.
/// </para>
/// <para>
/// An object's dissolve is <c>ObjDissolve</c> (LightWave 5) or <c>ObjectDissolve</c> (6 on), a value or
/// <c>(envelope)</c>. Compositing: <c>BGImage</c>, <c>FGImage</c> and <c>FGAlphaImage</c> (a path, with
/// <c>(sequence)</c> after it for a numbered sequence; each goes in the scene's image list), <c>ForegroundKey</c> with
/// <c>LowClipColor</c> and <c>HighClipColor</c> (the colour key), and <c>FGDissolve</c>. An alpha image makes the key
/// the alpha key.
/// A light's <c>ShadowType</c> is 0 off, 1 ray traced, 2 shadow map, with <c>ShadowMapSize</c> and
/// <c>ShadowFuzziness</c> (or <c>ShadowMapFuzziness</c>); its lens flare is <c>LensFlare</c>, <c>FlareIntensity</c>
/// (a value or envelope), <c>FlareNominalDistance</c>, <c>FlareDissolve</c> (its first value if enveloped), and the
/// <c>LensFlareFade</c> and <c>LensFlareOptions</c> bits as <see cref="LwsLightBits"/> describes them.
/// </para>
/// </summary>
public static class LwsReader
{
    public static SceneLoadResult ReadFile(string path, Func<string, Document?> loadObject, string? contentDirectory = null) =>
        Read(File.ReadAllText(path), loadObject, Path.GetDirectoryName(Path.GetFullPath(path)), contentDirectory);

    /// <summary>Reads a scene from its text. Throws <see cref="InvalidDataException"/> if it isn't an LWSC file.</summary>
    public static SceneLoadResult Read(string text, Func<string, Document?> loadObject, string? sceneDirectory = null, string? contentDirectory = null)
    {
        var p = new Parser(text, loadObject, sceneDirectory, contentDirectory);
        p.Run();
        return new SceneLoadResult(p.Scene, p.Warnings);
    }

    private sealed class Parser(string text, Func<string, Document?> load, string? sceneDir, string? contentDir)
    {
        private LwText _in = null!;
        private int _pos => _in.Position;
        private int _version;
        public readonly Scene Scene = new();
        public readonly List<string> Warnings = [];
        private readonly HashSet<string> _warned = [];

        private readonly List<SceneItem> _objects = [];
        private readonly List<LightItem> _lights = [];
        private readonly List<CameraItem> _cameras = [];
        private readonly Dictionary<string, Document?> _documents = [];
        private readonly List<(SceneItem Item, string Kind, long Value, int Line)> _parents = [];
        private readonly List<(Bone Bone, string Kind, long Value, int Line)> _goals = [];
        private readonly HashSet<Bone> _fullTimeRead = [];
        private readonly HashSet<Envelope> _readInSeconds = [];
        private readonly List<Envelope> _inSeconds = [];
        private readonly Dictionary<LightItem, (int? Type, double Range, double Legacy)> _falloff = [];
        private SceneItem? _current;
        private ObjectItem? _lastObject;
        private Envelope[] _lastEnvelopes = [];
        private double _resolutionMultiplier = 1;
        private int? _currentCamera;

        private void Warn(string message)
        {
            if (_warned.Add(message)) Warnings.Add(message);
        }

        private string? Next() => _in.Next();
        private string? Peek() => _in.Peek();
        private static (string Key, string Tail) Split(string line) => LwText.Split(line);
        private static string[] Tokens(string s) => LwText.Tokens(s);
        private static bool TryNumber(string s, out double v) => LwText.TryNumber(s, out v);
        private double[] Numbers(string rest, int count, string key) => _in.Numbers(rest, count, key);

        private double Number(string rest, string key) => Numbers(rest, 1, key)[0];
        private int Int(string rest, string key) => (int)Math.Round(Number(rest, key));

        private Vec3 VectorOf(string rest, string key)
        {
            var n = Numbers(rest, 3, key);
            return new Vec3(n[0], n[1], n[2]);
        }

        private Vec3 ColorOf(string rest, string key) => _version < 3 ? VectorOf(rest, key) / 255 : VectorOf(rest, key);

        /// <summary>An 8-digit hexadecimal item id, as LightWave 8 and later put before names and paths.</summary>
        private static bool IsItemId(string token) =>
            token.Length == 8 && token.All(Uri.IsHexDigit);

        /// <summary>Drops a leading item id from the rest of a line.</summary>
        private static string WithoutId(string rest)
        {
            var (first, after) = Split(rest);
            return IsItemId(first) && after.Length > 0 ? after : IsItemId(first) ? "" : rest;
        }

        public void Run()
        {
            _in = new LwText(text, Warn);
            if (Next() != "LWSC") throw new InvalidDataException("Not a LightWave scene: it doesn't start with LWSC.");
            var v = Next();
            if (v == null || !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _version))
                throw new InvalidDataException("The LWSC header has no version number.");
            if (_version is not (1 or 3)) Warn($"Scene version {_version} is not 1 or 3; reading it as best it can.");

            for (var line = Next(); line != null; line = Next()) Line(line);
            Finish();
        }

        private void Line(string line)
        {
            if (line.StartsWith('{'))
            {
                SkipBlock(line);
                return;
            }
            var (key, rest) = Split(line);
            switch (key)
            {
                case "FirstFrame": Scene.FirstFrame = Int(rest, key); break;
                case "LastFrame": Scene.LastFrame = Int(rest, key); break;
                case "FrameStep": Scene.FrameStep = Math.Max(1, Int(rest, key)); break;
                case "FramesPerSecond": Scene.FramesPerSecond = Math.Max(1e-6, Number(rest, key)); break;

                case "LoadObject":
                    LoadObject(null, WithoutId(rest));
                    break;
                case "LoadObjectLayer":
                {
                    var (layer, path) = Split(rest);
                    LoadObject(TryNumber(layer, out double n) ? (int)n : 1, WithoutId(path));
                    break;
                }
                case "AddNullObject":
                {
                    string name = WithoutId(rest);
                    var n = Scene.AddNull(name.Length > 0 ? name : "Null");
                    _objects.Add(n);
                    _current = n;
                    break;
                }
                case "AddBone":
                    if (_lastObject == null)
                    {
                        Warn($"Line {_pos}: a bone comes before any object; it is skipped.");
                        _current = null;
                        break;
                    }
                    _current = Scene.Add(new Bone($"Bone {_lastObject.Bones.Count + 1}", _lastObject));
                    break;
                case "AddLight":
                {
                    var l = Scene.AddLight("Light");
                    _lights.Add(l);
                    _current = l;
                    break;
                }
                case "AddCamera":
                    _current = NewCamera();
                    break;
                case "ShowCamera" when _version < 3:
                    _current = _cameras.Count > 0 ? _cameras[0] : NewCamera();
                    break;

                case "LightName" or "CameraName" or "BoneName" when _current != null:
                    _current.Name = rest;
                    break;

                case "ObjectMotion" or "LightMotion" or "CameraMotion" or "BoneMotion":
                    if (key == "CameraMotion" && _current is not CameraItem)
                        _current = _cameras.Count > 0 && _version < 3 ? _cameras[0] : NewCamera();
                    Motion(_current);
                    break;
                case "EndBehavior":
                {
                    var b = Int(rest, key) switch { 0 => EndBehavior.Reset, 2 => EndBehavior.Repeat, _ => EndBehavior.Constant };
                    foreach (var e in _lastEnvelopes) e.PostBehavior = b;
                    break;
                }
                case "ParentObject" when _current != null:
                    _parents.Add((_current, _current is Bone ? "bone" : "object", Int(rest, key), _pos));
                    break;
                case "ParentItem" when _current != null:
                    if (long.TryParse(Tokens(rest).FirstOrDefault(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long id))
                        _parents.Add((_current, "item", id, _pos));
                    else Warn($"Line {_pos}: ParentItem needs a hexadecimal item number.");
                    break;
                case "PivotPoint" or "PivotPosition" when _current != null:
                    _current.Pivot = VectorOf(rest, key);
                    break;

                case "ObjDissolve" or "ObjectDissolve" when _current is ObjectItem o:
                    Assign(o.Dissolve, ValueOrEnvelope(rest, key));
                    break;

                case "BGImage": _compositing = _compositing with { Background = Image(rest) }; break;
                case "FGImage": _compositing = _compositing with { Foreground = Image(rest) }; break;
                case "FGAlphaImage": _compositing = _compositing with { ForegroundAlpha = Image(rest) }; break;
                case "ForegroundKey": _foregroundKey = Int(rest, key) != 0; break;
                case "LowClipColor": _compositing = _compositing with { KeyLow = Clip(ColorOf(rest, key)) }; break;
                case "HighClipColor": _compositing = _compositing with { KeyHigh = Clip(ColorOf(rest, key)) }; break;
                case "FGDissolve" or "ForegroundDissolve": _compositing = _compositing with { ForegroundDissolve = Math.Clamp(First(ValueOrEnvelope(rest, key)), 0, 1) }; break;

                case "ShadowOptions" when _current is ObjectItem o:
                {
                    int bits = Int(rest, key);
                    o.CastShadows = (bits & 2) != 0;
                    o.ReceiveShadows = (bits & 4) != 0;
                    break;
                }
                case "ShadowType" when _current is LightItem l:
                {
                    int t = Int(rest, key);
                    l.Shadows = t != 0;
                    l.ShadowMap = l.ShadowMap with { On = t == 2 };
                    break;
                }
                case "ShadowOptions" or "ShadowCasting" when _current is LightItem l:
                    l.Shadows = Int(rest, key) != 0;
                    break;
                case "ShadowMapSize" when _current is LightItem l:
                {
                    int n = Int(rest, key);
                    if (n < LightEffects.MinMapSize || n > LightEffects.MaxMapSize)
                        Warn($"Line {_pos}: shadow map size {n} is read as {Math.Clamp(n, LightEffects.MinMapSize, LightEffects.MaxMapSize)} (sizes are {LightEffects.MinMapSize} to {LightEffects.MaxMapSize}).");
                    l.ShadowMap = l.ShadowMap with { Size = Math.Clamp(n, LightEffects.MinMapSize, LightEffects.MaxMapSize) };
                    break;
                }
                case "ShadowFuzziness" or "ShadowMapFuzziness" when _current is LightItem l:
                    l.ShadowMap = l.ShadowMap with { Fuzziness = Math.Clamp(First(ValueOrEnvelope(rest, key)), 0, LightEffects.MaxFuzziness) };
                    break;
                case "LensFlare" when _current is LightItem l:
                    l.Flare = l.Flare with { On = Int(rest, key) != 0 };
                    break;
                case "FlareIntensity" when _current is LightItem l:
                    Assign(l.FlareIntensity, ValueOrEnvelope(rest, key));
                    break;
                case "FlareNominalDistance" when _current is LightItem l:
                {
                    double d = First(ValueOrEnvelope(rest, key));
                    if (d > 0 && double.IsFinite(d)) l.Flare = l.Flare with { NominalDistance = d };
                    break;
                }
                case "FlareDissolve" when _current is LightItem l:
                    l.Flare = l.Flare with { Dissolve = Math.Clamp(First(ValueOrEnvelope(rest, key)), 0, 1) };
                    break;
                case "LensFlareFade" when _current is LightItem l:
                    l.Flare = LwsLightBits.ReadFade(l.Flare, Int(rest, key));
                    break;
                case "LensFlareOptions" when _current is LightItem l:
                    l.Flare = LwsLightBits.ReadOptions(l.Flare, Int(rest, key));
                    break;

                case "LightType" when _current is LightItem l:
                {
                    int t = Int(rest, key);
                    if (t is < 0 or > 2 and not 4) Warn($"Line {_pos}: light type {t} isn't distant, point, spot or area; it is read as a point light.");
                    l.Type = t is >= 0 and <= 2 or 4 ? (LightType)t : LightType.Point;
                    break;
                }
                case "LightColor" when _current is LightItem l: l.Color = ColorOf(rest, key); break;
                case "LgtIntensity" or "LightIntensity" when _current is LightItem l:
                    Assign(l.Intensity, ValueOrEnvelope(rest, key));
                    break;
                case "ConeAngle" or "LightConeAngle" when _current is LightItem l:
                    l.ConeAngle = First(ValueOrEnvelope(rest, key));
                    break;
                case "EdgeAngle" or "LightEdgeAngle" when _current is LightItem l:
                    l.EdgeAngle = First(ValueOrEnvelope(rest, key));
                    break;
                case "LightFalloffType" when _current is LightItem l:
                    _falloff[l] = _falloff.GetValueOrDefault(l) with { Type = Int(rest, key) };
                    break;
                case "LightRange" when _current is LightItem l:
                    _falloff[l] = _falloff.GetValueOrDefault(l) with { Range = First(ValueOrEnvelope(rest, key)) };
                    break;
                case "LightFalloff" when _current is LightItem l:
                    _falloff[l] = _falloff.GetValueOrDefault(l) with { Legacy = First(ValueOrEnvelope(rest, key)) };
                    break;

                case "ZoomFactor":
                    Assign((_current as CameraItem ?? CurrentOrNewCamera()).Zoom, ValueOrEnvelope(rest, key));
                    break;
                case "FrameSize" or "CustomSize":
                {
                    var n = Numbers(rest, 2, key);
                    var cam = _current as CameraItem ?? CurrentOrNewCamera();
                    if (n[0] >= 1 && n[1] >= 1) (cam.Width, cam.Height) = ((int)n[0], (int)n[1]);
                    break;
                }
                case "ResolutionMultiplier": _resolutionMultiplier = Number(rest, key); break;
                case "PixelAspect" or "PixelAspectRatio":
                {
                    double a = Number(rest, key);
                    if (a > 0) (_current as CameraItem ?? CurrentOrNewCamera()).PixelAspect = a;
                    break;
                }
                case "CurrentCamera": _currentCamera = Int(rest, key); break;
                case "MotionBlur" when _current is CameraItem c: c.MotionBlur = c.MotionBlur with { On = Int(rest, key) != 0 }; break;
                case "BlurLength" when _current is CameraItem c:
                {
                    double b = First(ValueOrEnvelope(rest, key));
                    if (b > 0 && b <= 1) c.MotionBlur = c.MotionBlur with { Shutter = b };
                    break;
                }
                case "DepthOfField" or "DepthofField" when _current is CameraItem c: c.DepthOfField = c.DepthOfField with { On = Int(rest, key) != 0 }; break;
                case "FocalDistance" when _current is CameraItem c:
                {
                    double f = First(ValueOrEnvelope(rest, key));
                    if (f > 0) c.DepthOfField = c.DepthOfField with { FocalDistance = f };
                    break;
                }
                case "LensFStop" when _current is CameraItem c:
                {
                    double f = First(ValueOrEnvelope(rest, key));
                    if (f > 0) c.DepthOfField = c.DepthOfField with { FStop = f };
                    break;
                }

                case "AmbientColor": Scene.AmbientColor = ColorOf(rest, key); break;
                case "AmbIntensity" or "AmbientIntensity": Scene.AmbientIntensity = First(ValueOrEnvelope(rest, key)); break;
                case "BackdropColor": Scene.BackgroundColor = ColorOf(rest, key); break;
                case "ZenithColor": Scene.GradientTop = ColorOf(rest, key); break;
                case "NadirColor": Scene.GradientBottom = ColorOf(rest, key); break;
                case "SolidBackdrop": Scene.GradientBackground = Int(rest, key) == 0; break;
                case "FogType":
                    Scene.Fog = Int(rest, key) == 0 ? FogType.Off : FogType.Linear;
                    break;
                case "FogMinDist" or "FogMinDistance": Scene.FogMinDistance = First(ValueOrEnvelope(rest, key)); break;
                case "FogMaxDist" or "FogMaxDistance": Scene.FogMaxDistance = First(ValueOrEnvelope(rest, key)); break;
                case "FogColor": Scene.FogColor = ColorOf(rest, key); break;

                case "BoneRestPosition" when _current is Bone b: b.RestPosition = VectorOf(rest, key); break;
                case "BoneRestDirection" when _current is Bone b: b.RestRotation = VectorOf(rest, key); break;
                case "BoneRestLength" when _current is Bone b: b.RestLength = Number(rest, key); break;
                case "BoneStrength" when _current is Bone b: b.Strength = First(ValueOrEnvelope(rest, key)); break;
                case "BoneActive" when _current is Bone b: b.Active = Int(rest, key) != 0; break;
                case "BoneFalloffType" when _lastObject != null:
                    _lastObject.BoneFalloffExponent = Math.Pow(2, Int(rest, key));
                    break;

                case "GoalObject" when _current is Bone b:
                    _goals.Add((b, "object", Int(rest, key), _pos));
                    break;
                case "GoalItem" when _current is Bone b:
                    if (long.TryParse(Tokens(rest).FirstOrDefault(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long goalId))
                        _goals.Add((b, "item", goalId, _pos));
                    else Warn($"Line {_pos}: GoalItem needs a hexadecimal item number.");
                    break;
                case "GoalObject" or "GoalItem" when _current != null:
                    Warn($"{_current.Name} has an IK goal: IK is read for bones only, so it is left out.");
                    break;
                case "FullTimeIK" when _current is Bone b:
                    b.FullTimeIK = Int(rest, key) != 0;
                    _fullTimeRead.Add(b);
                    break;
                case "HLimits" or "PLimits" or "BLimits" when _current is Bone b:
                {
                    var n = Numbers(rest, 2, key);
                    int channel = key[0] switch { 'H' => 0, 'P' => 1, _ => 2 };
                    // LightWave writes 0 0 for a channel without limits.
                    if (n[0] != 0 || n[1] != 0)
                        b.IKLimits = b.IKLimits.With(channel, new IKLimit(Math.Min(n[0], n[1]), Math.Max(n[0], n[1])));
                    break;
                }

                case "Plugin":
                    SkipUntil("EndPlugin");
                    break;
                default:
                    // Anything else is a setting this reader doesn't use; a following envelope belongs to it.
                    if (rest == "(envelope)") ReadEnvelope(key);
                    break;
            }
        }

        private CompositingSettings _compositing = new();
        private bool _foregroundKey;

        private static Vec3 Clip(Vec3 c) => new(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1));

        /// <summary>
        /// A compositing image's line: its path, with "(sequence)" after it for a numbered sequence (as LightWave marked
        /// them). The image goes in the scene's list (once) and its full path is returned. A sequence named by its
        /// prefix alone ("Images/fire (sequence)") becomes the first numbered file found beside it.
        /// </summary>
        private string Image(string rest)
        {
            string raw = WithoutId(rest).Trim();
            bool sequence = raw.EndsWith("(sequence)", StringComparison.OrdinalIgnoreCase);
            if (sequence) raw = raw[..^"(sequence)".Length].Trim();
            raw = raw.Trim('"');
            if (raw.Length == 0) return "";
            string path = ResolvePath(raw);
            if (sequence && ImageSequence.Pattern(path) == null && FirstNumbered(path) is { } first) path = first;
            if (sequence && ImageSequence.Pattern(path) == null)
            {
                Warn($"Line {_pos}: the image sequence {raw} has no numbered files beside it; it is read as a still.");
                sequence = false;
            }
            if (SceneImages.Find(Scene, path) is { } had) return had.Path;
            Scene.Images = [.. Scene.Images, new SceneImage { Path = path, Sequence = sequence }];
            return path;
        }

        /// <summary>The first file beside a prefix ("…/fire") whose name is the prefix, digits and an extension.</summary>
        private static string? FirstNumbered(string prefix)
        {
            try
            {
                string? folder = Path.GetDirectoryName(Path.GetFullPath(prefix));
                string name = Path.GetFileName(prefix);
                if (folder == null || !Directory.Exists(folder)) return null;
                return Directory.EnumerateFiles(folder, name + "*")
                    .Where(f => Path.GetFileNameWithoutExtension(f) is var stem && stem.Length > name.Length && stem[name.Length..].All(char.IsAsciiDigit))
                    .Order(StringComparer.Ordinal).FirstOrDefault();
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
        }

        private CameraItem NewCamera()
        {
            var c = Scene.AddCamera("Camera");
            _cameras.Add(c);
            return c;
        }

        private CameraItem CurrentOrNewCamera() => _cameras.Count > 0 ? _cameras[^1] : NewCamera();

        private static double First(Envelope e) => e.Keys.Count > 0 ? e.Keys[0].Value : 0;

        private void SkipBlock(string opening)
        {
            int depth = opening.Count(c => c == '{') - opening.Count(c => c == '}');
            while (depth > 0 && Next() is { } l) depth += l.Count(c => c == '{') - l.Count(c => c == '}');
        }

        private void SkipUntil(string end)
        {
            for (var l = Next(); l != null && l != end; l = Next()) { }
        }

        private void LoadObject(int? layer, string rawPath)
        {
            string path = ResolvePath(rawPath);
            if (!_documents.TryGetValue(path, out var doc))
            {
                try
                {
                    doc = load(path);
                    if (doc == null) Warn($"Couldn't load object {rawPath}; a placeholder stands in for it.");
                }
                catch (Exception e)
                {
                    Warn($"Couldn't load object {rawPath} ({e.Message}); a placeholder stands in for it.");
                    doc = null;
                }
                _documents[path] = doc;
            }
            string name = Path.GetFileNameWithoutExtension(rawPath.Replace('\\', '/'));
            int? index = null;
            if (layer is int n)
            {
                index = n - 1;
                if (doc != null && (index < 0 || index >= doc.Layers.Count))
                {
                    Warn($"{rawPath} has no layer {n}; all its layers are used.");
                    index = null;
                }
                if (n > 1) name += $":{n}";
            }
            var o = Scene.AddObject(name, doc, index);
            o.SourcePath = path;
            _objects.Add(o);
            _current = _lastObject = o;
        }

        private string ResolvePath(string raw)
        {
            string p = raw.Trim().Trim('"').Replace('\\', '/');
            var bases = new[] { contentDir, sceneDir }.Where(b => !string.IsNullOrEmpty(b)).Cast<string>().ToList();
            var candidates = new List<string>();
            if (Path.IsPathRooted(p)) candidates.Add(p);
            // The path against each base, then shorter and shorter tails of it: a path from another machine (or an
            // Amiga volume such as "Objects:Ship.lwo") can still find the file under this content directory.
            var parts = p.Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                foreach (var b in bases) candidates.Add(Path.Combine([b, .. parts[i..]]));
            return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault() ?? p;
        }

        /// <summary>A number on the line, or the envelope that follows it when the line says "(envelope)".</summary>
        private Envelope ValueOrEnvelope(string rest, string key) =>
            rest == "(envelope)" ? ReadEnvelope(key) : new Envelope(Number(rest, key));

        /// <summary>A single-channel envelope in either version's form.</summary>
        private Envelope ReadEnvelope(string key)
        {
            if (Peek()?.StartsWith('{') == true) return V3Envelope();
            var e = V1Envelopes(key);
            return e.Length > 0 ? e[0] : new Envelope();
        }

        /// <summary>Copies a parsed envelope into an item's own, noting it for conversion if its times are in seconds.</summary>
        private void Assign(Envelope target, Envelope source)
        {
            target.CopyFrom(source);
            if (_readInSeconds.Contains(source)) _inSeconds.Add(target);
            // A version 1 EndBehavior line after the envelope applies to it.
            _lastEnvelopes = [target];
        }

        private void Motion(SceneItem? item)
        {
            if (item == null) Warn($"Line {_pos}: a motion with no item to belong to is skipped.");
            if (Peek()?.StartsWith("NumChannels") == true)
            {
                Next();
                while (Peek()?.StartsWith("Channel ") == true)
                {
                    var (_, n) = Split(Next()!);
                    var e = V3Envelope();
                    if (item != null && TryNumber(n, out double c) && c is >= 0 and < 9) Assign(item.Channels[(int)c], e);
                }
            }
            else
            {
                var channels = V1Envelopes("motion");
                if (item != null)
                    for (int c = 0; c < Math.Min(9, channels.Length); c++) Assign(item.Channels[c], channels[c]);
            }
            _lastEnvelopes = item?.Channels.ToArray() ?? [];
        }

        /// <summary>A version 1 envelope: channel count, key count, then per key a line of values and a line of "frame linear tension continuity bias".</summary>
        private Envelope[] V1Envelopes(string key)
        {
            var envelopes = _in.V1Envelopes(out bool cut);
            if (envelopes == null || cut) Warn($"Line {_pos}: {key} envelope is cut short.");
            if (envelopes == null) return [];
            _lastEnvelopes = envelopes;
            return envelopes;
        }

        /// <summary>A version 3 envelope block, its times in seconds (converted to frames at the end).</summary>
        private Envelope V3Envelope()
        {
            var e = _in.V3Envelope();
            _readInSeconds.Add(e);
            return e;
        }

        private void Finish()
        {
            foreach (var e in _inSeconds) LwText.SecondsToFrames(e, Scene.FramesPerSecond);

            foreach (var (item, kind, value, line) in _parents)
            {
                SceneItem? parent = kind switch
                {
                    "object" => At(_objects, (int)value - 1),
                    "bone" => item is Bone b ? At(b.Owner.Bones, (int)value - 1) : null,
                    _ => ItemById(value),
                };
                if (parent == null)
                {
                    Warn($"Line {line}: {item.Name}'s parent ({value:X}) isn't in the scene.");
                    continue;
                }
                try
                {
                    item.Parent = parent;
                }
                catch (InvalidOperationException e)
                {
                    Warn($"Line {line}: {e.Message}");
                }
            }

            foreach (var (bone, kind, value, line) in _goals)
            {
                var goal = kind == "object" ? At(_objects, (int)value - 1) : ItemById(value);
                if (goal == null)
                {
                    Warn($"Line {line}: {bone.Name}'s IK goal ({value:X}) isn't in the scene.");
                    continue;
                }
                if (InverseKinematics.GoalProblem(bone, goal) is { } problem)
                {
                    Warn($"Line {line}: {problem}");
                    continue;
                }
                bone.IKGoal = goal;
                // LightWave's Full-time IK is off unless the file says so.
                if (!_fullTimeRead.Contains(bone)) bone.FullTimeIK = false;
            }

            if (_resolutionMultiplier > 0 && _resolutionMultiplier != 1)
                foreach (var c in _cameras)
                {
                    c.Width = Math.Max(1, (int)Math.Round(c.Width * _resolutionMultiplier));
                    c.Height = Math.Max(1, (int)Math.Round(c.Height * _resolutionMultiplier));
                }

            foreach (var (light, (type, range, legacy)) in _falloff)
                light.Range = type > 0 ? range : type == null && legacy > 0 ? 1 / legacy : 0;

            if (_currentCamera is int i && At(_cameras, i) is { } cam) Scene.CurrentCamera = cam;

            if (_compositing.Foreground.Length > 0)
            {
                // An alpha image keys by alpha (one that is the foreground itself: by its own alpha); else ForegroundKey keys by colour.
                if (_compositing.ForegroundAlpha.Length > 0)
                    _compositing = _compositing with
                    {
                        Key = ForegroundKey.Alpha,
                        ForegroundAlpha = SceneImages.Same(_compositing.ForegroundAlpha, _compositing.Foreground) ? "" : _compositing.ForegroundAlpha,
                    };
                else if (_foregroundKey) _compositing = _compositing with { Key = ForegroundKey.Color };
            }
            else _compositing = _compositing with { ForegroundAlpha = "" };
            try
            {
                Scene.Compositing = _compositing.Checked();
            }
            catch (ArgumentException e)
            {
                Warn($"The compositing settings couldn't be used ({e.Message}); they're left off.");
            }
        }

        private static T? At<T>(IReadOnlyList<T> list, int i) where T : class => i >= 0 && i < list.Count ? list[i] : null;

        /// <summary>An item from a version 3 item number: type in the top hex digit, then the index (bones: 4BBBOOOO).</summary>
        private SceneItem? ItemById(long id)
        {
            int type = (int)(id >> 28) & 0xF;
            int index = (int)(id & 0x0FFFFFFF);
            return type switch
            {
                1 => At(_objects, index),
                2 => At(_lights, index),
                3 => At(_cameras, index),
                4 => At(_objects, (int)(id & 0xFFFF)) is ObjectItem o ? At(o.Bones, (int)(id >> 16) & 0xFFF) : null,
                _ => null,
            };
        }
    }
}
