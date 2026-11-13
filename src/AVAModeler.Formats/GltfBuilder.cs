using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;

namespace AVAModeler.Formats;

/// <summary>How a glTF asset is packed: one binary .glb, a .gltf with its data inside it (base64), or a .gltf with a .bin beside it.</summary>
public enum GltfPacking { Binary, Embedded, Separate }

/// <summary>
/// Builds a glTF 2.0 asset by hand (System.Text.Json for the JSON, one binary buffer for the data): meshes from
/// LightWave meshes and surfaces, nodes, cameras, KHR_lights_punctual lights and animations. Everything given to it
/// is in LightWave's space; it converts to glTF's right-handed, +Y up space (Z negated, faces' winding reversed) as
/// it goes. <see cref="Gltf"/> uses it for objects, and Layout's scene export for scenes.
/// </summary>
public sealed class GltfBuilder
{
    // glTF's numbers for component types, buffer view targets and samplers' wrap modes.
    internal const int Float = 5126, UnsignedShort = 5123, UnsignedInt = 5125;
    internal const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;
    internal const int Repeat = 10497, MirroredRepeat = 33648, ClampToEdge = 33071;
    public const string LightsExtension = "KHR_lights_punctual";

    private readonly MemoryStream _bin = new();
    private readonly JsonArray _bufferViews = [], _accessors = [], _meshes = [], _materials = [], _textures = [], _images = [],
        _samplers = [], _nodes = [], _cameras = [], _animations = [], _lights = [];
    private readonly Dictionary<(Surface Surface, string? Base), int> _materialOf = [];
    private readonly Dictionary<string, int> _imageOf = [];
    private readonly Dictionary<(int S, int T), int> _samplerOf = [];

    /// <summary>Things that couldn't be carried over (an image that can't be found, say), once each.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>LightWave's space to glTF's: Z negated.</summary>
    public static Vec3 ToGltf(Vec3 p) => new(p.X, p.Y, p.Z == 0 ? 0 : -p.Z);

    /// <summary>glTF's space to LightWave's: Z negated (the same flip).</summary>
    public static Vec3 FromGltf(Vec3 p) => ToGltf(p);

    /// <summary>A colour value from 0 to 1 as displayed (sRGB, as LightWave's colours are) to linear light, as glTF's factors are.</summary>
    public static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>A linear colour value back to sRGB (0 to 1).</summary>
    public static double FromLinear(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }

    /// <summary>
    /// LightWave's glossiness (a Phong exponent: 16 low to 1024 very high) as a PBR roughness, through the usual
    /// Blinn-Phong match (alpha = √(2 / (n + 2)), roughness = √alpha). A surface without specular is fully rough.
    /// </summary>
    public static double Roughness(Surface s) => s.Specular <= 0 ? 1 : Math.Clamp(Math.Sqrt(Math.Sqrt(2 / (Math.Max(s.Glossiness, 0) + 2))), 0, 1);

    /// <summary>The inverse of <see cref="Roughness"/>: the glossiness that gives a roughness.</summary>
    public static double Glossiness(double roughness)
    {
        double r = Math.Clamp(roughness, 0.05, 1);
        return Math.Max(1, 2 / Math.Pow(r, 4) - 2);
    }

    /// <summary>The colour texture a surface shows in glTF: its first image layer on the colour channel, if any.</summary>
    public static TextureLayer? ColourTexture(Surface s) =>
        s.TexturesOf(TextureChannel.Color).FirstOrDefault(t => t.IsImage && t.Image.Length > 0 && t.Opacity > 0);

