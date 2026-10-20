using System.Windows.Media.Imaging;

namespace CardViewer;

public interface IViewerImageSource
{
    Task<System.Windows.Size> GetDimensionsAsync(ImageFile file, CancellationToken token);
    Task<BitmapSource> GetPreviewAsync(ImageFile file, CancellationToken token);
    Task<ImageFile> ResolveFullImageAsync(ImageFile file, CancellationToken token);
    Task<BitmapSource> GetFullImageAsync(ImageFile file, CancellationToken token, IProgress<FullImageProgress>? progress = null);
}
