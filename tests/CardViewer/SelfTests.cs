using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace CardViewer;

public static partial class SelfTests
{
    public static async Task<int> Run(string folder, string? outputDirectory = null)
    {
        var output = Path.GetFullPath(outputDirectory ?? "artifacts");
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "self-test.txt"));
        string section = "Source discovery";
        var passedBySection = new Dictionary<string, int>();
        void Section(string name, string purpose)
        {
            section = name;
            passedBySection.TryAdd(name, 0);
            log.WriteLine($"\nSECTION {name}\nTesting: {purpose}");
            log.Flush();
        }
        void Check(bool ok, string text)
        {
            if (!ok)
                throw new Exception($"[{section}] {text}");
            passedBySection.TryAdd(section, 0);
            passedBySection[section]++;
            log.WriteLine($"PASS [{section}] {text}");
            log.Flush();
        }
        string root = Path.GetFullPath(Path.Combine(output, "test-cache-" + Guid.NewGuid().ToString("N")));
        try
        {
            Section("Source discovery", "HIF/RAF discovery ignores hidden files and AppleDouble sidecars.");
            var files = new DirectoryInfo(folder).EnumerateFiles().Where(f => ImageFileFilter.IsHif(f)).OrderBy(f => f.Name).ToArray();
            Check(files.Length > 0, "At least one visible HIF sample is available for camera-image regression checks");
            var discovery = Path.Combine(root, "dot-files");
            Directory.CreateDirectory(discovery);
            foreach (var name in new[] { "visible.HIF", "visible.RAF", ".hidden.HIF", ".hidden.RAF", "._visible.HIF", "._visible.RAF", ".DS_Store" })
                await File.WriteAllTextAsync(Path.Combine(discovery, name), "discovery fixture");
            var discovered = new DirectoryInfo(discovery).EnumerateFiles().ToArray();
            Check(discovered.Where(ImageFileFilter.IsHif).Select(f => f.Name).SequenceEqual(new[] { "visible.HIF" }), "HIF discovery ignores dot-prefixed files including AppleDouble sidecars");
            Check(discovered.Where(ImageFileFilter.IsRaf).Select(f => f.Name).SequenceEqual(new[] { "visible.RAF" }), "RAF discovery ignores dot-prefixed files");
            var hashes = files.ToDictionary(f => f.FullName, f => SHA256.HashData(File.ReadAllBytes(f.FullName)));
            Section("JPEG and PNG", "JPEG orientation and reduced-resolution previews, PNG transparency, and warm-cache reads.");
            await VerifyJpegOrientationAndPngTransparencyAsync(root, Check);
            Section("Supported formats and RAW companions", "Real encoded fixtures, GIF exclusion, corrupt input, and disposable RAW pair association/deletion; no RAW sensor decoding.");
            await FormatTests.RunAsync(root, folder, Check);
            Section("Camera JPEG read budget", "Pass-1 metadata probing reads less than 32 KB per camera JPEG; available embedded previews decode.");
            await using (var jpegStore = new ThumbnailStore(Path.Combine(root, "camera-jpeg-cache")))
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles().Where(ImageFileFilter.IsJpeg))
                {
                    await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
                    var parsed = await JpegMetadata.ReadAsync(stream, default);
                    Check(parsed.BytesRead < 32 * 1024, "Camera JPEG pass-1 probe stays below 32 KB: " + file.Name);
                    if (parsed.Preview != null)
                    {
                        var item = new ImageFile(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
                        var bitmap = await jpegStore.GetEmbeddedAsync(item, 10, default);
                        Check(bitmap != null && bitmap.IsFrozen, "Camera JPEG embedded preview decodes: " + file.Name);
                    }
                }
            }
            await using var store = new ThumbnailStore(root);
            var items = files.Select(f => new ImageFile(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks)).ToArray();
            Section("Deletion and selection", "Confirmation, selection, protected paths, partial failures, and deletion of disposable image/RAW pairs.");
            await DeletionTests.RunAsync(root, Check);
            Section("Lossless ZIP export", "ZIP contents match source bytes, shared RAWs are deduplicated, progress and failure cleanup work.");
            await ExportTests.RunAsync(root, items[0], Check);
            Section("Cache budget and eviction", "2 GB default, least-recently-used pruning, persisted recency, concurrency, and legacy-preview cleanup.");
            await CacheTests.RunAsync(root, Check);
            Section("HIF thumbnail passes", "Bounded embedded-preview reads, 320px refinement, and orientation for every HIF sample.");
            foreach (var item in items)
            {
                HifIndex index;
                using (var stream = File.OpenRead(item.Path))
                {
                    var parsed = await HifMetadata.ReadAsync(stream, default);
                    index = parsed.Index;
                    Check(index.Previews.Length == 3 && parsed.Bytes < 2048, "Indexed three JPEGs using <2 KB metadata: " + item.Name);
                }
                var embedded = await store.GetEmbeddedAsync(item, 10, default);
                Check(embedded != null && Math.Max(embedded.PixelWidth, embedded.PixelHeight) == 160, "Pass 1 decodes embedded 160px JPEG: " + item.Name);
                var grid = await store.GetAsync(item, default);
                Check(grid.IsFrozen && Math.Max(grid.PixelWidth, grid.PixelHeight) == 320, "Pass 2 creates frozen 320px grid: " + item.Name);
                Check((grid.PixelHeight > grid.PixelWidth) == (index.Rotation is 90 or 270), "Grid applies container rotation: " + item.Name);
            }
            Check(!Directory.Exists(Path.Combine(root, "preview1600")), "Grid browsing never generates 1600px previews");
            Section("Fallback, on-demand preview, and cache", "Missing previews fall back in pass 2; 1600px previews are regenerated; cache invalidation, corruption, cancellation, and clearing are safe.");
            // Change only a test copy's thumbnail references to exercise generic HEIF fallback.
            var fallbackBytes = await File.ReadAllBytesAsync(items[0].Path);
            for (int p = 0; p < Math.Min(1024, fallbackBytes.Length) - 4; p++)
                if (fallbackBytes.AsSpan(p, 4).SequenceEqual("thmb"u8))
                    "skip"u8.CopyTo(fallbackBytes.AsSpan(p, 4));
            string fallbackPath = Path.Combine(output, "no-preview.HIF");
            await File.WriteAllBytesAsync(fallbackPath, fallbackBytes);
            await using (var fallbackStore = new ThumbnailStore(Path.Combine(root, "fallback")))
            {
                var fallback = new ImageFile(fallbackPath, fallbackBytes.Length, 0);
                Check(await fallbackStore.GetEmbeddedAsync(fallback, 10, default) == null, "Pass 1 never full-decodes when an indexed preview is absent");
                var decoded = await fallbackStore.GetAsync(fallback, default);
                Check(Math.Max(decoded.PixelWidth, decoded.PixelHeight) == 320, "Pass 2 falls back to maintained full HEIF decoder");
            }
            var portrait = items.FirstOrDefault(i => i.Name == "DSCF0062.HIF") ?? items[0];
            var large = await store.GetPreviewAsync(portrait, default);
            Check(Math.Max(large.PixelWidth, large.PixelHeight) == 1600, "On-demand pass 3 generates 1600px preview");
            Check(!Directory.Exists(Path.Combine(root, "preview1600")), "1600px preview is served without a persistent cache");
            await using var cached = new ThumbnailStore(root, new ThrowDecoder());
            var warm = await ThumbnailBenchmark.RunAsync(cached, items);
            Check(warm.Loaded == items.Length && warm.CacheHits == items.Length && warm.SourceReads == 0, "Warm grid uses SSD cache with no source reads or decoder calls");
            int previewReads = store.SourceReads;
            await store.GetPreviewAsync(portrait, default);
            Check(store.SourceReads == previewReads + 1, "Repeated 1600px requests regenerate from source instead of disk cache");
            var key = items[0];
            Check(key.CacheKey != (key with
            {
                ModifiedTicks = key.ModifiedTicks + 1
            }).CacheKey && key.CacheKey != (key with
            {
                Length = key.Length + 1
            }).CacheKey, "Changing either source timestamp or byte length changes the thumbnail cache key");
            await File.WriteAllTextAsync(Path.Combine(root, key.CacheKey + ".jpg"), "broken");
            var repaired = await store.GetAsync(key, default);
            Check(repaired.PixelWidth > 0, "Corrupt cache is regenerated");
            var broken = Path.Combine(output, "invalid.HIF");
            await File.WriteAllTextAsync(broken, "not an image");
            var bad = new ImageFile(broken, 12, 0);
            Check(await store.GetEmbeddedAsync(bad, 10, default) == null, "Missing embedded preview is a nonfatal pass-1 miss");
            var errors = await ThumbnailBenchmark.RunAsync(store, new[] { bad });
            Check(errors.Failed == 1 && errors.Errors.Length == 1, "Invalid full decode is reported without crashing benchmark");
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                bool canceled = false;
                try
                {
                    await store.GetAsync(key, stop.Token);
                }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Cancelled request does no further work");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "keep.jpg"), "keep");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "nested", key.CacheKey + ".jpg"), "keep");
            int removed = await store.ClearAsync();
            Check(removed == items.Length, "Clear removes grid cache entries");
            Check(File.Exists(Path.Combine(root, "keep.jpg")) && File.Exists(Path.Combine(root, "nested", key.CacheKey + ".jpg")), "Clear preserves unrelated files and folders");
            Check(await store.ClearAsync() == 0, "Clearing empty cache is safe");
            await store.GetAsync(key, default);
            var jpeg = await File.ReadAllBytesAsync(Path.Combine(root, key.CacheKey + ".jpg"));
            Section("RAW association and full HIF loading", "Backend RAW matching, invalid RAW errors, HIF-only thumbnails/previews, full HIF dimensions, and measured read progress.");
            await VerifyRawAssociationAndFullHifLoadingAsync(root, key, Check);
            Section("Abandoned RAW worker", "One cancelled RAW worker releases resources so subsequent HIF work can complete; no exhaustive RAW decode testing.");
            await VerifyCancelledRawDoesNotBlockHifAsync(root, key, folder, Check);
            Section("Scheduling and responsiveness", "Visible work overtakes queued background work, cancelled jobs never run, and read/decode/UI work remain independent.");
            await VerifyVisiblePriorityAndQueuedCancellationAsync(Check);
            await VerifyReadsContinueDuringBlockedDecodeAsync(root, key, jpeg, Check);
            await VerifyDispatcherRemainsResponsiveDuringDecodeAsync(root, key, jpeg, Check,
                () => Section("WPF controls and viewer operations", "Actual controls/bindings, navigation, stable viewer sizing, progress, selection, and dispatcher responsiveness."));
            Section("Pipeline measurements and source preservation", "Pass-1 reads stay below 32 KB, all four timing fields are nonnegative, and source HIF hashes remain unchanged.");
            var rows = File.ReadAllLines(Path.Combine(root, "pipeline.jsonl")).Select(line => JsonDocument.Parse(line)).ToArray();
            var embeddedRows = rows.Select(r => r.RootElement.GetProperty("Timing")).Where(t => t.GetProperty("Pass").GetString() == "embedded" && t.GetProperty("Error").ValueKind == JsonValueKind.Null).ToArray();
            Check(embeddedRows.Length == items.Length && embeddedRows.All(t => t.GetProperty("BytesRead").GetInt64() < 32 * 1024), "Pass 1 reads <32 KB per sample, never full HIF payload");
            Check(rows.All(r => new[] { "ReadMs", "DecodeMs", "ResizeMs", "WriteMs" }.All(s => r.RootElement.GetProperty("Timing").GetProperty(s).GetDouble() >= 0)), "Every pipeline log entry contains nonnegative read, decode, resize, and write durations");
            foreach (var f in files)
                Check(hashes[f.FullName].SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.FullName))), "Source unchanged " + f.Name);
            foreach (var group in passedBySection)
                log.WriteLine($"GROUP PASS | {group.Key} | {group.Value} assertions");
            log.WriteLine("ALL TESTS PASSED");
            return 0;
        }
        catch (Exception ex) { log.WriteLine($"FAIL [{section}] {ex}"); return 1; }
    }
}
