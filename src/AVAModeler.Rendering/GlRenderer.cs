using AVAModeler.Core;
using Silk.NET.OpenGL;

namespace AVAModeler.Rendering;

/// <summary>
/// The OpenGL renderer (GL 3.3 core, or GLES 3 where that's what the platform gives): the same picture as the
/// software renderer, drawn on the GPU into whatever framebuffer is bound. It keeps nothing between frames but its
/// shaders and buffers, so it never goes stale.
/// </summary>
public sealed unsafe class GlRenderer : IRenderer
{
    private readonly GL _gl;
    private readonly bool _es;
    private uint _lineProgram, _faceProgram, _textureProgram, _vao, _vbo;
    private bool _ready;
    /// <summary>The GL textures made for images, and those used this frame (the rest are let go after it).</summary>
    private readonly Dictionary<TextureImage, uint> _textures = [];
    private readonly HashSet<TextureImage> _used = [];

    public string Name => _es ? "OpenGL ES 3" : "OpenGL 3.3";

    /// <param name="gl">The context's functions (from the host's proc-address loader).</param>
    /// <param name="es">Whether the context is OpenGL ES (it changes the shader dialect).</param>
    public GlRenderer(GL gl, bool es)
    {
        _gl = gl;
        _es = es;
    }

    private const string LineVertex = """
        layout(location = 0) in vec3 position;
        layout(location = 1) in vec4 color;
        uniform mat4 mvp;
        uniform float pointSize;
        out vec4 vColor;
        void main() {
            gl_Position = mvp * vec4(position, 1.0);
            gl_PointSize = pointSize;
            vColor = color;
        }
        """;

    private const string LineFragment = """
        in vec4 vColor;
        out vec4 fragColor;
        void main() { fragColor = vColor; }
        """;

    private const string FaceVertex = """
        layout(location = 0) in vec3 position;
        layout(location = 1) in vec4 color;
        layout(location = 2) in vec3 normal;
        uniform mat4 mvp;
        out vec4 vColor;
        out vec3 vNormal;
        void main() {
            gl_Position = mvp * vec4(position, 1.0);
            vColor = color;
            vNormal = normal;
        }
        """;

    // The colour arrives lit already (as the software renderer lights it), so the two renderers agree.
    private const string FaceFragment = """
        in vec4 vColor;
        in vec3 vNormal;
        out vec4 fragColor;
        void main() { fragColor = vColor; }
        """;

    private const string TextureVertex = """
        layout(location = 0) in vec3 position;
        layout(location = 1) in vec4 light;
        layout(location = 2) in vec4 uv01;
        layout(location = 3) in vec4 uv23;
        layout(location = 4) in vec4 on;
        layout(location = 5) in vec3 local;
        layout(location = 6) in vec3 world;
        uniform mat4 mvp;
        out vec4 vLight;
        out vec4 vUv01;
        out vec4 vUv23;
        out vec4 vOn;
        out vec3 vLocal;
        out vec3 vWorld;
        void main() {
            gl_Position = mvp * vec4(position, 1.0);
            vLight = light;
            vUv01 = uv01;
            vUv23 = uv23;
            vOn = on;
            vLocal = local;
            vWorld = world;
        }
        """;