    /// <summary>
    /// Adds a mesh made from a LightWave mesh's faces (points and lines are left out, as are detail polygons), one
    /// primitive per surface, triangulated. Normals follow each surface's smoothing: a smooth surface's corners take
    /// the average of the faces sharing the point within its smoothing angle, a flat one's the face's own. Where the
    /// surface has a colour image, TEXCOORD_0 comes from the UV map it uses (a UV projection) or from its projection
    /// worked out at each corner (planar, cylindrical, spherical, cubic). A surface with colour procedurals and no
    /// image has them baked into a picture (<see cref="ProceduralBake"/>) with the UVs to match. Returns the mesh's
    /// index, or -1 if it has no faces.
    /// </summary>
    public int AddMesh(string name, Mesh mesh, IReadOnlyList<Surface> surfaces, string? imageBase)
    {
        var faceNormals = new Vec3[mesh.Polygons.Count];
        for (int i = 0; i < faceNormals.Length; i++) faceNormals[i] = mesh.Polygons[i].Count >= 3 ? mesh.Normal(i) : Vec3.Zero;
        var primitives = new JsonArray();
        foreach (var group in Enumerable.Range(0, mesh.Polygons.Count).Where(i => mesh.Polygons[i].Count >= 3).GroupBy(i => mesh.Polygons[i].Surface))
        {
            var surface = group.Key >= 0 && group.Key < surfaces.Count ? surfaces[group.Key] : null;
            var texture = surface == null ? null : ColourTexture(surface);
            bool uvs = texture != null && ImageBytes(texture, imageBase) != null;
            // A surface with colour procedurals and no image gets them baked into one.
            var bake = !uvs && surface != null ? ProceduralBake.Of(surface, mesh, group.ToList()) : null;
            if (bake != null) uvs = true;
            var positions = new List<float>();
            var normals = new List<float>();
            var texcoords = new List<float>();
            var colors = new List<float>();
            // A surface taking a colour map writes it as COLOR_0, which glTF multiplies into the base colour as the ray tracer does.
            var colorMap = surface?.VertexColorMap is { } vcName ? mesh.FindPointMap(vcName, PointMapKind.Rgb) : null;
            if (colorMap is { IsColor: false }) colorMap = null;
            double vcIntensity = Math.Clamp(surface?.VertexColorIntensity ?? 1, 0, 1);
            var indices = new List<int>();
            var vertexOf = new Dictionary<(int Point, Vec3 Normal, Vec2 UV), int>();
            double limit = Math.Cos(Mat4.Deg(Math.Clamp(surface?.SmoothingAngle ?? 0, 0, 180)));
            foreach (int pi in group)
            {
                var poly = mesh.Polygons[pi];
                var tris = Triangulator.TriangulatePolygon(mesh, poly);
                if (tris.Length == 0) continue;
                var cornerUV = bake != null ? bake.CornerUVs[pi] : uvs ? CornerUVs(mesh, poly, faceNormals[pi], texture!) : null;
                var vertex = new int[poly.Count];
                for (int c = 0; c < poly.Count; c++)
                {
                    int point = poly.Points[c];
                    var n = faceNormals[pi];
                    if (surface is { Smoothing: true })
                    {
                        var sum = Vec3.Zero;
                        foreach (int other in mesh.PolygonsOf(point))
                            if (mesh.Polygons[other].Surface == poly.Surface && mesh.Polygons[other].Count >= 3 && Vec3.Dot(faceNormals[other], faceNormals[pi]) >= limit - 1e-9)
                                sum += faceNormals[other];
                        if (sum.LengthSquared > 0) n = sum.Normalized();
                    }
                    if (n.LengthSquared == 0) n = Vec3.UnitY;
                    var uv = cornerUV?[c] ?? Vec2.Zero;
                    var key = (point, n, uv);
                    if (!vertexOf.TryGetValue(key, out int v))
                    {
                        vertexOf[key] = v = positions.Count / 3;
                        var p = ToGltf(mesh.Points[point]);
                        positions.AddRange([(float)p.X, (float)p.Y, (float)p.Z]);
                        var gn = ToGltf(n);
                        normals.AddRange([(float)gn.X, (float)gn.Y, (float)gn.Z]);
                        // glTF's V runs down the image from its top; LightWave's up from its bottom.
                        if (uvs) texcoords.AddRange([(float)uv.X, (float)(1 - uv.Y)]);
                        if (colorMap != null)
                        {
                            var vc = Vec3.Lerp(Vec3.One, colorMap.Color(point) ?? Vec3.One, vcIntensity);
                            colors.AddRange([(float)vc.X, (float)vc.Y, (float)vc.Z]);
                        }
                    }
                    vertex[c] = v;
                }
                // Negating Z mirrors the space, so each triangle's winding is reversed to keep it facing out.
                for (int i = 0; i < tris.Length; i += 3)
                    indices.AddRange([vertex[tris[i]], vertex[tris[i + 2]], vertex[tris[i + 1]]]);
            }
            if (indices.Count == 0) continue;
            var attributes = new JsonObject
            {
                ["POSITION"] = FloatAccessor(positions, 3, ArrayBuffer, withBounds: true),
                ["NORMAL"] = FloatAccessor(normals, 3, ArrayBuffer, withBounds: false),
            };
            if (uvs) attributes["TEXCOORD_0"] = FloatAccessor(texcoords, 2, ArrayBuffer, withBounds: false);
            if (colorMap != null) attributes["COLOR_0"] = FloatAccessor(colors, 3, ArrayBuffer, withBounds: false);
            var primitive = new JsonObject { ["attributes"] = attributes, ["indices"] = IndexAccessor(indices, positions.Count / 3), ["mode"] = 4 };
            if (surface != null) primitive["material"] = bake != null ? BakedMaterial(surface, bake) : Material(surface, imageBase, uvs ? texture : null);
            primitives.Add(primitive);
        }
        if (primitives.Count == 0) return -1;
        var m = new JsonObject { ["primitives"] = primitives };
        if (name.Length > 0) m["name"] = name;
        _meshes.Add(m);
        return _meshes.Count - 1;
    }

