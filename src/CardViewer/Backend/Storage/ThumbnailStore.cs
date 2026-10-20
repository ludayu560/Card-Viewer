using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using ImageMagick;
namespace CardViewer;

public sealed class ThumbnailStore : IAsyncDisposable, IViewerImageSource
{
    private readonly string root;
    private readonly ThumbnailDiskCache diskCache;
    private readonly IThumbnailDecoder? decoder;
    private readonly PriorityWorkQueue reads = new(1), decodes = new(2), writes = new(1);
    private readonly PriorityWorkQueue rawDecodes = new(1);
    private readonly Dictionary<string, HifIndex> indexes = [];
    private readonly PipelineLog log;
    private readonly object metrics = new();
    private StageTotals totals = new(0, 0, 0, 0, 0);
    public StageTotals Timings
    {
        get
        {
            lock (metrics)
                return totals;
        }
    }
    private void Record(Measurement measurement)
    {
        var row = measurement.Result();
        lock (metrics)
            totals = new(totals.BytesRead + row.BytesRead, totals.ReadMs + row.ReadMs, totals.DecodeMs + row.DecodeMs, totals.ResizeMs + row.ResizeMs, totals.WriteMs + row.WriteMs);
        log.Write(row);
    }
    public SemaphoreSlim SourceGate { get; } = new(1, 1);
    private int sourceReads, cacheHits;
    public int SourceReads => Volatile.Read(ref sourceReads);
    public int CacheHits => Volatile.Read(ref cacheHits);
    private readonly object operations = new();
    private int active;
    private TaskCompletionSource drained = NewSignal(), available = NewSignal();
    private bool clearing;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static string DefaultCachePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CardViewer", "Thumbnails", "v3");
    public ThumbnailStore(string root, IThumbnailDecoder? decoder = null, long cacheBudgetBytes = ThumbnailDiskCache.DefaultBudgetBytes)
    {
        this.root = System.IO.Path.GetFullPath(root);
        this.decoder = decoder;
        string? legacyRoot = this.root.Equals(DefaultCachePath, StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HifBrowser", "Thumbnails", "v3")
            : null;
        diskCache = new(this.root, cacheBudgetBytes, legacyRoot);
        log = new(System.IO.Path.Combine(this.root, "pipeline.jsonl"));
    }
    private async Task Enter(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (operations)
            {
                if (!clearing)
                {
                    active++;
                    return;
                }
                wait = available.Task;
            }
            await wait.WaitAsync(token);
        }
    }
    private void Exit()
    {
        lock (operations)
        {
            if (--active == 0)
                drained.TrySetResult();
        }
    }
    public async Task<int> ClearAsync(CancellationToken token = default)
    {
        Task wait;
        lock (operations)
        {
            if (clearing)
                throw new InvalidOperationException("Cache clearing is already running.");
            clearing = true;
            available = NewSignal();
            drained = NewSignal();
            wait = active == 0 ? Task.CompletedTask : drained.Task;
        }
        try
        {
            await wait.WaitAsync(token);
            return await diskCache.ClearAsync(token);
        }
        finally { lock (operations) { clearing = false; available.TrySetResult(); } }
    }
    public Task<BitmapSource> GetAsync(ImageFile file, CancellationToken token) => GetSizedAsync(file, 320, 30, token);
    public Task<System.Windows.Size> GetDimensionsAsync(ImageFile file, CancellationToken token) => reads.Enqueue(0, async () =>
    {
        await SourceGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var image = new MagickImage();
            image.Ping(stream);
            token.ThrowIfCancellationRequested();
            bool rotated = (int)image.Orientation is 5 or 6 or 7 or 8;
            return rotated ? new System.Windows.Size(image.Height, image.Width) : new System.Windows.Size(image.Width, image.Height);
        }
        finally { SourceGate.Release(); }
    }, token);
    public Task<BitmapSource> GetPreviewAsync(ImageFile file, CancellationToken token) => GetSizedAsync(file, 1600, 0, token);
    public Task<ImageFile> ResolveFullImageAsync(ImageFile hif, CancellationToken token) => reads.Enqueue(0, async () =>
    {
        await SourceGate.WaitAsync(token);
        try
        {
            var folder = System.IO.Path.GetDirectoryName(hif.Path)!;
            var stem = System.IO.Path.GetFileNameWithoutExtension(hif.Path);
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (ImageFileFilter.IsRaw(file) && System.IO.Path.GetFileNameWithoutExtension(file.Name).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return new ImageFile(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
            }
            return hif;
        }
        finally { SourceGate.Release(); }
    }, token);
    public async Task<BitmapSource> GetFullImageAsync(ImageFile file, CancellationToken token, IProgress<FullImageProgress>? progress = null)
    {
        bool raf = System.IO.Path.GetExtension(file.Path).Equals(".raf", StringComparison.OrdinalIgnoreCase);
        int priority = raf ? 5 : 0;
        await Enter(token);
        var timing = new Measurement(file, "full-image");
        try
        {
            progress?.Report(new(FullImageLoadStage.QueuedRead));
            var source = await reads.Enqueue(priority, async () =>
            {
                await SourceGate.WaitAsync(token);
                var clock = Stopwatch.StartNew();
                try
                {
                    Interlocked.Increment(ref sourceReads);
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length > 512L * 1024 * 1024)
                        throw new InvalidDataException("Image exceeds 512 MB decode budget.");
                    var bytes = new byte[checked((int)stream.Length)];
                    progress?.Report(new(FullImageLoadStage.Reading, 0, bytes.Length));
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int count = await stream.ReadAsync(bytes.AsMemory(offset, Math.Min(1024 * 1024, bytes.Length - offset)), token);
                        if (count == 0)
                            throw new EndOfStreamException("Image changed while it was being read.");
                        offset += count;
                        timing.Bytes = offset;
                        progress?.Report(new(FullImageLoadStage.Reading, offset, bytes.Length));
                    }
                    return bytes;
                }
                finally { timing.ReadMs = clock.Elapsed.TotalMilliseconds; SourceGate.Release(); }
            }, token);
            progress?.Report(new(FullImageLoadStage.QueuedDecode, source.Length, source.Length));
            return await (raf ? rawDecodes : decodes).Enqueue(priority, async () =>
            {
                progress?.Report(new(FullImageLoadStage.Decoding, source.Length, source.Length));
                if (!raf)
                    return FullImageDecoder.Decode(source, false, token, ms => timing.DecodeMs = ms);
                var timer = Stopwatch.StartNew();
                try
                {
                    return await RafWorkerDecoder.DecodeAsync(source, token);
                }
                finally { timing.DecodeMs = timer.Elapsed.TotalMilliseconds; }
            }, token);
        }
        catch (Exception ex) { timing.Error = ex.Message; throw; }
        finally { Record(timing); Exit(); }
    }
    public Task<BitmapSource> GetGridAsync(ImageFile file, int priority, CancellationToken token) => GetSizedAsync(file, 320, priority, token);
    public async Task<BitmapSource?> GetEmbeddedAsync(ImageFile file, int priority, CancellationToken token)
    {
        if (!ImageFileFilter.IsHeif(new FileInfo(file.Path)) && !ImageFileFilter.IsJpeg(new FileInfo(file.Path)))
            return null;
        await Enter(token);
        var timing = new Measurement(file, "embedded");
        try
        {
            var data = await ReadImage(file, 0, priority, timing, token);
            if (data == null)
                return null;
            return await decodes.Enqueue(priority, () => Task.FromResult(Decode(data, 0, timing, token).Bitmap), token);
        }
        catch (OperationCanceledException) { timing.Error = "Cancelled"; throw; }
        catch (Exception ex) { timing.Error = ex.Message; return null; }
        finally { Record(timing); Exit(); }
    }
    private async Task<BitmapSource> GetSizedAsync(ImageFile file, int size, int priority, CancellationToken token)
    {
        await Enter(token);
        var timing = new Measurement(file, size == 320 ? "grid320" : "viewer1600");
        try
        {
            var cached = size == 320 ? await TryCacheCore(file, size, timing, token) : null;
            if (cached != null)
                return cached;
            var data = await ReadImage(file, size, priority, timing, token) ?? throw new InvalidDataException("No image data found.");
            var result = await decodes.Enqueue(priority, () => Task.FromResult(Decode(data, size, timing, token)), token);
            if (size != 320) return result.Bitmap;
            await writes.Enqueue(priority, async () =>
            {
                token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                var path = CachePath(file, size);
                try { await diskCache.WriteAsync(path, result.Encoded, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { timing.Error = "Cache write: " + ex.Message; }
                finally { timing.WriteMs += clock.Elapsed.TotalMilliseconds; }
                return true;
            }, token);
            return result.Bitmap;
        }
        catch (Exception ex) { timing.Error = ex.Message; throw; }
        finally { Record(timing); Exit(); }
    }
    public async Task<BitmapSource?> TryGridCacheAsync(ImageFile file, CancellationToken token)
    {
        await Enter(token);
        var timing = new Measurement(file, "grid320");
        try
        {
            return await TryCacheCore(file, 320, timing, token);
        }
        finally { if (timing.Cached) Record(timing); Exit(); }
    }
    private string CachePath(ImageFile file, int size) => System.IO.Path.Combine(size == 320 ? root : System.IO.Path.Combine(root, "preview1600"), file.Key(size) + (ImageFileFilter.UsesPngCache(new FileInfo(file.Path)) ? ".png" : ".jpg"));
    private async Task<BitmapSource?> TryCacheCore(ImageFile file, int size, Measurement timing, CancellationToken token)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var bytes = await diskCache.ReadAsync(CachePath(file, size), token);
            if (bytes == null) return null;
            timing.ReadMs += clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var bitmap = Load(bytes);
            timing.DecodeMs += clock.Elapsed.TotalMilliseconds;
            timing.Cached = true;
            Interlocked.Increment(ref cacheHits);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException) { return null; }
    }
    private sealed record ImageData(byte[] Bytes, bool Embedded, int Rotation, int? Mirror, bool Png = false, int JpegOrientation = 0);
    private Task<ImageData?> ReadImage(ImageFile file, int size, int priority, Measurement timing, CancellationToken token) => reads.Enqueue<ImageData?>(priority, async () =>
    {
        await SourceGate.WaitAsync(token);
        var clock = Stopwatch.StartNew();
        try
        {
            Interlocked.Increment(ref sourceReads);
            // Disable read-ahead so bounded JPEG probing also bounds FileStream reads.
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (size == 0 && ImageFileFilter.IsJpeg(new FileInfo(file.Path)))
            {
                var parsed = await JpegMetadata.ReadAsync(stream, token);
                timing.Bytes += parsed.BytesRead;
                return parsed.Preview == null ? null : new(parsed.Preview.Bytes, false, 0, null, JpegOrientation: parsed.Preview.Orientation);
            }
            HifIndex? index = null;
            if ((decoder == null || size == 0) && ImageFileFilter.IsHeif(new FileInfo(file.Path)))
            {
                if (!indexes.TryGetValue(file.CacheKey, out index))
                {
                    try
                    {
                        var parsed = await HifMetadata.ReadAsync(stream, token);
                        index = parsed.Index;
                        timing.Bytes += parsed.Bytes;
                        indexes[file.CacheKey] = index;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException or OverflowException or InvalidOperationException or ArgumentException)
                    {
                        if (size == 0)
                        {
                            timing.Error = "Embedded preview unavailable: " + ex.Message;
                            return null;
                        }
                    }
                }
            }
            EmbeddedJpeg? jpeg = size == 0 ? index?.Previews.FirstOrDefault() : index?.Previews.FirstOrDefault(p => Math.Max(p.Width, p.Height) >= size);
            if (jpeg != null && (size != 0 || jpeg.Length <= 256 * 1024))
            {
                stream.Seek(jpeg.Offset, SeekOrigin.Begin);
                var bytes = new byte[jpeg.Length];
                await stream.ReadExactlyAsync(bytes, token);
                timing.Bytes += bytes.Length;
                return new(bytes, true, index!.Rotation, index.Mirror);
            }
            if (size == 0)
            {
                timing.Error = "No supported bounded JPEG thumbnail; full decode deferred to pass 2.";
                return null;
            }
            stream.Position = 0;
            if (stream.Length > 512L * 1024 * 1024)
                throw new InvalidDataException("Image exceeds 512 MB decode budget.");
            var source = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(source, token);
            timing.Bytes += source.Length;
            return new(source, false, 0, null, ImageFileFilter.UsesPngCache(new FileInfo(file.Path)));
        }
        finally { timing.ReadMs += clock.Elapsed.TotalMilliseconds; SourceGate.Release(); }
    }, token);
    private (BitmapSource Bitmap, byte[] Encoded) Decode(ImageData data, int size, Measurement timing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        if (decoder != null && size == 320)
        {
            var jpeg = decoder.Decode(data.Bytes);
            timing.DecodeMs += clock.Elapsed.TotalMilliseconds;
            return (Load(jpeg), jpeg);
        }
        using var image = ThumbnailDecoder.Read(data.Bytes, size);
        timing.DecodeMs += clock.Elapsed.TotalMilliseconds;
        token.ThrowIfCancellationRequested();
        clock.Restart();
        if (data.Embedded)
        {
            // HEIF transforms apply to the primary image; indexed JPEG previews
            // in these cameras do not carry EXIF Orientation themselves.
            if (data.Mirror == 0)
                image.Flop();
            else if (data.Mirror == 1)
                image.Flip();
            if (data.Rotation != 0)
                image.Rotate(-data.Rotation);
        }
        else
        {
            if (data.JpegOrientation != 0)
                image.Orientation = (OrientationType)data.JpegOrientation;
            image.AutoOrient();
        }
        if (size > 0)
            image.Thumbnail((uint)size, (uint)size);
        image.ColorSpace = ColorSpace.sRGB;
        image.Strip();
        image.Quality = 85;
        timing.ResizeMs += clock.Elapsed.TotalMilliseconds;
        token.ThrowIfCancellationRequested();
        clock.Restart();
        var output = image.ToByteArray(data.Png ? MagickFormat.Png : MagickFormat.Jpeg);
        var bitmap = Load(output);
        timing.DecodeMs += clock.Elapsed.TotalMilliseconds;
        return (bitmap, output);
    }
    private static BitmapSource Load(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
    private sealed class Measurement(ImageFile file, string pass)
    {
        public bool Cached; public long Bytes; public double ReadMs, DecodeMs, ResizeMs, WriteMs; public string? Error;
        public PipelineTiming Result() => new(file.Name, pass, Cached, Bytes, ReadMs, DecodeMs, ResizeMs, WriteMs, Error);
    }
    public async ValueTask DisposeAsync()
    {
        await diskCache.Ready;
        await reads.DisposeAsync();
        await decodes.DisposeAsync();
        await rawDecodes.DisposeAsync();
        await writes.DisposeAsync();
    }
}