    // Texturing.Fragment and TextureImage.Sample in GLSL: the same tiling, the same alpha-weighted bilinear blend,
    // the same layering and lighting, so a textured face looks as the software renderer draws it. Procedural layers
    // repeat Noise and ProceduralTexture's sums, with Perlin's permutation table in a 256 × 1 texture.
    private const string TextureFragment = """
        in vec4 vLight;
        in vec4 vUv01;
        in vec4 vUv23;
        in vec4 vOn;
        in vec3 vLocal;
        in vec3 vWorld;
        out vec4 fragColor;
        uniform sampler2D permTable;
        uniform int procKind[4];
        uniform vec3 procCenter[4];
        uniform vec3 procSize[4];
        uniform int procAxis[4];
        uniform vec4 procF[4];
        uniform int procI[4];
        uniform vec3 procColor[4];
        uniform vec3 procFalloff[4];
        uniform int procWorld[4];
        uniform vec3 baseColor;
        uniform int layerCount;
        uniform sampler2D image0;
        uniform sampler2D image1;
        uniform sampler2D image2;
        uniform sampler2D image3;
        uniform ivec2 size[4];
        uniform ivec2 tiling[4];
        uniform int blend[4];
        uniform float opacity[4];
        uniform int negative[4];
        uniform int smoothing[4];

        // GLSL leaves % undefined for negative numbers, so the remainder is worked out by floor.
        int pmod(int a, int n) {
            int r = a - n * int(floor(float(a) / float(n)));
            if (r < 0) r += n;
            if (r >= n) r -= n;
            return r;
        }

        int wrapIndex(int i, int n, int mode) {
            if (mode == 1) return pmod(i, n);
            if (mode == 2) { int m = pmod(i, 2 * n); return m < n ? m : 2 * n - 1 - m; }
            if (mode == 3) return clamp(i, 0, n - 1);
            return (i < 0 || i >= n) ? -1 : i;
        }

        vec4 texel(sampler2D t, ivec2 n, int x, int y, ivec2 mode) {
            int tx = wrapIndex(x, n.x, mode.x);
            int ty = wrapIndex(y, n.y, mode.y);
            if (tx < 0 || ty < 0) return vec4(0.0);
            return texelFetch(t, ivec2(tx, ty), 0);
        }

        vec4 sampleImage(sampler2D t, ivec2 n, vec2 uv, ivec2 mode, int blendPixels) {
            uv = clamp(uv, vec2(-1000.0), vec2(1000.0));
            float x = uv.x * float(n.x);
            float y = (1.0 - uv.y) * float(n.y);
            if (blendPixels == 0) return texel(t, n, int(floor(x)), int(floor(y)), mode);
            x -= 0.5;
            y -= 0.5;
            float fx0 = floor(x), fy0 = floor(y);
            int x0 = int(fx0), y0 = int(fy0);
            float fx = x - fx0, fy = y - fy0;
            vec4 a = texel(t, n, x0, y0, mode), b = texel(t, n, x0 + 1, y0, mode);
            vec4 c = texel(t, n, x0, y0 + 1, mode), d = texel(t, n, x0 + 1, y0 + 1, mode);
            float wa = (1.0 - fx) * (1.0 - fy) * a.a, wb = fx * (1.0 - fy) * b.a, wc = (1.0 - fx) * fy * c.a, wd = fx * fy * d.a;
            float sum = wa + wb + wc + wd;
            if (sum <= 1.0e-12) return vec4(0.0);
            return vec4((a.rgb * wa + b.rgb * wb + c.rgb * wc + d.rgb * wd) / sum, min(1.0, sum));
        }

        int perm(int i) { return int(texelFetch(permTable, ivec2(i & 255, 0), 0).r * 255.0 + 0.5); }
        float fade(float t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }
        float grad(int hash, float x, float y, float z) {
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
        float perlin(vec3 p) {
            vec3 f = floor(p);
            int X = int(f.x) & 255, Y = int(f.y) & 255, Z = int(f.z) & 255;
            vec3 r = p - f;
            float x = r.x, y = r.y, z = r.z;
            float u = fade(x), v = fade(y), w = fade(z);
            int a = perm(X) + Y, aa = perm(a) + Z, ab = perm(a + 1) + Z;
            int b = perm(X + 1) + Y, ba = perm(b) + Z, bb = perm(b + 1) + Z;
            return mix(mix(mix(grad(perm(aa), x, y, z), grad(perm(ba), x - 1.0, y, z), u),
                           mix(grad(perm(ab), x, y - 1.0, z), grad(perm(bb), x - 1.0, y - 1.0, z), u), v),
                       mix(mix(grad(perm(aa + 1), x, y, z - 1.0), grad(perm(ba + 1), x - 1.0, y, z - 1.0), u),
                           mix(grad(perm(ab + 1), x, y - 1.0, z - 1.0), grad(perm(bb + 1), x - 1.0, y - 1.0, z - 1.0), u), v), w);
        }
        const vec3 octaveShift = vec3(19.1, 33.4, 47.3);
        float fractalNoise(vec3 p, int octaves, float smallPower) {
            float sum = 0.0, weights = 0.0, weight = 1.0, scale = 1.0;
            for (int i = 0; i < 8; i++) {
                if (i >= octaves) break;
                sum += weight * perlin(p * scale + octaveShift * float(i));
                weights += weight;
                weight *= smallPower;
                scale *= 2.0;
            }
            return weights > 0.0 ? sum / weights : 0.0;
        }
        float turbulence(vec3 p, int octaves, float smallPower) {
            float sum = 0.0, weights = 0.0, weight = 1.0, scale = 1.0;
            for (int i = 0; i < 8; i++) {
                if (i >= octaves) break;
                sum += weight * abs(perlin(p * scale + octaveShift * float(i)));
                weights += weight;
                weight *= smallPower;
                scale *= 2.0;
            }
            return weights > 0.0 ? sum / weights : 0.0;
        }
        vec3 waveSource(int i) { return vec3(float(perm(3 * i)), float(perm(3 * i + 1)), float(perm(3 * i + 2))) / 127.5 - 1.0; }
        float wavePhase(int i) { return float(perm(3 * i + 100)) / 255.0 * 6.283185307179586; }
        float pattern(int kind, int axis, vec4 F, int I, vec3 q) {
            vec3 c = axis == 0 ? vec3(q.z, q.y, q.x) : (axis == 1 ? vec3(q.x, q.z, q.y) : q);
            float s = c.x, t = c.y, along = c.z;
            if (kind == 1) {
                float sum = floor(q.x + 1.0e-4) + floor(q.y + 1.0e-4) + floor(q.z + 1.0e-4);
                return mod(sum, 2.0) < 0.5 ? 0.0 : 1.0;
            }
            if (kind == 2) return min(abs(s - round(s)), abs(t - round(t))) < F.x / 2.0 ? 1.0 : 0.0;
            if (kind == 3) {
                float ds = s - round(s), dt = t - round(t);
                float dist = sqrt(ds * ds + dt * dt), r = F.x / 2.0, soft = F.y * r;
                if (soft <= 0.0) return dist < r ? 1.0 : 0.0;
                return 1.0 - smoothstep(r - soft, r, dist);
            }
            if (kind == 4) return 0.5 + F.x * fractalNoise(q, I, F.y);
            if (kind == 5) {
                float v = (along + F.x * turbulence(q, I, 0.5)) / max(F.y, 1.0e-6);
                return pow(1.0 - abs(sin(3.141592653589793 * v)), max(F.z, 1.0e-3));
            }
            if (kind == 6) {
                float r = sqrt(s * s + t * t);
                float v = (r + F.x * turbulence(q, I, 0.5)) / max(F.y, 1.0e-6);
                return pow(v - floor(v), max(F.z, 1.0e-3));
            }
            if (kind == 7 || kind == 8) {
                float k = 6.283185307179586 / max(F.x, 1.0e-6), sum = 0.0;
                for (int i = 0; i < 16; i++) {
                    if (i >= I) break;
                    float d = length(q - waveSource(i)) * k;
                    sum += kind == 7 ? cos(d) : sin(d + wavePhase(i));
                }
                if (kind == 7) return 0.5 + 0.5 * sum / float(I);
                return pow(1.0 - abs(sum / float(I)), max(F.z, 1.0e-3));
            }
            if (kind == 9) {
                vec3 warp = vec3(fractalNoise(q + vec3(5.2, 1.3, 7.7), I, F.x), fractalNoise(q + vec3(1.7, 9.2, 3.4), I, F.x),
                                 fractalNoise(q + vec3(8.3, 2.8, 4.1), I, F.x));
                return 2.5 * turbulence(q + warp, I, F.x);
            }
            if (kind == 10) return F.x * 2.5 * turbulence(q, I, F.y);
            if (kind == 12) {
                float spacing = max(F.y, 1.0e-6);
                float cs = s / spacing, ct = t / spacing;
                float ds = (cs - round(cs)) * spacing, dt = (ct - round(ct)) * spacing;
                float r = max(F.x, 1.0e-6), x = (ds * ds + dt * dt) / (r * r);
                return x >= 1.0 ? 0.0 : sqrt(1.0 - x);
            }
            return 1.0;
        }
        float amount(int i) {
            vec3 d = (procWorld[i] != 0 ? vWorld : vLocal) - procCenter[i];
            float f = clamp(pattern(procKind[i], procAxis[i], procF[i], procI[i], d / procSize[i]), 0.0, 1.0);
            if (negative[i] != 0) f = 1.0 - f;
            vec3 fall = max(vec3(0.0), vec3(1.0) - procFalloff[i] * abs(d));
            return f * fall.x * fall.y * fall.z;
        }

        vec3 layer(vec3 col, int i, sampler2D t, vec2 uv, float reach) {
            if (reach <= 0.0) return col;
            vec4 s;
            vec3 c;
            if (procKind[i] != 0) {
                s = vec4(1.0, 1.0, 1.0, amount(i));
                c = procColor[i];
            } else {
                s = sampleImage(t, size[i], uv, tiling[i], smoothing[i]);
                c = negative[i] != 0 ? vec3(1.0) - s.rgb : s.rgb;
            }
            float w = clamp(opacity[i], 0.0, 1.0) * s.a * min(reach, 1.0);
            if (blend[i] == 1) return col + c * w;
            if (blend[i] == 2) return col * mix(vec3(1.0), c, w);
            return mix(col, c, w);
        }

        void main() {
            vec3 col = baseColor;
            if (layerCount > 0) col = layer(col, 0, image0, vUv01.xy, vOn.x);
            if (layerCount > 1) col = layer(col, 1, image1, vUv01.zw, vOn.y);
            if (layerCount > 2) col = layer(col, 2, image2, vUv23.xy, vOn.z);
            if (layerCount > 3) col = layer(col, 3, image3, vUv23.zw, vOn.w);
            fragColor = vec4(clamp(col * vLight.w + vLight.rgb, 0.0, 1.0), 1.0);
        }
        """;

