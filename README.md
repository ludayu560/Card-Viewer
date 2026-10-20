# Card Viewer

A HIF Windows image browser for quickly browsing camera photos on SD cards and other drives. Thumbnails appear progressively, so you can start browsing while the remaining images load. Specifically for HIF/HEIC files from Canon, Sony, Nikon, Fujifilm cameras. Works with other generic image types as well. 

## Getting started

Run the Card Viewer setup executable, then open **Card Viewer** from the Start menu. The installer includes the required .NET runtime and installs for the current Windows user.

1. Click **Choose folder…** and select your image folder.
2. Scroll through the thumbnail grid. The app prioritizes images currently on screen.
3. Click an image to open the viewer. Use the navigation controls or **Left/Right** arrow keys to browse, and **Escape** to close.

Folder scanning includes files directly inside the selected folder; it does not scan subfolders. Files whose names begin with a dot are ignored.

Supported formats: **HIF, HEIC, HEIF, JPG, JPEG, PNG, TIF, TIFF, BMP, WebP, and AVIF**. Multi-image containers display the primary/first image. GIF is not supported.

Choose a folder with a normal Windows filesystem path, such as `D:\DCIM`. Phone and camera storage exposed only through MTP under **This PC** is not currently supported.

## How images load

Card Viewer uses three steps to show thumbnails quickly:

### Stage 1 — Use a saved thumbnail

First, the app checks whether it has already made a thumbnail for this photo. If it has, it shows that saved copy and skips the next two steps.

### Stage 2 — Look for a built-in preview

If there is no saved thumbnail, the app looks for a small preview already inside the photo. HEIC/HIF and JPEG files straight from cameras often contain these built-in thumbnails. When the app finds a usable one, it shows it right away. They are optional, so photos without one show a placeholder until the next step finishes.

For JPEGs, this search reads less than **32 KB**. That is a limit on how much data is read, not the size of a thumbnail the app creates.

### Stage 3 — Make and save the finished thumbnail

The app makes a correctly oriented thumbnail up to **320 pixels** on its longest edge and replaces the temporary preview. It saves this thumbnail on your internal drive so it can skip the work next time.

This step may need to read the whole photo. For JPEGs, the app reads the whole file but decodes a smaller version, which uses less processing and memory than making a full-size image.

These steps happen for each visible photo. You can keep scrolling while images load, and newly visible photos get priority.

### Open photo — Show more detail

When you open a photo, its thumbnail stays visible while the app prepares a larger preview, up to **1600 pixels** on its longest edge. It then loads the full-resolution image in the background and replaces the preview when ready.

The larger preview is made only when needed and is not saved to the thumbnail cache. The viewer opens the selected photo; matching RAW files are used only for export and confirmed deletion.

## Selecting, exporting, and deleting

Use the tile checkboxes to select images. **Shift-click** selects a range from the previous selection; **Ctrl+Shift-click** adds a range. **Select all** and **Clear selection** manage the current folder's selection.

**Export selected…** creates a lossless ZIP in a folder you choose. It includes the selected images and matching RAW companions from the same folder, using the same filename stem. The original files remain unchanged.

**Delete selected** shows a confirmation listing the selected images and their associated RAW files. **Deletion is permanent**, including confirmed RAW companions; files are not sent to the Recycle Bin. Browsing and exporting do not modify source images.

## Thumbnail cache

Generated 320px thumbnails persist across sessions, with a **2 GB budget**. The least recently used entries are removed first when space is needed.

Click **Clear cache** to remove generated thumbnails. This does not remove your photos. **Benchmark load** measures thumbnail loading for the current folder using the cache as it currently exists; clear the cache first to measure thumbnail generation again.

The cache is stored at `%LOCALAPPDATA%\CardViewer\Thumbnails\v3`. On the first launch after this update, existing grid thumbnails are moved from the earlier `HifBrowser` cache in the background. Existing entries in the new cache are preserved. Old logs and unrelated files are left in the earlier folder.

## Building from source

Requires Windows x64 and the .NET 10 SDK, or the repository's local SDK setup.

```powershell
.\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
```

The build runs regression tests and reports cold/warm thumbnail benchmarks. The installer is written to `artifacts/installer/`.

For implementation details, see the [architecture](docs/architecture.md), [test documentation](tests/CardViewer/README.md), and [sample attribution](docs/test-images.md).

## License

Card Viewer is licensed under the [GNU General Public License v3.0](LICENSE) (GPL-3.0-only).

Third-party libraries and sample images retain their own licenses and notices. See the bundled library notices and [sample attribution](docs/test-images.md).

For Bela with <3
