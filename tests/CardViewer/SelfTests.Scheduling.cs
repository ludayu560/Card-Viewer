using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace CardViewer;

public static partial class SelfTests
{
    private static async Task VerifyVisiblePriorityAndQueuedCancellationAsync(Action<bool, string> check)
    {
        await using var queue = new PriorityWorkQueue(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var active = queue.Enqueue(50, async () => { entered.SetResult(); await release.Task; return true; });
        await entered.Task;
        var background = queue.Enqueue(100, () => { order.Add("background"); return Task.FromResult(true); });
        using var stop = new CancellationTokenSource();
        var canceled = queue.Enqueue(0, () => { order.Add("cancelled"); return Task.FromResult(true); }, stop.Token);
        stop.Cancel();
        var visible = queue.Enqueue(10, () => { order.Add("visible"); return Task.FromResult(true); });
        release.SetResult();
        await Task.WhenAll(active, background, visible);
        try
        {
            await canceled;
        }
        catch (OperationCanceledException) { }
        check(order.SequenceEqual(new[] { "visible", "background" }), "Visible queued work overtakes background; cancelled queued work never runs");
    }

    private static async Task VerifyReadsContinueDuringBlockedDecodeAsync(string root, ImageFile file, byte[] jpeg, Action<bool, string> check)
    {
        using var decoder = new BlockingDecoder(jpeg);
        await using var store = new ThumbnailStore(Path.Combine(root, "race-cache"), decoder);
        var loading = Task.Run(() => store.GetAsync(file, default));
        check(decoder.Entered.Wait(TimeSpan.FromSeconds(10)), "Decode reached independent worker");
        try
        {
            var preview = store.GetEmbeddedAsync(file, 0, default);
            check(await Task.WhenAny(preview, Task.Delay(5000)) == preview && await preview != null, "Source reading and preview decoding continue while another decode is blocked");
            var clear = Task.Run(() => store.ClearAsync());
            await Task.Delay(100);
            check(!clear.IsCompleted, "Clear waits for in-flight writes");
            decoder.Release.Set();
            await loading;
            check(await clear == 1, "Clear removes the completed in-flight cache entry");
        }
        finally { decoder.Release.Set(); }
    }

    private static async Task VerifyDispatcherRemainsResponsiveDuringDecodeAsync(string root, ImageFile file, byte[] jpeg, Action<bool, string> check, Action beginUiChecks)
    {
        using var blocking = new BlockingDecoder(jpeg);
        await using var store = new ThumbnailStore(Path.Combine(root, "dispatcher-cache"), blocking);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                using var scheduler = new ThumbnailScheduler(store, dispatcher);
                try
                {
                    var item = new ImageItem(file);
                    var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImageItem.Thumbnail) && item.Thumbnail != null) shown.TrySetResult(); };
                    scheduler.Show(item);
                    await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    check(Math.Max(item.Thumbnail!.PixelWidth, item.Thumbnail.PixelHeight) == 160, "Dispatcher displays pass 1 before pass 2 completes");
                    check(await Task.Run(() => blocking.Entered.Wait(TimeSpan.FromSeconds(5))), "Grid refinement starts after embedded preview");
                    scheduler.Hide(item);
                    blocking.Release.Set();
                    await scheduler.PauseAsync();
                    check(item.Thumbnail == null, "Leaving viewport cancels refinement and prevents stale UI replacement");
                    scheduler.Show(item);
                    scheduler.Resume();
                    var refined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImageItem.Thumbnail) && item.Thumbnail != null && Math.Max(item.Thumbnail.PixelWidth, item.Thumbnail.PixelHeight) == 320) refined.TrySetResult(); };
                    await refined.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    check(item.Thumbnail!.IsFrozen, "Dispatcher atomically replaces preview with final frozen grid image");
                    await scheduler.PauseAsync();
                    beginUiChecks();
                    await VerifyPreviewReplacementAndCloseDuringLoadingAsync(file, check);
                    DeletionTests.CheckConfirmation(file, check);
                    await VerifyBrowserSelectionAndDeletionCommandsAsync(root, check);
                    await UiTests.RunAsync(root, check);
                    done.SetResult();
                }
                catch (Exception ex) { blocking.Release.Set(); done.TrySetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
        thread.Join();
    }

    private sealed class BlockingDecoder(byte[] jpeg) : IThumbnailDecoder, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new();
        public byte[] Decode(byte[] bytes)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException();
            return jpeg;
        }
        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
