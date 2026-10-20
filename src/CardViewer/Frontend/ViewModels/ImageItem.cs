using System.Windows.Media.Imaging;
namespace CardViewer;

public sealed record RangeSelectionRequest(ImageItem Item, bool Additive = false);

public sealed class ImageItem : ObservableObject
{
    public ImageItem(ImageFile file)
    {
        File = file;
    }

    public ImageFile File
    {
        get;
    }
    public string Name => File.Name;
    public FileTypeTag FormatTag => FileTypeTag.FromPath(File.Path);
    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
                return;
            isSelected = value;
            OnPropertyChanged();
        }
    }
    private BitmapSource? thumbnail;
    public BitmapSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            thumbnail = value;
            OnPropertyChanged();
        }
    }
    private string status = "Waiting for preview…";
    public string Status
    {
        get => status;
        set
        {
            status = value;
            OnPropertyChanged();
        }
    }
}

