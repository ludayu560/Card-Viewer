using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace CardViewer;

public static class BuildVerification
{
    private sealed record FullLoadResult(
        string File, string Kind, int Width, int Height, double ElapsedSeconds, StageTotals Stages);

    public static async Task<int> RunAsync(string folder, string output)
    {
        folder = Path.GetFullPath(folder);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        // Separate builds must not clear one another's benchmark cache.
        FileStream exclusive;
        try
        {
            exclusive = new FileStream(Path.Combine(output, "verification.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) { Console.Error.WriteLine("Image verification already running for this configuration: " + ex.Message); return 1; }
        using var ownedLock = exclusive;
        string run = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        using var detail = new StreamWriter(Path.Combine(run, "verification.txt")) { AutoFlush = true };
        void Note(string text)
        {
            Console.WriteLine(text);
            detail.WriteLine(text);
        }
        void Check(bool ok, string text)
        {
            if (!ok)
                throw new InvalidOperationException(text);
            Note("PASS " + text);
        }
        var total = Stopwatch.StartNew();
        var fullLoads = new List<FullLoadResult>();
        BenchmarkResult? cold = null, warm = null;
        int hifCount = 0, rafCount = 0, jpegCount = 0, pngCount = 0, removed = 0, checks = 0;
        string? failure = null;
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formatCounts = new Dictionary<string, int>();
        async Task<string> Hash(string path)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
        try
        {
            var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly).Where(f => !ImageFileFilter.IsIgnored(f)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var hifs = files.Where(f => ImageFileFilter.IsHif(f)).Select(f => new ImageFile(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks)).ToArray();
            var rafs = files.Where(f => ImageFileFilter.IsRaf(f)).Select(f => new ImageFile(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks)).ToArray();
            var images = files.Where(ImageFileFilter.IsSupported).Select(f => new ImageFile(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks)).ToArray();
            formatCounts = images.GroupBy(file => Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant()).ToDictionary(group => group.Key, group => group.Count());
            pngCount = files.Count(ImageFileFilter.IsPng);
            hifCount = hifs.Length;
            rafCount = rafs.Length;
            jpegCount = files.Count(f => f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
            Check(images.Length > 0, "Discovered supported image samples");
            Note($"Samples: {hifCount} HIF, {rafCount} RAF, {jpegCount} JPEG, {pngCount} PNG.");
            Note("Image formats: " + string.Join(", ", formatCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}: {pair.Value}")));
            foreach (var file in files)
                before[file.FullName] = await Hash(file.FullName);
            await File.WriteAllTextAsync(Path.Combine(run, "source-hashes-before.json"), JsonSerializer.Serialize(before, new JsonSerializerOptions { WriteIndented = true }));
            var cache = Path.Combine(output, "cache");
            await using (var clear = new ThumbnailStore(cache))
                removed = await clear.ClearAsync();
            bool Empty(string path) => !Directory.Exists(path) || !Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Any(p => Path.GetExtension(p) is ".jpg" or ".png");
            Check(Empty(cache) && Empty(Path.Combine(cache, "preview1600")), "Generated test thumbnails stripped before cold load");
            Note($"Cleared {removed} stored test thumbnails. User cache and embedded source previews are unchanged.");
            await using (var store = new ThumbnailStore(cache))
                cold = await ThumbnailBenchmark.RunAsync(store, images);
            Note("COLD GRID: " + cold);
            Check(cold.Loaded == images.Length && cold.Failed == 0 && cold.CacheHits == 0, "Cold grid loads every supported image without cached thumbnails");
            await using (var store = new ThumbnailStore(cache))
                warm = await ThumbnailBenchmark.RunAsync(store, images);
            Note("WARM GRID: " + warm);
            Check(warm.Loaded == images.Length && warm.CacheHits == images.Length && warm.SourceReads == 0, "Warm grid uses cache exclusively");
            int regression = await SelfTests.Run(folder, Path.Combine(run, "regression"));
            string log = Path.Combine(run, "regression", "self-test.txt");
            if (File.Exists(log))
            {
                checks = File.ReadLines(log).Count(line => line.StartsWith("PASS ", StringComparison.Ordinal));
                foreach (var group in File.ReadLines(log).Where(line => line.StartsWith("GROUP PASS |", StringComparison.Ordinal)))
                    Note(group);
            }
            Check(regression == 0, $"Regression suite ({checks} assertions); details: {log}");
            // Complete sample coverage, not just one representative camera file.
            await using (var store = new ThumbnailStore(cache))
            {
                foreach (var hif in images)
                {
                    string kind = Path.GetExtension(hif.Path).TrimStart('.').ToUpperInvariant();
                    var preview = await store.GetPreviewAsync(hif, default);
                    Check(preview.IsFrozen && Math.Max(preview.PixelWidth, preview.PixelHeight) <= 1600, "Viewer preview " + hif.Name);
                    var partner = rafs.FirstOrDefault(r => Path.GetFileNameWithoutExtension(r.Path).Equals(Path.GetFileNameWithoutExtension(hif.Path), StringComparison.OrdinalIgnoreCase));
                    var resolved = await store.ResolveFullImageAsync(hif, default);
                    Check(resolved.Path.Equals(partner?.Path ?? hif.Path, StringComparison.OrdinalIgnoreCase), "RAF association " + hif.Name);
                    var full = await MeasureFull(store, hif, kind);
                    Check(await store.GetDimensionsAsync(hif, default) == new System.Windows.Size(full.Width, full.Height), "Metadata dimensions match oriented full image " + hif.Name);
                    fullLoads.Add(full);
                    Check(full.Width > 0 && full.Height > 0, "Full image decodes " + hif.Name);
                    Note($"FULL {kind} {full.File}: {full.ElapsedSeconds:F3} s ({full.Width} × {full.Height})");
                }
                Note($"RAF coverage: association checks for {rafCount} files; full RAW decoding is excluded from automatic build verification.");
            }

        }
        catch (Exception ex) { failure = ex.ToString(); Note("IMAGE VERIFICATION FAILED: " + ex.Message); }
        finally
        {
            // Verify source preservation even when a decoder or regression check fails.
            try
            {
                foreach (var entry in before)
                    if (!entry.Value.Equals(await Hash(entry.Key), StringComparison.Ordinal))
                        throw new InvalidOperationException("Source changed: " + entry.Key);
                if (before.Count > 0)
                    Note($"PASS Source SHA-256 unchanged for all {before.Count} files (including RAF/JPEG).");
            }
            catch (Exception ex) { failure = (failure ?? "") + Environment.NewLine + ex; Note("SOURCE PRESERVATION FAILED: " + ex.Message); }
        }
        total.Stop();
        var text = new StringBuilder();
        text.AppendLine(failure == null ? "BUILD IMAGE VERIFICATION PASSED" : "BUILD IMAGE VERIFICATION FAILED");
        text.AppendLine($"Samples: {hifCount} HIF, {rafCount} RAF, {jpegCount} JPEG, {pngCount} PNG; {checks} regression assertions.");
        text.AppendLine($"Removed {removed} generated test thumbnails before cold run.");
        if (cold != null)
        {
            text.AppendLine("Cold grid: " + cold);
            text.AppendLine($"First preview: {cold.FirstPreviewMs:F3} ms; embedded previews: {cold.EmbeddedPreviews}; source bytes: {cold.Stages.BytesRead:N0}");
            text.AppendLine($"Cold stages: read {cold.Stages.ReadMs:F3} ms, decode/encode {cold.Stages.DecodeMs:F3} ms, orient/resize {cold.Stages.ResizeMs:F3} ms, write {cold.Stages.WriteMs:F3} ms");
        }
        if (warm != null)
            text.AppendLine("Warm grid: " + warm);
        foreach (var kind in fullLoads.Select(load => load.Kind).Distinct().Order())
        {
            var loads = fullLoads.Where(f => f.Kind == kind).ToArray();
            if (loads.Length > 0)
                text.AppendLine($"Full {kind}: {loads.Length} images, {loads.Sum(f => f.ElapsedSeconds):F3} s total, {loads.Average(f => f.ElapsedSeconds):F3} s average.");
        }
        text.AppendLine($"Total verification: {total.Elapsed.TotalSeconds:F3} s. Reports: {run}");
        text.AppendLine("Cold means empty generated thumbnail cache. OS file caches are not flushed; sources and embedded previews are never modified.");
        if (failure != null)
            text.AppendLine(failure);
        var json = JsonSerializer.Serialize(new
        {
            Passed = failure == null,
            SampleFolder = folder,
            RunDirectory = run,
            RemovedThumbnails = removed,
            HifCount = hifCount,
            RafCount = rafCount,
            JpegCount = jpegCount,
            PngCount = pngCount,
            Formats = formatCounts,
            RegressionAssertions = checks,
            Cold = cold,
            Warm = warm,
            FullImages = fullLoads,
            TotalSeconds = total.Elapsed.TotalSeconds,
            Failure = failure
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(run, "summary.txt"), text.ToString());
        await File.WriteAllTextAsync(Path.Combine(run, "summary.json"), json);
        // Publishing latest reports via replacement avoids half-written reports.
        foreach (var pair in new[] { ("latest.txt", text.ToString()), ("latest.json", json) })
        {
            var temporary = Path.Combine(output, pair.Item1 + ".tmp");
            await File.WriteAllTextAsync(temporary, pair.Item2);
            File.Move(temporary, Path.Combine(output, pair.Item1), true);
        }
        Console.WriteLine(text.ToString());
        return failure == null ? 0 : 1;
    }
    private static async Task<FullLoadResult> MeasureFull(ThumbnailStore store, ImageFile file, string kind)
    {
        var before = store.Timings;
        var timer = Stopwatch.StartNew();
        var bitmap = await store.GetFullImageAsync(file, default);
        timer.Stop();
        if (!bitmap.IsFrozen)
            throw new InvalidOperationException("Unfrozen full image: " + file.Name);
        return new(file.Name, kind, bitmap.PixelWidth, bitmap.PixelHeight, timer.Elapsed.TotalSeconds, store.Timings - before);
    }
}



