using System.Diagnostics;
using System.IO;
using System.IO.Compression;
namespace CardViewer;

public sealed class ImageExportService(SemaphoreSlim sourceGate)
{
    public Task<ExportResult> ExportAsync(IReadOnlyList<ImageFile> selection, string destination, CancellationToken token, IProgress<ExportProgress>? progress = null) => Task.Run(async () =>
    {
        progress?.Report(new("Finding matching RAW files", null, 0, 0, 0, 0));
        var files = await new ImageDeletionService(sourceGate).PrepareAsync(selection, token);
        string folder = Path.GetFullPath(destination);
        if (ImageDeletionService.IsProtectedPath(folder)) throw new UnauthorizedAccessException("TestImages cannot be an export destination.");
        if (files.Any(file => Path.GetDirectoryName(Path.GetFullPath(file.Path))!.Equals(folder, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Choose an export folder different from the source folder.");
        for (var directory = new DirectoryInfo(folder); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Export destinations inside linked folders are not supported.");
        Directory.CreateDirectory(folder);
        string archivePath = Path.Combine(folder, $"CardViewer-export-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.zip");
        string temporary = archivePath + ".tmp";
        var copied = new List<ImageFile>();
        var failures = new List<ExportFailure>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long processed = 0, total = files.Sum(file => file.Length);
        int completed = 0;
        var timer = Stopwatch.StartNew();
        void Report(string stage, string? name, bool force = false)
        {
            if (!force && timer.ElapsedMilliseconds < 50) return;
            timer.Restart();
            progress?.Report(new(stage, name, processed, total, completed, files.Count));
        }
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var buffer = new byte[128 * 1024];
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    bool entryStarted = false;
                    try
                    {
                        if (!names.Add(file.Name)) throw new IOException("Duplicate file name in selection: " + file.Name);
                        FileStream input;
                        await sourceGate.WaitAsync(token);
                        try
                        {
                            var info = new FileInfo(file.Path);
                            if (info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.ModifiedTicks)
                                throw new IOException("Source changed since selection. Reopen the folder before exporting.");
                            input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        }
                        finally { sourceGate.Release(); }
                        await using (input)
                        {
                            // Fastest DEFLATE is lossless; camera JPEG/HEIF/RAW files are already compressed.
                            var entry = archive.CreateEntry(file.Name, CompressionLevel.Fastest);
                            entryStarted = true;
                            await using var entryStream = entry.Open();
                            Report("Packing", file.Name, true);
                            while (true)
                            {
                                int count;
                                await sourceGate.WaitAsync(token);
                                try { count = await input.ReadAsync(buffer, token); }
                                finally { sourceGate.Release(); }
                                if (count == 0) break;
                                await entryStream.WriteAsync(buffer.AsMemory(0, count), token);
                                processed += count;
                                Report("Packing", file.Name);
                            }
                        }
                        copied.Add(file);
                    }
                    catch (Exception ex) when (!entryStarted && ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add(new(file, ex.Message));
                        total -= file.Length;
                    }
                    completed++;
                    Report("Packing", file.Name, true);
                }
                Report("Finalizing ZIP", null, true);
            }
            token.ThrowIfCancellationRequested();
            if (copied.Count == 0) return new ExportResult(copied, failures);
            File.Move(temporary, archivePath, false);
            Report("ZIP ready", null, true);
            return new ExportResult(copied, failures, archivePath);
        }
        finally
        {
            // A mid-file failure aborts the whole archive rather than publishing truncated entries.
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }, token);
}

