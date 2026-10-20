namespace CardViewer;

public sealed record BenchmarkResult(int Images, int Loaded, int Failed, double ElapsedSeconds, int CacheHits, int SourceReads, string[] Errors, int EmbeddedPreviews, double? FirstPreviewMs, StageTotals Stages)
{
    public override string ToString() => $"{Loaded}/{Images} thumbnails loaded in {ElapsedSeconds:F3} s · {CacheHits} cache hits · {SourceReads} source reads · {Failed} errors";
}