    private uint Compile(string vertex, string fragment)
    {
        string header = _es ? "#version 300 es\nprecision highp float;\nprecision highp int;\n" : "#version 330 core\n";
        uint Shader(ShaderType type, string src)
        {
            uint s = _gl.CreateShader(type);
            _gl.ShaderSource(s, header + src);
            _gl.CompileShader(s);
            _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{type} failed to compile: {_gl.GetShaderInfoLog(s)}");
            return s;
        }
        uint v = Shader(ShaderType.VertexShader, vertex), f = Shader(ShaderType.FragmentShader, fragment);
        uint p = _gl.CreateProgram();
        _gl.AttachShader(p, v);
        _gl.AttachShader(p, f);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException($"The shaders failed to link: {_gl.GetProgramInfoLog(p)}");
        _gl.DeleteShader(v);
        _gl.DeleteShader(f);
        return p;
    }

    private void Init()
    {
        if (_ready) return;
        _lineProgram = Compile(LineVertex, LineFragment);
        _faceProgram = Compile(FaceVertex, FaceFragment);
        _textureProgram = Compile(TextureVertex, TextureFragment);
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ready = true;
    }

    /// <summary>
    /// The camera as an OpenGL matrix. Modeler's space is left-handed and GL's right-handed, so the view matrix maps
    /// the view's right/up/forward basis onto GL's x, y and -z: a mirror, which is exactly the handedness change.
    /// </summary>
    public static Mat4 ViewProjection(ViewCamera cam)
    {
        var (r, u, f) = cam.Basis();
        var eye = cam.IsPerspective ? cam.Eye : cam.Center;
        var view = Mat4.FromRows(
            r.X, r.Y, r.Z, -Vec3.Dot(r, eye),
            u.X, u.Y, u.Z, -Vec3.Dot(u, eye),
            -f.X, -f.Y, -f.Z, Vec3.Dot(f, eye),
            0, 0, 0, 1);
        double aspect = (double)cam.Width / cam.Height;
        if (cam.IsPerspective)
            return Mat4.Perspective(Mat4.Deg(cam.FieldOfView), aspect, cam.Near, cam.Distance * 1000 + 1000) * view;
        double hw = cam.Width / 2.0 / cam.PixelsPerUnit, hh = cam.Height / 2.0 / cam.PixelsPerUnit;
        double depth = 1e5 / Math.Max(cam.PixelsPerUnit, 1e-9) + 1e4;
        return Mat4.Orthographic(-hw, hw, -hh, hh, -depth, depth) * view;
    }