    /// <summary>A face's corners' coordinates in a texture's image (u across, v up), from its UV map or its projection.</summary>
    private static Vec2[] CornerUVs(Mesh mesh, Polygon poly, Vec3 normal, TextureLayer t)
    {
        var result = new Vec2[poly.Count];
        if (t.Projection == TextureProjection.UV)
        {
            for (int c = 0; c < poly.Count; c++)
                result[c] = mesh.TryGetUV(t.UVMap, poly, poly.Points[c], out var uv) ? uv : Vec2.Zero;
            return result;
        }
        var points = poly.Points.Select(i => mesh.Points[i]).ToArray();
        TextureMapping.ProjectFace(t, points, normal, result);
        return result;
    }

    /// <summary>A surface's material (made once): its colour × diffuse as the base colour, glossiness as roughness, luminosity as emission, transparency as alpha.</summary>
    private int Material(Surface s, string? imageBase, TextureLayer? texture)
    {
        if (_materialOf.TryGetValue((s, imageBase), out int index)) return index;
        var c = s.Color.ToVec();
        double diffuse = Math.Clamp(s.Diffuse, 0, 1);
        var linear = new Vec3(ToLinear(c.X), ToLinear(c.Y), ToLinear(c.Z));
        // Under an image the factor only darkens it by the diffuse level, so the picture shows its own colours.
        var factor = texture != null && texture.Opacity >= 1 && texture.Blend == TextureBlend.Normal ? Vec3.One * diffuse : linear * diffuse;
        double alpha = Math.Clamp(1 - s.Transparency, 0, 1);
        var pbr = new JsonObject
        {
            ["baseColorFactor"] = Numbers(factor.X, factor.Y, factor.Z, alpha),
            ["metallicFactor"] = 0.0,
            ["roughnessFactor"] = Math.Round(Roughness(s), 6),
        };
        if (texture != null && Texture(texture, imageBase) is int tex) pbr["baseColorTexture"] = new JsonObject { ["index"] = tex };
        var m = new JsonObject { ["name"] = s.Name, ["pbrMetallicRoughness"] = pbr };
        double glow = Math.Clamp(s.Luminosity, 0, 1);
        if (glow > 0) m["emissiveFactor"] = Numbers(linear.X * glow, linear.Y * glow, linear.Z * glow);
        if (alpha < 1) m["alphaMode"] = "BLEND";
        if (s.DoubleSided) m["doubleSided"] = true;
        _materials.Add(m);
        return _materialOf[(s, imageBase)] = _materials.Count - 1;
    }

