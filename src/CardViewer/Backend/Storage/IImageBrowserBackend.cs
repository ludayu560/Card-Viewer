namespace CardViewer;

public interface IImageBrowserBackend
{
    IAsyncEnumerable<IReadOnlyList<ImageFile>> ScanAsync(string folder, CancellationToken token);
    Task<int> ClearCacheAsync(CancellationToken token);
    Task<BenchmarkResult> BenchmarkAsync(IReadOnlyList<ImageFile> files, CancellationToken token, IProgress<int>? progress = null);
    Task<DeletionResult> DeleteAsync(IReadOnlyList<ImageFile> files, CancellationToken token);
    Task<IReadOnlyList<ImageFile>> PrepareDeletionAsync(IReadOnlyList<ImageFile> files, CancellationToken token);
    Task<ExportResult> ExportAsync(IReadOnlyList<ImageFile> files, string destination, CancellationToken token, IProgress<ExportProgress>? progress = null);
}

