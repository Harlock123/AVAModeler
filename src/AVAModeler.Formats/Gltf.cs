using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;

namespace AVAModeler.Formats;

/// <summary>
/// glTF 2.0 objects, as .glb (binary, one file) or .gltf (JSON, with its data inside it or in a .bin beside it).
/// glTF's space is right-handed with +Y up and faces wound anticlockwise; LightWave's is left-handed with them
/// clockwise, so Z is negated and each triangle's winding reversed both ways, and a model faces the same way in both.
/// </summary>
public static class Gltf
{
    /// <summary>The UV map that TEXCOORD_0 goes into when a glTF file is read.</summary>
    public const string UVMapName = Obj.UVMapName;
    /// <summary>The colour map COLOR_0 goes into when a glTF file is read (and the surfaces using it take it as their vertex colours).</summary>
    public const string ColorMapName = "Color";

    /// <summary>
    /// Writes an object: a node and a mesh for each layer with faces (named after the layer, or "Layer n"), one
    /// primitive per surface, triangulated, with normals that follow the surfaces' smoothing, a PBR material per
    /// surface and its colour image (see <see cref="GltfBuilder.AddMesh"/>). Points and 2-point lines are left out.
    /// Returns the .glb or .gltf file's bytes and, for <see cref="GltfPacking.Separate"/>, the .bin's, with any warnings.
    /// Relative image paths are found from <paramref name="imageBase"/> (the document's own folder if not given).
    /// </summary>
    public static (byte[] Main, byte[]? Bin, List<string> Warnings) Write(Document doc, GltfPacking packing, string binName = "buffer.bin", string? imageBase = null)
    {
        imageBase ??= doc.ImageBase;
        var builder = new GltfBuilder();
        var nodes = new List<int>();
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            string name = doc.Layers[i].Name.Length > 0 ? doc.Layers[i].Name : $"Layer {i + 1}";
            int mesh = builder.AddMesh(name, doc.Layers[i].Mesh, doc.Surfaces, imageBase);
            if (mesh >= 0) nodes.Add(builder.AddNode(new JsonObject { ["name"] = name, ["mesh"] = mesh }));
        }
        var (main, bin) = builder.Build(nodes, packing, binName);
        return (main, bin, builder.Warnings);
    }

    /// <summary>
    /// Reads a .glb or .gltf file's bytes. <paramref name="folder"/> is where its outside files (.bin and images) are
    /// looked for, and <paramref name="name"/> names images that were inside it (see <see cref="Read(byte[], string?, string)"/>).
    /// </summary>
    public static Document Read(string path)
    {
        var data = File.ReadAllBytes(path);
        return Read(data, Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Reads a glTF 2.0 asset (.glb or .gltf). The default scene's nodes are walked with their transforms; each node
    /// with a mesh becomes a layer (named after the node or its mesh), adding layers past the usual ten as needed. Its
    /// triangles (and triangle strips and fans) become polygons, with points at the same place merged; TEXCOORD_0
    /// goes into the UV map <see cref="UVMapName"/>. Each material becomes a surface: its base colour (and alpha, as
    /// transparency), emission as luminosity, roughness as glossiness, double-sidedness, and its base colour texture as
    /// a UV-mapped colour image. An image in a file beside it is referred to; one inside the asset is decoded and kept
    /// in memory (<see cref="TextureImages.Put"/>) under a made-up path in <paramref name="folder"/>. Points, lines,
    /// normals (a surface is made smooth where the file's normals bend across its faces, and flat otherwise), other texture channels, skins, morph targets, animations, cameras
    /// and lights aren't read.
    /// </summary>
    public static Document Read(byte[] data, string? folder, string name)
    {
        var (json, bin) = Split(data);
        JsonNode root;
        try
        {
            root = JsonNode.Parse(json) ?? throw new FormatException("The glTF file's JSON is empty.");
        }
        catch (JsonException e)
        {
            throw new FormatException($"The glTF file's JSON can't be read: {e.Message}");
        }
        var reader = new Reader(root, bin, folder, name);
        return reader.Document();
    }

    /// <summary>The JSON text and the binary chunk (null in a .gltf) of a glTF file.</summary>
    private static (string Json, byte[]? Bin) Split(byte[] data)
    {
        if (data.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46546C67)
            return (Encoding.UTF8.GetString(data).TrimStart('﻿'), null);
        if (data.Length < 20) throw new FormatException("The .glb file is too short for its header.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (version != 2) throw new FormatException($"The .glb file is glTF version {version}; only version 2 is read.");
        long length = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)), data.Length);
        string? json = null;
        byte[]? bin = null;
        long at = 12;
        while (at + 8 <= length)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at + 4));
            if (at + 8 + chunkLength > length) throw new FormatException("A chunk of the .glb file runs past its end.");
            var body = data.AsSpan((int)at + 8, (int)chunkLength);
            if (type == 0x4E4F534A && json == null) json = Encoding.UTF8.GetString(body);
            else if (type == 0x004E4942 && bin == null) bin = body.ToArray();
            at += 8 + chunkLength;
        }
        return (json ?? throw new FormatException("The .glb file has no JSON chunk."), bin);
    }

    private sealed class Reader(JsonNode root, byte[]? glbBin, string? folder, string name)
    {
        private readonly Dictionary<int, byte[]> _buffers = [];
        private readonly Document _doc = new() { Surfaces = [] };
        private readonly Dictionary<int, int> _surfaceOf = [];
        private readonly HashSet<int> _smooth = [];
        private int _layers;

        private JsonArray Array(string key) => root[key] as JsonArray ?? [];

        private static int Int(JsonNode? n, int fallback = -1) => n is JsonValue v && v.TryGetValue(out double d) ? (int)d : fallback;

        private static double Num(JsonNode? n, double fallback) => n is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

        private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        public Document Document()
        {
            var scenes = Array("scenes");
            int sceneIndex = Int(root["scene"], 0);
            IEnumerable<int> top;
            if (sceneIndex >= 0 && sceneIndex < scenes.Count && scenes[sceneIndex]?["nodes"] is JsonArray sceneNodes)
                top = sceneNodes.Select(n => Int(n));
            else
            {
                // No scene: every node that isn't a child of another.
                var children = Array("nodes").SelectMany(n => (n?["children"] as JsonArray ?? []).Select(c => Int(c))).ToHashSet();
                top = Enumerable.Range(0, Array("nodes").Count).Where(i => !children.Contains(i));
            }
            var visited = new HashSet<int>();
            foreach (int n in top) Visit(n, Mat4.Identity, visited);
            foreach (var (layer, skin, targets) in _skinned) AddSkelegons(layer, skin, targets);
            // A surface is smooth where the file's normals bend across its faces; without normals glTF shades flat.
            for (int i = 0; i < _doc.Surfaces.Count; i++) _doc.Surfaces[i].Smoothing = _smooth.Contains(i);
            if (_doc.Surfaces.Count == 0) _doc.Surfaces.Add(new Surface("Default"));
            Loading.ShowFirstLayer(_doc);
            return _doc;
        }

        private void Visit(int index, Mat4 parent, HashSet<int> visited)
        {
            var nodes = Array("nodes");
            if (index < 0 || index >= nodes.Count || !visited.Add(index)) return;
            var node = nodes[index]!;
            var world = parent * Local(node);
            _worldOf[index] = world;
            int mesh = Int(node["mesh"]);
            if (mesh >= 0) AddMesh(mesh, world, Str(node["name"]), Int(node["skin"]));
            foreach (var c in node["children"] as JsonArray ?? []) Visit(Int(c), world, visited);
        }

        /// <summary>A node's transform in glTF's space: its matrix (column by column), or translation · rotation · scale.</summary>
        private static Mat4 Local(JsonNode node)
        {
            if (node["matrix"] is JsonArray { Count: 16 } m)
            {
                var v = m.Select(x => Num(x, 0)).ToArray();
                return Mat4.FromRows(v[0], v[4], v[8], v[12], v[1], v[5], v[9], v[13], v[2], v[6], v[10], v[14], v[3], v[7], v[11], v[15]);
            }
            var t = node["translation"] is JsonArray { Count: 3 } ta ? new Vec3(Num(ta[0], 0), Num(ta[1], 0), Num(ta[2], 0)) : Vec3.Zero;
            var s = node["scale"] is JsonArray { Count: 3 } sa ? new Vec3(Num(sa[0], 1), Num(sa[1], 1), Num(sa[2], 1)) : Vec3.One;
            var r = Mat4.Identity;
            if (node["rotation"] is JsonArray { Count: 4 } q)
                r = Rotation(Num(q[0], 0), Num(q[1], 0), Num(q[2], 0), Num(q[3], 1));
            return Mat4.Translation(t) * r * Mat4.Scaling(s);
        }

        private static Mat4 Rotation(double x, double y, double z, double w)
        {
            double len = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (len == 0) return Mat4.Identity;
            (x, y, z, w) = (x / len, y / len, z / len, w / len);
            return Mat4.FromRows(
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0,
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0,
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0,
                0, 0, 0, 1);
        }

        private readonly Dictionary<int, Mat4> _worldOf = [];
        private readonly List<(Layer Layer, int Skin, List<Layer> Targets)> _skinned = [];

        /// <summary>A joint's name for its skelegon and weight map: the node's name, or "Joint n".</summary>
        private string JointName(int node)
        {
            var nodes = Array("nodes");
            string n = node >= 0 && node < nodes.Count && Str(nodes[node]?["name"]) is { Length: > 0 } s ? s : $"Joint {node + 1}";
            return n.Trim(' ', '_', '-', '.', ':') is { Length: > 0 } t ? t : $"Joint {node + 1}";
        }

        /// <summary>
        /// A skin's joints as skelegons in the skinned mesh's layer: each from its joint to its first child joint (or,
        /// at the end of a chain, on half as far again), on a surface "Skelegon_" and its name, so Layout's bones made
        /// from them follow the weight maps of their names.
        /// </summary>
        private void AddSkelegons(Layer layer, int skin, List<Layer> targets)
        {
            var skins = Array("skins");
            if (skin < 0 || skin >= skins.Count) return;
            var joints = (skins[skin]?["joints"] as JsonArray ?? []).Select(j => Int(j)).Where(j => j >= 0).ToList();
            var set = joints.ToHashSet();
            var nodes = Array("nodes");
            Vec3 Head(int j) => GltfBuilder.FromGltf(_worldOf.TryGetValue(j, out var w) ? w.TransformPoint(Vec3.Zero) : Vec3.Zero);
            var parentOf = new Dictionary<int, int>();
            foreach (int j in joints)
                foreach (var c in nodes[j]?["children"] as JsonArray ?? [])
                    if (set.Contains(Int(c))) parentOf.TryAdd(Int(c), j);
            var mesh = layer.Mesh;
            foreach (int j in joints)
            {
                var head = Head(j);
                int child = (nodes[j]?["children"] as JsonArray ?? []).Select(c => Int(c)).FirstOrDefault(set.Contains, -1);
                Vec3 tail;
                if (child >= 0 && (Head(child) - head).LengthSquared > 1e-18) tail = Head(child);
                else if (parentOf.TryGetValue(j, out int parent) && (head - Head(parent)).LengthSquared > 1e-18) tail = head + (head - Head(parent)) * 0.5;
                else tail = head + new Vec3(0, 0.1, 0);
                int surface = _doc.SurfaceIndex(SkelegonNames.SurfacePrefix + "_" + JointName(j));
                // The morph targets get them too, so they keep the base's points one for one.
                foreach (var m in targets.Select(t => t.Mesh).Prepend(mesh)) m.AddPolygon([m.AddPoint(head), m.AddPoint(tail)], surface);
            }
        }

        private void AddMesh(int index, Mat4 world, string? nodeName, int skin = -1)
        {
            var meshes = Array("meshes");
            if (index >= meshes.Count) throw new FormatException($"A node uses mesh {index}, which doesn't exist.");
            var json = meshes[index]!;
            var layer = Loading.LayerAt(_doc, _layers++);
            layer.Name = nodeName ?? Str(json["name"]) ?? "";
            var mesh = layer.Mesh;
            var pointOf = new Dictionary<Vec3, int>();
            // A mirroring transform turns faces inside out; glTF says to reverse their winding then.
            bool mirrored = world.Determinant() < 0;
            var targetsOf = new List<(JsonArray Targets, int[] Points)>();
            var joints = skin >= 0 && skin < Array("skins").Count ? (Array("skins")[skin]?["joints"] as JsonArray ?? []).Select(j => Int(j)).ToList() : null;
            foreach (var primitive in json["primitives"] as JsonArray ?? [])
            {
                if (primitive == null) continue;
                int mode = Int(primitive["mode"], 4);
                if (mode is not (4 or 5 or 6)) continue;
                var attributes = primitive["attributes"];
                int positionAccessor = Int(attributes?["POSITION"]);
                if (positionAccessor < 0) continue;
                var positions = Accessor(positionAccessor, out int pc);
                if (pc != 3) throw new FormatException("POSITION must be a VEC3 accessor.");
                int count = positions.Length / 3;
                double[]? uvs = null;
                if (Int(attributes?["TEXCOORD_0"]) is int uvAccessor and >= 0)
                {
                    uvs = Accessor(uvAccessor, out int uc);
                    if (uc != 2 || uvs.Length / 2 != count) uvs = null;
                }
                int[] indices = Int(primitive["indices"]) is int ia and >= 0
                    ? [.. Accessor(ia, out _).Select(v => (int)v)]
                    : [.. Enumerable.Range(0, count)];
                double[]? colors = null;
                int colorSize = 0;
                if (Int(attributes?["COLOR_0"]) is int ca and >= 0)
                {
                    colors = Accessor(ca, out colorSize);
                    if (colorSize is not (3 or 4) || colors.Length / colorSize != count) colors = null;
                }
                double[]? normals = null;
                if (Int(attributes?["NORMAL"]) is int na and >= 0)
                {
                    normals = Accessor(na, out int nc);
                    if (nc != 3 || normals.Length != positions.Length) normals = null;
                }
                int surface = SurfaceFor(Int(primitive["material"]));
                var points = new int[count];
                for (int i = 0; i < count; i++)
                {
                    var p = GltfBuilder.FromGltf(world.TransformPoint(new Vec3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2])));
                    if (!pointOf.TryGetValue(p, out int at))
                    {
                        pointOf[p] = at = mesh.Points.Count;
                        mesh.Points.Add(p);
                    }
                    points[i] = at;
                }
                if (primitive["targets"] is JsonArray { Count: > 0 } targets) targetsOf.Add((targets, points));
                // A skin's weights: each vertex's four joints and how much each moves it, as one weight map per joint.
                if (joints != null && Int(attributes?["JOINTS_0"]) is int ja and >= 0 && Int(attributes?["WEIGHTS_0"]) is int wa and >= 0)
                {
                    var jv = Accessor(ja, out int jc);
                    var wv = Accessor(wa, out int wc);
                    if (jc == 4 && wc == 4 && jv.Length == count * 4 && wv.Length == count * 4)
                        for (int i = 0; i < count; i++)
                            for (int k = 0; k < 4; k++)
                            {
                                double weight = wv[i * 4 + k];
                                int joint = (int)jv[i * 4 + k];
                                if (weight <= 0 || joint < 0 || joint >= joints.Count) continue;
                                string mapName = JointName(joints[joint]);
                                var map = mesh.FindPointMap(mapName, PointMapKind.Weight) ?? mesh.AddPointMap(mapName, PointMapKind.Weight);
                                map.SetWeight(points[i], Math.Clamp(weight, 0, 1));
                            }
                }
                if (colors != null)
                {
                    // glTF's vertex colours are linear; the map keeps them as they are (0 to 1), and the surface multiplies them in.
                    var map = mesh.FindPointMap(ColorMapName, PointMapKind.Rgb) ?? mesh.AddPointMap(ColorMapName, PointMapKind.Rgb);
                    for (int i = 0; i < count; i++)
                        map.SetColor(points[i], new Vec3(colors[i * colorSize], colors[i * colorSize + 1], colors[i * colorSize + 2]));
                    var s = _doc.Surfaces[surface];
                    s.VertexColorMap = ColorMapName;
                }
                foreach (var (a, b, c) in Triangles(indices, mode))
                {
                    if (a < 0 || b < 0 || c < 0 || a >= count || b >= count || c >= count)
                        throw new FormatException("A triangle's index is past the end of its vertices.");
                    if (normals != null && !_smooth.Contains(surface) && Bends(positions, normals, a, b, c)) _smooth.Add(surface);
                    // Back to LightWave's clockwise winding: reversed, unless the node mirrors it already.
                    int[] corners = mirrored ? [a, b, c] : [a, c, b];
                    if (points[corners[0]] == points[corners[1]] || points[corners[1]] == points[corners[2]] || points[corners[0]] == points[corners[2]]) continue;
                    var poly = new Polygon(corners.Select(k => points[k]), surface);
                    mesh.Polygons.Add(poly);
                    if (uvs != null)
                        foreach (int k in corners)
                            mesh.SetCornerUV(UVMapName, poly, points[k], new Vec2(uvs[k * 2], 1 - uvs[k * 2 + 1]));
                }
            }
            mesh.Touch();
            var targetLayers = new List<Layer>();
            if (joints != null) _skinned.Add((layer, skin, targetLayers));
            // Morph targets: each a layer of its own (named as the file names it), the base's points moved by its
            // offsets, point for point, ready to be a morph target in Layout.
            if (targetsOf.Count > 0)
            {
                var names = json["extras"]?["targetNames"] as JsonArray;
                int targets = targetsOf.Max(t => t.Targets.Count);
                for (int k = 0; k < targets; k++)
                {
                    var target = Loading.LayerAt(_doc, _layers++);
                    targetLayers.Add(target);
                    target.Name = names != null && k < names.Count && Str(names[k]) is { Length: > 0 } tn ? tn : $"{layer.Name} target {k + 1}";
                    var tm = target.Mesh;
                    tm.Points = [.. mesh.Points];
                    tm.Polygons = [.. mesh.Polygons.Select(p => p.Clone())];
                    foreach (var (list, pts) in targetsOf)
                    {
                        if (k >= list.Count || Int(list[k]?["POSITION"]) is not (int pa and >= 0)) continue;
                        var delta = Accessor(pa, out int dc);
                        if (dc != 3 || delta.Length / 3 != pts.Length) continue;
                        for (int i = 0; i < pts.Length; i++)
                        {
                            var d = GltfBuilder.FromGltf(world.TransformVector(new Vec3(delta[i * 3], delta[i * 3 + 1], delta[i * 3 + 2])));
                            tm.Points[pts[i]] = mesh.Points[pts[i]] + d;
                        }
                    }
                    tm.Touch();
                }
            }
        }

        /// <summary>Whether a triangle's vertex normals differ from its face's (by more than a degree): the file shades it smooth.</summary>
        private static bool Bends(double[] p, double[] n, int a, int b, int c)
        {
            Vec3 P(int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
            var face = Vec3.Cross(P(b) - P(a), P(c) - P(a)).Normalized();
            if (face.LengthSquared == 0) return false;
            foreach (int i in (int[])[a, b, c])
            {
                var v = new Vec3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]).Normalized();
                if (v.LengthSquared > 0 && Vec3.Dot(v, face) < 0.99985) return true;
            }
            return false;
        }

        private static IEnumerable<(int, int, int)> Triangles(int[] i, int mode)
        {
            switch (mode)
            {
                case 5:
                    for (int k = 0; k + 2 < i.Length; k++)
                        yield return k % 2 == 0 ? (i[k], i[k + 1], i[k + 2]) : (i[k + 1], i[k], i[k + 2]);
                    break;
                case 6:
                    for (int k = 1; k + 1 < i.Length; k++) yield return (i[0], i[k], i[k + 1]);
                    break;
                default:
                    for (int k = 0; k + 2 < i.Length; k += 3) yield return (i[k], i[k + 1], i[k + 2]);
                    break;
            }
        }

        /// <summary>The surface for a material (made the first time it's used); "Default" for none.</summary>
        private int SurfaceFor(int material)
        {
            if (_surfaceOf.TryGetValue(material, out int s)) return s;
            var materials = Array("materials");
            Surface surface;
            if (material < 0 || material >= materials.Count) surface = new Surface("Default");
            else
            {
                var m = materials[material]!;
                string baseName = Str(m["name"]) is { Length: > 0 } n ? n : $"Material {material + 1}";
                string unique = baseName;
                for (int k = 2; _doc.Surfaces.Any(x => x.Name == unique); k++) unique = $"{baseName} {k}";
                surface = new Surface(unique) { Diffuse = 1, Specular = 0, Glossiness = 64 };
                var pbr = m["pbrMetallicRoughness"];
                var factor = pbr?["baseColorFactor"] as JsonArray;
                var linear = factor is { Count: 4 } ? new Vec3(Num(factor[0], 1), Num(factor[1], 1), Num(factor[2], 1)) : Vec3.One;
                double alpha = factor is { Count: 4 } ? Num(factor[3], 1) : 1;
                surface.Color = Rgb.FromVec(new Vec3(GltfBuilder.FromLinear(linear.X), GltfBuilder.FromLinear(linear.Y), GltfBuilder.FromLinear(linear.Z)));
                if (Str(m["alphaMode"]) == "BLEND" && alpha < 1) surface.Transparency = Math.Clamp(1 - alpha, 0, 1);
                double roughness = Num(pbr?["roughnessFactor"], 1);
                if (roughness < 0.999)
                {
                    surface.Specular = Math.Clamp(1 - roughness, 0, 1);
                    surface.Glossiness = Math.Round(GltfBuilder.Glossiness(roughness), 3);
                }
                if (m["emissiveFactor"] is JsonArray { Count: 3 } e)
                {
                    double glow = Math.Max(Num(e[0], 0), Math.Max(Num(e[1], 0), Num(e[2], 0)));
                    double lit = Math.Max(linear.X, Math.Max(linear.Y, linear.Z));
                    surface.Luminosity = Math.Clamp(lit > 0 ? glow / lit : glow, 0, 1);
                }
                surface.DoubleSided = m["doubleSided"] is JsonValue ds && ds.TryGetValue(out bool two) && two;
                if (pbr?["baseColorTexture"] is JsonNode tex && Int(tex["texCoord"], 0) == 0 && Texture(Int(tex["index"])) is { } layer)
                    surface.Textures.Add(layer);
                // The other maps on TEXCOORD_0: normals as a normal map, occlusion multiplied into the colour,
                // emission as luminosity, and roughness (the green of metallic-roughness, near enough) as specular.
                void Map(JsonNode? node, Action<TextureLayer> set)
                {
                    if (node is JsonNode t && Int(t["texCoord"], 0) == 0 && Texture(Int(t["index"])) is { } l) { set(l); surface.Textures.Add(l); }
                }
                Map(m["normalTexture"], l => l.Channel = TextureChannel.Normal);
                Map(m["occlusionTexture"], l => l.Blend = TextureBlend.Multiply);
                Map(m["emissiveTexture"], l => l.Channel = TextureChannel.Luminosity);
                Map(pbr?["metallicRoughnessTexture"], l => { l.Channel = TextureChannel.Specular; l.Negative = true; });
            }
            _doc.Surfaces.Add(surface);
            return _surfaceOf[material] = _doc.Surfaces.Count - 1;
        }

        private TextureLayer? Texture(int index)
        {
            var textures = Array("textures");
            if (index < 0 || index >= textures.Count) return null;
            var t = textures[index]!;
            int source = Int(t["source"]);
            var images = Array("images");
            if (source < 0 || source >= images.Count) return null;
            var layer = new TextureLayer { Channel = TextureChannel.Color, Projection = TextureProjection.UV, UVMap = UVMapName };
            var samplers = Array("samplers");
            int sampler = Int(t["sampler"]);
            if (sampler >= 0 && sampler < samplers.Count)
            {
                layer.WidthTiling = Tiling(Int(samplers[sampler]!["wrapS"], GltfBuilder.Repeat));
                layer.HeightTiling = Tiling(Int(samplers[sampler]!["wrapT"], GltfBuilder.Repeat));
            }
            var image = images[source]!;
            string? uri = Str(image["uri"]);
            if (uri != null && !uri.StartsWith("data:", StringComparison.Ordinal))
            {
                layer.Image = Uri.UnescapeDataString(uri);
                return layer;
            }
            byte[] bytes = uri != null ? DataUri(uri) : View(Int(image["bufferView"]));
            string imageName = Str(image["name"]) is { Length: > 0 } n ? Safe(n) : $"image{source + 1}";
            string ext = Str(image["mimeType"]) == "image/jpeg" || (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) ? ".jpg" : ".png";
            string path = Path.Combine(folder ?? Directory.GetCurrentDirectory(), $"{name}-{imageName}{ext}");
            layer.Image = path;
            if (TextureImages.Decode(bytes, path) is { } decoded)
            {
                TextureImages.Put(path, decoded);
                if (folder != null) TextureImages.Put(path, decoded, folder);
            }
            return layer;
        }

        private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c));

        private static TextureTiling Tiling(int wrap) => wrap switch
        {
            GltfBuilder.MirroredRepeat => TextureTiling.Mirror,
            GltfBuilder.ClampToEdge => TextureTiling.Edge,
            _ => TextureTiling.Repeat,
        };

        /// <summary>An accessor's values as doubles (normalized integers scaled to 0..1 or -1..1), and its components per element.</summary>
        private double[] Accessor(int index, out int components)
        {
            var accessors = Array("accessors");
            if (index < 0 || index >= accessors.Count) throw new FormatException($"Accessor {index} doesn't exist.");
            var a = accessors[index]!;
            if (a["sparse"] != null) throw new NotSupportedException("Sparse accessors aren't read.");
            components = Str(a["type"]) switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16,
                var t => throw new FormatException($"Accessor {index} has an unknown type {t}."),
            };
            int count = Int(a["count"], 0);
            int type = Int(a["componentType"]);
            bool normalized = a["normalized"] is JsonValue nv && nv.TryGetValue(out bool nb) && nb;
            int size = type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new FormatException($"Accessor {index} has an unknown component type {type}.") };
            var result = new double[count * components];
            int viewIndex = Int(a["bufferView"]);
            if (viewIndex < 0) return result; // all zeros, as the spec says
            var view = Array("bufferViews")[viewIndex] ?? throw new FormatException($"Buffer view {viewIndex} doesn't exist.");
            var buffer = Buffer(Int(view["buffer"], 0));
            int viewOffset = Int(view["byteOffset"], 0), viewLength = Int(view["byteLength"], 0);
            int stride = Int(view["byteStride"], 0);
            if (stride == 0) stride = size * components;
            int offset = viewOffset + Int(a["byteOffset"], 0);
            if (count > 0 && (viewOffset + viewLength > buffer.Length || offset + (long)(count - 1) * stride + size * components > buffer.Length))
                throw new FormatException($"Accessor {index} runs past the end of its buffer.");
            var span = buffer.AsSpan();
            for (int i = 0; i < count; i++)
                for (int c = 0; c < components; c++)
                {
                    var at = span[(offset + i * stride + c * size)..];
                    result[i * components + c] = type switch
                    {
                        5126 => BinaryPrimitives.ReadSingleLittleEndian(at),
                        5125 => BinaryPrimitives.ReadUInt32LittleEndian(at),
                        5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(at) / 65535.0 : BinaryPrimitives.ReadUInt16LittleEndian(at),
                        5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(at) / 32767.0, -1) : BinaryPrimitives.ReadInt16LittleEndian(at),
                        5121 => normalized ? at[0] / 255.0 : at[0],
                        _ => normalized ? Math.Max((sbyte)at[0] / 127.0, -1) : (sbyte)at[0],
                    };
                }
            return result;
        }

        private byte[] View(int index)
        {
            var views = Array("bufferViews");
            if (index < 0 || index >= views.Count) throw new FormatException($"Buffer view {index} doesn't exist.");
            var view = views[index]!;
            var buffer = Buffer(Int(view["buffer"], 0));
            int offset = Int(view["byteOffset"], 0), length = Int(view["byteLength"], 0);
            if (offset < 0 || length < 0 || offset + length > buffer.Length) throw new FormatException($"Buffer view {index} runs past the end of its buffer.");
            return buffer.AsSpan(offset, length).ToArray();
        }

        private byte[] Buffer(int index)
        {
            if (_buffers.TryGetValue(index, out var b)) return b;
            var buffers = Array("buffers");
            if (index < 0 || index >= buffers.Count) throw new FormatException($"Buffer {index} doesn't exist.");
            string? uri = Str(buffers[index]!["uri"]);
            if (uri == null)
                b = index == 0 && glbBin != null ? glbBin : throw new FormatException("A buffer has no uri, and there's no .glb binary chunk for it.");
            else if (uri.StartsWith("data:", StringComparison.Ordinal)) b = DataUri(uri);
            else
            {
                string file = Path.Combine(folder ?? Directory.GetCurrentDirectory(), Uri.UnescapeDataString(uri));
                if (!File.Exists(file)) throw new FileNotFoundException($"The glTF file's buffer {Path.GetFileName(file)} isn't beside it.", file);
                b = File.ReadAllBytes(file);
            }
            return _buffers[index] = b;
        }

        private static byte[] DataUri(string uri)
        {
            int comma = uri.IndexOf(',');
            if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.Ordinal)) throw new FormatException("Only base64 data URIs are read.");
            try
            {
                return Convert.FromBase64String(uri[(comma + 1)..]);
            }
            catch (FormatException)
            {
                throw new FormatException("A data URI's base64 can't be read.");
            }
        }
    }
}
