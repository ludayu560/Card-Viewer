using System.Windows.Threading;

namespace CardViewer;

internal static class AppBootstrapper
{
    internal static MainWindow CreateMainWindow(string? initialFolder = null)
    {
        var store = new ThumbnailStore(ThumbnailStore.DefaultCachePath);
        MainWindow? window = null;
        var dialogs = new WpfBrowserDialogs(() => window!);
        var viewer = new WpfImageViewerService(() => window!, store);
        var thumbnails = new ThumbnailScheduler(store, Dispatcher.CurrentDispatcher);
        var viewModel = new MainWindowViewModel(new ImageBrowserService(store), thumbnails, dialogs, viewer);
        window = new MainWindow(viewModel, initialFolder);
        window.Closed += async (_, _) =>
        {
            await viewModel.Completion;
            await viewer.CloseAsync();
            await store.DisposeAsync();
        };
        return window;
    }
}
