namespace CardViewer;

public enum FullImageLoadStage
{
    QueuedRead, Reading, QueuedDecode, Decoding
}
public sealed record FullImageProgress(FullImageLoadStage Stage, long BytesRead = 0, long TotalBytes = 0);

