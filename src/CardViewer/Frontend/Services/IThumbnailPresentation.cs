namespace CardViewer;

public interface IThumbnailPresentation : IDisposable
{
    void Show(ImageItem item);
    void Hide(ImageItem item);
    Task PauseAsync();
    void Resume();
}