    /// <summary>
    /// A material for a surface whose colour procedurals were baked into an image (one per mesh, as the bake depends on
    /// the faces): as <see cref="Material"/>, with the baked picture as its base colour texture, clamped at its edges.
    /// </summary>
    private int BakedMaterial(Surface s, ProceduralBake bake)
    {
        var c = s.Color.ToVec();
        double diffuse = Math.Clamp(s.Diffuse, 0, 1);
        var linear = new Vec3(ToLinear(c.X), ToLinear(c.Y), ToLinear(c.Z));
        double alpha = Math.Clamp(1 - s.Transparency, 0, 1);
        int view = View(Png.Encode(bake.Width, bake.Height, bake.Rgba), target: null);
        _images.Add(new JsonObject { ["bufferView"] = view, ["mimeType"] = "image/png", ["name"] = s.Name + " (procedural)" });
        if (!_samplerOf.TryGetValue((ClampToEdge, ClampToEdge), out int sampler))
        {
            _samplers.Add(new JsonObject { ["wrapS"] = ClampToEdge, ["wrapT"] = ClampToEdge });
            _samplerOf[(ClampToEdge, ClampToEdge)] = sampler = _samplers.Count - 1;
        }
        _textures.Add(new JsonObject { ["sampler"] = sampler, ["source"] = _images.Count - 1 });
        var pbr = new JsonObject
        {
            ["baseColorFactor"] = Numbers(diffuse, diffuse, diffuse, alpha),
            ["metallicFactor"] = 0.0,
            ["roughnessFactor"] = Math.Round(Roughness(s), 6),
            ["baseColorTexture"] = new JsonObject { ["index"] = _textures.Count - 1 },
        };
        var m = new JsonObject { ["name"] = s.Name, ["pbrMetallicRoughness"] = pbr };
        double glow = Math.Clamp(s.Luminosity, 0, 1);
        if (glow > 0) m["emissiveFactor"] = Numbers(linear.X * glow, linear.Y * glow, linear.Z * glow);
        if (alpha < 1) m["alphaMode"] = "BLEND";
        if (s.DoubleSided) m["doubleSided"] = true;
        _materials.Add(m);
        return _materials.Count - 1;
    }

    /// <summary>A texture for an image layer: its image in the buffer and a sampler for its tiling. Null if the image can't be had.</summary>
    private int? Texture(TextureLayer t, string? imageBase)
    {
        if (ImageBytes(t, imageBase) is not { } image) return null;
        if (!_imageOf.TryGetValue(image.Key, out int source))
        {
            int view = View(image.Bytes, target: null);
            var img = new JsonObject { ["bufferView"] = view, ["mimeType"] = image.Mime };
            string name = Path.GetFileNameWithoutExtension(t.Image.Replace('\\', '/'));
            if (name.Length > 0) img["name"] = name;
            _images.Add(img);
            _imageOf[image.Key] = source = _images.Count - 1;
        }
        int s = Wrap(t.WidthTiling), tt = Wrap(t.HeightTiling);
        if (!_samplerOf.TryGetValue((s, tt), out int sampler))
        {
            _samplers.Add(new JsonObject { ["wrapS"] = s, ["wrapT"] = tt });
            _samplerOf[(s, tt)] = sampler = _samplers.Count - 1;
        }
        _textures.Add(new JsonObject { ["sampler"] = sampler, ["source"] = source });
        return _textures.Count - 1;
    }

