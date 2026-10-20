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
    private static async Task VerifyViewerNavigationProgressAndSizingAsync(string root, Action<bool, string> check)
    {
        var source = new TestImageSource();
        var items = new[] { new ImageItem(new ImageFile("ui-first.jpg", 0, 0)), new ImageItem(new ImageFile("ui-second.png", 0, 0)) };
        using var viewModel = new ViewerViewModel(source, items, 0);
        var window = new ViewerWindow(viewModel);
        HideFromDesktop(window);
        try
        {
            window.Show();
            await source.PairStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await FlushAsync();
            var progress = (ProgressBar)window.FindName("LoadProgress");
            var image = Children<Image>(window).Single();
            var buttons = Children<Button>(window).ToArray();
            var modes = Children<RadioButton>(window).ToArray();
            check(ReferenceEquals(image.Source, source.Preview) && progress.Visibility == Visibility.Visible, "Showing the actual viewer displays a fast image and loading progress");
            check(!buttons[0].IsEnabled && buttons[1].IsEnabled && modes.Length == 0, "Viewer enables valid navigation and has no RAW viewing controls");
            check(!window.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.H || binding.Key == Key.R), "Viewer has no RAW switching keyboard shortcuts");
            check(image.Margin.Left == 12 && image.Margin.Right == 12 && image.Margin.Bottom == 12, "Full image has matching side and bottom padding");
            check(((Border)window.FindName("ViewerHeader")).CornerRadius.TopLeft == 10 && viewModel.PositionLabel == "1 / 2", "Viewer header displays styled navigation and image position");
            check(window.FindName("FitButton") == null && viewModel.ImageDimensions == new Size(6000, 4000), "Viewer sizes from original metadata and has no Fit button");
            var initialBounds = new Rect(window.Left, window.Top, window.Width, window.Height);
            foreach (var dimensions in new[] { new Size(6000, 4000), new Size(4000, 6000), new Size(8000, 1000), new Size(100, 100) })
            {
                var fitted = ViewerWindow.FitSize(dimensions, new Size(1920, 1080), new Size(40, 160), new Size(560, 320));
                check(fitted.Width <= 1888 && fitted.Height <= 1048 && fitted.Width >= 560 && fitted.Height >= 320,
                    "Image-fit sizing stays within screen bounds for " + dimensions);
            }
            source.Report(new(FullImageLoadStage.Reading, 25, 100));
            await FlushAsync();
            check(!progress.IsIndeterminate && progress.Value == 25, "Read progress updates the real progress control without write-back errors");
            source.Report(new(FullImageLoadStage.Decoding));
            await FlushAsync();
            check(progress.IsIndeterminate, "Native decode switches the progress control to indeterminate");
            var visual = (FrameworkElement)window.Content;
            var render = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            render.Render(visual);
            var screenshot = new PngBitmapEncoder();
            screenshot.Frames.Add(BitmapFrame.Create(render));
            using (var output = File.Create(Path.Combine(root, "viewer-layout.png"))) screenshot.Save(output);
            source.OriginalReady.SetResult(source.Original);
            await viewModel.Completion;
            await FlushAsync();
            check(initialBounds == new Rect(window.Left, window.Top, window.Width, window.Height), "Window bounds remain stable when thumbnail is replaced by full image");
            check(ReferenceEquals(image.Source, source.Original) && progress.Visibility == Visibility.Collapsed && source.FullCalls == 1 && source.ResolveCalls == 0, "Viewer displays the completed original without resolving or loading RAW");
            Click(buttons[1]);
            await viewModel.Completion;
            await FlushAsync();
            check(window.Title.StartsWith("ui-second") && buttons[0].IsEnabled && !buttons[1].IsEnabled, $"Next button changes the actual viewer title and navigation boundaries (title={window.Title}, model={viewModel.Title}, previous={buttons[0].IsEnabled}, next={buttons[1].IsEnabled}, calls={source.FullCalls})");
            PressKey(window, Key.Left);
            await viewModel.Completion;
            await FlushAsync();
            check(window.Title.StartsWith("ui-first"), "Previous keyboard binding returns to the first image");
            PressKey(window, Key.Escape);
            await FlushAsync();
            check(!window.IsVisible && viewModel.DisplayedImage == null && !viewModel.NextCommand.CanExecute(null), "Escape closes the actual viewer and releases retained images");
        }
        finally { window.Close(); }

        var pending = new TestImageSource();
        using var pendingVm = new ViewerViewModel(pending, items, 0);
        var pendingWindow = new ViewerWindow(pendingVm);
        HideFromDesktop(pendingWindow);
        pendingWindow.Show();
        await pending.PairStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        pendingWindow.Close();
        await pendingVm.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        check(pendingVm.DisplayedImage == null && !pendingVm.IsLoading, "Closing during a pending full-image load cancels cleanly without late UI publication");
    }
}