    public void Render(ViewScene scene, ViewCamera camera)
    {
        Init();
        _gl.Viewport(0, 0, (uint)camera.Width, (uint)camera.Height);
        uint bg = camera.IsPerspective ? Palette.PerspectiveBackground : Palette.Background;
        _gl.ClearColor(((bg >> 16) & 0xFF) / 255f, ((bg >> 8) & 0xFF) / 255f, (bg & 0xFF) / 255f, 1);
        _gl.ClearDepth(1);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        if (!_es) _gl.Enable(EnableCap.ProgramPointSize);

        var mvp = ViewProjection(camera).Transposed().ToFloats();
        bool shaded = camera.IsPerspective && scene.Style != DisplayStyle.Wireframe;

        // Grid and background layers: no depth test.
        _gl.Disable(EnableCap.DepthTest);
        _used.Clear();
        if (BackdropDrawing.For(scene.Backdrops, camera.Kind, scene.ImageBase) is { } reference)
        {
            var (surface, data) = ReferenceTriangles(reference);
            DrawTextured(surface, data, mvp);
        }
        var lines = new List<float>();
        if (scene.ShowGrid) GridLines(camera, lines);
        DrawLines(lines, mvp, PrimitiveType.Lines, 1);
        lines.Clear();
        if (scene.Backdrop != null && !camera.IsPerspective)
        {
            var (backdrop, data) = BackdropTriangles(scene.Backdrop);
            DrawTextured(backdrop, data, mvp);
        }
        foreach (var layer in scene.Layers.Where(l => l.Background)) WireLines(layer.Mesh, Palette.BackgroundWire, lines);
        DrawLines(lines, mvp, PrimitiveType.Lines, 1);

        // Faces, lit on the CPU exactly as the software renderer lights them; textured ones by surface.
        if (shaded)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(1, 1);
            var faces = new List<float>();
            var textured = new List<(TexturedSurface, List<float>)>();
            foreach (var layer in scene.Layers.Where(l => !l.Background)) FaceTriangles(layer, scene, camera, faces, textured);
            DrawFaces(faces, mvp);
            foreach (var (ts, data) in textured) DrawTextured(ts, data, mvp);
            _gl.Disable(EnableCap.PolygonOffsetFill);
        }

