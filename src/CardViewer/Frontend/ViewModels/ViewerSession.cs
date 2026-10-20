using System.Windows.Media.Imaging;
namespace CardViewer;

// Keep the fast preview visible until the selected original finishes loading.
// RAW companions are reserved for export, never requested by the viewer.
public sealed class ViewerSession : IDisposable
{
    private readonly IViewerImageSource source;
    private CancellationTokenSource? request;
    private ImageFile? original;
    private string? error;
    public ViewerSession(IViewerImageSource source) => this.source = source;
    public BitmapSource? OriginalPreview { get; private set; }
    public BitmapSource? OriginalFull { get; private set; }
    public System.Windows.Size? ImageDimensions { get; private set; }
    public bool LoadingOriginal { get; private set; }
    public FullImageProgress? Progress { get; private set; }
    public BitmapSource? DisplayedImage => OriginalFull ?? OriginalPreview;
    public string DisplayedName => original?.Name ?? "";
    public string OriginalFormat => System.IO.Path.GetExtension(original?.Path ?? "").TrimStart('.').ToUpperInvariant();
    public Task Completion { get; private set; } = Task.CompletedTask;
    public event Action? Changed;
    public string Status
    {
        get
        {
            var image = DisplayedImage;
            string display = image == null ? $"Loading {OriginalFormat} preview…" : $"{DisplayedName} · {image.PixelWidth} × {image.PixelHeight}";
            return display + (LoadingOriginal ? $" · Loading full-resolution {OriginalFormat}…" : error == null ? "" : " · " + error);
        }
    }
    public void Open(ImageFile file, BitmapSource? initial = null)
    {
        request?.Cancel();
        request = new();
        original = file;
        OriginalPreview = initial;
        OriginalFull = null;
        ImageDimensions = null;
        Progress = null;
        error = null;
        LoadingOriginal = true;
        Changed?.Invoke();
        Completion = Task.WhenAll(Completion, LoadAsync(file, request.Token));
    }
    private async Task LoadAsync(ImageFile file, CancellationToken token)
    {
        try
        {
            try
            {
                var dimensions = await Task.Run(() => source.GetDimensionsAsync(file, token), token);
                token.ThrowIfCancellationRequested();
                ImageDimensions = dimensions;
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Full decoding can still succeed if metadata probing fails. */ }
            try
            {
                var preview = await Task.Run(() => source.GetPreviewAsync(file, token), token);
                token.ThrowIfCancellationRequested();
                OriginalPreview = preview;
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { if (!token.IsCancellationRequested) error = "Preview: " + ex.Message; }
            var progress = new Progress<FullImageProgress>(value =>
            {
                if (token.IsCancellationRequested || !LoadingOriginal) return;
                Progress = value;
                Changed?.Invoke();
            });
            var bitmap = await Task.Run(() => source.GetFullImageAsync(file, token, progress), token);
            token.ThrowIfCancellationRequested();
            OriginalFull = bitmap;
            ImageDimensions ??= new(bitmap.PixelWidth, bitmap.PixelHeight);
            error = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) error = "Cannot load image: " + ex.Message; }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                LoadingOriginal = false;
                Changed?.Invoke();
            }
        }
    }
    public void Dispose()
    {
        request?.Cancel();
        OriginalPreview = null;
        OriginalFull = null;
        original = null;
        ImageDimensions = null;
        LoadingOriginal = false;
        Progress = null;
        Changed?.Invoke();
    }
}
