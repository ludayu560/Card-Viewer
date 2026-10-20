namespace CardViewer;

public interface IImageViewerService : IDisposable
{
    void Open(IReadOnlyList<ImageItem> images, int index);
    void Close();
    Task CloseAsync();
}
