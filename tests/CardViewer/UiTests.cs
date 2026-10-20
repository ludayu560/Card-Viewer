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
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        using var report = new StreamWriter(Path.Combine(root, "ui-tests.txt")) { AutoFlush = true };
        void Check(bool ok, string message)
        {
            report.WriteLine((ok ? "PASS " : "FAIL ") + message);
            check(ok, "UI: " + message);
        }

        var errors = new BindingErrors();
        var trace = PresentationTraceSources.DataBindingSource;
        var previousLevel = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Error;
        trace.Listeners.Add(errors);
        try
        {
            var tagExtensions = new[] { "hif", "heic", "heif", "jpg", "jpeg", "png", "tif", "tiff", "bmp", "webp", "avif", "raf", "nef", "nrw", "arw", "sr2", "cr2", "cr3", "crw", "dng", "orf", "rw2", "pef", "srw" };
            var tags = tagExtensions.Select(extension => FileTypeTag.FromPath("image." + extension)).ToArray();
            Check(tags.Select(tag => tag.Background).Distinct().Count() == tags.Length, "Each image and RAW filetype has its own tag color");
            Check(tags.All(tag => FileTypeTag.FromPath("image." + tag.Name) == tag), "Filetype tag colors are stable and case insensitive");
            await VerifyMainWindowControlsAndSelectionAsync(root, Check);
            await VerifyCacheMaintenanceDoesNotBlockUiAsync(root, Check);
            await VerifyViewerNavigationProgressAndSizingAsync(root, Check);
            await VerifyScrollbarPagingAndDraggingAsync(Check);
            VerifyConfirmationAndErrorDialogs(root, Check);
            await FlushAsync();
            Check(errors.Messages.Count == 0, "No WPF binding errors: " + string.Join(" | ", errors.Messages));
        }
        finally
        {
            trace.Listeners.Remove(errors);
            trace.Switch.Level = previousLevel;
            errors.Dispose();
        }
    }
}
