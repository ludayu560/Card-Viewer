using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace CardViewer;

public static partial class SelfTests
{
    private static async Task VerifyJpegOrientationAndPngTransparencyAsync(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "standard-images");
        Directory.CreateDirectory(folder);
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Blue, 800, 400))
        {
            image.Orientation = ImageMagick.OrientationType.RightTop;
            var exif = new ImageMagick.ExifProfile();
            exif.SetValue(ImageMagick.ExifTag.Orientation, (ushort)6);
            image.SetProfile(exif);
            image.Write(Path.Combine(folder, "rotated.jpg"));
            image.Write(Path.Combine(folder, "rotated.JPEG"));
        }
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Transparent, 400, 800))
            image.Write(Path.Combine(folder, "alpha.PNG"));
        foreach (var name in new[] { ".hidden.PNG", "._rotated.jpg", "ignored.txt" })
            await File.WriteAllTextAsync(Path.Combine(folder, name), "ignored fixture");
        var files = new DirectoryInfo(folder).EnumerateFiles().Where(ImageFileFilter.IsSupported).ToArray();
        check(files.Length == 3, "Mixed discovery includes JPG, JPEG and PNG while excluding dot files and unsupported formats");
        var hashes = files.ToDictionary(f => f.FullName, f => SHA256.HashData(File.ReadAllBytes(f.FullName)));
        var cache = Path.Combine(root, "standard-cache");
        await using var store = new ThumbnailStore(cache);
        var items = files.Select(f => new ImageFile(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks)).ToArray();
        foreach (var item in items)
        {
            int reads = store.SourceReads;
            bool png = item.Name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase);
            check(await store.GetEmbeddedAsync(item, 10, default) == null && store.SourceReads == reads + (png ? 0 : 1), "PNG skips probing; JPEG without EXIF thumbnail has a bounded pass-1 miss: " + item.Name);
            var grid = await store.GetAsync(item, default);
            check(grid.IsFrozen && grid.PixelHeight > grid.PixelWidth && Math.Max(grid.PixelWidth, grid.PixelHeight) <= 320, "Oriented grid thumbnail: " + item.Name);
            var preview = await store.GetPreviewAsync(item, default);
            check(preview.IsFrozen && Math.Max(preview.PixelWidth, preview.PixelHeight) <= 1600, "Viewer preview: " + item.Name);
            var full = await store.GetFullImageAsync(item, default);
            check(full.PixelWidth == 400 && full.PixelHeight == 800, "Full-image decoder auto-detects format and preserves oriented native dimensions: " + item.Name);
            check(await store.GetDimensionsAsync(item, default) == new System.Windows.Size(400, 800), "Metadata sizing preserves original orientation: " + item.Name);
            if (item.Name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase))
            {
                static byte Alpha(System.Windows.Media.Imaging.BitmapSource bitmap)
                {
                    var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    var pixel = new byte[4];
                    converted.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                    return pixel[3];
                }
                check(Alpha(grid) == 0 && Alpha(preview) == 0 && Alpha(full) == 0, "PNG transparency survives thumbnails, viewer previews and full-image decoding");
            }
            check(await store.ResolveFullImageAsync(item, default) == item, "Standard image without a RAF partner views its own source");
        }
        await using var warm = new ThumbnailStore(cache, new ThrowDecoder());
        var benchmark = await ThumbnailBenchmark.RunAsync(warm, items);
        check(benchmark.Loaded == 3 && benchmark.CacheHits == 3 && benchmark.SourceReads == 0, "Warm JPG/JPEG/PNG grid loads from persistent cache without source reads");
        check(await store.ClearAsync() == 3, "Clearing removes JPEG and transparent PNG grid caches only");
        foreach (var f in files)
            check(hashes[f.FullName].SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.FullName))), "Standard source unchanged " + f.Name);
        await VerifyBoundedEmbeddedJpegPreviewReadsAsync(root, check);
    }

    private static async Task VerifyBoundedEmbeddedJpegPreviewReadsAsync(string root, Action<bool, string> check)
    {
        byte[] main, thumbnail;
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Blue, 4000, 2000))
            main = image.ToByteArray(ImageMagick.MagickFormat.Jpeg);
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 160, 80))
            thumbnail = image.ToByteArray(ImageMagick.MagickFormat.Jpeg);

        using (var scaled = ThumbnailDecoder.Read(main, 320))
            check(scaled.Width < 4000 && scaled.Height < 2000 && Math.Max(scaled.Width, scaled.Height) >= 320, "JPEG decoder produces reduced pixels before the final grid resize");
        using (var scaled = ThumbnailDecoder.Read(main, 1600))
            check(scaled.Width < 4000 && Math.Max(scaled.Width, scaled.Height) >= 1600, "JPEG viewer preview also uses reduced-resolution decoding");

        byte[] WithPreview(bool littleEndian, byte[] preview, bool invalidOffset = false)
        {
            const int thumbnailOffset = 68;
            var tiff = new byte[thumbnailOffset + preview.Length];
            void U16(int at, ushort value)
            {
                if (littleEndian)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), value);
                else
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), value);
            }
            void U32(int at, uint value)
            {
                if (littleEndian)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at), value);
                else
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(at), value);
            }
            void Entry(int at, ushort tag, ushort type, uint value)
            {
                U16(at, tag);
                U16(at + 2, type);
                U32(at + 4, 1);
                if (type == 3)
                    U16(at + 8, (ushort)value);
                else
                    U32(at + 8, value);
            }
            tiff[0] = tiff[1] = (byte)(littleEndian ? 'I' : 'M');
            U16(2, 42);
            U32(4, 8);
            U16(8, 1);
            Entry(10, 0x0112, 3, 1);
            U32(22, 26);
            U16(26, 3);
            Entry(28, 0x0112, 3, 6);
            Entry(40, 0x0201, 4, invalidOffset ? uint.MaxValue : thumbnailOffset);
            Entry(52, 0x0202, 4, (uint)preview.Length);
            preview.CopyTo(tiff, thumbnailOffset);
            using var output = new MemoryStream();
            output.Write(new byte[] { 0xff, 0xd8, 0xff, 0xe1 });
            int length = tiff.Length + 8;
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)length);
            output.Write("Exif\0\0"u8);
            output.Write(tiff);
            output.Write(main.AsSpan(2));
            return output.ToArray();
        }

        var folder = Path.Combine(root, "jpeg-previews");
        Directory.CreateDirectory(folder);
        await using var store = new ThumbnailStore(Path.Combine(root, "jpeg-preview-cache"));
        foreach (bool littleEndian in new[] { true, false })
        {
            var bytes = WithPreview(littleEndian, thumbnail);
            using var stream = new MemoryStream(bytes);
            var parsed = await JpegMetadata.ReadAsync(stream, default);
            check(parsed.Preview != null && parsed.Preview.Orientation == 6 && parsed.Preview.Bytes.SequenceEqual(thumbnail) && parsed.BytesRead < 32 * 1024, "Bounded EXIF thumbnail extraction and IFD1 orientation, little-endian=" + littleEndian);
            string path = Path.Combine(folder, littleEndian + ".jpg");
            await File.WriteAllBytesAsync(path, bytes);
            var info = new FileInfo(path);
            var file = new ImageFile(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            var embedded = await store.GetEmbeddedAsync(file, 10, default);
            check(embedded != null && embedded.PixelWidth == 80 && embedded.PixelHeight == 160, "Pass 1 displays the embedded JPEG with its own orientation");
            var grid = await store.GetAsync(file, default);
            check(grid.PixelWidth == 320 && grid.PixelHeight == 160, "Pass 2 replaces the small JPEG preview with scaled main-image pixels and main-image orientation");
            var unchanged = await File.ReadAllBytesAsync(path);
            check(bytes.SequenceEqual(unchanged), "JPEG preview extraction and scaled decoding preserve source bytes");
        }

        foreach (var bytes in new[] { main, WithPreview(true, thumbnail, true), WithPreview(true, new byte[33 * 1024]), new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0, 1 } })
        {
            using var stream = new MemoryStream(bytes);
            var parsed = await JpegMetadata.ReadAsync(stream, default);
            check(parsed.Preview == null && parsed.BytesRead <= JpegMetadata.ReadBudget, "Missing, malformed, out-of-range or over-budget JPEG preview safely misses without reading full pixels");
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool stopped = false;
        try
        {
            using var stream = new MemoryStream(main);
            await JpegMetadata.ReadAsync(stream, canceled.Token);
        }
        catch (OperationCanceledException) { stopped = true; }
        check(stopped, "JPEG metadata probing honors cancellation");
    }

    private sealed class ThrowDecoder : IThumbnailDecoder
    {
        public byte[] Decode(byte[] bytes) => throw new Exception("Unexpected decode");
    }
}
