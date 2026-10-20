using System.IO;

namespace CardViewer;

// The caller confirms an immutable selection before invoking this service.
public sealed class ImageDeletionService
{
    private readonly SemaphoreSlim sourceGate;

    public ImageDeletionService(SemaphoreSlim sourceGate)
    {
        this.sourceGate = sourceGate;
    }

    public static bool IsProtectedPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("TestImages", StringComparison.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyList<ImageFile>> PrepareAsync(IReadOnlyList<ImageFile> selection, CancellationToken token = default) => Task.Run(async () =>
    {
        await sourceGate.WaitAsync(token);
        try
        {
            var files = selection.ToList();
            foreach (var group in selection.GroupBy(file => Path.GetDirectoryName(Path.GetFullPath(file.Path))!, StringComparer.OrdinalIgnoreCase))
            {
                var stems = group.Select(file => Path.GetFileNameWithoutExtension(file.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var info in new DirectoryInfo(group.Key).EnumerateFiles())
                {
                    token.ThrowIfCancellationRequested();
                    if (ImageFileFilter.IsRaw(info) && stems.Contains(Path.GetFileNameWithoutExtension(info.Name)))
                        files.Add(new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks));
                }
            }
            return (IReadOnlyList<ImageFile>)files.DistinctBy(file => Path.GetFullPath(file.Path), StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { sourceGate.Release(); }
    }, token);

    public Task<DeletionResult> DeleteAsync(IReadOnlyList<ImageFile> selection, CancellationToken token = default)
    {
        var files = selection.DistinctBy(file => Path.GetFullPath(file.Path), StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.Run(async () =>
        {
            var deleted = new List<ImageFile>();
            var failures = new List<DeletionFailure>();
            foreach (var file in files)
            {
                if (token.IsCancellationRequested)
                {
                    failures.Add(new(file, "Deletion canceled."));
                    continue;
                }
                bool entered = false;
                try
                {
                    await sourceGate.WaitAsync(token);
                    entered = true;
                    bool companion = ImageFileFilter.IsRaw(new FileInfo(file.Path)) && files.Any(image =>
                        ImageFileFilter.IsSupported(new FileInfo(image.Path)) &&
                        Path.GetDirectoryName(Path.GetFullPath(image.Path))!.Equals(Path.GetDirectoryName(Path.GetFullPath(file.Path)), StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileNameWithoutExtension(image.Path).Equals(Path.GetFileNameWithoutExtension(file.Path), StringComparison.OrdinalIgnoreCase));
                    Validate(file, companion);
                    token.ThrowIfCancellationRequested();
                    File.Delete(file.Path);
                    deleted.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or NotSupportedException)
                {
                    failures.Add(new(file, ex is OperationCanceledException ? "Deletion canceled." : ex.Message));
                }
                finally
                {
                    if (entered)
                        sourceGate.Release();
                }
            }
            return new DeletionResult(deleted, failures);
        });
    }

    private static void Validate(ImageFile file, bool companion)
    {
        if (IsProtectedPath(file.Path))
            throw new UnauthorizedAccessException("Files under TestImages are protected from deletion.");
        var info = new FileInfo(file.Path);
        if (!ImageFileFilter.IsSupported(info) && !companion)
            throw new UnauthorizedAccessException("Only selected images and their confirmed RAW companions can be deleted.");
        if (!info.Exists)
            throw new FileNotFoundException("The selected image no longer exists.", file.Path);

        // Reject aliases so a link cannot bypass the protected-folder rule.
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Linked image files cannot be deleted.");
        for (var directory = info.Directory; directory != null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Images inside linked folders cannot be deleted.");
        }
        if (info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.ModifiedTicks)
            throw new IOException("This image changed since it was scanned. Reopen the folder before deleting it.");
    }
}

