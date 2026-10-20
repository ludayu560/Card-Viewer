using System.Windows;

namespace CardViewer;

internal sealed class WpfImageViewerService : IImageViewerService
{
    private readonly Func<Window> owner;
    private readonly IViewerImageSource source;
    private ViewerWindow? current;
    private Task closing = Task.CompletedTask;

    internal WpfImageViewerService(Func<Window> owner, IViewerImageSource source)
    {
        this.owner = owner;
        this.source = source;
    }

    public void Open(IReadOnlyList<ImageItem> images, int index)
    {
        Close();
        current = new ViewerWindow(new ViewerViewModel(source, images, index), deferDisplay: true) { Owner = owner() };
        current.Show();
    }

    public void Close()
    {
        if (current == null)
            return;
        var window = current;
        current = null;
        window.Close();
        closing = Task.WhenAll(closing, window.Completion);
    }

    public async Task CloseAsync()
    {
        Close();
        await closing;
    }

    public void Dispose() => Close();
}

