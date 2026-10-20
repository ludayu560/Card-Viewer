using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace CardViewer;

public static partial class SelfTests
{
    private static async Task VerifyRawAssociationAndFullHifLoadingAsync(string root, ImageFile original, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "pairs");
        Directory.CreateDirectory(folder);
        var hifPath = Path.Combine(folder, "Pair.HIF");
        File.Copy(original.Path, hifPath);
        var info = new FileInfo(hifPath);
        var hif = new ImageFile(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        await using var store = new ThumbnailStore(Path.Combine(root, "pairs-cache"));
        await File.WriteAllTextAsync(Path.Combine(folder, "Pair-other.RAF"), "unrelated");
        check(await store.ResolveFullImageAsync(hif, default) == hif, "Backend companion lookup returns the original HIF when no exact RAF partner exists");
        var rafPath = Path.Combine(folder, "pair.rAf");
        await File.WriteAllTextAsync(rafPath, "invalid RAW fixture");
        var resolved = await store.ResolveFullImageAsync(hif, default);
        check(resolved.Path == rafPath, "Backend export/deletion companion lookup finds a same-folder, same-stem RAF case-insensitively");
        var grid = await store.GetAsync(hif, default);
        var preview = await store.GetPreviewAsync(hif, default);
        check(Math.Max(grid.PixelWidth, grid.PixelHeight) == 320 && Math.Max(preview.PixelWidth, preview.PixelHeight) == 1600, "Grid and fast viewer preview remain HIF-only even with a RAF partner");
        check(FullImageDecoder.Settings(true).GetDefine(ImageMagick.MagickFormat.Dng, "read-thumbnails") == "false" && FullImageDecoder.Settings(true).GetDefine(ImageMagick.MagickFormat.Dng, "read-thumbnail") == "false", "RAW full decode explicitly disables embedded thumbnails");
        check(ImageMagick.MagickNET.SupportedFormats.Any(f => f.Format == ImageMagick.MagickFormat.Raf && f.SupportsReading), "Bundled decoder reports RAF reading support");
        bool failed = false;
        try
        {
            await store.GetFullImageAsync(resolved, default);
        }
        catch (Exception ex) when (ex is ImageMagick.MagickException or InvalidDataException) { failed = true; }
        check(failed, "Invalid RAF reports a decode error instead of displaying a RAF preview");
        var updates = new List<FullImageProgress>();
        var full = await store.GetFullImageAsync(hif, default, new CaptureProgress(updates));
        check(full.IsFrozen && Math.Max(full.PixelWidth, full.PixelHeight) == 7728, "Full HIF decode retains native pixels without resizing");
        var reading = updates.Where(p => p.Stage == FullImageLoadStage.Reading).ToArray();
        check(reading.Length > 1 && reading[0].BytesRead == 0 && reading[^1].BytesRead == hif.Length && reading.All(p => p.TotalBytes == hif.Length) && reading.Select(p => p.BytesRead).SequenceEqual(reading.Select(p => p.BytesRead).Order()), "Full-image read progress reports monotonically increasing actual byte counts through 100 percent");
        check(updates[0].Stage == FullImageLoadStage.QueuedRead && updates[^1].Stage == FullImageLoadStage.Decoding && updates.FindIndex(p => p.Stage == FullImageLoadStage.QueuedDecode) > updates.FindLastIndex(p => p.Stage == FullImageLoadStage.Reading), "Progress distinguishes queued I/O, measured reading and indeterminate decoding");
        check(File.ReadAllText(rafPath) == "invalid RAW fixture", "RAF lookup and failed decode do not modify the file");
    }

    private sealed class CaptureProgress(List<FullImageProgress> updates) : IProgress<FullImageProgress>
    {
        public void Report(FullImageProgress value)
        {
            lock (updates)
                updates.Add(value);
        }
    }

