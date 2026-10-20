namespace CardViewer;

public sealed record PipelineTiming(string File, string Pass, bool Cached, long BytesRead, double ReadMs, double DecodeMs, double ResizeMs, double WriteMs, string? Error);
public sealed record StageTotals(long BytesRead, double ReadMs, double DecodeMs, double ResizeMs, double WriteMs)
{
    public static StageTotals operator -(StageTotals a, StageTotals b) => new(a.BytesRead - b.BytesRead, a.ReadMs - b.ReadMs, a.DecodeMs - b.DecodeMs, a.ResizeMs - b.ResizeMs, a.WriteMs - b.WriteMs);
}

