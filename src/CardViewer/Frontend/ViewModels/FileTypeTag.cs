using System.IO;
namespace CardViewer;

public sealed record FileTypeTag(string Name, string Background, string Foreground)
{
    private static readonly Dictionary<string, (string Background, string Foreground)> colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HIF"] = ("#46346C", "#D9BDFF"), ["HEIC"] = ("#284B62", "#A9E0FF"), ["HEIF"] = ("#274E54", "#A8E9E4"),
        ["JPG"] = ("#614424", "#FFD38E"), ["JPEG"] = ("#62352B", "#FFB99B"), ["PNG"] = ("#24543E", "#A3E8BF"),
        ["TIF"] = ("#493E21", "#F2DE93"), ["TIFF"] = ("#4B4C24", "#E1E8A0"), ["BMP"] = ("#49384E", "#E2BBE9"),
        ["WEBP"] = ("#224E49", "#9DE4D7"), ["AVIF"] = ("#323F6C", "#B7C9FF"),
        ["RAF"] = ("#66323A", "#FFB6BF"), ["NEF"] = ("#615322", "#FFE69B"), ["NRW"] = ("#575A24", "#EDF2A6"),
        ["ARW"] = ("#653D26", "#FFC59B"), ["SR2"] = ("#5B382F", "#EBBBA8"), ["CR2"] = ("#63302D", "#FFB1A8"),
        ["CR3"] = ("#602C45", "#FFB0D8"), ["CRW"] = ("#5A3340", "#EFB3CA"), ["DNG"] = ("#3B3668", "#CCC2FF"),
        ["ORF"] = ("#284A63", "#A9D5FA"), ["RW2"] = ("#2C5262", "#ACE3F5"), ["PEF"] = ("#395433", "#BCE4AB"), ["SRW"] = ("#3F435E", "#C8CCF1")
    };
    public static FileTypeTag FromPath(string path)
    {
        string name = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var color = colors.GetValueOrDefault(name, ("#3B4859", "#E9EEF5"));
        return new(name, color.Item1, color.Item2);
    }
}
