using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CardViewer;

// Runs on the existing STA test dispatcher. Hidden windows exercise real WPF
// templates, bindings and command routing without sending input to the desktop.

internal static partial class UiTests
{
    private static async Task VerifyCacheMaintenanceDoesNotBlockUiAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "ui-images");
        await using var store = new ThumbnailStore(Path.Combine(root, "maintenance-cache"));
        using var scheduler = new ThumbnailScheduler(store, Dispatcher.CurrentDispatcher);
        using var viewModel = new MainWindowViewModel(new ImageBrowserService(store), scheduler, new TestDialogs(folder), new TestViewer());
        var window = new MainWindow(viewModel);
        check(window.Title == "Card Viewer" && typeof(MainWindow).Assembly.GetName().Name == "CardViewer", "Application and assembly use Card Viewer branding");
        check(window.Icon != null, "SD-card application icon loads from assembly resources");
        HideFromDesktop(window);
        int ticks = 0;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) => ticks++;
        try
        {
            window.Show();
            viewModel.OpenFolderCommand.Execute(folder);
            await viewModel.ScanCompletion;
            await FlushAsync();
            heartbeat.Start();
            for (int i = 0; i < 3; i++)
            {
                await viewModel.ClearCacheCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await viewModel.BenchmarkCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            check(viewModel.CanInteract && !viewModel.Benchmark.StartsWith("Benchmark failed"), "Real scheduler and backend complete repeated clear/benchmark operations");
            check(viewModel.MaintenanceCompleted == viewModel.Images.Count, "Real benchmark reports progress for every completed image");
            check(ticks > 0, "Dispatcher remains responsive during real cache maintenance");
        }
        finally { heartbeat.Stop(); window.Close(); }
    }
}
