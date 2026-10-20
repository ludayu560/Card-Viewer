using System.IO;
using System.Security.Cryptography;
using ImageMagick;

namespace CardViewer;

internal static class FormatTests
{
    internal static async Task RunAsync(string root, string sampleFolder, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "format-fixtures");
        Directory.CreateDirectory(folder);
        var formats = new (string Extension, MagickFormat Format)[]
        {
            ("HIF", MagickFormat.Heic), ("JPG", MagickFormat.Jpeg), ("jpeg", MagickFormat.Jpeg), ("PNG", MagickFormat.Png),
            ("HEIC", MagickFormat.Heic), ("heif", MagickFormat.Heic),
            ("TIF", MagickFormat.Tiff), ("tiff", MagickFormat.Tiff), ("BMP", MagickFormat.Bmp),
            ("WEBP", MagickFormat.WebP), ("AVIF", MagickFormat.Avif)
        };
        var images = new List<ImageFile>();
        var hashes = new Dictionary<string, byte[]>();
        await using var store = new ThumbnailStore(Path.Combine(root, "format-cache"));
        foreach (var (extension, format) in formats)
        {
            string path = Path.Combine(folder, "sample-" + extension + "." + extension);
            string packaged = Path.Combine(sampleFolder, "format-sample." + extension);
            check(File.Exists(packaged), "Packaged " + extension + " fixture is available in TestImages");
            // Work only on a disposable copy; TestImages remains read-only after packaging.
            await File.WriteAllBytesAsync(path, await File.ReadAllBytesAsync(packaged));
            var info = new FileInfo(path);
            var file = new ImageFile(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            images.Add(file);
            hashes[path] = SHA256.HashData(await File.ReadAllBytesAsync(path));
            await VerifyDecodedImageDimensionsAndThumbnailsAsync(file, format, store, check);
            string corruptPath = Path.Combine(folder, "corrupt." + extension);
            await File.WriteAllTextAsync(corruptPath, "invalid image bytes");
            var corruptInfo = new FileInfo(corruptPath);
            bool failed = false;
            try { await store.GetAsync(new(corruptInfo.FullName, corruptInfo.Length, corruptInfo.LastWriteTimeUtc.Ticks), default); }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed = true; }
            check(failed, extension + " corrupt input reports a decode error");
            File.Delete(corruptPath);
        }
        // Discovery depends on the extension, so no GIF decoder or real GIF fixture is needed.
        string gifPath = Path.Combine(folder, "excluded.GiF");
        await File.WriteAllTextAsync(gifPath, "unsupported extension fixture; never decoded");
        check(!ImageFileFilter.IsSupported(new FileInfo(gifPath)), "GIF exclusion: a mixed-case .GiF file is not a supported browsing format");
        var backend = new ImageBrowserService(store);
        var discovered = new List<ImageFile>();
        await foreach (var batch in backend.ScanAsync(folder, default)) discovered.AddRange(batch);
        check(discovered.Select(file => file.Path).Order().SequenceEqual(images.Select(file => file.Path).Order()), "Folder scan returns exactly the supported image fixtures and excludes the GIF fixture");
        await using (var warm = new ThumbnailStore(Path.Combine(root, "format-cache")))
        {
            var result = await ThumbnailBenchmark.RunAsync(warm, images);
            check(result.Loaded == images.Count && result.SourceReads == 0 && result.CacheHits == images.Count, "All added formats load exclusively from the persistent warm cache");
        }
        foreach (var file in images)
        {
            var source = await File.ReadAllBytesAsync(file.Path);
            check(hashes[file.Path].SequenceEqual(SHA256.HashData(source)), "Format test leaves source unchanged: " + file.Name);
        }

