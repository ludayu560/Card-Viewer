using System.Collections.ObjectModel;
using System.ComponentModel;

namespace CardViewer;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IImageBrowserBackend backend;
    private readonly IThumbnailPresentation thumbnails;
    private readonly IBrowserDialogs dialogs;
    private readonly IImageViewerService viewer;
    private readonly List<ImageItem> images = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? scan;
    private bool disposed;
    private bool changingSelection;
    private ImageItem? selectionAnchor;
    private bool busy;
    private bool exporting;
    private CancellationTokenSource? maintenance;
    private int maintenanceCompleted, maintenanceTotal;
    private bool scanning;
    private string folderLabel = "Choose a folder to browse images.";
    private string status = "Open a folder to get started. Check tiles to select images.";
    private string? statusDetails;
    private string benchmark = "";
    private string? benchmarkDetails;
    private int selectedCount;
    private bool protectedSelection;

    public MainWindowViewModel(IImageBrowserBackend backend, IThumbnailPresentation thumbnails, IBrowserDialogs dialogs, IImageViewerService viewer)
    {
        this.backend = backend;
        this.thumbnails = thumbnails;
        this.dialogs = dialogs;
        this.viewer = viewer;
        OpenFolderCommand = new(p => { if (p is string folder) StartScan(folder); }, _ => CanInteract);
        ChooseFolderCommand = new(_ => ChooseFolder(), _ => CanInteract);
        SelectAllCommand = new(_ => SetSelection(true), _ => SelectionEnabled);
        ClearSelectionCommand = new(_ => SetSelection(false), _ => SelectionEnabled && selectedCount > 0);
        SelectRangeCommand = new(p => SelectRange((RangeSelectionRequest)p!), p => SelectionEnabled && p is RangeSelectionRequest request && images.Contains(request.Item));
        OpenImageCommand = new(p => OpenImage((ImageItem)p!), p => (CanInteract || exporting) && !disposed && p is ImageItem item && images.Contains(item));
        ShowThumbnailCommand = new(p => thumbnails.Show((ImageItem)p!), p => !disposed && p is ImageItem);
        HideThumbnailCommand = new(p => thumbnails.Hide((ImageItem)p!), p => !disposed && p is ImageItem);
        ClearCacheCommand = new(ClearCacheAsync, () => CanMaintain, ReportError);
        BenchmarkCommand = new(BenchmarkAsync, () => CanMaintain && images.Count > 0, ReportError);
        DeleteSelectedCommand = new(DeleteSelectedAsync, () => SelectionEnabled && selectedCount > 0 && !protectedSelection, ReportError);
        ExportSelectedCommand = new(ExportSelectedAsync, () => SelectionEnabled && selectedCount > 0, ReportError);
    }

    public ObservableCollection<ImageRow> Rows { get; } = [];
    public IReadOnlyList<ImageItem> Images => images;
    public Task ScanCompletion { get; private set; } = Task.CompletedTask;
    public Task Completion => Task.WhenAll(ScanCompletion, ClearCacheCommand.ExecutionTask, BenchmarkCommand.ExecutionTask, DeleteSelectedCommand.ExecutionTask, ExportSelectedCommand.ExecutionTask);
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand SelectRangeCommand { get; }
    public RelayCommand OpenImageCommand { get; }
    public RelayCommand ShowThumbnailCommand { get; }
    public RelayCommand HideThumbnailCommand { get; }
    public AsyncCommand ClearCacheCommand { get; }
    public AsyncCommand BenchmarkCommand { get; }
    public AsyncCommand DeleteSelectedCommand { get; }
    public AsyncCommand ExportSelectedCommand { get; }
    public bool CanInteract => !disposed && !busy;
    public bool GridEnabled => CanInteract || IsMaintaining;
    public bool IsMaintaining => !disposed && maintenance != null;
    public bool MaintenanceIndeterminate => maintenanceTotal == 0;
    public int MaintenanceCompleted => maintenanceCompleted;
    public int MaintenanceTotal => Math.Max(1, maintenanceTotal);
    public bool SelectionEnabled => CanMaintain && images.Count > 0;
    private bool CanMaintain => CanInteract && !scanning;
    public string FolderLabel { get => folderLabel; private set => SetProperty(ref folderLabel, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string? StatusDetails { get => statusDetails; private set => SetProperty(ref statusDetails, value); }
    public string Benchmark { get => benchmark; private set => SetProperty(ref benchmark, value); }
    public string? BenchmarkDetails { get => benchmarkDetails; private set => SetProperty(ref benchmarkDetails, value); }
    public string SelectionLabel => $"{selectedCount:N0} selected" + (protectedSelection ? " · TestImages is protected" : "");
    public string DeleteLabel => selectedCount == 0 ? "Delete selected…" : $"Delete selected ({selectedCount:N0})…";
    public string DeleteToolTip => protectedSelection ? "Files under TestImages cannot be deleted." : "Review selected files before permanently deleting them.";

    private void ReportError(Exception error)
    {
        if (!disposed)
            Status = "Operation failed: " + error.Message;
    }

    private void ChooseFolder()
    {
        try
        {
            string? folder = dialogs.ChooseFolder();
            if (folder != null)
                StartScan(folder);
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void StartScan(string folder)
    {
        scan?.Cancel();
        scan = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        viewer.Close();
        ClearImages();
        scanning = true;
        FolderLabel = folder;
        Status = "Scanning…";
        StatusDetails = null;
        Benchmark = "";
        BenchmarkDetails = null;
        RefreshState();
        // Retain canceled scans until they finish so shutdown can drain every request.
        ScanCompletion = Task.WhenAll(ScanCompletion, ScanFolderAsync(folder, scan));
    }

    private async Task ScanFolderAsync(string folder, CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            await foreach (var batch in backend.ScanAsync(folder, token))
            {
                token.ThrowIfCancellationRequested();
                foreach (var file in batch)
                {
                    var item = new ImageItem(file);
                    item.PropertyChanged += ItemChanged;
                    if (images.Count % 6 == 0)
                        Rows.Add(new(new ObservableCollection<ImageItem>()));
                    images.Add(item);
                    ((ObservableCollection<ImageItem>)Rows[^1].Images).Add(item);
                }
                Status = $"Scanning… {images.Count:N0} images found";
            }
            token.ThrowIfCancellationRequested();
            Status = images.Count == 0 ? $"No supported images found. Supported: {ImageFileFilter.SupportedDescription}."
                : $"{images.Count:N0} images · Click to view · Check tiles to select for deletion";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Status = "Cannot scan folder: " + ex.Message; }
        finally
        {
            if (ReferenceEquals(scan, request))
            {
                scanning = false;
                scan = null;
                RefreshState();
            }
            request.Dispose();
        }
    }

    private void ClearImages()
    {
        selectionAnchor = null;
        foreach (var item in images)
            item.PropertyChanged -= ItemChanged;
        images.Clear();
        Rows.Clear();
        UpdateSelection();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!changingSelection && e.PropertyName == nameof(ImageItem.IsSelected))
        {
            selectionAnchor = (ImageItem?)sender;
            UpdateSelection();
        }
    }

    private void SetSelection(bool value)
    {
        if (!value) selectionAnchor = null;
        changingSelection = true;
        try
        {
            foreach (var item in images)
                item.IsSelected = value;
        }
        finally { changingSelection = false; }
        UpdateSelection();
    }

    private void SelectRange(RangeSelectionRequest request)
    {
        int end = images.IndexOf(request.Item);
        int start = selectionAnchor == null ? -1 : images.IndexOf(selectionAnchor);
        if (start < 0) { start = end; selectionAnchor = request.Item; }
        changingSelection = true;
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                bool inRange = i >= Math.Min(start, end) && i <= Math.Max(start, end);
                if (inRange || !request.Additive) images[i].IsSelected = inRange;
            }
        }
        finally { changingSelection = false; }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = images.Where(item => item.IsSelected).ToArray();
        selectedCount = selected.Length;
        protectedSelection = selected.Any(item => ImageDeletionService.IsProtectedPath(item.File.Path));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(DeleteToolTip));
        RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(GridEnabled));
        OnPropertyChanged(nameof(IsMaintaining));
        OnPropertyChanged(nameof(MaintenanceIndeterminate));
        OnPropertyChanged(nameof(MaintenanceCompleted));
        OnPropertyChanged(nameof(MaintenanceTotal));
        OnPropertyChanged(nameof(SelectionEnabled));
        ChooseFolderCommand?.NotifyCanExecuteChanged();
        OpenFolderCommand?.NotifyCanExecuteChanged();
        SelectAllCommand?.NotifyCanExecuteChanged();
        ClearSelectionCommand?.NotifyCanExecuteChanged();
        SelectRangeCommand?.NotifyCanExecuteChanged();
        OpenImageCommand?.NotifyCanExecuteChanged();
        ClearCacheCommand?.NotifyCanExecuteChanged();
        BenchmarkCommand?.NotifyCanExecuteChanged();
        DeleteSelectedCommand?.NotifyCanExecuteChanged();
        ExportSelectedCommand?.NotifyCanExecuteChanged();
    }

    private void OpenImage(ImageItem item)
    {
        selectionAnchor = item;
        viewer.Open(images.ToArray(), images.IndexOf(item));
    }

    private async Task RunMaintenanceAsync(Func<CancellationToken, Task> action, string errorPrefix)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        maintenance = stop;
        busy = true;
        RefreshState();
        try
        {
            await thumbnails.PauseAsync();
            await viewer.CloseAsync();
            stop.Token.ThrowIfCancellationRequested();
            await action(stop.Token);
        }
        catch (OperationCanceledException) { if (!disposed) Benchmark = "Operation canceled. Thumbnail browsing resumed."; }
        catch (Exception ex) { if (!disposed) Benchmark = errorPrefix + ex.Message; }
        finally
        {
            busy = false;
            maintenance = null;
            if (!disposed)
                thumbnails.Resume();
            RefreshState();
        }
    }

    private Task ClearCacheAsync()
    {
        Benchmark = "Waiting for the current thumbnail, then clearing the disk cache…";
        BenchmarkDetails = null;
        maintenanceTotal = maintenanceCompleted = 0;
        return RunMaintenanceAsync(async token =>
        {
            int count = await backend.ClearCacheAsync(token);
            Benchmark = $"Cleared {count:N0} cached thumbnails. Displayed previews remain in memory; new requests will rebuild the cache.";
        }, "Cannot clear cache: ");
    }

    private Task BenchmarkAsync()
    {
        var files = images.Select(item => item.File).ToArray();
        Benchmark = $"Benchmarking {files.Length:N0} images… (existing disk cache; excludes scanning and painting)";
        BenchmarkDetails = null;
        maintenanceCompleted = 0;
        maintenanceTotal = files.Length;
        return RunMaintenanceAsync(async token =>
        {
            var progress = new Progress<int>(count =>
            {
                if (disposed || !IsMaintaining || token.IsCancellationRequested) return;
                maintenanceCompleted = count;
                OnPropertyChanged(nameof(MaintenanceCompleted));
                Benchmark = $"Benchmarking {count:N0}/{files.Length:N0} images…";
            });
            var result = await backend.BenchmarkAsync(files, token, progress);
            Benchmark = result.ToString();
            BenchmarkDetails = string.Join(Environment.NewLine, result.Errors);
        }, "Benchmark failed: ");
    }

    private async Task DeleteSelectedAsync()
    {
        var files = images.Where(item => item.IsSelected).Select(item => item.File).ToArray();
        busy = true;
        RefreshState();
        bool paused = false;
        try
        {
            files = (await backend.PrepareDeletionAsync(files, lifetime.Token)).ToArray();
            lifetime.Token.ThrowIfCancellationRequested();
            if (!dialogs.ConfirmDeletion(files))
                return;
            lifetime.Token.ThrowIfCancellationRequested();
            Status = $"Deleting {files.Length:N0} selected images…";
            paused = true;
            await thumbnails.PauseAsync();
            await viewer.CloseAsync();
            lifetime.Token.ThrowIfCancellationRequested();
            var result = await backend.DeleteAsync(files, lifetime.Token);
            var deleted = result.Deleted.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in images.Where(item => deleted.Contains(item.File.Path)))
            {
                item.PropertyChanged -= ItemChanged;
                thumbnails.Hide(item);
            }
            images.RemoveAll(item => deleted.Contains(item.File.Path));
            Rows.Clear();
            for (int i = 0; i < images.Count; i += 6)
                Rows.Add(new(new ObservableCollection<ImageItem>(images.Skip(i).Take(6))));
            Status = $"Deleted {result.Deleted.Count:N0} files (including matching RAW) · {images.Count:N0} images remaining";
            StatusDetails = null;
            if (result.Failures.Count > 0)
            {
                Status += $" · {result.Failures.Count:N0} could not be deleted";
                StatusDetails = string.Join(Environment.NewLine, result.Failures.Select(failure => $"{failure.File.Name}: {failure.Message}"));
                if (!disposed)
                    dialogs.ShowErrors("Some images were not deleted", StatusDetails);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) Status = "Cannot delete images: " + ex.Message; }
        finally
        {
            busy = false;
            if (paused && !disposed)
                thumbnails.Resume();
            UpdateSelection();
        }
    }

    private async Task ExportSelectedAsync()
    {
        string? destination = dialogs.ChooseExportFolder();
        if (destination == null) return;
        var files = images.Where(item => item.IsSelected).Select(item => item.File).ToArray();
        maintenanceTotal = maintenanceCompleted = 0;
        Benchmark = "Preparing selected images and matching RAW files for export…";
        BenchmarkDetails = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        maintenance = stop;
        busy = exporting = true;
        RefreshState();
        try
        {
            var progress = new Progress<ExportProgress>(value =>
            {
                if (disposed || maintenance != stop) return;
                maintenanceTotal = value.TotalBytes > 0 ? 1000 : 0;
                maintenanceCompleted = value.TotalBytes > 0 ? (int)Math.Clamp(1000.0 * value.BytesProcessed / value.TotalBytes, 0, 1000) : 0;
                OnPropertyChanged(nameof(MaintenanceTotal));
                OnPropertyChanged(nameof(MaintenanceCompleted));
                OnPropertyChanged(nameof(MaintenanceIndeterminate));
                string percent = value.TotalBytes > 0 ? $" · {100.0 * value.BytesProcessed / value.TotalBytes:F0}%" : "";
                Benchmark = $"{value.Stage}{(value.FileName == null ? "" : " · " + value.FileName)}{percent} · {value.CompletedFiles:N0}/{value.TotalFiles:N0} files";
            });
            var result = await backend.ExportAsync(files, destination, stop.Token, progress);
            Benchmark = result.ArchivePath == null ? "No files were exported." : $"ZIP ready · {result.Copied.Count:N0} files · {result.ArchivePath}";
            BenchmarkDetails = string.Join(Environment.NewLine, result.Failures.Select(failure => $"{failure.File.Name}: {failure.Message}"));
            if (result.Failures.Count > 0)
            {
                Benchmark += $" · {result.Failures.Count:N0} could not be exported";
                if (!disposed) dialogs.ShowErrors("Some files were not exported", BenchmarkDetails);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) Benchmark = "Export failed: " + ex.Message; }
        finally
        {
            maintenance = null;
            busy = exporting = false;
            RefreshState();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        scan?.Cancel();
        foreach (var item in images)
            item.PropertyChanged -= ItemChanged;
        thumbnails.Dispose();
        viewer.Dispose();
        RefreshState();
    }
}