        lines.Clear();
        foreach (var layer in scene.Layers.Where(l => !l.Background))
        {
            bool wire = !shaded || scene.Style == DisplayStyle.ShadedWireframe || layer.Subdivided != null;
            if (wire) WireLines(layer.Mesh, layer.Subdivided != null ? Palette.Cage : Palette.Wire, lines);
            if (layer.Subdivided != null && !shaded) WireLines(layer.Subdivided, Palette.Wire, lines);
        }
        DrawLines(lines, mvp, PrimitiveType.Lines, 1);

        // The selection and overlays on top of everything.
        _gl.Disable(EnableCap.DepthTest);
        lines.Clear();
        var points = new List<float>();
        foreach (var layer in scene.Layers.Where(l => !l.Background))
        {
            var mesh = layer.Mesh;
            if (layer.SelectedPolygons != null)
                foreach (int pi in layer.SelectedPolygons.Where(i => i < mesh.Polygons.Count))
                    foreach (var (a, b) in mesh.Polygons[pi].Edges()) AddLine(lines, mesh.Points[a], mesh.Points[b], Palette.Selected);
            if (layer.SelectedEdges != null)
                foreach (var e in layer.SelectedEdges.Where(e => e.A < mesh.Points.Count && e.B < mesh.Points.Count))
                    AddLine(lines, mesh.Points[e.A], mesh.Points[e.B], Palette.Selected);
            foreach (var p in mesh.Polygons.Where(p => p.Count == 1)) AddVertex(points, mesh.Points[p.Points[0]], Palette.Wire);
            if (scene.ShowPoints)
                for (int i = 0; i < mesh.Points.Count; i++) AddVertex(points, mesh.Points[i], Palette.Point);
            if (layer.SelectedPoints != null)
                foreach (int i in layer.SelectedPoints.Where(i => i < mesh.Points.Count)) AddVertex(points, mesh.Points[i], Palette.Selected);
        }
        foreach (var o in scene.Overlay) AddLine(lines, o.A, o.B, o.Color);
        DrawLines(lines, mvp, PrimitiveType.Lines, 1);
        DrawLines(points, mvp, PrimitiveType.Points, 5);
        ReleaseUnusedTextures();
    }

    private static void AddVertex(List<float> v, Vec3 p, uint c)
    {
        v.Add((float)p.X); v.Add((float)p.Y); v.Add((float)p.Z);
        v.Add(((c >> 16) & 0xFF) / 255f); v.Add(((c >> 8) & 0xFF) / 255f); v.Add((c & 0xFF) / 255f); v.Add(((c >> 24) & 0xFF) / 255f);
    }

    private static void AddLine(List<float> v, Vec3 a, Vec3 b, uint c)
    {
        AddVertex(v, a, c);
        AddVertex(v, b, c);
    }

    private static void WireLines(Mesh mesh, uint color, List<float> v)
    {
        foreach (var e in mesh.EdgeMap().Keys) AddLine(v, mesh.Points[e.A], mesh.Points[e.B], color);
    }

    private static void GridLines(ViewCamera cam, List<float> v)
    {
        if (cam.IsPerspective)
        {
            double s = cam.GridStep(30);
            double cx = Math.Round(cam.Center.X / s) * s, cz = Math.Round(cam.Center.Z / s) * s;
            for (int i = -10; i <= 10; i++)
            {
                AddLine(v, new Vec3(cx + i * s, 0, cz - 10 * s), new Vec3(cx + i * s, 0, cz + 10 * s), Math.Abs(cx + i * s) < s / 2 ? Palette.Axis : Palette.Grid);
                AddLine(v, new Vec3(cx - 10 * s, 0, cz + i * s), new Vec3(cx + 10 * s, 0, cz + i * s), Math.Abs(cz + i * s) < s / 2 ? Palette.Axis : Palette.Grid);
            }
            return;
        }
        var (r, u, _) = cam.Basis();
        double step = cam.GridStep();
        double hw = cam.Width / 2.0 / cam.PixelsPerUnit, hh = cam.Height / 2.0 / cam.PixelsPerUnit;
        double cr = Vec3.Dot(cam.Center, r), cu = Vec3.Dot(cam.Center, u);
        var origin = cam.Center - r * cr - u * cu;
        for (double x = Math.Floor((cr - hw) / step) * step; x <= cr + hw; x += step)
        {
            uint c = Math.Abs(x) < step / 2 ? Palette.Axis : (long)Math.Round(x / step) % 10 == 0 ? Palette.GridMajor : Palette.Grid;
            AddLine(v, origin + r * x + u * (cu - hh), origin + r * x + u * (cu + hh), c);
        }
        for (double y = Math.Floor((cu - hh) / step) * step; y <= cu + hh; y += step)
        {
            uint c = Math.Abs(y) < step / 2 ? Palette.Axis : (long)Math.Round(y / step) % 10 == 0 ? Palette.GridMajor : Palette.Grid;
            AddLine(v, origin + u * y + r * (cr - hw), origin + u * y + r * (cr + hw), c);
        }
    }

    /// <summary>
    /// A layer's faces as triangles: untextured ones lit per corner into <paramref name="v"/> (position, colour, normal),
    /// textured ones into a list per surface (see <see cref="AddTextured"/>).
    /// </summary>
    public static void FaceTriangles(ViewLayer layer, ViewScene scene, ViewCamera cam, List<float> v, List<(TexturedSurface Surface, List<float> Vertices)> textured)
    {
        var mesh = layer.Subdivided ?? layer.Mesh;
        var selected = layer.Subdivided == null ? layer.SelectedPolygons : null;
        var space = layer.Subdivided ?? layer.TextureSpace ?? layer.Mesh;
        var light = Shading.Light(cam);
        var corners = Shading.CornerNormals(mesh, scene.Surfaces);
        var prepared = new Dictionary<int, (TexturedSurface Surface, List<float> Vertices)?>();
        Span<TexturedCorner> tc = stackalloc TexturedCorner[3];
        foreach (var t in Tessellation.Of(mesh))
        {
            var poly = mesh.Polygons[t.Polygon];
            var n = mesh.Normal(poly);
            var surface = Shading.SurfaceOf(scene, poly);
            bool sel = selected != null && selected.Contains(t.Polygon);
            if (scene.ShowTextures && surface != null)
            {
                if (!prepared.TryGetValue(poly.Surface, out var batch))
                {
                    var made = Texturing.Prepare(surface, layer.ImageBase ?? scene.ImageBase);
                    batch = made == null ? null : (made, new List<float>());
                    if (batch is { } b) textured.Add(b);
                    prepared[poly.Surface] = batch;
                }
                if (batch is var (ts, list))
                {
                    Texturing.Corners(ts, mesh, space, t.Polygon, t.A, t.B, t.C, point => Shading.FaceLight(n, mesh.Points[point], surface, cam, light,
                        scene.BackfaceTint, sel, surface.Smoothing ? Shading.Corner(corners, mesh, t.Polygon, point) : null), tc);
                    AddTextured(list, mesh.Points[t.A], tc[0]);
                    AddTextured(list, mesh.Points[t.B], tc[1]);
                    AddTextured(list, mesh.Points[t.C], tc[2]);
                    continue;
                }
            }
            foreach (int i in new[] { t.A, t.B, t.C })
            {
                var smooth = surface is { Smoothing: true } ? Shading.Corner(corners, mesh, t.Polygon, i) : (Vec3?)null;
                var col = Shading.FaceColor(n, mesh.Points[i], surface, cam, light, scene.BackfaceTint, sel, smooth);
                var p = mesh.Points[i];
                var nn = smooth ?? n;
                v.Add((float)p.X); v.Add((float)p.Y); v.Add((float)p.Z);
                v.Add((float)col.X); v.Add((float)col.Y); v.Add((float)col.Z); v.Add(1);
                v.Add((float)nn.X); v.Add((float)nn.Y); v.Add((float)nn.Z);
            }
        }
    }

    /// <summary>
    /// How many floats a textured vertex has: position, lighting (add, mul), two pairs of UVs per vec4, the layers'
    /// reach, and the point in object and world coordinates (for procedurals).
    /// </summary>
    public const int TexturedVertexFloats = 25;

    /// <summary>One textured vertex: position, the lighting's offset and multiplier, the four layers' UVs, their reach, and the point in object and world coordinates.</summary>
    public static void AddTextured(List<float> v, Vec3 p, in TexturedCorner c)
    {
        v.Add((float)p.X); v.Add((float)p.Y); v.Add((float)p.Z);
        v.Add((float)c.Add.X); v.Add((float)c.Add.Y); v.Add((float)c.Add.Z); v.Add((float)c.Mul);
        v.Add((float)c.Uv0.X); v.Add((float)c.Uv0.Y); v.Add((float)c.Uv1.X); v.Add((float)c.Uv1.Y);
        v.Add((float)c.Uv2.X); v.Add((float)c.Uv2.Y); v.Add((float)c.Uv3.X); v.Add((float)c.Uv3.Y);
        v.Add((float)c.On0); v.Add((float)c.On1); v.Add((float)c.On2); v.Add((float)c.On3);
        v.Add((float)c.Local.X); v.Add((float)c.Local.Y); v.Add((float)c.Local.Z);
        v.Add((float)c.World.X); v.Add((float)c.World.Y); v.Add((float)c.World.Z);
    }

    /// <summary>Perlin's permutation table (<see cref="Noise.Permutation"/>) as a 256 × 1 image, in its red channel, for the shader.</summary>
    private static readonly TextureImage PermutationImage = new(256, 1, Noise.Permutation.SelectMany(b => new[] { b, (byte)0, (byte)0, (byte)255 }).ToArray());

    /// <summary>The UV view's image as two textured triangles over the 0–1 square, as the software renderer draws it.</summary>
    public static (TexturedSurface Surface, List<float> Vertices) BackdropTriangles(TextureImage image)
    {
        var ts = Texturing.Backdrop(image);
        var v = new List<float>();
        foreach (var p in new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0) })
        {
            var c = new TexturedCorner { Mul = Texturing.BackdropBrightness };
            c.Set(0, new Vec2(p.X, p.Y), 1);
            AddTextured(v, p, c);
        }
        return (ts, v);
    }

    /// <summary>A flat view's reference image as two textured triangles, corner for corner as the software renderer draws it.</summary>
    public static (TexturedSurface Surface, List<float> Vertices) ReferenceTriangles(BackdropQuad quad)
    {
        var v = new List<float>();
        foreach (int i in BackdropQuad.Triangles) AddTextured(v, quad.Corners[i], quad.Corner(i));
        return (quad.Surface, v);
    }

    /// <summary>A GL texture holding an image's pixels (top row first, as texel row 0), made on first use.</summary>
    private uint TextureOf(TextureImage image)
    {
        _used.Add(image);
        if (_textures.TryGetValue(image, out uint t)) return t;
        t = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, t);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = image.Rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        _textures[image] = t;
        return t;
    }

    private void ReleaseUnusedTextures()
    {
        foreach (var image in _textures.Keys.Where(i => !_used.Contains(i)).ToList())
        {
            _gl.DeleteTexture(_textures[image]);
            _textures.Remove(image);
        }
    }

    private void DrawTextured(TexturedSurface ts, List<float> data, float[] mvp)
    {
        if (data.Count == 0) return;
        _gl.UseProgram(_textureProgram);
        for (int i = 0; i < Texturing.MaxLayers; i++)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + i);
            var (layer, image) = i < ts.Layers.Count ? ts.Layers[i] : ts.Layers[0];
            _gl.BindTexture(TextureTarget.Texture2D, TextureOf(image));
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"image{i}"), i);
            _gl.Uniform2(_gl.GetUniformLocation(_textureProgram, $"size[{i}]"), image.Width, image.Height);
            _gl.Uniform2(_gl.GetUniformLocation(_textureProgram, $"tiling[{i}]"), (int)layer.WidthTiling, (int)layer.HeightTiling);
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"blend[{i}]"), (int)layer.Blend);
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"opacity[{i}]"), (float)layer.Opacity);
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"negative[{i}]"), layer.Negative ? 1 : 0);
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"smoothing[{i}]"), layer.PixelBlending ? 1 : 0);
            var proc = i < ts.Layers.Count ? ts.ProceduralOf(i) : null;
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"procKind[{i}]"), proc == null ? 0 : (int)proc.Kind);
            if (proc == null) continue;
            static float F(double d) => (float)d;
            _gl.Uniform3(_gl.GetUniformLocation(_textureProgram, $"procCenter[{i}]"), F(proc.Center.X), F(proc.Center.Y), F(proc.Center.Z));
            _gl.Uniform3(_gl.GetUniformLocation(_textureProgram, $"procSize[{i}]"), F(proc.Size.X), F(proc.Size.Y), F(proc.Size.Z));
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"procAxis[{i}]"), (int)proc.Axis);
            var f = proc.Parameters;
            _gl.Uniform4(_gl.GetUniformLocation(_textureProgram, $"procF[{i}]"), F(f[0]), F(f[1]), F(f[2]), F(f[3]));
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"procI[{i}]"), proc.Count);
            _gl.Uniform3(_gl.GetUniformLocation(_textureProgram, $"procColor[{i}]"), F(proc.Color.X), F(proc.Color.Y), F(proc.Color.Z));
            _gl.Uniform3(_gl.GetUniformLocation(_textureProgram, $"procFalloff[{i}]"), F(proc.Falloff.X), F(proc.Falloff.Y), F(proc.Falloff.Z));
            _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, $"procWorld[{i}]"), proc.WorldCoordinates ? 1 : 0);
        }
        _gl.ActiveTexture(TextureUnit.Texture0 + Texturing.MaxLayers);
        _gl.BindTexture(TextureTarget.Texture2D, TextureOf(PermutationImage));
        _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, "permTable"), Texturing.MaxLayers);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.Uniform3(_gl.GetUniformLocation(_textureProgram, "baseColor"), (float)ts.Color.X, (float)ts.Color.Y, (float)ts.Color.Z);
        _gl.Uniform1(_gl.GetUniformLocation(_textureProgram, "layerCount"), ts.Layers.Count);
        SetMatrix(_textureProgram, "mvp", mvp);
        Upload(data);
        const int stride = TexturedVertexFloats * sizeof(float);
        int[] sizes = [3, 4, 4, 4, 4, 3, 3];
        int offset = 0;
        for (int a = 0; a < sizes.Length; a++)
        {
            _gl.EnableVertexAttribArray((uint)a);
            _gl.VertexAttribPointer((uint)a, sizes[a], VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
            offset += sizes[a];
        }
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(data.Count / TexturedVertexFloats));
        for (uint a = 3; a < sizes.Length; a++) _gl.DisableVertexAttribArray(a);
    }

    private void Upload(List<float> data)
    {
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data);
        fixed (float* p = span)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(span.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
    }

    private void DrawLines(List<float> data, float[] mvp, PrimitiveType type, float pointSize)
    {
        if (data.Count == 0) return;
        Upload(data);
        const int stride = 7 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.DisableVertexAttribArray(2);
        _gl.UseProgram(_lineProgram);
        SetMatrix(_lineProgram, "mvp", mvp);
        _gl.Uniform1(_gl.GetUniformLocation(_lineProgram, "pointSize"), pointSize);
        _gl.DrawArrays(type, 0, (uint)(data.Count / 7));
    }

    private void DrawFaces(List<float> data, float[] mvp)
    {
        if (data.Count == 0) return;
        Upload(data);
        const int stride = 10 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float)));
        _gl.UseProgram(_faceProgram);
        SetMatrix(_faceProgram, "mvp", mvp);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(data.Count / 10));
    }

    private void SetMatrix(uint program, string name, float[] m)
    {
        fixed (float* p = m) _gl.UniformMatrix4(_gl.GetUniformLocation(program, name), 1, false, p);
    }

    public void Dispose()
    {
        if (!_ready) return;
        _gl.DeleteProgram(_lineProgram);
        _gl.DeleteProgram(_faceProgram);
        _gl.DeleteProgram(_textureProgram);
        foreach (uint t in _textures.Values) _gl.DeleteTexture(t);
        _textures.Clear();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _ready = false;
    }
}
