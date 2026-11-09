using AVAModeler.Core;
using Silk.NET.OpenGL;

namespace AVAModeler.Rendering.Tests;

/// <summary>Sizing an OpenGL view from the framebuffer itself.</summary>
[Collection("OpenGL")]
public class GlFramebufferTests
{
    [Fact]
    public void The_size_comes_from_the_attachment_then_the_viewport_then_the_bounds()
    {
        // At 2× with a framebuffer the toolkit made 2× the bounds: the attachment says so, whatever the scaling reads.
        Assert.Equal((800, 600, "attachment"), GlFramebuffer.Choose((800, 600), (400, 300), 400, 300, 1));
        Assert.Equal((800, 600, "viewport"), GlFramebuffer.Choose(null, (800, 600), 400, 300, 1));
        // A viewport of a pixel (never set) isn't believed.
        Assert.Equal((800, 600, "bounds"), GlFramebuffer.Choose(null, (1, 1), 400, 300, 2));
        Assert.Equal((601, 1, "bounds"), GlFramebuffer.Choose((0, 0), null, 400.4, 0, 1.5));
    }

    [Fact]
    public void A_real_framebuffer_reports_its_attachments_size()
    {
        using var gl = OffscreenGl.TryCreate(320, 200, out string why);
        if (gl == null)
        {
            Assert.Skip($"No OpenGL here ({why}).");
            return;
        }
        // The offscreen framebuffer's colour is a renderbuffer.
        Assert.Equal((320, 200), GlFramebuffer.AttachmentSize(gl.Gl));
        gl.Gl.Viewport(0, 0, 123, 45);
        Assert.Equal((123, 45), GlFramebuffer.ViewportSize(gl.Gl));

        // A framebuffer with a texture for its colour, as Avalonia's may be: the texture's size, and its binding put back.
        var api = gl.Gl;
        uint fbo = api.GenFramebuffer(), tex = api.GenTexture(), other = api.GenTexture();
        api.BindTexture(TextureTarget.Texture2D, tex);
        api.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 640, 360, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new byte[640 * 360 * 4]);
        api.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        api.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        api.BindTexture(TextureTarget.Texture2D, other);
        var size = GlFramebuffer.AttachmentSize(api);
        // OpenGL ES before 3.1 can't say a texture's size: then null, and the viewport is used instead.
        if (size != null) Assert.Equal((640, 360), size);
        api.GetInteger(GLEnum.TextureBinding2D, out int bound);
        Assert.Equal((int)other, bound);
        api.DeleteFramebuffer(fbo);
        api.DeleteTexture(tex);
        api.DeleteTexture(other);
    }

    [Fact]
    public void The_renderer_fills_a_framebuffer_sized_from_its_attachment()
    {
        // Drawn at the size the attachment gives, a square at the middle of the view lands at the middle of the picture.
        using var gl = OffscreenGl.TryCreate(200, 120, out string why);
        if (gl == null)
        {
            Assert.Skip($"No OpenGL here ({why}).");
            return;
        }
        var size = GlFramebuffer.Choose(GlFramebuffer.AttachmentSize(gl.Gl), GlFramebuffer.ViewportSize(gl.Gl), 100, 60, 1);
        Assert.Equal((200, 120, "attachment"), size);
        var cam = new ViewCamera(ViewKind.Front) { PixelsPerUnit = 40 };
        cam.Resize(size.Width, size.Height);
        var mesh = new Mesh();
        mesh.AddFace(0, new Vec3(-0.25, -0.25, 0), new Vec3(-0.25, 0.25, 0), new Vec3(0.25, 0.25, 0), new Vec3(0.25, -0.25, 0));
        using var renderer = new GlRenderer(gl.Gl, gl.IsEs);
        renderer.Render(new ViewScene { Layers = [new ViewLayer { Mesh = mesh }], ShowGrid = false }, cam);
        var px = gl.Read();
        uint bg = px[0];
        // The square's wire sits round the middle, not in the bottom-left corner.
        var lit = Enumerable.Range(0, px.Length).Where(i => px[i] != bg).ToList();
        Assert.NotEmpty(lit);
        double mx = lit.Average(i => i % 200), my = lit.Average(i => i / 200);
        Assert.InRange(mx, 90, 110);
        Assert.InRange(my, 50, 70);
    }
}
