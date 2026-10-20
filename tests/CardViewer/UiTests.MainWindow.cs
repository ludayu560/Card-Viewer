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
    private static async Task VerifyMainWindowControlsAndSelectionAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "ui-images");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Pixel(50)));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        for (int i = 0; i < 48; i++)
            await File.WriteAllBytesAsync(Path.Combine(folder, $"image-{i:D2}.png"), bytes.ToArray());
        await File.WriteAllTextAsync(Path.Combine(folder, "image-00.RAF"), "untouched companion");
        await File.WriteAllTextAsync(Path.Combine(folder, ".hidden.png"), "ignored");

        await using var store = new ThumbnailStore(Path.Combine(root, "ui-cache"));
        var backend = new TestBackend(new ImageBrowserService(store));
        var presentation = new TestThumbnails();
        var viewer = new TestViewer();
        var dialogs = new TestDialogs(folder);
        using var viewModel = new MainWindowViewModel(backend, presentation, dialogs, viewer);
        var window = new MainWindow(viewModel);
        HideFromDesktop(window);
        try
        {
            window.Show();
            await FlushAsync();
            var selection = (StackPanel)window.FindName("SelectionBar");
            var actions = selection.Children.OfType<Button>().ToArray();
            var clearCache = (Button)window.FindName("ClearButton");
            var benchmark = (Button)window.FindName("BenchmarkButton");
            var grid = (ListBox)window.FindName("GridList");
            var choose = Children<Button>(window).Single(button => Equals(button.Content, "Choose folder…"));
            check(!selection.IsEnabled && !benchmark.IsEnabled && !actions[2].IsEnabled, "Empty grid disables selection, benchmark and deletion");
            Click(choose);
            await viewModel.ScanCompletion;
            await FlushAsync();
            window.UpdateLayout();
            check(viewModel.Images.Count == 48 && grid.Items.Count == 8 && selection.IsEnabled, "Folder button scans supported images and binds grid rows");
            check(((TextBlock)window.FindName("FolderLabel")).Text == folder, "Folder and status labels update through bindings");
            check(VirtualizingPanel.GetIsVirtualizing(grid) && grid.ItemContainerGenerator.ContainerFromIndex(7) == null, "Grid keeps offscreen rows virtualized");
            check(presentation.Shown.Count > 0 && presentation.Shown.Count < 48, "Only realized tiles request thumbnails");

            var first = viewModel.Images[0];
            var box = Children<CheckBox>(grid).First(control => ReferenceEquals(control.DataContext, first));
            Click(box);
            await FlushAsync();
            check(first.IsSelected && actions[2].IsEnabled && ((TextBlock)window.FindName("SelectionLabel")).Text.StartsWith("1 selected"), "Tile checkbox updates selection count and delete enablement");
            MouseUp(box);
            check(viewer.Opens == 0, "Checkbox interaction does not open the image viewer");
            var tile = Children<Border>(grid).First(border => ReferenceEquals(border.DataContext, first) && border.Cursor == Cursors.Hand);
            MouseUp(tile);
            check(viewer.Opens == 1 && viewer.LastIndex == 0, "Clicking an image tile invokes the viewer operation");
            window.ApplySelectionGesture(viewModel.Images[12], ModifierKeys.Shift, false);
            MouseUp(tile);
            check(viewModel.Images.Take(13).All(item => item.IsSelected) && viewModel.Images.Skip(13).All(item => !item.IsSelected) && viewer.Opens == 1,
                "Shift-click selects an inclusive range across rows without opening the viewer");
            window.ApplySelectionGesture(viewModel.Images[5], ModifierKeys.Shift, true);
            MouseUp(box);
            check(viewModel.Images.Count(item => item.IsSelected) == 6, "Repeated Shift-click keeps the original anchor and replaces the range");
            window.ApplySelectionGesture(viewModel.Images[10], ModifierKeys.Control, false);
            MouseUp(tile);
            window.ApplySelectionGesture(viewModel.Images[2], ModifierKeys.Shift, false);
            MouseUp(tile);
            check(viewModel.Images.Skip(2).Take(9).All(item => item.IsSelected) && viewModel.Images.Count(item => item.IsSelected) == 9, "Shift-click supports backward ranges from the previous Ctrl-click");
            window.ApplySelectionGesture(viewModel.Images[15], ModifierKeys.Shift | ModifierKeys.Control, false);
            MouseUp(tile);
            check(viewModel.Images.Count(item => item.IsSelected) == 14, "Ctrl+Shift-click adds a range while preserving existing selections");
            viewModel.ClearSelectionCommand.Execute(null);
            window.ApplySelectionGesture(viewModel.Images[7], ModifierKeys.Shift, false);
            MouseUp(tile);
            check(viewModel.Images.Count(item => item.IsSelected) == 1 && viewModel.Images[7].IsSelected, "Shift-click without an anchor selects only the clicked image");
            viewModel.ClearSelectionCommand.Execute(null);
            Click(box);
            var placeholder = Children<TextBlock>(tile).Single(text => text.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == "Status");
            check(placeholder.Visibility == Visibility.Visible, "Empty tile shows its placeholder");
            first.Thumbnail = Pixel(70);
            await FlushAsync();
            check(placeholder.Visibility == Visibility.Collapsed, "An available thumbnail hides placeholder and loading text");
            first.Status = "Cannot decode image: test error";
            await FlushAsync();
            check(placeholder.Visibility == Visibility.Collapsed && Equals(Children<Image>(tile).Single().ToolTip, first.Status), "A retained preview exposes a refinement error only in its tooltip");
            first.Thumbnail = null;
            await FlushAsync();
            check(placeholder.Visibility == Visibility.Visible && placeholder.Text.Contains("test error"), "A failed image without a preview shows a useful error");

            grid.ScrollIntoView(viewModel.Rows[^1]);
            await FlushAsync();
            var scroll = Children<ScrollViewer>(grid).First();
            scroll.ScrollToEnd();
            grid.UpdateLayout();
            await FlushAsync();
            await WaitForAsync(() => presentation.Hidden.Count > 0);
            check(first.IsSelected && presentation.Hidden.Count > 0, $"Scrolling releases tile work without losing selection (selected={first.IsSelected}, hidden={presentation.Hidden.Count}, shown={presentation.Shown.Count}, offset={scroll.VerticalOffset}, firstRow={grid.ItemContainerGenerator.ContainerFromIndex(0) != null}, lastRow={grid.ItemContainerGenerator.ContainerFromIndex(7) != null})");
            Click(actions[0]);
            await FlushAsync();
            check(viewModel.Images.All(item => item.IsSelected), "Select all button selects the complete folder, including offscreen tiles");
            Click(actions[1]);
            await FlushAsync();
            check(viewModel.Images.All(item => !item.IsSelected) && !actions[2].IsEnabled, "Clear selection button disables deletion");

            backend.PendingClear = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(clearCache);
            await FlushAsync();
            check(!choose.IsEnabled && grid.IsEnabled && !benchmark.IsEnabled, "Maintenance keeps scrolling enabled while disabling conflicting commands");
            Click(clearCache);
            check(backend.ClearCalls == 1, "Disabled controls prevent duplicate maintenance requests");
            backend.PendingClear.SetException(new IOException("test cache unavailable"));
            await viewModel.ClearCacheCommand.ExecutionTask;
            await FlushAsync();
            check(clearCache.IsEnabled && choose.IsEnabled && grid.IsEnabled && ((TextBlock)window.FindName("BenchmarkLabel")).Text.Contains("test cache unavailable"), "Maintenance errors are displayed and controls recover");
            Click(benchmark);
            await viewModel.BenchmarkCommand.ExecutionTask;
            await FlushAsync();
            check(backend.Benchmarks == 1 && ((TextBlock)window.FindName("BenchmarkLabel")).Text.Contains("48/48"), "Benchmark button displays the backend result");

            first.IsSelected = true;
            viewModel.Images[1].IsSelected = true;
            await FlushAsync();
            var export = (Button)window.FindName("ExportButton");
            check(export.IsEnabled, "Selecting tiles enables Export selected");
            Click(export);
            await viewModel.ExportSelectedCommand.ExecutionTask;
            check(viewModel.Images.Count(item => item.IsSelected) == 2, "Canceling export destination picker preserves selection");
            dialogs.ExportFolder = Path.Combine(root, "ui-export");
            Click(export);
            await viewModel.ExportSelectedCommand.ExecutionTask.WaitAsync(TimeSpan.FromSeconds(5));
            using (var archive = ZipFile.OpenRead(Directory.GetFiles(dialogs.ExportFolder, "*.zip").Single()))
                check(archive.GetEntry(first.Name) != null && archive.GetEntry("image-00.RAF") != null && File.Exists(first.File.Path), "Export button creates a ZIP containing selected images and RAW while preserving sources");
            check(viewModel.CanInteract && viewModel.Images.Count(item => item.IsSelected) == 2, "Export restores interaction and keeps selection");
            backend.PendingExport = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int pauses = presentation.PauseCalls;
            Click(export);
            backend.ExportUpdates!.Report(new("Packing", first.Name, 1024, 4096, 1, 3));
            await FlushAsync();
            check(grid.IsEnabled && viewModel.OpenImageCommand.CanExecute(first) && presentation.PauseCalls == pauses,
                "Export keeps scrolling, image viewing and thumbnail loading available");
            check(((ProgressBar)window.FindName("MaintenanceProgress")).Value == 250 && viewModel.Benchmark.Contains("25%"),
                "Export displays byte percentage before the file finishes");
            bool dispatcherRan = await window.Dispatcher.InvokeAsync(() => true, DispatcherPriority.Background);
            check(dispatcherRan && !viewModel.ExportSelectedCommand.ExecutionTask.IsCompleted, "Dispatcher processes input while ZIP work is pending");
            backend.PendingExport.SetResult(new ExportResult([], []));
            await viewModel.ExportSelectedCommand.ExecutionTask;
            dialogs.Accept = false;
            Click(actions[2]);
            await viewModel.DeleteSelectedCommand.ExecutionTask;
            check(backend.Deletions == 0 && viewModel.Images.Count == 48 && File.Exists(first.File.Path), "Canceling the actual confirmation dialog leaves all files and tiles intact");
            dialogs.Accept = true;
            string locked = viewModel.Images[1].File.Path;
            using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                PressKey(window, Key.Delete);
                await viewModel.DeleteSelectedCommand.ExecutionTask;
            }
            await FlushAsync();
            check(backend.Deletions == 1 && viewModel.Images.Count == 47 && !File.Exists(first.File.Path), "Delete key confirms and removes successful files from the bound grid");
            check(viewModel.Images.Count(item => item.IsSelected) == 1 && dialogs.Errors == 1 && File.Exists(locked), "Partial deletion keeps failed tiles selected and reports the error");
            check(!File.Exists(Path.Combine(folder, "image-00.RAF")), "UI batch deletion also removes the confirmed RAF companion");
            check(dialogs.LastSelection.Count == 3 && dialogs.LastSelection.Any(file => file.Path.EndsWith(".RAF")), "Confirmation lists exactly the batch sent for deletion");
            foreach (var button in Children<Button>(window))
            {
                button.ApplyTemplate();
                check(button.Template.FindName("Chrome", button) != null, "Shared button template: " + button.Content);
            }
        }
        finally { window.Close(); }
        check(presentation.Disposed && viewer.Disposed && !viewModel.OpenFolderCommand.CanExecute(folder), "Closing the main window disposes UI services and disables commands");
    }
}
