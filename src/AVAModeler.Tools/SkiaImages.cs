using System.Runtime.InteropServices;
using AVAModeler.Core;
using SkiaSharp;

namespace AVAModeler.Tools;

/// <summary>
/// Texture images in the formats SkiaSharp reads (JPEG, GIF, WebP and the rest, beyond the PNG and BMP that Core
/// reads itself), for <see cref="TextureImages"/>. The app and the command API turn it on.
/// </summary>
public static class SkiaImages
{
    private static int _registered;

    /// <summary>Adds the SkiaSharp reader to <see cref="TextureImages.Decoders"/> (once, however often it's called).</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0) TextureImages.Decoders.Add(Decode);
    }

    /// <summary>An image file's bytes as RGBA pixels (not premultiplied), or null if SkiaSharp can't read them.</summary>
    public static TextureImage? Decode(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream);
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        }
        finally
        {
            handle.Free();
        }
        return new TextureImage(info.Width, info.Height, pixels);
    }
}
