using ImageMagick;

namespace CardViewer;

internal static class ThumbnailDecoder
{
    internal static MagickImage Read(byte[] bytes, int size)
    {
        var settings = new MagickReadSettings();
        if (size > 0 && bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            // Ask libjpeg to downscale during decoding, before allocating full-size pixels.
            // This affects decode cost; it does not bound compressed source bytes read.
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{size}x{size}");
        }
        return new MagickImage(bytes, settings);
    }
}
