using System.IO;
using System.IO.Compression;

namespace CardViewer;

internal static class ExportTests
{
    internal static async Task RunAsync(string root, ImageFile protectedSource, Action<bool, string> check)
    {
        string sourceFolder = Path.Combine(root, "export-source");
        string destinationFolder = Path.Combine(root, "export-destination");
        Directory.CreateDirectory(sourceFolder);
        using var sourceGate = new SemaphoreSlim(1, 1);
        var service = new ImageExportService(sourceGate);

        var jpeg = await CreateImageAsync(sourceFolder, "pair.jpg");
        var png = await CreateImageAsync(sourceFolder, "pair.png");
        var raw = await CreateImageAsync(sourceFolder, "pair.NEF");
        var hiddenRaw = await CreateImageAsync(sourceFolder, ".pair.ARW");
        var unrelatedRaw = await CreateImageAsync(sourceFolder, "other.RAF");

        string firstArchive = await ArchiveContainsOriginalBytesAndMatchingRawOnceAsync(
            service, destinationFolder, jpeg, png, raw, hiddenRaw, unrelatedRaw, check);
        await RepeatedExportDoesNotOverwriteExistingArchiveAsync(service, destinationFolder, jpeg, firstArchive, check);
        await ChangedSourceDoesNotProduceAnArchiveAsync(service, sourceFolder, destinationFolder, check);
        await SourceAndProtectedDestinationsAreRefusedAsync(service, root, sourceFolder, jpeg, check);
        await ProtectedCameraSourceCanBeExportedReadOnlyAsync(service, destinationFolder, protectedSource, check);
        await CancellationDuringWritingRemovesPartialArchiveAsync(service, root, png, check);
        await CancellationWhileWaitingForSourceGateCreatesNoDestinationAsync(service, sourceGate, root, jpeg, check);
    }

