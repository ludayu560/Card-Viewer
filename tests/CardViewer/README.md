# Regression and build verification

These files are physically separate from application source. `CardViewer.csproj` explicitly includes them so the existing diagnostic CLI and automatic post-build verification remain available. They use the app's existing assertion harness, not a separate test framework.

- `SelfTests.cs`: suite entry point and named report sections. Scenario implementations are in `SelfTests.ImageDecoding.cs`, `SelfTests.Scheduling.cs`, `SelfTests.Viewer.cs`, and `SelfTests.Selection.cs`.
- `DeletionTests.cs`: disposable-file deletion and WPF selection/confirmation checks.
- `UiTests.cs`: UI suite entry point. Window scenarios are in `UiTests.MainWindow.cs`, `UiTests.Viewer.cs`, and `UiTests.Dialogs.cs`; maintenance and scrollbar cases have their own files. Shared control-driving helpers and controlled fake services live in `UiTests.Helpers.cs` and `UiTests.TestDoubles.cs`.
- `BuildVerification.cs`: inventories samples, hashes sources, runs cold/warm benchmarks and regression tests, checks full-image loading, and publishes reports.
- `FormatTests.cs`: real encoded fixtures, supported-format discovery, explicit GIF exclusion, dimensions, corrupt-input errors, and RAW companion operations.
- `CacheTests.cs`: cache byte budget, eviction order, persistent recency, one-time cache-folder migration, and cleanup safety.
- `ExportTests.cs`: lossless ZIP contents, progress, companion deduplication, and failure cleanup.

## Reading the test source

Each suite's `RunAsync` method lists its scenarios in execution order. Cache, deletion, and export cases have descriptive method names such as `ReadingThumbnailMakesItMostRecentlyUsedAsync`, `RawChangedAfterConfirmationIsPreservedAsync`, and `CancellationDuringWritingRemovesPartialArchiveAsync`. Each method sets up its inputs, performs the operation, and checks the expected result. Comments explain intentional edge cases or synchronization; they do not repeat individual statements.

The large pipeline and UI suites use partial classes to group related scenarios into smaller files while keeping the same CLI entry points and private helpers. Test doubles are controlled substitutes used to pause a decode, report progress, or simulate a dialog; they are kept away from the main UI scenarios. No new test framework or additional test runner is required. Cache scenarios use separate artifact subfolders so their eviction order does not depend on an earlier scenario's files.

## Reading the results

`self-test.txt` is grouped into named sections. Each section starts with `Testing:` describing its scope; individual results use `PASS [section] expected behavior`. Failures include the section and the failed expectation. The build console also prints one `GROUP PASS` summary per section with its assertion count, followed by cold/warm and full-image timings. Assertion counts are checks, not separate test cases.

For example, format tests separately check that a grid bitmap is frozen, its longest edge is at most 320 pixels, and its orientation/aspect ratio is retained. Preview size and native full-image dimensions have their own checks, so a failure identifies which property broke.

| Section | What is verified |
| --- | --- |
| Source discovery | Hidden files and AppleDouble sidecars are excluded. |
| JPEG and PNG | JPEG orientation/reduced previews and PNG transparency survive decoding and caching. |
| Supported formats and RAW companions | Real encoded fixtures load; GIF is excluded by both filtering and folder scanning; corrupt images fail; dummy RAW pairs associate and delete. |
| Camera JPEG read budget | Each camera JPEG's pass-1 metadata probe reads less than 32 KB. |
| Deletion and selection | Confirmation, selection, protected paths, and partial deletion failures use disposable files. |
| Lossless ZIP export | Extracted ZIP bytes match originals; RAW deduplication, progress, and cleanup work. |
| Cache budget and eviction | The 2 GB default and small deterministic eviction budgets enforce persistent LRU ordering. |
| HIF thumbnail passes | Every HIF fixture gets an embedded preview, oriented 320px thumbnail, and bounded metadata reads. |
| Fallback, on-demand preview, and cache | Missing previews, uncached 1600px generation, stale/corrupt cache entries, cancellation, and clearing behave as expected. |
| RAW association and full HIF loading | Backend pairing, invalid RAW errors, native HIF dimensions, and monotonic byte-level progress. |
| WPF controls and viewer operations | Actual WPF controls, bindings, sizing, navigation, progress, selection, and responsiveness work. |
| Abandoned RAW worker | One cancelled worker cannot stall later HIF loading; this is not RAW image-quality coverage. |
| Scheduling and responsiveness | Visible jobs overtake background jobs; cancellation and independent worker queues preserve responsiveness. |
| Pipeline measurements and source preservation | Timing fields exist and are nonnegative, reads stay bounded, and HIF source hashes match. |

