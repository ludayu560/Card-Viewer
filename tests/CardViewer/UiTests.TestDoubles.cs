using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CardViewer;

// Runs on the existing STA test dispatcher. Hidden windows exercise real WPF
// templates, bindings and command routing without sending input to the desktop.

internal static partial class UiTests
{
    private sealed class TestBackend(IImageBrowserBackend inner) : IImageBrowserBackend
    {
        public int ClearCalls, Benchmarks, Deletions;
        public TaskCompletionSource<int>? PendingClear;
        public TaskCompletionSource<ExportResult>? PendingExport;
        public IProgress<ExportProgress>? ExportUpdates;
        public IAsyncEnumerable<IReadOnlyList<ImageFile>> ScanAsync(string folder, CancellationToken token) => inner.ScanAsync(folder, token);
        public Task<int> ClearCacheAsync(CancellationToken token) { ClearCalls++; return PendingClear?.Task.WaitAsync(token) ?? inner.ClearCacheAsync(token); }
        public Task<BenchmarkResult> BenchmarkAsync(IReadOnlyList<ImageFile> files, CancellationToken token, IProgress<int>? progress = null)
        {
            Benchmarks++;
            return Task.FromResult(new BenchmarkResult(files.Count, files.Count, 0, 0.01, files.Count, 0, [], 0, 0, new StageTotals(0, 0, 0, 0, 0)));
        }
        public Task<DeletionResult> DeleteAsync(IReadOnlyList<ImageFile> files, CancellationToken token) { Deletions++; return inner.DeleteAsync(files, token); }
        public Task<IReadOnlyList<ImageFile>> PrepareDeletionAsync(IReadOnlyList<ImageFile> files, CancellationToken token) => inner.PrepareDeletionAsync(files, token);
        public Task<ExportResult> ExportAsync(IReadOnlyList<ImageFile> files, string destination, CancellationToken token, IProgress<ExportProgress>? progress = null)
        {
            ExportUpdates = progress;
            return PendingExport?.Task.WaitAsync(token) ?? inner.ExportAsync(files, destination, token, progress);
        }
    }

    private sealed class TestThumbnails : IThumbnailPresentation
    {
        public HashSet<ImageItem> Shown { get; } = [];
        public HashSet<ImageItem> Hidden { get; } = [];
        public bool Disposed;
        public int PauseCalls;
        public void Show(ImageItem item) => Shown.Add(item);
        public void Hide(ImageItem item) => Hidden.Add(item);
        public Task PauseAsync() { PauseCalls++; return Task.CompletedTask; }
        public void Resume() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class TestViewer : IImageViewerService
    {
        public int Opens, LastIndex;
        public bool Disposed;
        public void Open(IReadOnlyList<ImageItem> images, int index) { Opens++; LastIndex = index; }
        public void Close() { }
        public Task CloseAsync() => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    private sealed class TestDialogs(string folder) : IBrowserDialogs
    {
        public bool Accept;
        public int Errors;
        public IReadOnlyList<ImageFile> LastSelection = [];
        public string? ChooseFolder() => folder;
        public string? ExportFolder;
        public string? ChooseExportFolder() => ExportFolder;
        public bool ConfirmDeletion(IReadOnlyList<ImageFile> files)
        {
            LastSelection = files.ToArray();
            var window = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(files));
            return Modal(window, () => Click(Accept ? window.DeleteButton : window.CancelButton)) == true;
        }
        public void ShowErrors(string title, string details)
        {
            Errors++;
            var window = new ErrorDetailsWindow(title, details);
            Modal(window, () => Click(Children<Button>(window).Single()));
        }
    }

    private sealed class TestImageSource : IViewerImageSource
    {
        public BitmapSource Preview { get; } = Pixel(20);
        public BitmapSource Original { get; } = Pixel(90);
        public BitmapSource Raf { get; } = Pixel(180);
        public TaskCompletionSource PairStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BitmapSource> OriginalReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BitmapSource> RafReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FullCalls, ResolveCalls;
        public Task<Size> GetDimensionsAsync(ImageFile file, CancellationToken token) => Task.FromResult(new Size(6000, 4000));
        private IProgress<FullImageProgress>? progress;
        public Task<BitmapSource> GetPreviewAsync(ImageFile file, CancellationToken token) => Task.FromResult(Preview);
        public Task<ImageFile> ResolveFullImageAsync(ImageFile file, CancellationToken token) { ResolveCalls++; return Task.FromResult(file with { Path = Path.ChangeExtension(file.Path, ".RAF") }); }
        public Task<BitmapSource> GetFullImageAsync(ImageFile file, CancellationToken token, IProgress<FullImageProgress>? progress = null)
        {
            bool raf = file.Path.EndsWith(".RAF", StringComparison.OrdinalIgnoreCase);
            this.progress = progress;
            if (Interlocked.Increment(ref FullCalls) == 1)
                PairStarted.TrySetResult();
            return (raf ? RafReady.Task : OriginalReady.Task).WaitAsync(token);
        }
        public void Report(FullImageProgress value) => progress!.Report(value);
    }
}