    private static async Task VerifyCancelledRawDoesNotBlockHifAsync(string root, ImageFile hif, string folder, Action<bool, string> check)
    {
        var sample = new DirectoryInfo(folder).EnumerateFiles().FirstOrDefault(f => ImageFileFilter.IsRaf(f));
        if (sample == null)
            return;
        var raf = new ImageFile(sample.FullName, sample.Length, sample.LastWriteTimeUtc.Ticks);
        await using var store = new ThumbnailStore(Path.Combine(root, "cancelled-raw"));
        using var firstStop = new CancellationTokenSource();
        using var secondStop = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.GetFullImageAsync(raf, firstStop.Token, new CallbackProgress(p => { if (p.Stage == FullImageLoadStage.Decoding) firstStarted.TrySetResult(); }));
        Task<System.Windows.Media.Imaging.BitmapSource>? second = null;
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = store.GetFullImageAsync(raf, secondStop.Token, new CallbackProgress(p => { if (p.Stage == FullImageLoadStage.QueuedDecode) secondQueued.TrySetResult(); }));
            await secondQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var preview = await store.GetEmbeddedAsync(hif, 0, default).WaitAsync(TimeSpan.FromSeconds(5));
            check(preview != null, "HIF preview loads while one RAW decode runs and another is queued");
        }
        finally
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            firstStop.Cancel();
            secondStop.Cancel();
            try
            {
                await Task.WhenAll(second == null ? new[] { first } : new[] { first, second }).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) { }
            check(timer.Elapsed < TimeSpan.FromSeconds(5), "Abandoned RAW requests terminate promptly instead of blocking HIF workers");
        }
        var grid = await store.GetAsync(hif, default).WaitAsync(TimeSpan.FromSeconds(5));
        check(grid.IsFrozen && store.SourceGate.CurrentCount == 1, "HIF loading continues after RAW cancellation without a leaked source gate");
        // Verify termination of an actual child process, not just suppression of its result.
        using var stop = new CancellationTokenSource();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        string? workerFolder = null;
        var bytes = await File.ReadAllBytesAsync(sample.FullName);
        try
        {
            await RafWorkerDecoder.DecodeAsync(bytes, stop.Token, process =>
            {
                workerFolder = process.StartInfo.Environment["MAGICK_TEMPORARY_PATH"];
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => exited.TrySetResult();
                stop.CancelAfter(500);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { cancelled = true; }
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(cancelled, "Cancelling native RAW decode exits its child process (no full RAW benchmark)");
        check(workerFolder != null && !Directory.Exists(workerFolder), "RAW cancellation removes its private temporary input, output and scratch files");
    }

    private sealed class CallbackProgress(Action<FullImageProgress> callback) : IProgress<FullImageProgress>
    {
        public void Report(FullImageProgress value) => callback(value);
    }

    private static async Task VerifyPreviewReplacementAndCloseDuringLoadingAsync(ImageFile file, Action<bool, string> check)
    {
        static System.Windows.Media.Imaging.BitmapSource Bitmap(byte value)
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { value, value, value, 255 }, 4);
            bitmap.Freeze();
            return bitmap;
        }
        var preview = Bitmap(10);
        var original = Bitmap(20);
        var source = new PairSource(preview);
        using var session = new ViewerSession(source);
        session.Open(file);
        await source.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(ReferenceEquals(session.DisplayedImage, preview) && session.LoadingOriginal, "Viewer shows fast preview while original loads");
        source.HifReady.SetResult(original);
        await session.Completion;
        check(ReferenceEquals(session.OriginalFull, original) && ReferenceEquals(session.DisplayedImage, original), "Viewer replaces preview with the original full-resolution image");
        check(source.PreviewCalls == 1 && source.FullCalls == 1 && source.ResolveCalls == 0, "Viewer never resolves or decodes a RAW companion");
        session.Dispose();
        check(session.OriginalFull == null && session.DisplayedImage == null, "Closing viewer releases retained images");
        var cancelledSource = new PairSource(preview);
        using var cancelled = new ViewerSession(cancelledSource);
        cancelled.Open(file);
        await cancelledSource.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Dispose();
        await cancelled.Completion;
        check(cancelled.DisplayedImage == null, "Closing during background loading prevents stale image publication");

        var vmSource = new PairSource(preview);
        using var viewModel = new ViewerViewModel(vmSource, new[] { new ImageItem(file) }, 0);
        var viewerWindow = new ViewerWindow(viewModel);
        try
        {
            var content = (System.Windows.FrameworkElement)viewerWindow.Content;
            content.Measure(new System.Windows.Size(1100, 800));
            content.Arrange(new System.Windows.Rect(0, 0, 1100, 800));
            content.UpdateLayout();
            var progress = (System.Windows.Controls.ProgressBar)viewerWindow.FindName("LoadProgress");
            var binding = progress.GetBindingExpression(System.Windows.Controls.Primitives.RangeBase.ValueProperty)!;
            binding.UpdateTarget();
            check(!binding.HasError && progress.Value == viewModel.ProgressPercent, "Actual viewer XAML binds read-only progress without a write-back exception");
            viewModel.LoadCommand.Execute(null);
            await vmSource.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(ReferenceEquals(viewModel.DisplayedImage, preview) && viewModel.IsLoading, "Viewer VM exposes fast preview and loading progress");
            vmSource.HifReady.SetResult(original);
            await viewModel.Completion;
            check(ReferenceEquals(viewModel.DisplayedImage, original) && !viewModel.IsLoading, "Viewer VM binds the loaded original and completed progress");
            check(!viewModel.NextCommand.CanExecute(null) && !viewModel.PreviousCommand.CanExecute(null), "Viewer guards navigation boundaries");
        }
        finally { viewerWindow.Close(); }
    }

    private sealed class PairSource(System.Windows.Media.Imaging.BitmapSource preview) : IViewerImageSource
    {
        public TaskCompletionSource<System.Windows.Media.Imaging.BitmapSource> HifReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<System.Windows.Media.Imaging.BitmapSource> RafReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PreviewCalls, FullCalls, ResolveCalls;
        public Task<System.Windows.Size> GetDimensionsAsync(ImageFile file, CancellationToken token) => Task.FromResult(new System.Windows.Size(6000, 4000));
        public Task<System.Windows.Media.Imaging.BitmapSource> GetPreviewAsync(ImageFile file, CancellationToken token)
        {
            Interlocked.Increment(ref PreviewCalls);
            return Task.FromResult(preview);
        }
        public Task<ImageFile> ResolveFullImageAsync(ImageFile file, CancellationToken token) { ResolveCalls++; return Task.FromResult(file with { Path = Path.ChangeExtension(file.Path, ".RAF") }); }
        public Task<System.Windows.Media.Imaging.BitmapSource> GetFullImageAsync(ImageFile file, CancellationToken token, IProgress<FullImageProgress>? progress = null)
        {
            if (Interlocked.Increment(ref FullCalls) == 1)
                BothStarted.TrySetResult();
            return (file.Path.EndsWith(".RAF", StringComparison.OrdinalIgnoreCase) ? RafReady : HifReady).Task.WaitAsync(token);
        }
    }
}
