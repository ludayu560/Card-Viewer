using System.IO;
namespace CardViewer;

public static class ImageFileFilter
{
    private static readonly HashSet<string> images = new(StringComparer.OrdinalIgnoreCase)
        { ".hif", ".heic", ".heif", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".webp", ".avif" };
    private static readonly HashSet<string> raw = new(StringComparer.OrdinalIgnoreCase)
        { ".raf", ".nef", ".nrw", ".arw", ".sr2", ".cr2", ".cr3", ".crw", ".dng", ".orf", ".rw2", ".pef", ".srw" };
    public const string SupportedDescription = "HIF, HEIC/HEIF, JPG/JPEG, PNG, TIFF, BMP, WebP and AVIF";
    public static bool IsIgnored(FileInfo file) => file.Name.StartsWith(".", StringComparison.Ordinal);
    public static bool IsHif(FileInfo file) => !IsIgnored(file) && file.Extension.Equals(".hif", StringComparison.OrdinalIgnoreCase);
    public static bool IsRaf(FileInfo file) => !IsIgnored(file) && file.Extension.Equals(".raf", StringComparison.OrdinalIgnoreCase);
    public static bool IsRaw(FileInfo file) => !IsIgnored(file) && raw.Contains(file.Extension);
    public static bool IsHeif(FileInfo file) => IsHif(file) || (!IsIgnored(file) && (file.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)));
    public static bool IsJpeg(FileInfo file) => !IsIgnored(file) && (file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
    public static bool IsPng(FileInfo file) => !IsIgnored(file) && file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    public static bool IsSupported(FileInfo file) => !IsIgnored(file) && images.Contains(file.Extension);
    public static bool UsesPngCache(FileInfo file) => !IsHeif(file) && !IsJpeg(file);
}
