using System.Security.Cryptography;
using System.Text;

namespace CardViewer;

public sealed record ImageFile(string Path, long Length, long ModifiedTicks)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string CacheKey => Key(320);
    public string Key(int size) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v3-{size}|{Path.ToUpperInvariant()}|{Length}|{ModifiedTicks}")));
}

