using System.Windows.Media.Imaging;

namespace CardViewer;

public sealed class ThumbnailScheduler : IThumbnailPresentation
{
    private sealed class Request
    {
        public CancellationTokenSource Stop { get; } = new(); public bool Ready;
    }
    private readonly Dictionary<ImageItem, Request> visible = [];
    private readonly HashSet<Task> pending = [];
    private readonly PriorityWorkQueue work = new(3);
    private readonly ThumbnailStore store;
    private readonly System.Windows.Threading.Dispatcher dispatcher;
    private bool paused, disposed;
    public ThumbnailScheduler(ThumbnailStore store, System.Windows.Threading.Dispatcher dispatcher)
    {
        this.store = store;
        this.dispatcher = dispatcher;
    }
    public void Show(ImageItem item)
    {
        lock (visible)
        {
            if (disposed || visible.ContainsKey(item))
                return;
            var request = new Request();
            visible.Add(item, request);
            if (!paused)
                Queue(item, request, 10, true);
        }
    }
    public void Hide(ImageItem item)
    {
        lock (visible)
        {
            if (visible.Remove(item, out var request))
                request.Stop.Cancel();
        }
        item.Thumbnail = null;
        item.Status = "Waiting for preview…";
    }
    private void Queue(ImageItem item, Request request, int priority, bool first)
    {
        var task = work.Enqueue(priority, async () =>
        {
            var token = request.Stop.Token;
            try
            {
                if (first)
                {
                    var cached = await store.TryGridCacheAsync(item.File, token);
                    if (cached != null)
                    {
                        await Publish(item, request, cached, "", true);
                        return true;
                    }
                    var embedded = await store.GetEmbeddedAsync(item.File, priority, token);
                    if (embedded != null)
                        await Publish(item, request, embedded, "", false);
                    lock (visible)
                        if (!paused && IsCurrent(item, request))
                            Queue(item, request, 30, false);
                }
                else
                {
                    var grid = await store.GetGridAsync(item.File, priority, token);
                    await Publish(item, request, grid, "", true);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await Publish(item, request, null, "Cannot decode image: " + ex.Message, false); }
            return true;
        }, request.Stop.Token);
        pending.Add(task);
        _ = task.ContinueWith(t => { _ = t.Exception; lock (visible) pending.Remove(t); }, TaskScheduler.Default);
    }
    private bool IsCurrent(ImageItem item, Request request) => !disposed && !request.Stop.IsCancellationRequested && visible.TryGetValue(item, out var current) && ReferenceEquals(current, request);
    private async Task Publish(ImageItem item, Request request, BitmapSource? bitmap, string status, bool ready)
    {
        if (dispatcher.HasShutdownStarted)
            return;
        await dispatcher.InvokeAsync(() =>
        {
            lock (visible)
            {
                if (!IsCurrent(item, request))
                    return;
                // One dispatcher transaction: the preview cannot overwrite a newer grid result.
                if (bitmap != null)
                    item.Thumbnail = bitmap;
                item.Status = status;
                request.Ready = ready;
            }
        });
    }
    public async Task PauseAsync()
    {
        Task[] tasks;
        lock (visible)
        {
            paused = true;
            foreach (var request in visible.Values)
                request.Stop.Cancel();
            tasks = pending.ToArray();
        }
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
    }
    public void Resume()
    {
        lock (visible)
        {
            if (disposed)
                return;
            paused = false;
            foreach (var item in visible.Keys.ToArray())
            {
                var old = visible[item];
                if (old.Ready)
                    continue;
                var request = new Request();
                visible[item] = request;
                Queue(item, request, 10, true);
            }
        }
    }
    public void Dispose()
    {
        lock (visible)
        {
            disposed = true;
            foreach (var request in visible.Values)
                request.Stop.Cancel();
        }
        _ = work.DisposeAsync();
    }
}
