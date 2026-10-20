using System.Windows.Media.Imaging;

namespace CardViewer;

public sealed class ViewerViewModel : ObservableObject, IDisposable
{
    private readonly ViewerSession session;
    private readonly IReadOnlyList<ImageItem> images;
    private int position;
    private bool started;
    private bool disposed;

    public ViewerViewModel(IViewerImageSource source, IReadOnlyList<ImageItem> images, int position)
    {
        if (position < 0 || position >= images.Count)
            throw new ArgumentOutOfRangeException(nameof(position));
        this.images = images;
        this.position = position;
        session = new(source);
        session.Changed += SessionChanged;
        LoadCommand = new(_ => { started = true; LoadSelected(); }, _ => !started && !disposed);
        PreviousCommand = new(_ => Navigate(-1), _ => !disposed && this.position > 0);
        NextCommand = new(_ => Navigate(1), _ => !disposed && this.position < images.Count - 1);
        CloseCommand = new(_ => CloseRequested?.Invoke(), _ => !disposed);
    }

    public RelayCommand LoadCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand CloseCommand { get; }
    public event Action? CloseRequested;
    public Task Completion => session.Completion;
    public BitmapSource? DisplayedImage => session.DisplayedImage;
    public System.Windows.Size? ImageDimensions => session.ImageDimensions;
    public string Title => session.DisplayedName + " · Image Viewer";
    public string Status => session.Status;
    public string OriginalFormat => session.OriginalFormat;
    public FileTypeTag FormatTag => FileTypeTag.FromPath(session.DisplayedName);
    public string ImageName => session.DisplayedName;
    public string PositionLabel => $"{position + 1:N0} / {images.Count:N0}";
    public bool IsLoading => session.LoadingOriginal;
    private FullImageProgress? Progress => session.Progress;
    private bool Reading => Progress?.Stage == FullImageLoadStage.Reading && Progress.TotalBytes > 0;
    public bool IsProgressIndeterminate => IsLoading && !Reading;
    public double ProgressPercent => Reading ? 100.0 * Progress!.BytesRead / Progress.TotalBytes : 0;
    private void Navigate(int delta)
    {
        position += delta;
        LoadSelected();
    }

    private void LoadSelected()
    {
        var item = images[position];
        session.Open(item.File, item.Thumbnail);
    }

    private void SessionChanged()
    {
        foreach (string property in new[] { nameof(ImageDimensions), nameof(DisplayedImage), nameof(Title), nameof(Status), nameof(OriginalFormat), nameof(FormatTag), nameof(ImageName), nameof(PositionLabel), nameof(IsLoading), nameof(IsProgressIndeterminate), nameof(ProgressPercent) })
            OnPropertyChanged(property);
        LoadCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        session.Dispose();
        session.Changed -= SessionChanged;
        CloseCommand.NotifyCanExecuteChanged();
    }
}



