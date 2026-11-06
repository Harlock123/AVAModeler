using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace AVAModeler.Rendering;

/// <summary>
/// An OpenGL 3.3 core (or OpenGL ES 3) context with no window (EGL's surfaceless platform, as Mesa offers it), drawing into a
/// framebuffer of its own, for testing the OpenGL renderer for real (this engine's tests and another app's, such as
/// AVALightWave comparing its OpenGL and software Layout pictures). <see cref="TryCreate"/> gives null, with the reason,
/// where there's no such thing (no EGL library, as on Windows and macOS, or no driver): tests then skip. The context is
/// current on the thread that made it; draw with <see cref="GlRenderer"/> on <see cref="Gl"/> and read with <see cref="Read"/>.
/// </summary>
public sealed class OffscreenGl : IDisposable
{
    private const string Egl = "libEGL.so.1";
    private const int PlatformSurfacelessMesa = 0x31DD, OpenGlApi = 0x30A2, OpenGlEsApi = 0x30A0, None = 0x3038;
    private const int ContextMajor = 0x3098, ContextMinor = 0x30FB, ProfileMask = 0x30FD, CoreProfileBit = 1;

    [DllImport(Egl)] private static extern IntPtr eglGetPlatformDisplay(int platform, IntPtr nativeDisplay, IntPtr attribs);
    [DllImport(Egl)] private static extern bool eglInitialize(IntPtr display, out int major, out int minor);
    [DllImport(Egl)] private static extern bool eglBindAPI(int api);
    [DllImport(Egl)] private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attribs);
    [DllImport(Egl)] private static extern bool eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    [DllImport(Egl)] private static extern IntPtr eglGetProcAddress(string name);
    [DllImport(Egl)] private static extern bool eglDestroyContext(IntPtr display, IntPtr context);
    [DllImport(Egl)] private static extern bool eglTerminate(IntPtr display);
    [DllImport(Egl)] private static extern int eglGetError();

    /// <summary>
    /// Every context shares the one surfaceless display, and terminating it pulls the rug from under any other context
    /// still drawing (a crash, when tests run side by side): so contexts are made and let go one at a time, and the
    /// display is terminated only when the last goes.
    /// </summary>
    private static readonly Lock Gate = new();
    private static int _open;

    private readonly IntPtr _display, _context;
    private bool _disposed;
    private readonly uint _framebuffer, _color, _depth;
    public GL Gl { get; }
    /// <summary>Whether it's an OpenGL ES context (the renderer then speaks its shader dialect).</summary>
    public bool IsEs { get; private init; }
    public int Width { get; }
    public int Height { get; }

    private OffscreenGl(IntPtr display, IntPtr context, int width, int height)
    {
        _display = display;
        _context = context;
        Width = width;
        Height = height;
        Gl = GL.GetApi(eglGetProcAddress);
        _framebuffer = Gl.GenFramebuffer();
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _color = Gl.GenRenderbuffer();
        Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
        Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)width, (uint)height);
        Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _color);
        _depth = Gl.GenRenderbuffer();
        Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height);
        Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
        if (Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("The offscreen framebuffer isn't complete.");
    }

    /// <summary>A context and a framebuffer of a size, or null (with why) where OpenGL can't be had.</summary>
    public static OffscreenGl? TryCreate(int width, int height, out string why)
    {
        lock (Gate) return Create(width, height, out why);
    }

    private static OffscreenGl? Create(int width, int height, out string why)
    {
        why = "";
        try
        {
            var display = eglGetPlatformDisplay(PlatformSurfacelessMesa, IntPtr.Zero, IntPtr.Zero);
            if (display == IntPtr.Zero || !eglInitialize(display, out _, out _)) { why = "no surfaceless EGL display"; return null; }
            // Desktop OpenGL 3.3 core if the driver has it, else OpenGL ES 3 (as a virtual machine's GPU may only offer).
            // With no surface the context needs no config (EGL_KHR_no_config_context).
            bool es = false;
            var context = eglBindAPI(OpenGlApi)
                ? eglCreateContext(display, IntPtr.Zero, IntPtr.Zero, [ContextMajor, 3, ContextMinor, 3, ProfileMask, CoreProfileBit, None])
                : IntPtr.Zero;
            if (context == IntPtr.Zero && eglBindAPI(OpenGlEsApi))
            {
                context = eglCreateContext(display, IntPtr.Zero, IntPtr.Zero, [ContextMajor, 3, None]);
                es = context != IntPtr.Zero;
            }
            if (context == IntPtr.Zero) { why = $"no OpenGL 3.3 or OpenGL ES 3 context (EGL error 0x{eglGetError():X})"; return null; }
            if (!eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, context)) { why = $"can't use a context without a surface (EGL error 0x{eglGetError():X})"; return null; }
            var made = new OffscreenGl(display, context, width, height) { IsEs = es };
            _open++;
            return made;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            why = e.Message;
            return null;
        }
    }

    /// <summary>What was drawn, as 0xAARRGGBB words, top row first (as the software renderer has them).</summary>
    public uint[] Read()
    {
        var rgba = new byte[Width * Height * 4];
        Gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan());
        var pixels = new uint[Width * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = ((Height - 1 - y) * Width + x) * 4;
                pixels[y * Width + x] = 0xFF000000u | (uint)rgba[i] << 16 | (uint)rgba[i + 1] << 8 | rgba[i + 2];
            }
        return pixels;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            Gl.DeleteFramebuffer(_framebuffer);
            Gl.DeleteRenderbuffer(_color);
            Gl.DeleteRenderbuffer(_depth);
            eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            eglDestroyContext(_display, _context);
            if (--_open == 0) eglTerminate(_display);
        }
    }
}
