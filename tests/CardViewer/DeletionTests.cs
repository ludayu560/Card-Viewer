using System.IO;

namespace CardViewer;

internal static class DeletionTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        // All files are disposable; camera samples are never deletion targets.
        string folder = Path.Combine(root, "deletion-tests");
        Directory.CreateDirectory(folder);
        using var sourceGate = new SemaphoreSlim(1, 1);
        var service = new ImageDeletionService(sourceGate);
        await BatchDeletionContinuesAfterIndividualFailuresAsync(folder, service, check);
        await MatchingRawIsIncludedOnceAndStandaloneRawIsProtectedAsync(folder, service, check);
        await RawChangedAfterConfirmationIsPreservedAsync(folder, service, check);
        await CancelledDeletionPreservesTheSourceAsync(folder, service, check);
        await DeletionWaitsForAndReleasesSourceGateAsync(folder, service, sourceGate, check);
        await ReadOnlySourceIsNotModifiedAsync(folder, service, check);
    }

    private static async Task<ImageFile> CreateImageAsync(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, "disposable deletion fixture");
        var info = new FileInfo(path);
        return new ImageFile(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static async Task BatchDeletionContinuesAfterIndividualFailuresAsync(string folder, ImageDeletionService service, Action<bool, string> check)
    {
        var first = await CreateImageAsync(folder, "first.jpg");
        var second = await CreateImageAsync(folder, "second.PNG");
        var raf = await CreateImageAsync(folder, "first.RAF");
        var hidden = await CreateImageAsync(folder, ".hidden.jpg");
        var changed = await CreateImageAsync(folder, "changed.HIF");
        await File.AppendAllTextAsync(changed.Path, "changed after selection");
        var locked = await CreateImageAsync(folder, "locked.jpg");
        var missing = first with { Path = Path.Combine(folder, "missing.jpg") };
        // Never pass camera sources to a deletion test, even when testing protection.
        var protectedImage = first with { Path = Path.Combine(folder, "TestImages", "protected.jpg") };
        using (var held = new FileStream(locked.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await service.DeleteAsync(new[] { first, first, second, raf, hidden, changed, locked, missing, protectedImage });
            check(result.Deleted.Count == 3 && !File.Exists(first.Path) && !File.Exists(second.Path) && !File.Exists(raf.Path), "Batch deletion removes selected images and confirmed RAW companions and continues past failures");
            check(result.Failures.Count == 5, "Deletion reports protected, hidden, changed, locked and missing files individually");
            check(File.Exists(hidden.Path), "Deletion preserves dot-prefixed files");
            check(File.Exists(changed.Path) && File.Exists(locked.Path), "Deletion preserves changed and locked files");
            check(result.Failures.Any(failure => failure.File == protectedImage && failure.Message.Contains("protected", StringComparison.OrdinalIgnoreCase)), "TestImages paths are refused before any file access");
        }
        check(ImageDeletionService.IsProtectedPath(Path.Combine(folder, "testimages", "nested", "photo.jpg")), "TestImages protection covers nested paths and ignores case");
        check(!ImageDeletionService.IsProtectedPath(Path.Combine(folder, "TestImages-copy", "photo.jpg")), "Protected-folder matching uses complete path components");
    }

    private static async Task MatchingRawIsIncludedOnceAndStandaloneRawIsProtectedAsync(string folder, ImageDeletionService service, Action<bool, string> check)
    {
        var pair = await CreateImageAsync(folder, "pair.jpg");
        var pairPng = await CreateImageAsync(folder, "pair.png");
        var pairRaw = await CreateImageAsync(folder, "pair.rAf");
        var hiddenRaw = await CreateImageAsync(folder, ".pair.RAF");
        var unrelatedRaw = await CreateImageAsync(folder, "unrelated.RAF");
        var prepared = await service.PrepareAsync(new[] { pair, pairPng, pair });
        check(prepared.Count == 3 && prepared.Contains(pairRaw), "Deletion preparation includes matching RAW once across JPEG/PNG and duplicate selections, ignoring extension case");
        check(!prepared.Contains(hiddenRaw) && !prepared.Contains(unrelatedRaw), "Deletion preparation excludes hidden and unrelated RAW files");
        var pairResult = await service.DeleteAsync(prepared);
        check(pairResult.Deleted.Count == 3 && !File.Exists(pairRaw.Path), "Confirmed batch removes the associated RAW and both selected images");
        var orphanResult = await service.DeleteAsync(new[] { unrelatedRaw });
        check(orphanResult.Failures.Count == 1 && File.Exists(unrelatedRaw.Path), "Standalone RAW files cannot be deleted without a selected matching image");
    }

    private static async Task RawChangedAfterConfirmationIsPreservedAsync(string folder, ImageDeletionService service, Action<bool, string> check)
    {
        var changedPair = await CreateImageAsync(folder, "changed-pair.jpg");
        var changedRaw = await CreateImageAsync(folder, "changed-pair.RAF");
        var changedSelection = await service.PrepareAsync(new[] { changedPair });
        await File.AppendAllTextAsync(changedRaw.Path, "modified after confirmation snapshot");
        var changedResult = await service.DeleteAsync(changedSelection);
        check(changedResult.Deleted.Count == 1 && changedResult.Failures.Single().File == changedRaw && File.Exists(changedRaw.Path), "RAW companions changed after confirmation are preserved and reported");
    }

    private static async Task CancelledDeletionPreservesTheSourceAsync(string folder, ImageDeletionService service, Action<bool, string> check)
    {
        var canceled = await CreateImageAsync(folder, "canceled.jpg");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            var result = await service.DeleteAsync(new[] { canceled }, stop.Token);
            check(result.Deleted.Count == 0 && result.Failures.Count == 1 && File.Exists(canceled.Path), "Canceled deletion leaves files intact and returns a result");
        }
    }

    private static async Task DeletionWaitsForAndReleasesSourceGateAsync(string folder, ImageDeletionService service, SemaphoreSlim sourceGate, Action<bool, string> check)
    {
        var waiting = await CreateImageAsync(folder, "waiting.jpg");
        await sourceGate.WaitAsync();
        var pending = service.DeleteAsync(new[] { waiting });
        try
        {
            await Task.Delay(50);
            check(!pending.IsCompleted && File.Exists(waiting.Path), "Deletion waits for the shared source-read gate");
        }
        finally { sourceGate.Release(); }
        var completed = await pending;
        check(completed.Deleted.Count == 1 && sourceGate.CurrentCount == 1, "Deletion releases the source gate after completing");
    }

    private static async Task ReadOnlySourceIsNotModifiedAsync(string folder, ImageDeletionService service, Action<bool, string> check)
    {
        var readOnly = await CreateImageAsync(folder, "read-only.jpg");
        File.SetAttributes(readOnly.Path, FileAttributes.ReadOnly);
        try
        {
            var result = await service.DeleteAsync(new[] { readOnly });
            check(result.Failures.Count == 1 && File.Exists(readOnly.Path), "Read-only files report failure without changing their attributes");
        }
        finally { File.SetAttributes(readOnly.Path, FileAttributes.Normal); }
    }

    // Called on the existing STA dispatcher test thread; does not show a dialog or delete files.
    internal static void CheckConfirmation(ImageFile file, Action<bool, string> check)
    {
        var selected = new ImageItem(file);
        int changes = 0;
        selected.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImageItem.IsSelected)) changes++; };
        selected.IsSelected = true;
        selected.IsSelected = true;
        selected.IsSelected = false;
        check(changes == 2, "Tile selection notifies bindings only when its value changes");
        var dialog = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(new[] { file, file with { Path = file.Path + ".jpg" } }));
        check(dialog.CancelButton.IsDefault && dialog.CancelButton.IsCancel && !dialog.DeleteButton.IsDefault, "Deletion confirmation defaults to Cancel and supports Escape");
        check(dialog.FileList.Items.Count == 2 && ((DeletionGroup)dialog.FileList.Items[0]).Files[0].Path == file.Path, "Confirmation lists every exact selected path");
        var protectedDialog = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(new[] { file with { Path = Path.Combine(Path.GetDirectoryName(file.Path)!, "TestImages", "image.jpg") } }));
        check(!protectedDialog.DeleteButton.IsEnabled, "Confirmation cannot authorize deleting protected TestImages files");
        var empty = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(Array.Empty<ImageFile>()));
        check(!empty.DeleteButton.IsEnabled, "Empty deletion confirmation cannot authorize deletion");

        var ordinary = file with { Path = Path.Combine(Path.GetTempPath(), "CardViewer-confirmation", "photo.jpg") };
        var allowed = new DeleteConfirmationWindow(new DeleteConfirmationViewModel(new[] { ordinary }));
        check(allowed.DeleteButton.IsEnabled, "Explicit deletion action is available for an ordinary selected image");

    }
}