    private static async Task<ImageFile> CreateImageAsync(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, "export fixture " + name);
        var info = new FileInfo(path);
        return new ImageFile(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static async Task<string> ArchiveContainsOriginalBytesAndMatchingRawOnceAsync(
        ImageExportService service, string destination, ImageFile jpeg, ImageFile png,
        ImageFile raw, ImageFile hiddenRaw, ImageFile unrelatedRaw, Action<bool, string> check)
    {
        // Arrange: JPEG and PNG share a RAW; JPEG is deliberately selected twice.
        var updates = new List<ExportProgress>();
        var selection = new[] { jpeg, png, jpeg };

        // Act: export the selection into a lossless ZIP.
        var result = await service.ExportAsync(selection, destination, default, new CaptureProgress(updates.Add));

        // Assert: exactly the two images and their shared RAW are exported.
        check(result.Copied.Count == 3 && result.Failures.Count == 0 && result.ArchivePath != null,
            "ZIP export deduplicates selections and shared RAW companions");
        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        foreach (var file in new[] { jpeg, png, raw })
        {
            byte[] sourceBytes = await File.ReadAllBytesAsync(file.Path);
            using var extracted = new MemoryStream();
            using var entryStream = archive.GetEntry(file.Name)!.Open();
            await entryStream.CopyToAsync(extracted);
            check(sourceBytes.SequenceEqual(extracted.ToArray()),
                "ZIP extraction matches source byte-for-byte: " + file.Name);
        }
        check(archive.Entries.Count == 3 && archive.GetEntry(hiddenRaw.Name) == null && archive.GetEntry(unrelatedRaw.Name) == null,
            "ZIP excludes hidden and unrelated RAW files");
        check(updates.Any(update => update.BytesProcessed > 0 && update.TotalBytes > 0) && updates.Last().Stage == "ZIP ready",
            "Export reports byte-level progress and archive finalization");
        return result.ArchivePath!;
    }

    private static async Task RepeatedExportDoesNotOverwriteExistingArchiveAsync(
        ImageExportService service, string destination, ImageFile image, string firstArchive, Action<bool, string> check)
    {
        byte[] originalArchive = await File.ReadAllBytesAsync(firstArchive);

        var repeatedExport = await service.ExportAsync(new[] { image }, destination, default);

        byte[] originalArchiveAfter = await File.ReadAllBytesAsync(firstArchive);
        check(repeatedExport.ArchivePath != firstArchive && originalArchive.SequenceEqual(originalArchiveAfter),
            "Repeated exports create unique archives without overwriting prior ZIPs");
        check(!Directory.EnumerateFiles(destination, "*.tmp").Any(), "ZIP export leaves no temporary files");
    }

    private static async Task ChangedSourceDoesNotProduceAnArchiveAsync(
        ImageExportService service, string source, string destination, Action<bool, string> check)
    {
        var selectedImage = await CreateImageAsync(source, "changed.jpg");
        await File.AppendAllTextAsync(selectedImage.Path, "changed after selection");

        var result = await service.ExportAsync(new[] { selectedImage }, destination, default);

        check(result.Failures.Count == 1 && result.ArchivePath == null,
            "Changed sources are refused and empty ZIPs are not published");
    }

    private static async Task SourceAndProtectedDestinationsAreRefusedAsync(
        ImageExportService service, string root, string source, ImageFile image, Action<bool, string> check)
    {
        bool sourceRefused = false;
        try
        {
            await service.ExportAsync(new[] { image }, source, default);
        }
        catch (IOException) { sourceRefused = true; }
        check(sourceRefused, "ZIP export refuses source folder as destination");

        string protectedDestination = Path.Combine(root, "TestImages", "export-target");
        bool protectedRefused = false;
        try
        {
            await service.ExportAsync(new[] { image }, protectedDestination, default);
        }
        catch (UnauthorizedAccessException) { protectedRefused = true; }
        check(protectedRefused && !Directory.Exists(protectedDestination), "ZIP export never writes into TestImages");
    }

    private static async Task ProtectedCameraSourceCanBeExportedReadOnlyAsync(
        ImageExportService service, string destination, ImageFile cameraSource, Action<bool, string> check)
    {
        var result = await service.ExportAsync(new[] { cameraSource }, destination, default);

        check(result.Copied.Count >= 1 && File.Exists(cameraSource.Path),
            "TestImages can be exported with read-only source access");
    }

    private static async Task CancellationDuringWritingRemovesPartialArchiveAsync(
        ImageExportService service, string root, ImageFile image, Action<bool, string> check)
    {
        string destination = Path.Combine(root, "zip-interrupted");
        using var cancellation = new CancellationTokenSource();
        var cancelAfterFirstBytes = new CaptureProgress(update =>
        {
            if (update.BytesProcessed > 0)
                cancellation.Cancel();
        });

        bool wasCancelled = false;
        try
        {
            await service.ExportAsync(new[] { image }, destination, cancellation.Token, cancelAfterFirstBytes);
        }
        catch (OperationCanceledException) { wasCancelled = true; }

        check(wasCancelled && !Directory.EnumerateFiles(destination).Any(),
            "Interrupted ZIP is never published and temporary data is removed");
    }

    private static async Task CancellationWhileWaitingForSourceGateCreatesNoDestinationAsync(
        ImageExportService service, SemaphoreSlim sourceGate, string root, ImageFile image, Action<bool, string> check)
    {
        string destination = Path.Combine(root, "export-canceled");
        using var cancellation = new CancellationTokenSource();
        await sourceGate.WaitAsync();
        var export = service.ExportAsync(new[] { image }, destination, cancellation.Token);
        try
        {
            await Task.Delay(20);
            check(!export.IsCompleted, "ZIP source reads wait for the shared source gate");
            cancellation.Cancel();
        }
        finally { sourceGate.Release(); }

        bool wasCancelled = false;
        try { await export; }
        catch (OperationCanceledException) { wasCancelled = true; }
        check(wasCancelled && !Directory.Exists(destination), "Canceled queued export creates no destination");
    }

    private sealed class CaptureProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}