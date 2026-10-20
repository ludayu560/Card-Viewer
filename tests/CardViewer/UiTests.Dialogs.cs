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
    private static void VerifyConfirmationAndErrorDialogs(string root, Action<bool, string> check)
    {
        var pairFiles = new[] { new ImageFile(Path.Combine(root, "pair.hif"), 0, 0), new ImageFile(Path.Combine(root, "pair.RAF"), 0, 0) };
        var pair = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(pairFiles));
        Modal(pair, () =>
        {
            var group = (DeletionGroup)pair.FileList.Items[0];
            check(pair.FileList.Items.Count == 1 && group.Name == "pair" && group.Formats == "HIF + RAF" && group.Paths.Contains("pair.hif") && group.Paths.Contains("pair.RAF") && group.DisplayPath == Path.Combine(root, "pair"), "Deletion confirmation collapses image/RAW into one name with both filetypes and exact path details");
            check(Children<TextBlock>(pair.FileList).Any(text => text.Text == group.Icon) && Children<TextBlock>(pair.FileList).Any(text => text.Text == "HIF") && Children<TextBlock>(pair.FileList).Any(text => text.Text == "RAF"), "Grouped confirmation renders stacked-file icon and format badge");
            Click(pair.CancelButton);
        });
        var files = new[] { new ImageFile(Path.Combine(root, "confirm-a.jpg"), 0, 0), new ImageFile(Path.Combine(root, "confirm-b.png"), 0, 0) };
        var cancel = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(files));
        var canceled = Modal(cancel, () =>
        {
            check(cancel.CancelButton.IsDefault && cancel.CancelButton.IsCancel && cancel.FileList.Items.Count == 2, "Confirmation defaults to Cancel and lists every selected path");
            Click(cancel.CancelButton);
        });
        check(canceled == false, "Confirmation Cancel button returns false and closes the dialog");
        var confirm = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(files));
        check(Modal(confirm, () => Click(confirm.DeleteButton)) == true, "Explicit delete button confirms and closes the dialog");
        var closed = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(files));
        check(Modal(closed, closed.Close) != true, "Closing confirmation without accepting never authorizes deletion");
        var protectedDialog = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(new[] { files[0] with { Path = Path.Combine(root, "TestImages", "protected.jpg") } }));
        Modal(protectedDialog, () =>
        {
            check(!protectedDialog.DeleteButton.IsEnabled && !Click(protectedDialog.DeleteButton), "Protected TestImages cannot be confirmed through the UI");
            Click(protectedDialog.CancelButton);
        });
        var error = new ErrorDetailsWindow("Test operation error", "first error\nsecond error");
        Modal(error, () =>
        {
            var text = Children<TextBox>(error).Single();
            check(text.IsReadOnly && text.Text.Contains("second error"), "Error dialog exposes all details in a read-only control");
            Click(Children<Button>(error).Single());
        });
        check(!error.IsVisible, "Error dialog Close button completes its modal operation");
    }
}
