namespace CardViewer;

public sealed class DeleteConfirmationViewModel
{
    public DeleteConfirmationViewModel(IReadOnlyList<ImageFile> files)
    {
        Files = files.ToArray();
        Groups = Files.GroupBy(file => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file.Path))!, System.IO.Path.GetFileNameWithoutExtension(file.Path)), StringComparer.OrdinalIgnoreCase)
            .Select(group => new DeletionGroup(group.ToArray())).ToArray();
        ConfirmCommand = new(_ => CloseRequested?.Invoke(true), _ => Files.Count > 0 && Files.All(file => !ImageDeletionService.IsProtectedPath(file.Path)));
        CancelCommand = new(_ => CloseRequested?.Invoke(false));
    }

    public IReadOnlyList<ImageFile> Files { get; }
    public IReadOnlyList<DeletionGroup> Groups { get; }
    public IEnumerable<string> Paths => Files.Select(file => file.Path);
    public string Heading => $"Permanently delete {Files.Count:N0} file{(Files.Count == 1 ? "" : "s")}?";
    public string ConfirmLabel => $"Delete {Files.Count:N0} permanently";
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }
    public event Action<bool>? CloseRequested;
}

public sealed class DeletionGroup(IReadOnlyList<ImageFile> files)
{
    public IReadOnlyList<ImageFile> Files { get; } = files;
    public string Name => Files.Count > 1 ? System.IO.Path.GetFileNameWithoutExtension(Files[0].Path) : Files[0].Name;
    public IReadOnlyList<FileTypeTag> Tags => Files.Select(file => FileTypeTag.FromPath(file.Path)).DistinctBy(tag => tag.Name).ToArray();
    public string Formats => string.Join(" + ", Files.Select(file => System.IO.Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant()).Distinct());
    public string Icon => Files.Count > 1 ? "\uE8C8" : "\uE8A5";
    public string Description => $"{Name} · {Formats} · {Files.Count} file(s)";
    public string Paths => string.Join(Environment.NewLine, Files.Select(file => file.Path));
    public string Folder => System.IO.Path.GetDirectoryName(Files[0].Path)!;
    public string DisplayPath => Files.Count > 1 ? System.IO.Path.Combine(Folder, System.IO.Path.GetFileNameWithoutExtension(Files[0].Path)) : Files[0].Path;
}

