using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
namespace CardViewer;

public static partial class SelfTests
{
    private static async Task VerifyBrowserSelectionAndDeletionCommandsAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "mvvm-images");
        Directory.CreateDirectory(folder);
        foreach (string name in new[] { "first.jpg", "second.png", "first.RAF", ".hidden.jpg" })
            await File.WriteAllTextAsync(Path.Combine(folder, name), "disposable MVVM fixture");
        await using var store = new ThumbnailStore(Path.Combine(root, "mvvm-cache"));
        var dialogs = new TestBrowserDialogs();
        using var viewModel = new MainWindowViewModel(new ImageBrowserService(store),
            new ThumbnailScheduler(store, System.Windows.Threading.Dispatcher.CurrentDispatcher), dialogs, new TestViewerService());
        viewModel.OpenFolderCommand.Execute(folder);
        await viewModel.ScanCompletion;
        check(viewModel.Images.Count == 2 && viewModel.Rows.Count == 1, "MVVM scanning exposes supported images and virtualized rows");
        var window = new MainWindow(viewModel);
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
            var controls = (System.Windows.Controls.StackPanel)window.FindName("SelectionBar");
            var buttons = controls.Children.OfType<System.Windows.Controls.Button>().ToArray();
            check(ReferenceEquals(buttons[0].Command, viewModel.SelectAllCommand) && ReferenceEquals(buttons[2].Command, viewModel.DeleteSelectedCommand), "Main view binds actions to view-model commands");
            viewModel.SelectAllCommand.Execute(null);
            check(viewModel.Images.All(item => item.IsSelected) && viewModel.DeleteSelectedCommand.CanExecute(null), "MVVM Select all enables batch deletion");
            viewModel.ClearSelectionCommand.Execute(null);
            check(viewModel.Images.All(item => !item.IsSelected) && !viewModel.DeleteSelectedCommand.CanExecute(null), "MVVM Clear selection disables deletion");
            var first = viewModel.Images[0];
            first.IsSelected = true;
            await viewModel.DeleteSelectedCommand.ExecuteAsync();
            check(dialogs.Confirmations == 1 && viewModel.Images.Count == 2 && File.Exists(first.File.Path), "MVVM canceled confirmation never deletes files");
            dialogs.Confirm = true;
            await viewModel.DeleteSelectedCommand.ExecuteAsync();
            check(viewModel.Images.Count == 1 && !File.Exists(first.File.Path) && !File.Exists(Path.Combine(folder, "first.RAF")) && viewModel.SelectionEnabled, "MVVM confirmed deletion updates the grid, deletes the matching RAW and restores commands");
        }
        finally { window.Close(); }
    }

    private sealed class TestBrowserDialogs : IBrowserDialogs
    {
        public bool Confirm;
        public int Confirmations;
        public string? ChooseFolder() => null;
        public string? ChooseExportFolder() => null;
        public bool ConfirmDeletion(IReadOnlyList<ImageFile> files) { Confirmations++; return Confirm; }
        public void ShowErrors(string title, string details) => throw new Exception(details);
    }

    private sealed class TestViewerService : IImageViewerService
    {
        public void Open(IReadOnlyList<ImageItem> images, int index) { }
        public void Close() { }
        public Task CloseAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