    private static int Wrap(TextureTiling tiling) => tiling switch
    {
        TextureTiling.Repeat => Repeat,
        TextureTiling.Mirror => MirroredRepeat,
        _ => ClampToEdge,
    };

    private readonly Dictionary<(string, string?), (string Key, byte[] Bytes, string Mime)?> _bytesOf = [];

    /// <summary>
    /// An image layer's picture as glTF can hold it: a PNG or JPEG file as it is, anything else (or an image held only
    /// in memory) encoded as PNG. Null, with a warning, if it can't be had.
    /// </summary>
    private (string Key, byte[] Bytes, string Mime)? ImageBytes(TextureLayer t, string? imageBase)
    {
        if (_bytesOf.TryGetValue((t.Image, imageBase), out var known)) return known;
        (string, byte[], string)? result = null;
        string? file = TextureImages.Resolve(t.Image, imageBase);
        try
        {
            if (file != null)
            {
                var data = File.ReadAllBytes(file);
                if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G') result = (file, data, "image/png");
                else if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) result = (file, data, "image/jpeg");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (result == null)
        {
            var image = TextureImages.Load(t.Image, imageBase);
            if (!image.IsPlaceholder) result = (file ?? t.Image, Png.Encode(image.Width, image.Height, image.Rgba), "image/png");
            else Warnings.Add(image.Problem ?? $"Can't read the image {t.Image}; it's left out.");
        }
        _bytesOf[(t.Image, imageBase)] = result;
        return result;
    }

    /// <summary>Adds a node (its JSON as glTF has it); returns its index.</summary>
    public int AddNode(JsonObject node)
    {
        _nodes.Add(node);
        return _nodes.Count - 1;
    }

    /// <summary>A node added earlier, to fill in (its transform, say).</summary>
    public JsonObject Node(int index) => (JsonObject)_nodes[index]!;

    /// <summary>Makes one node a child of another.</summary>
    public void AddChild(int parent, int child)
    {
        var node = (JsonObject)_nodes[parent]!;
        if (node["children"] is not JsonArray children) node["children"] = children = [];
        children.Add(child);
    }

    public int AddCamera(JsonObject camera)
    {
        _cameras.Add(camera);
        return _cameras.Count - 1;
    }

    /// <summary>Adds a KHR_lights_punctual light; returns its index.</summary>
    public int AddLight(JsonObject light)
    {
        _lights.Add(light);
        return _lights.Count - 1;
    }

    public void AddAnimation(JsonObject animation) => _animations.Add(animation);

    /// <summary>An accessor of floats (components per element: 1 for SCALAR, 3 for VEC3...) with no buffer view target, for animations.</summary>
    public int AddFloats(IReadOnlyList<float> values, int components, bool withBounds) => FloatAccessor(values, components, null, withBounds);

    private int FloatAccessor(IReadOnlyList<float> values, int components, int? target, bool withBounds)
    {
        var bytes = new byte[values.Count * 4];
        for (int i = 0; i < values.Count; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        int view = View(bytes, target);
        var a = new JsonObject
        {
            ["bufferView"] = view,
            ["componentType"] = Float,
            ["count"] = values.Count / components,
            ["type"] = TypeName(components),
        };
        if (withBounds)
        {
            var min = new double[components];
            var max = new double[components];
            Array.Fill(min, double.PositiveInfinity);
            Array.Fill(max, double.NegativeInfinity);
            for (int i = 0; i < values.Count; i++)
            {
                min[i % components] = Math.Min(min[i % components], values[i]);
                max[i % components] = Math.Max(max[i % components], values[i]);
            }
            a["min"] = Numbers(min);
            a["max"] = Numbers(max);
        }
        _accessors.Add(a);
        return _accessors.Count - 1;
    }

    private int IndexAccessor(List<int> indices, int vertexCount)
    {
        bool small = vertexCount <= ushort.MaxValue;
        var bytes = new byte[indices.Count * (small ? 2 : 4)];
        for (int i = 0; i < indices.Count; i++)
            if (small) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)indices[i]);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)indices[i]);
        int view = View(bytes, ElementArrayBuffer);
        _accessors.Add(new JsonObject
        {
            ["bufferView"] = view,
            ["componentType"] = small ? UnsignedShort : UnsignedInt,
            ["count"] = indices.Count,
            ["type"] = "SCALAR",
        });
        return _accessors.Count - 1;
    }

    private static string TypeName(int components) => components switch { 1 => "SCALAR", 2 => "VEC2", 3 => "VEC3", 4 => "VEC4", 16 => "MAT4", _ => throw new ArgumentOutOfRangeException(nameof(components)) };

    /// <summary>Puts bytes in the buffer at the next multiple of four; returns the buffer view's index.</summary>
    private int View(byte[] bytes, int? target)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = bytes.Length };
        if (target is int t) view["target"] = t;
        _bin.Write(bytes);
        _bufferViews.Add(view);
        return _bufferViews.Count - 1;
    }

    internal static JsonArray Numbers(params double[] values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(Math.Round(v, 9))!)]);

    /// <summary>The asset's JSON, with the given nodes at the top of its one scene; the buffer's uri as given (null in a .glb).</summary>
    private JsonObject Root(IEnumerable<int> sceneNodes, string? bufferUri, long bufferLength)
    {
        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "AVA Modeler" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray([.. sceneNodes.Select(n => (JsonNode)n)]) }),
        };
        if (_lights.Count > 0)
        {
            root["extensionsUsed"] = new JsonArray(LightsExtension);
            root["extensions"] = new JsonObject { [LightsExtension] = new JsonObject { ["lights"] = _lights.DeepClone() } };
        }
        void Put(string key, JsonArray a)
        {
            if (a.Count > 0) root[key] = a.DeepClone();
        }
        Put("nodes", _nodes);
        Put("meshes", _meshes);
        Put("materials", _materials);
        Put("textures", _textures);
        Put("images", _images);
        Put("samplers", _samplers);
        Put("cameras", _cameras);
        Put("animations", _animations);
        Put("accessors", _accessors);
        Put("bufferViews", _bufferViews);
        if (bufferLength > 0)
        {
            var buffer = new JsonObject { ["byteLength"] = bufferLength };
            if (bufferUri != null) buffer["uri"] = bufferUri;
            root["buffers"] = new JsonArray(buffer);
        }
        return root;
    }

    /// <summary>
    /// The finished asset's bytes, packed as asked: the .glb or .gltf file, and for <see cref="GltfPacking.Separate"/>
    /// the .bin (named <paramref name="binName"/>) to go beside it.
    /// </summary>
    public (byte[] Main, byte[]? Bin) Build(IEnumerable<int> sceneNodes, GltfPacking packing, string binName = "buffer.bin")
    {
        var bin = _bin.ToArray();
        var options = new JsonSerializerOptions { WriteIndented = packing != GltfPacking.Binary };
        switch (packing)
        {
            case GltfPacking.Binary:
            {
                var json = Encoding.UTF8.GetBytes(Root(sceneNodes, null, bin.Length).ToJsonString(options));
                int jsonLength = Pad(json.Length), binLength = Pad(bin.Length);
                int total = 12 + 8 + jsonLength + (bin.Length > 0 ? 8 + binLength : 0);
                var glb = new byte[total];
                var span = glb.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(span, 0x46546C67); // "glTF"
                BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
                BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);
                BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)jsonLength);
                BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0x4E4F534A); // "JSON"
                json.CopyTo(span[20..]);
                span.Slice(20 + json.Length, jsonLength - json.Length).Fill((byte)' ');
                if (bin.Length > 0)
                {
                    int at = 20 + jsonLength;
                    BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)binLength);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], 0x004E4942); // "BIN\0"
                    bin.CopyTo(span[(at + 8)..]);
                }
                return (glb, null);
            }
            case GltfPacking.Embedded:
            {
                string? uri = bin.Length > 0 ? "data:application/octet-stream;base64," + Convert.ToBase64String(bin) : null;
                return (Encoding.UTF8.GetBytes(Root(sceneNodes, uri, bin.Length).ToJsonString(options)), null);
            }
            default:
                return (Encoding.UTF8.GetBytes(Root(sceneNodes, bin.Length > 0 ? Uri.EscapeDataString(binName) : null, bin.Length).ToJsonString(options)),
                    bin.Length > 0 ? bin : null);
        }
    }

    private static int Pad(int length) => (length + 3) & ~3;

    /// <summary>A rotation matrix (the upper 3×3 of a transform with no scale) as a unit quaternion [x, y, z, w].</summary>
    public static double[] Quaternion(Mat4 m)
    {
        double trace = m[0, 0] + m[1, 1] + m[2, 2];
        double x, y, z, w;
        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1) * 2;
            w = s / 4;
            x = (m[2, 1] - m[1, 2]) / s;
            y = (m[0, 2] - m[2, 0]) / s;
            z = (m[1, 0] - m[0, 1]) / s;
        }
        else if (m[0, 0] > m[1, 1] && m[0, 0] > m[2, 2])
        {
            double s = Math.Sqrt(1 + m[0, 0] - m[1, 1] - m[2, 2]) * 2;
            w = (m[2, 1] - m[1, 2]) / s;
            x = s / 4;
            y = (m[0, 1] + m[1, 0]) / s;
            z = (m[0, 2] + m[2, 0]) / s;
        }
        else if (m[1, 1] > m[2, 2])
        {
            double s = Math.Sqrt(1 + m[1, 1] - m[0, 0] - m[2, 2]) * 2;
            w = (m[0, 2] - m[2, 0]) / s;
            x = (m[0, 1] + m[1, 0]) / s;
            y = s / 4;
            z = (m[1, 2] + m[2, 1]) / s;
        }
        else
        {
            double s = Math.Sqrt(1 + m[2, 2] - m[0, 0] - m[1, 1]) * 2;
            w = (m[1, 0] - m[0, 1]) / s;
            x = (m[0, 2] + m[2, 0]) / s;
            y = (m[1, 2] + m[2, 1]) / s;
            z = s / 4;
        }
        double len = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (len == 0) return [0, 0, 0, 1];
        if (w < 0) len = -len;
        return [x / len, y / len, z / len, w / len];
    }

    /// <summary>
    /// A LightWave transform (no shear) as glTF's translation, rotation (quaternion) and scale, in glTF's space: the
    /// matrix is mirrored through Z on both sides, which keeps the rotation a rotation.
    /// </summary>
    public static (double[] T, double[] R, double[] S) Decompose(Mat4 lightwave)
    {
        var flip = Mat4.Scaling(new Vec3(1, 1, -1));
        var m = flip * lightwave * flip;
        var x = new Vec3(m[0, 0], m[1, 0], m[2, 0]);
        var y = new Vec3(m[0, 1], m[1, 1], m[2, 1]);
        var z = new Vec3(m[0, 2], m[1, 2], m[2, 2]);
        double sx = x.Length, sy = y.Length, sz = z.Length;
        if (m.Determinant() < 0) sx = -sx;
        static Vec3 Unit(Vec3 v, double s) => Math.Abs(s) > 1e-12 ? v / s : Vec3.Zero;
        var (ux, uy, uz) = (Unit(x, sx), Unit(y, sy), Unit(z, sz));
        if (ux.LengthSquared == 0 || uy.LengthSquared == 0 || uz.LengthSquared == 0) (ux, uy, uz) = (Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        var rotation = Mat4.FromRows(ux.X, uy.X, uz.X, 0, ux.Y, uy.Y, uz.Y, 0, ux.Z, uy.Z, uz.Z, 0, 0, 0, 0, 1);
        return ([m[0, 3], m[1, 3], m[2, 3]], Quaternion(rotation), [sx, sy, sz]);
    }
}
