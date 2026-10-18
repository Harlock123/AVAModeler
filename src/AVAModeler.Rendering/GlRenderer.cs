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
    private uint _lineProgram, _faceProgram, _vao, _vbo;
    private bool _ready;

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

    private uint Compile(string vertex, string fragment)
    {
        string header = _es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
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
        var lines = new List<float>();
        if (scene.ShowGrid) GridLines(camera, lines);
        foreach (var layer in scene.Layers.Where(l => l.Background)) WireLines(layer.Mesh, Palette.BackgroundWire, lines);
        DrawLines(lines, mvp, PrimitiveType.Lines, 1);

        // Faces, lit on the CPU exactly as the software renderer lights them.
        if (shaded)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(1, 1);
            var faces = new List<float>();
            foreach (var layer in scene.Layers.Where(l => !l.Background))
                FaceTriangles(layer.Subdivided ?? layer.Mesh, scene, camera, layer.Subdivided == null ? layer.SelectedPolygons : null, faces);
            DrawFaces(faces, mvp);
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

    private static void FaceTriangles(Mesh mesh, ViewScene scene, ViewCamera cam, IReadOnlySet<int>? selected, List<float> v)
    {
        var light = Shading.Light(cam);
        var corners = Shading.CornerNormals(mesh, scene.Surfaces);
        foreach (var t in Tessellation.Of(mesh))
        {
            var poly = mesh.Polygons[t.Polygon];
            var n = mesh.Normal(poly);
            var surface = Shading.SurfaceOf(scene, poly);
            bool sel = selected != null && selected.Contains(t.Polygon);
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
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _ready = false;
    }
}
