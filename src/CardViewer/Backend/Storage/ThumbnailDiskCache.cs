using System.IO;
namespace CardViewer;

// Only owns hash-named grid thumbnails, never source images or arbitrary cache-folder files.
// LastWriteTimeUtc is explicitly updated by this app as a persistent LRU access stamp.
internal sealed class ThumbnailDiskCache
{
    internal const long DefaultBudgetBytes = 2_000_000_000;
    private sealed record Entry(long Length, DateTime Used);
    private readonly string root;
    private readonly long budget;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task initialization;
    private long bytes;
    private DateTime lastUsed;

    internal ThumbnailDiskCache(string root, long budget = DefaultBudgetBytes, string? legacyRoot = null)
    {
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
        this.root = root;
        this.budget = budget;
        initialization = Task.Run(() =>
        {
            try
            {
            if (legacyRoot != null) MigrateLegacyThumbnails(legacyRoot);
            if (Directory.Exists(root))
                foreach (var path in Directory.EnumerateFiles(root).Where(IsCacheFile))
                {
                    try { var info = new FileInfo(path); entries[path] = new(info.Length, info.LastWriteTimeUtc); bytes += info.Length; if (info.LastWriteTimeUtc > lastUsed) lastUsed = info.LastWriteTimeUtc; }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            Trim();
            // Retire the old persistent viewer cache. New 1600px previews are memory-only.
            string legacy = Path.Combine(root, "preview1600");
            if (Directory.Exists(legacy))
                foreach (var path in Directory.EnumerateFiles(legacy).Where(IsCacheFile))
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        });
    }

    internal Task Ready => initialization;
    private void MigrateLegacyThumbnails(string legacyRoot)
    {
        string marker = Path.Combine(root, ".legacy-cache-migrated");
        if (File.Exists(marker) || !Directory.Exists(legacyRoot)) return;
        // Move only owned grid thumbnails. Never traverse directories or replace newer entries.
        if ((File.GetAttributes(legacyRoot) & FileAttributes.ReparsePoint) != 0) return;
        Directory.CreateDirectory(root);
        foreach (var source in Directory.EnumerateFiles(legacyRoot).Where(IsCacheFile))
        {
            try
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                string destination = Path.Combine(root, Path.GetFileName(source));
                if (!File.Exists(destination)) File.Move(source, destination);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // A best-effort, one-time import must not resurrect thumbnails after Clear cache.
        File.WriteAllText(marker, "Grid thumbnails migrated from the previous application folder.");
    }
    private DateTime Stamp() => lastUsed = new(Math.Max(DateTime.UtcNow.Ticks, lastUsed.Ticks + 1), DateTimeKind.Utc);
    internal static bool IsCacheFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
        return name.Length == 64 && name.All(Uri.IsHexDigit) && (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".png", StringComparison.OrdinalIgnoreCase));
    }

    internal async Task<byte[]?> ReadAsync(string path, CancellationToken token)
    {
        await initialization.WaitAsync(token).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] data;
            try { data = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (entries.Remove(path, out var missing)) bytes -= missing.Length;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            var used = Stamp();
            if (entries.Remove(path, out var old)) bytes -= old.Length;
            entries[path] = new(data.LongLength, used);
            bytes += data.LongLength;
            try { File.SetLastWriteTimeUtc(path, used); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return data;
        }
        finally { gate.Release(); }
    }

    internal async Task WriteAsync(string path, byte[] data, CancellationToken token)
    {
        await initialization.WaitAsync(token).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
            var used = Stamp();
            if (entries.Remove(path, out var old)) bytes -= old.Length;
            entries[path] = new(data.LongLength, used);
            bytes += data.LongLength;
            try { File.SetLastWriteTimeUtc(path, used); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Trim();
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            gate.Release();
        }
    }

    private void Trim()
    {
        if (bytes <= budget) return;
        foreach (var pair in entries.OrderBy(pair => pair.Value.Used).ThenBy(pair => pair.Key).ToArray())
        {
            if (bytes <= budget) break;
            try
            {
                File.Delete(pair.Key);
                entries.Remove(pair.Key);
                bytes -= pair.Value.Length;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal async Task<int> ClearAsync(CancellationToken token)
    {
        await initialization.WaitAsync(token).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int count = 0;
            foreach (var folder in new[] { root, Path.Combine(root, "preview1600") })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var path in Directory.EnumerateFiles(folder).Where(IsCacheFile))
                {
                    token.ThrowIfCancellationRequested();
                    File.Delete(path);
                    if (entries.Remove(path, out var old)) bytes -= old.Length;
                    count++;
                }
            }
            return count;
        }
        finally { gate.Release(); }
    }
}
