using System.Diagnostics;
using System.IO;
using System.Text.Json;
namespace CardViewer;

public static class ThumbnailBenchmark
{
    // Measures thumbnail retrieval/decode/cache writes; folder enumeration and UI painting are excluded.
    public static async Task<BenchmarkResult> RunAsync(ThumbnailStore store, IReadOnlyList<ImageFile> images, CancellationToken token = default, IProgress<int>? progress = null)
    {
        int reads = store.SourceReads, hits = store.CacheHits, loaded = 0;
        var errors = new List<string>();
        var startStages = store.Timings;
        var timer = Stopwatch.StartNew();
        int embeddedCount = 0;
        double? firstPreview = null;
        int completed = 0;
        foreach (var image in images)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var cached = await store.TryGridCacheAsync(image, token);
                if (cached == null)
                {
                    var embedded = await store.GetEmbeddedAsync(image, 10, token);
                    if (embedded != null)
                    {
                        embeddedCount++;
                        firstPreview ??= timer.Elapsed.TotalMilliseconds;
                    }
                    await store.GetAsync(image, token);
                }
                firstPreview ??= timer.Elapsed.TotalMilliseconds;
                loaded++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add($"{image.Name}: {ex.Message}"); }
            progress?.Report(++completed);
        }
        timer.Stop();
        return new(images.Count, loaded, errors.Count, timer.Elapsed.TotalSeconds, store.CacheHits - hits, store.SourceReads - reads, errors.ToArray(), embeddedCount, firstPreview, store.Timings - startStages);
    }
    public static async Task<int> RunCommandAsync(string folder)
    {
        Directory.CreateDirectory("artifacts");
        try
        {
            var images = new DirectoryInfo(folder).EnumerateFiles().Where(f => ImageFileFilter.IsSupported(f))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new ImageFile(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks)).ToArray();
            if (images.Length == 0)
                throw new InvalidOperationException("No supported images found.");
            // Isolated new cache makes the cold run repeatable without deleting the user's cache.
            var cache = Path.GetFullPath(Path.Combine("artifacts", "benchmark-cache-" + Guid.NewGuid().ToString("N")));
            await using var coldStore = new ThumbnailStore(cache);
            var cold = await RunAsync(coldStore, images);
            await using var warmStore = new ThumbnailStore(cache);
            var warm = await RunAsync(warmStore, images);
            await File.WriteAllTextAsync("artifacts/benchmark.json", JsonSerializer.Serialize(new
            {
                Folder = Path.GetFullPath(folder),
                Cache = cache,
                Cold = cold,
                Warm = warm
            }, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync("artifacts/benchmark.txt", $"Cold thumbnail cache: {cold}\nFirst cold preview: {cold.FirstPreviewMs:F3} ms; embedded previews: {cold.EmbeddedPreviews}; source bytes: {cold.Stages.BytesRead:N0}\nCold stages: read {cold.Stages.ReadMs:F3} ms, decode/encode {cold.Stages.DecodeMs:F3} ms, orient/resize {cold.Stages.ResizeMs:F3} ms, write {cold.Stages.WriteMs:F3} ms\nWarm thumbnail cache: {warm}\nTiming excludes enumeration and UI painting. OS file cache is not flushed.\n");
            return cold.Failed + warm.Failed == 0 ? 0 : 1;
        }
        catch (Exception ex) { await File.WriteAllTextAsync("artifacts/benchmark.txt", ex.ToString()); return 1; }
    }
}

