# Packaged format fixtures

The integration tests use `TestImages/format-sample.*`, real encoded images for supported extensions: HIF, HEIC, HEIF, JPG, JPEG, PNG, TIF, TIFF, BMP, WebP, and AVIF. GIF is no longer supported; its exclusion is tested using a disposable filename-only fixture under artifacts, without requiring a GIF in TestImages. Build verification records current source SHA-256 hashes under `artifacts/build-verification` and verifies that tests leave them unchanged. Originally twelve format samples, including GIF, were added under the user's one-time permission; all 24 pre-existing camera files were verified unchanged.

All subsequent builds and tests treat TestImages as read-only. They neither generate nor download format examples. Destructive tests operate only on disposable copies under artifacts. Small generated unit fixtures remain for malformed metadata, transparency and cancellation cases.

Provenance:
- `format-sample.hif`: byte-for-byte copy of the existing `DSCF0048.HIF` camera sample.
- `format-sample.heic` and `.heif`: the same genuine HEVC/HEIF container from the official libheif example, exercising both filename extensions. Source: https://github.com/strukturag/libheif/blob/b97d0c2b2353c8c132f334729fc75e2d47d3763d/examples/example.heic . SHA-256: 7F8B363E4936C0666A25F64F3A92FDA10BD8E5453BE4592530B65A55DD98F3F2. Upstream MIT license is reproduced in `libheif-COPYING`.
- Other extensions: a simple blue/gold geometric image encoded once in each actual format, then stored as a fixture. No encoding step runs during builds.

Tests read the packaged TestImages files. This document and the adjacent license preserve attribution; the build reports provide current source-integrity checks.