Build verification separately checks hashes for **every** visible source fixture (including unsupported GIF and RAW), cold/warm grid loads for supported formats, and full viewing for each supported image. GIF exclusion uses a disposable filename-only fixture under artifacts; no GIF source sample or decoder is required. Any unsupported files present in TestImages are hashed for preservation but never decoded or benchmarked. A cold benchmark clears generated thumbnails, not the Windows filesystem cache. UI tests drive controls offscreen; they do not test physical mouse input or compare screenshots.

From the repository root:

```powershell
dotnet build src/CardViewer/CardViewer.csproj -c Release
dotnet run --project src/CardViewer/CardViewer.csproj -c Release --no-build -- --self-test TestImages
```

The build already runs the regression suite and benchmark. The second command is for running regression checks independently. Use the local SDK environment commands in the main README if .NET is not installed globally.

`TestImages` is read-only reference data. Tests create disposable files beneath `artifacts`; deletion tests never receive real camera files as deletion targets. Full RAW decoding is excluded from automatic build verification.

## UI checks

The existing STA dispatcher runs UI checks on every build and with `--self-test`. Windows are invisible, positioned offscreen, do not activate, and do not appear in the taskbar. Run these checks on Windows with a desktop session. Modal tests have a timeout so a broken confirmation cannot leave a build waiting indefinitely.

Tests drive WPF's real button click implementation, checkbox bindings, and registered key commands. They do not inject physical input into the desktop. Coverage includes:

- Folder selection, empty states, checkbox selection, selection counts, Select all/Clear selection, and tile opening.
- Virtualized rows, viewport work requests, and selection persistence after scrolling.
- Thumbnail placeholders, available previews, and error presentation.
- Command enablement, duplicate-operation prevention, maintenance progress and responsiveness, failure recovery, and benchmark output. Repeated clear/benchmark operations also run against the real scheduler and cache backend with a dispatcher heartbeat to check responsiveness.
- Actual modal cancel, confirm, window-close, protected-path, and error-dialog operations.
- Disposable batch deletion with a locked file, exact confirmation targets, retained failure selection, and confirmed RAW companion deletion.
- Actual viewer opening, percentage/indeterminate/completed progress, original-only loading, matching bottom padding, navigation, key bindings, and close/cancel during loading.
- Shared button templates and WPF binding errors, which fail the suite.

UI tests use tiny generated images and controlled in-memory full-image results, with no additional full RAW decodes. Detailed `ui-tests.txt` output lives under the run's generated regression cache directory; assertions also appear in `self-test.txt`. These are control/operation tests, not screenshot comparisons or hardware-input end-to-end tests.


`FormatTests.cs` exercises packaged TestImages fixtures for HIF, HEIC/HEIF, JPG/JPEG, PNG, TIFF, BMP, WebP, and AVIF, including corrupt files, cold decode, warm cache, full viewing, scanning and deletion. Official HEIC fixture provenance is in [sample provenance](../../docs/test-images.md); builds use no network. Thirteen RAW companion extensions have association and deletion checks using generated dummy files (no extra RAW decodes). Viewer tests check metadata-based automatic window sizing and stable bounds through preview/full-image replacement, screen bounds for landscape/portrait/panoramic/small images, header state, and grouped deletion icons/badges with exact paths. `viewer-layout.png` is rendered alongside the UI test report for visual inspection.

`ExportTests.cs` covers lossless ZIP entries, shared-companion deduplication, hidden/unrelated files, unique archive names, changed sources, temporary cleanup, source-gate cancellation, and protected destinations. A camera fixture is exported using read-only access to verify exporting from TestImages; source hashes are checked by the build. UI tests cover the export button, destination-picker cancellation, preserved selection and paired-file copying. Camera metadata sizes are compared with oriented full-image sizes on every build.

ZIP export tests extract entries and compare every byte against the originals, check shared RAW deduplication, unique output names, interrupted-archive cleanup, protected sources/destinations, and byte-level progress. UI tests hold an export pending to verify scrolling, opening images, active thumbnail scheduling, dispatcher responsiveness and intermediate percentage updates. Tag tests check distinct, stable, case-insensitive colors for all 24 image/RAW extensions. Integration fixtures come only from TestImages; there is no build-time example generation or network access.


CacheTests uses small byte budgets to verify LRU order, persistent recency across cache instances, startup pruning, concurrent publications, oversized entries, locked-entry handling, exact 2 GB default, and safe legacy-preview cleanup. Preview tests verify repeated 1600px requests read the source and create no disk cache. UI tests exercise forward/backward/repeated/additive Shift ranges across virtualized rows, checkbox anchoring, and styled scrollbar paging and thumb dragging in both orientations.

