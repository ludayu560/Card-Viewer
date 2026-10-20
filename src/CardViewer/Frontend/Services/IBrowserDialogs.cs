namespace CardViewer;

public interface IBrowserDialogs
{
    string? ChooseFolder();
    string? ChooseExportFolder();
    bool ConfirmDeletion(IReadOnlyList<ImageFile> files);
    void ShowErrors(string title, string details);
}
