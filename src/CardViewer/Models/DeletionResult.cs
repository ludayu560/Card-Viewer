namespace CardViewer;

public sealed record DeletionFailure(ImageFile File, string Message);
public sealed record DeletionResult(IReadOnlyList<ImageFile> Deleted, IReadOnlyList<DeletionFailure> Failures);


