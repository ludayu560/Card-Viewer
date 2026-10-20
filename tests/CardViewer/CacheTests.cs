using System.IO;

namespace CardViewer;

internal static class CacheTests
{
    private const long TwoThumbnailBudget = 120;
    private const int ThumbnailBytes = 60;

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        await ReadingThumbnailMakesItMostRecentlyUsedAsync(root, check);
        await RecencySurvivesRestartAndSmallerBudgetAsync(root, check);
        await ClearPreservesUnrelatedFilesAsync(root, check);
        await OversizedThumbnailIsNotRetainedAsync(root, check);
        await StartupRemovesOnlyOwnedLegacyPreviewsAsync(root, check);
        await EvictionSkipsLockedThumbnailAsync(root, check);
        await ConcurrentWritesRespectBudgetAsync(root, check);
        await LegacyMigrationPreservesNewEntriesAndDoesNotRepeatAsync(root, check);
        check(ThumbnailDiskCache.DefaultBudgetBytes == 2_000_000_000,
            "Default grid cache budget is exactly 2 GB");
        check(ThumbnailStore.DefaultCachePath == Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CardViewer", "Thumbnails", "v3"),
            "Application cache uses the CardViewer folder under LocalAppData");
    }

    private static string ThumbnailPath(string folder, char id) =>
        Path.Combine(folder, new string(id, 64) + ".jpg");

    private static Task WriteThumbnailAsync(ThumbnailDiskCache cache, string folder, char id) =>
        cache.WriteAsync(ThumbnailPath(folder, id), new byte[ThumbnailBytes], default);

    private static async Task ReadingThumbnailMakesItMostRecentlyUsedAsync(string root, Action<bool, string> check)
    {
        // Arrange: the budget holds A and B, but cannot hold a third thumbnail.
        string folder = Path.Combine(root, "cache-read-recency");
        var cache = new ThumbnailDiskCache(folder, TwoThumbnailBudget);
        await WriteThumbnailAsync(cache, folder, 'A');
        await WriteThumbnailAsync(cache, folder, 'B');

        // Act: read the older thumbnail, then add C.
        var cachedBytes = await cache.ReadAsync(ThumbnailPath(folder, 'A'), default);
        await WriteThumbnailAsync(cache, folder, 'C');

        // Assert: the read kept A alive; B was the least recently used entry.
        check(cachedBytes != null, "Reading an existing thumbnail returns its cached bytes");
        check(File.Exists(ThumbnailPath(folder, 'A')), "Recently read thumbnail A survives eviction");
        check(!File.Exists(ThumbnailPath(folder, 'B')), "Unread thumbnail B is evicted before recently read A");
        check(File.Exists(ThumbnailPath(folder, 'C')), "New thumbnail C is retained within the two-thumbnail budget");
    }

    private static async Task RecencySurvivesRestartAndSmallerBudgetAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "cache-restart-recency");
        var original = new ThumbnailDiskCache(folder, TwoThumbnailBudget);
        await WriteThumbnailAsync(original, folder, 'A');
        await WriteThumbnailAsync(original, folder, 'B');
        await original.ReadAsync(ThumbnailPath(folder, 'A'), default);

        // A new instance must reconstruct recency from disk, rather than creation order.
        var restarted = new ThumbnailDiskCache(folder, TwoThumbnailBudget);
        await WriteThumbnailAsync(restarted, folder, 'C');
        check(File.Exists(ThumbnailPath(folder, 'A')) && !File.Exists(ThumbnailPath(folder, 'B')),
            "A new cache instance preserves A's recorded read recency and evicts B");

        // Reducing the budget on startup must keep only the newest entry, C.
        var smaller = new ThumbnailDiskCache(folder, ThumbnailBytes);
        await smaller.Ready;
        check(!File.Exists(ThumbnailPath(folder, 'A')) && File.Exists(ThumbnailPath(folder, 'C')),
            "Startup with a one-thumbnail budget removes older A and retains newer C");
    }

    private static async Task ClearPreservesUnrelatedFilesAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "cache-clear-safety");
        var cache = new ThumbnailDiskCache(folder, TwoThumbnailBudget);
        await WriteThumbnailAsync(cache, folder, 'A');
        string unrelatedFile = Path.Combine(folder, "keep.jpg");
        await File.WriteAllTextAsync(unrelatedFile, "not an owned cache entry");

        int removed = await cache.ClearAsync(default);

        check(removed == 1, "Cache clear reports exactly one owned thumbnail removed");
        check(File.Exists(unrelatedFile), "Cache clear preserves a JPEG without an owned hash filename");
    }

    private static async Task OversizedThumbnailIsNotRetainedAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "cache-oversized-entry");
        var cache = new ThumbnailDiskCache(folder, ThumbnailBytes);

        await cache.WriteAsync(ThumbnailPath(folder, 'A'), new byte[100], default);

        check(!File.Exists(ThumbnailPath(folder, 'A')),
            "A 100-byte thumbnail is not retained in a 60-byte cache");
    }

    private static async Task StartupRemovesOnlyOwnedLegacyPreviewsAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "cache-legacy-cleanup");
        string legacyFolder = Path.Combine(folder, "preview1600");
        Directory.CreateDirectory(legacyFolder);
        string oldPreview = Path.Combine(legacyFolder, new string('F', 64) + ".png");
        string unrelatedFile = Path.Combine(legacyFolder, "keep.txt");
        await File.WriteAllBytesAsync(oldPreview, new byte[20]);
        await File.WriteAllTextAsync(unrelatedFile, "unrelated");

        var cache = new ThumbnailDiskCache(folder, TwoThumbnailBudget);
        await cache.Ready;

        check(!File.Exists(oldPreview), "Startup removes the legacy hash-named 1600px preview");
        check(File.Exists(unrelatedFile), "Legacy cleanup preserves unrelated files in preview1600");
    }

    private static async Task EvictionSkipsLockedThumbnailAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "cache-locked-entry");
        var cache = new ThumbnailDiskCache(folder, TwoThumbnailBudget);
        await WriteThumbnailAsync(cache, folder, 'A');
        await WriteThumbnailAsync(cache, folder, 'B');

        using var heldFile = new FileStream(ThumbnailPath(folder, 'A'), FileMode.Open, FileAccess.Read, FileShare.None);
        await WriteThumbnailAsync(cache, folder, 'C');

        check(File.Exists(ThumbnailPath(folder, 'A')), "Eviction leaves externally locked thumbnail A intact");
        check(!File.Exists(ThumbnailPath(folder, 'B')), "Eviction skips locked A and removes eligible thumbnail B");
        check(File.Exists(ThumbnailPath(folder, 'C')), "The new thumbnail is published even when the oldest entry is locked");
    }

    private static async Task ConcurrentWritesRespectBudgetAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "cache-concurrent-writes");
        var cache = new ThumbnailDiskCache(folder, TwoThumbnailBudget);
        const string thumbnailIds = "0123456789AB";

        await Task.WhenAll(thumbnailIds.Select(id => WriteThumbnailAsync(cache, folder, id)));

        long cachedBytes = Directory.EnumerateFiles(folder)
            .Where(ThumbnailDiskCache.IsCacheFile)
            .Sum(path => new FileInfo(path).Length);
        check(cachedBytes <= TwoThumbnailBudget, "Twelve concurrent writes leave at most 120 bytes of cached thumbnails");
        check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Concurrent cache writes leave no partial temporary files");
    }

    private static async Task LegacyMigrationPreservesNewEntriesAndDoesNotRepeatAsync(string root, Action<bool, string> check)
    {
        // Both directories are disposable artifacts; never migrate the user's real cache in tests.
        string legacyFolder = Path.Combine(root, "migration-old-cache");
        string newFolder = Path.Combine(root, "migration-new-cache");
        Directory.CreateDirectory(legacyFolder);
        Directory.CreateDirectory(newFolder);
        string oldThumbnail = ThumbnailPath(legacyFolder, 'A');
        string oldDuplicate = ThumbnailPath(legacyFolder, 'B');
        string newDuplicate = ThumbnailPath(newFolder, 'B');
        var originalRecency = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await File.WriteAllBytesAsync(oldThumbnail, [1, 2, 3]);
        File.SetLastWriteTimeUtc(oldThumbnail, originalRecency);
        await File.WriteAllBytesAsync(oldDuplicate, [4]);
        await File.WriteAllBytesAsync(newDuplicate, [9]);
        await File.WriteAllTextAsync(Path.Combine(legacyFolder, "pipeline.jsonl"), "old diagnostics");
        string oldViewerFolder = Path.Combine(legacyFolder, "preview1600");
        Directory.CreateDirectory(oldViewerFolder);
        await File.WriteAllBytesAsync(ThumbnailPath(oldViewerFolder, 'C'), [5]);

        var migrated = new ThumbnailDiskCache(newFolder, legacyRoot: legacyFolder);
        await migrated.Ready;

        string movedThumbnail = ThumbnailPath(newFolder, 'A');
        check(File.Exists(movedThumbnail) && !File.Exists(oldThumbnail),
            "Migration moves owned grid thumbnails into the new cache without leaving duplicate copies");
        check(File.GetLastWriteTimeUtc(movedThumbnail) == originalRecency,
            "Migration preserves thumbnail timestamps used for LRU ordering");
        check((await File.ReadAllBytesAsync(newDuplicate)).SequenceEqual(new byte[] { 9 }),
            "Migration never overwrites an existing thumbnail in the new cache");
        check(File.Exists(Path.Combine(legacyFolder, "pipeline.jsonl")) && File.Exists(ThumbnailPath(oldViewerFolder, 'C')),
            "Migration leaves old logs and nested viewer previews untouched");

        await migrated.ClearAsync(default);
        var restarted = new ThumbnailDiskCache(newFolder, legacyRoot: legacyFolder);
        await restarted.Ready;
        check(!Directory.EnumerateFiles(newFolder).Any(ThumbnailDiskCache.IsCacheFile) && File.Exists(oldDuplicate),
            "Migration runs only once, so clearing the new cache does not reimport leftover old thumbnails");
    }
}
