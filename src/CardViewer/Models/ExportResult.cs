namespace CardViewer;

public sealed record ExportFailure(ImageFile File, string Message);
public sealed record ExportResult(IReadOnlyList<ImageFile> Copied, IReadOnlyList<ExportFailure> Failures, string? ArchivePath = null);
public sealed record ExportProgress(string Stage, string? FileName, long BytesProcessed, long TotalBytes, int CompletedFiles, int TotalFiles);

