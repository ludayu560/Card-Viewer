using System.IO;
using System.Runtime.CompilerServices;

namespace CardViewer;

public sealed class ImageBrowserService : IImageBrowserBackend
{
    private readonly ThumbnailStore store;

    public ImageBrowserService(ThumbnailStore store)
    {
        this.store = store;
    }

    public async IAsyncEnumerable<IReadOnlyList<ImageFile>> ScanAsync(string folder, [EnumeratorCancellation] CancellationToken token)
    {
        using var files = await Task.Run(async () =>
        {
            await store.SourceGate.WaitAsync(token);
            try { return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly).GetEnumerator(); }
            finally { store.SourceGate.Release(); }
        }, token).ConfigureAwait(false);
        bool more = true;
        while (more)
        {
            var batch = await Task.Run(async () =>
            {
                var found = new List<ImageFile>(64);
                await store.SourceGate.WaitAsync(token);
                try
                {
                    for (int i = 0; i < 64 && (more = files.MoveNext()); i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var file = files.Current;
                        if (ImageFileFilter.IsSupported(file))
                            found.Add(new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks));
                    }
                }
                finally { store.SourceGate.Release(); }
                return found;
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            yield return batch;
        }
    }

    public Task<int> ClearCacheAsync(CancellationToken token) => Task.Run(() => store.ClearAsync(token), token);
    public Task<BenchmarkResult> BenchmarkAsync(IReadOnlyList<ImageFile> files, CancellationToken token, IProgress<int>? progress = null) =>
        Task.Run(() => ThumbnailBenchmark.RunAsync(store, files, token, progress), token);
    public Task<DeletionResult> DeleteAsync(IReadOnlyList<ImageFile> files, CancellationToken token) =>
        new ImageDeletionService(store.SourceGate).DeleteAsync(files, token);
    public Task<IReadOnlyList<ImageFile>> PrepareDeletionAsync(IReadOnlyList<ImageFile> files, CancellationToken token) =>
        new ImageDeletionService(store.SourceGate).PrepareAsync(files, token);
    public Task<ExportResult> ExportAsync(IReadOnlyList<ImageFile> files, string destination, CancellationToken token, IProgress<ExportProgress>? progress = null) =>
        new ImageExportService(store.SourceGate).ExportAsync(files, destination, token, progress);
}

