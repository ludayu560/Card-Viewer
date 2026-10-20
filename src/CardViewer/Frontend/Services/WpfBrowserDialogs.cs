using System.Windows;
using Microsoft.Win32;

namespace CardViewer;

internal sealed class WpfBrowserDialogs : IBrowserDialogs
{
    private readonly Func<Window> owner;

    internal WpfBrowserDialogs(Func<Window> owner)
    {
        this.owner = owner;
    }

    public string? ChooseFolder()
    {
        var picker = new OpenFolderDialog { Title = "Choose an image folder" };
        return picker.ShowDialog(owner()) == true ? picker.FolderName : null;
    }

    public bool ConfirmDeletion(IReadOnlyList<ImageFile> files) =>
        new DeleteConfirmationWindow(new DeleteConfirmationViewModel(files)) { Owner = owner() }.ShowDialog() == true;
    public string? ChooseExportFolder()
    {
        var picker = new OpenFolderDialog { Title = "Choose a folder for the ZIP export (images and matching RAW files)" };
        return picker.ShowDialog(owner()) == true ? picker.FolderName : null;
    }

    public void ShowErrors(string title, string details) =>
        new ErrorDetailsWindow(title, details) { Owner = owner() }.ShowDialog();
}

