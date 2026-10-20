using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
namespace CardViewer;

public static class FullImageDecoder
{
    internal static MagickReadSettings Settings(bool raf)
    {
        var settings = new MagickReadSettings();
        if (raf)
            settings.Format = MagickFormat.Raf;
        // RAW processing must unpack/demosaic the sensor image, never a JPEG thumbnail.
        settings.SetDefine(MagickFormat.Dng, "read-thumbnails", "false");
        settings.SetDefine(MagickFormat.Dng, "read-thumbnail", "false");
        settings.SetDefine(MagickFormat.Dng, "use-camera-wb", "true");
        return settings;
    }
    public static BitmapSource Decode(byte[] source, bool raf, CancellationToken token, Action<double>? measured = null)
    {
        var pixels = DecodePixels(source, raf, token, measured);
        token.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Pixels, checked(pixels.Width * 4));
        bitmap.Freeze();
        return bitmap;
    }
    internal static (int Width, int Height, byte[] Pixels) DecodePixels(byte[] source, bool raf, CancellationToken token, Action<double>? measured = null)
    {
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        try
        {
            using var decoded = new MagickImage(source, Settings(raf));
            token.ThrowIfCancellationRequested();
            decoded.AutoOrient();
            decoded.ColorSpace = ColorSpace.sRGB;
            decoded.Depth = 8;
            // No resize or JPEG encoding: retain full sensor-image dimensions in memory.
            var pixels = decoded.ToByteArray(MagickFormat.Bgra);
            token.ThrowIfCancellationRequested();
            return (checked((int)decoded.Width), checked((int)decoded.Height), pixels);
        }
        finally { measured?.Invoke(timer.Elapsed.TotalMilliseconds); }
    }
}
