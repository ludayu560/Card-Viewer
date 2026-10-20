namespace CardViewer;

public sealed class PriorityWorkQueue : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly PriorityQueue<Func<Task>, (int Priority, long Sequence)> queue = new();
    private readonly SemaphoreSlim wake = new(0);
    private readonly Task[] workers;
    private bool stopping;
    private long sequence;
    public PriorityWorkQueue(int concurrency) => workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(Run)).ToArray();
    public Task<T> Enqueue<T>(int priority, Func<Task<T>> action, CancellationToken token = default)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (stopping)
                return Task.FromException<T>(new ObjectDisposedException(nameof(PriorityWorkQueue)));
            queue.Enqueue(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    result.TrySetResult(await action());
                }
                catch (OperationCanceledException ex) { result.TrySetCanceled(ex.CancellationToken); }
                catch (Exception ex) { result.TrySetException(ex); }
            }, (priority, ++sequence));
        }
        wake.Release();
        return result.Task;
    }
    private async Task Run()
    {
        while (true)
        {
            await wake.WaitAsync();
            Func<Task>? action;
            lock (sync)
            {
                if (queue.TryDequeue(out var work, out _))
                    action = work;
                else if (stopping)
                    return;
                else
                    continue;
            }
            await action();
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync)
            stopping = true;
        wake.Release(workers.Length);
        await Task.WhenAll(workers);
    }
}