        var deleteImages = await backend.DeleteAsync(images, default);
        check(deleteImages.Failures.Count == 0 && deleteImages.Deleted.Count == formats.Length,
            "Every added image format supports confirmed deletion");
        await VerifyRawCompanionAssociationAndDeletionAsync(folder, store, backend, check);
    }

    private static async Task VerifyRawCompanionAssociationAndDeletionAsync(
        string folder, ThumbnailStore store, ImageBrowserService backend, Action<bool, string> check)
    {
        string[] rawExtensions = ["RAF", "NEF", "nrw", "ARW", "sr2", "CR2", "cr3", "CRW", "DNG", "ORF", "RW2", "PEF", "SRW"];
        var originals = new List<ImageFile>();
        foreach (var extension in rawExtensions)
        {
            string path = Path.Combine(folder, "pair-" + extension + ".jpg");
            await File.WriteAllTextAsync(path, "disposable original");
            await File.WriteAllTextAsync(Path.ChangeExtension(path, extension), "disposable raw companion; not sensor-decoded");
            var info = new FileInfo(path);
            var original = new ImageFile(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            originals.Add(original);
            var raw = new FileInfo(Path.ChangeExtension(path, extension));
            check(ImageFileFilter.IsRaw(raw) && !ImageFileFilter.IsSupported(raw), extension + " is an export-only companion, excluded from image browsing");
            var resolved = await store.ResolveFullImageAsync(original, default);
            check(resolved.Path.Equals(raw.FullName, StringComparison.OrdinalIgnoreCase), extension + " companion association resolves by stem");
        }
        var selection = await backend.PrepareDeletionAsync(originals, default);
        check(selection.Count == originals.Count * 2, "Deletion preparation finds every common RAW companion");
        var grouped = new DeleteConfirmationViewModel(selection);
        check(grouped.Groups.Count == originals.Count && grouped.Groups.All(group => group.Files.Count == 2 && group.Formats.Contains("JPG")), "Deletion groups each original/RAW pair with a single name and both formats");
        var resultDelete = await backend.DeleteAsync(selection, default);
        check(resultDelete.Failures.Count == 0 && resultDelete.Deleted.Count == selection.Count, "Confirmed deletion removes every supported RAW pair");
    }

    private static async Task VerifyDecodedImageDimensionsAndThumbnailsAsync(
        ImageFile file, MagickFormat expectedFormat, ThumbnailStore store, Action<bool, string> check)
    {
        string extension = Path.GetExtension(file.Path).TrimStart('.');
        int width, height;
        using (var encoded = new MagickImage(File.ReadAllBytes(file.Path)))
        {
            check(encoded.Format == expectedFormat, "Fixture uses real " + extension + " encoding (detected " + encoded.Format + ")");
            encoded.AutoOrient();
            width = (int)encoded.Width;
            height = (int)encoded.Height;
        }
        check(ImageFileFilter.IsSupported(new FileInfo(file.Path)) && !ImageFileFilter.IsSupported(new FileInfo(Path.Combine(Path.GetDirectoryName(file.Path)!, ".hidden." + extension))), extension + " discovery is case insensitive and ignores dot files");
        // Exercise pass 1 too; formats without an embedded preview may return null.
        await store.GetEmbeddedAsync(file, 10, default);
        var grid = await store.GetAsync(file, default);
        var preview = await store.GetPreviewAsync(file, default);
        var full = await store.GetFullImageAsync(file, default);
        check(await store.GetDimensionsAsync(file, default) == new System.Windows.Size(width, height), extension + " original dimensions are available before full decode");
        bool expectedShape = extension == "HIF" ? (grid.PixelHeight > grid.PixelWidth) == (height > width)
            : Math.Abs((double)grid.PixelWidth / grid.PixelHeight - (double)width / height) < 0.02;
        check(grid.IsFrozen, extension + ": grid bitmap is frozen for safe cross-thread UI delivery");
        check(Math.Max(grid.PixelWidth, grid.PixelHeight) <= 320, extension + ": grid thumbnail longest edge is at most 320 pixels");
        check(expectedShape, extension + ": grid preserves oriented aspect ratio (HIF: portrait/landscape orientation)");
        check(preview.IsFrozen, extension + ": viewer preview is frozen for safe cross-thread UI delivery");
        check(Math.Max(preview.PixelWidth, preview.PixelHeight) <= 1600, extension + ": viewer preview longest edge is at most 1600 pixels");
        check(full.PixelWidth == width && full.PixelHeight == height, extension + ": full viewer image retains the oriented native width and height");
    }
}
