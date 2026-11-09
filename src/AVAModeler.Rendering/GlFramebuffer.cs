using Silk.NET.OpenGL;

namespace AVAModeler.Rendering;

/// <summary>
/// What OpenGL says about the framebuffer bound for drawing: its colour attachment's size, and the viewport. A view
/// is sized from these rather than from its control's bounds times a scaling, which can disagree with the framebuffer
/// the toolkit really made (at 2× on Wayland the picture filled only the bottom-left of each view).
/// </summary>
public static class GlFramebuffer
{
    /// <summary>
    /// The bound framebuffer's colour attachment's size, a renderbuffer's or a texture's, or null when OpenGL can't say
    /// (the default framebuffer, which has no attachment object, or an OpenGL ES without glGetTexLevelParameter).
    /// Puts back the renderbuffer or texture binding it borrows.
    /// </summary>
    public static (int Width, int Height)? AttachmentSize(GL gl)
    {
        try
        {
            while (gl.GetError() != GLEnum.NoError) { }
            gl.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                FramebufferAttachmentParameterName.ObjectType, out int type);
            if (gl.GetError() != GLEnum.NoError) return null;
            gl.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                FramebufferAttachmentParameterName.ObjectName, out int name);
            if (gl.GetError() != GLEnum.NoError || name == 0) return null;
            int w = 0, h = 0;
            if (type == (int)GLEnum.Renderbuffer)
            {
                gl.GetInteger(GLEnum.RenderbufferBinding, out int was);
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, (uint)name);
                gl.GetRenderbufferParameter(RenderbufferTarget.Renderbuffer, RenderbufferParameterName.Width, out w);
                gl.GetRenderbufferParameter(RenderbufferTarget.Renderbuffer, RenderbufferParameterName.Height, out h);
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, (uint)was);
            }
            else if (type == (int)GLEnum.Texture)
            {
                gl.GetInteger(GLEnum.TextureBinding2D, out int was);
                gl.BindTexture(TextureTarget.Texture2D, (uint)name);
                gl.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out w);
                gl.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out h);
                gl.BindTexture(TextureTarget.Texture2D, (uint)was);
            }
            bool ok = gl.GetError() == GLEnum.NoError;
            return ok && w > 0 && h > 0 ? (w, h) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The viewport's width and height as OpenGL has it set now, or null if it won't say.</summary>
    public static unsafe (int Width, int Height)? ViewportSize(GL gl)
    {
        int* v = stackalloc int[4];
        gl.GetInteger(GLEnum.Viewport, v);
        return gl.GetError() == GLEnum.NoError ? (v[2], v[3]) : null;
    }

    /// <summary>
    /// The size to draw at: the attachment's when OpenGL can say, else the viewport left set if it's a real size, else
    /// the bounds times <paramref name="scaling"/>, with which of the three it was.
    /// </summary>
    public static (int Width, int Height, string From) Choose((int W, int H)? attachment, (int W, int H)? viewport, double width, double height, double scaling)
    {
        if (attachment is { W: > 0, H: > 0 } a) return (a.W, a.H, "attachment");
        if (viewport is { W: > 1, H: > 1 } v) return (v.W, v.H, "viewport");
        return (Math.Max(1, (int)Math.Round(width * scaling)), Math.Max(1, (int)Math.Round(height * scaling)), "bounds");
    }
}
