# Negative Lab: design and implementation plan

> Historical design and evidence migrated from PhotoManage on 2026-11-08.
> PhotoManage no longer implements this workflow. Paths, UI descriptions, Catalog
> contracts and measurements below describe the former implementation, not
> FilmLight's shipped interface. See [processing.md](processing.md) for the current
> implementation and its remaining limitations.

Status: **accepted design, in implementation** (branch `feature/negative-lab`).
Supersedes the proposal in [negative-scan-processing.md](negative-scan-processing.md)
(its research and failure-state analysis still apply). Decision record:
[ADR 0020](../adr/0020-darktable-negadoctor-for-negative-conversion.md).

The Negative Lab is an independent window, like the Fujifilm Development Lab, where the
photographer turns **camera captures of film negatives** (RAW Photos, e.g. Fujifilm `.RAF`)
into positive JPEGs. They can experiment with the selected frames, preview the conversion
interactively, crop the film border, then export one frame or a checked batch. Each export
is a **Derived Photo Variant** of the source Photo.

## 1. Evidence this design is built on

Established by the 2026-11 experiment
(`%USERPROFILE%\.t3\scratch\2026-11-03-i-captured-this-picture-of-8b477717`, see its handoff):

| Fact | Consequence |
|---|---|
| darktable 5.6.2 (native ARM64) is installed at `%ProgramFiles%\darktable\bin\darktable-cli.exe`. Patching negadoctor's `<i18f` params in an XMP history and rendering with `darktable-cli` works for RAF batches (44 conversions). | The settings bridge the old plan feared is proven. It is pinned to negadoctor **modversion 2** and checked against darktable 5.6.2 source. |
| Measured on this machine: 1600 px linear-Rec.2020 PFM in **~3 s**, 1600 px positive JPEG in **~10 s** (cold), full-resolution 16-bit TIFF in **~10 s**. | darktable is too slow for slider feedback. Live preview must be native; darktable is the final renderer. |
| negadoctor's math is a small closed-form per-pixel function (`src/iop/negadoctor.c`, release-5.6.2). | It is ported exactly to C# and run on a cached **linear basis**, the darktable output with negadoctor disabled, in the same color space negadoctor sees. |
| The pipeline was rawprepare → demosaic → temperature → colorin → **negadoctor** → colorout. highlights, channelmixerrgb, exposure and sigmoid were disabled. | Templates keep that shape. Upstream white balance (temperature) is a separate **Light Balance**, never mixed up with the film base. |
| The lab-matched values (`lab-matched-settings.json`) are empirical and specific to one capture. | They can be imported as a Film Profile. They are never presented as a calibrated stock profile. |
| Crops were manual or done with SIFT against a lab scan; no automatic edge detector existed. | Frame detection is a labeled heuristic the user adjusts. It never decides the crop silently. |

## 2. Domain language (added to CONTEXT.md)

**Negative Lab**, **Negative Conversion**, **Film Base Sample**, **Light Balance**,
**Film Profile**, **Conversion Settings**, **Frame Crop**, **Live Preview**, **Proof**.
Exact definitions live in `CONTEXT.md`. Use these names in code, UI and docs.
Avoid "inversion" (it is not a plain inversion), "preset" for Film Profile (it implies a
universal stock look), "import" for conversion, and "WB pick" for the film-base sample.

## 3. Architecture

```
PhotoManage.UI  ── NegativeLabWindow (WinUI)          presents, edits Conversion Settings
      │            uses CropRotateOverlay, WriteableBitmap
      ▼ IPhotoManageEngine (host-neutral contracts)
PhotoManage.Engine
  ├─ NegativeConversion/   (public, pure, portable)
  │    NegadoctorMath        exact negadoctor port + Rec.2020→sRGB display encode
  │    NegativeAnalysis      film-base sample, auto tone, neutral pickers, frame detection, histogram
  │    ColorBalanceControls  warmth/tint ⇄ RGB bijection
  │    PfmReader             darktable PFM linear basis
  ├─ NegativeConversion/   (internal)
  │    NegativeLabModule     settings + profiles + caches + ConvertNegatives Activity
  │    DarktableHistory      embedded version-pinned XMP templates, param packing
  ├─ Abstractions/INegativeRenderer          replaceable port
  ├─ Adapters/DarktableCliNegativeRenderer   process adapter (portable System.Diagnostics)
  └─ Adapters/UnsupportedNegativeRenderer    default when darktable is absent
PhotoManage.Rendering.Windows
  └─ WindowsPhotoEditRenderer (existing) final crop/rotate/mirror + JPEG encode
```

Rules (ADR 0011/0015/0016):
- The Engine owns darktable invocation, caches, persistence, publication and the Activity.
  WinUI never starts darktable or touches SQLite.
- Public contracts carry no darktable, XMP, SQLite or WinUI types. `INegativeRenderer` speaks
  in `NegadoctorParameters`, `LightBalance` and paths. The darktable/XMP detail stays inside
  the adapter.
- The pure math and analysis are public static, host-neutral functions over
  `NegativeLinearImage`. They are cheap to unit test and called by the UI on a background
  thread.

### 3.1 Domain types (public, `Domain\NegativeConversion.cs`)

```csharp
public enum FilmType { Color = 1, BlackAndWhite = 0 }           // negadoctor v2 film_stock codes
public readonly record struct RgbTriple(double R, double G, double B);
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height);

// Exact negadoctor modversion 2 values, darktable semantics and bounds.
public sealed record NegadoctorParameters(
    FilmType FilmType,
    RgbTriple FilmBase,            // Dmin      [0.00001, 1.5]
    RgbTriple HighlightsBalance,   // wb_high   [0.25, 2]
    RgbTriple ShadowsBalance,      // wb_low    [0.25, 2]
    double DensityRange,           // D_max     [0.1, 6]
    double ScanExposureBias,       // offset    [-1, 1]
    double PaperBlack,             // black     [-0.5, 0.5]
    double PaperGrade,             // gamma     [1, 8]
    double PaperGloss,             // soft_clip [0.0001, 1]
    double PrintExposure);         // exposure  [0.5, 2]

// Upstream white balance of the scanning light (darktable temperature module).
// Coefficients == null means "as shot". Pinned coefficients are normalized to G = 1.
public sealed record LightBalance(RgbTriple? Coefficients, string? Source);

// Same semantics as SavePhotoEditCommand / CropRotateState so the existing overlay and
// IPhotoEditRenderer apply it unchanged. Geometry is expressed on the darktable-oriented output.
public sealed record NegativeFrameGeometry(
    double RotationDegrees, bool FlipHorizontal, bool FlipVertical, NormalizedRect Crop);

public sealed record FilmBaseSample(NormalizedRect Region, RgbTriple Value, double Uniformity,
    IReadOnlyList<FilmBaseSampleWarning> Warnings, bool IsEstimate);

public sealed record NegativeConversionSettings(
    int Version,                       // NegativeConversionSettings.CurrentVersion
    Guid? FilmProfileId,               // provenance only; values below are authoritative
    LightBalance Light,
    NegadoctorParameters Conversion,
    NegativeFrameGeometry Frame,
    FilmBaseSample? BaseSample);

public sealed record FilmProfile(Guid Id, string Name, FilmType FilmType, LightBalance Light,
    RgbTriple FilmBase, RgbTriple HighlightsBalance, RgbTriple ShadowsBalance, double DensityRange,
    NegativeToneDefaults? Tone, string? Notes, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
```

Validation lives in the Engine. Values must be finite and in bounds. Unknown `Version` is
rejected with a clear error, never coerced.

### 3.2 Engine facade (additions to `IPhotoManageEngine`)

```csharp
ValueTask<NegativeLabCapabilities> GetNegativeLabCapabilitiesAsync(CancellationToken ct);
    // darktable path/version, IsSupported, UnsupportedReason (missing / untested version)
ValueTask<IReadOnlyList<NegativeLabFrame>> GetNegativeLabFramesAsync(IReadOnlyList<Guid> mediaItemIds, CancellationToken ct);
    // per Photo: chosen RAW source variant (or ineligibility reason), saved settings,
    // last Negative Lab output variant, IsChangedSinceExport, as-shot light balance
ValueTask SaveNegativeConversionSettingsAsync(SaveNegativeConversionSettingsCommand cmd, CancellationToken ct); // batch upsert
ValueTask<NegativeLinearImage> GetNegativeLinearImageAsync(GetNegativeLinearImageCommand cmd, CancellationToken ct);
    // cached darktable linear Rec.2020 basis, long edge 1600 by default
ValueTask<NegativeProof> RenderNegativeProofAsync(RenderNegativeProofCommand cmd, CancellationToken ct);
    // cached darktable JPEG of the uncropped positive at preview size; cancellation kills the process tree
ValueTask<NegativeConversionResult> ConvertNegativesAsync(ConvertNegativesCommand cmd,
    IProgress<NegativeConversionProgress>? progress, CancellationToken ct);   // Activity
ValueTask<IReadOnlyList<FilmProfile>> ListFilmProfilesAsync(CancellationToken ct);
ValueTask<FilmProfile> SaveFilmProfileAsync(SaveFilmProfileCommand cmd, CancellationToken ct);
ValueTask DeleteFilmProfileAsync(Guid id, CancellationToken ct);
ValueTask<FilmProfile> ImportFilmProfileFromDarktableXmpAsync(string xmpPath, string name, CancellationToken ct);
```

`ConvertNegativesAsync` follows the CardCopy shape (`CopyCardCapturesAsync` with
`IProgress`). It runs as an Engine-owned Activity of the new kind
`ActivityKind.NegativeConversion` ("Negative conversion"). Closing the Lab cancels it.
Photos already published stay published.

### 3.3 Pure functions (public static)

- `NegadoctorMath.RenderPositive(NegativeLinearImage src, NegadoctorParameters p, int maxEdge)`
  returns `Bgra8Image` (sRGB-encoded, premultiplied alpha = 255). It is an exact port of
  `commit_params` + `_process_pixel` (release-5.6.2), including `THRESHOLD` (2^-32 clamp),
  B&W film using `Dmin[0]` for all channels, and the soft-clip formula. Output goes through
  linear Rec.2020 → linear sRGB matrix, clamped to [0,1], then the sRGB OETF. It is
  `Parallel.For` over rows, with no allocation per pixel. Target: ≤ 80 ms for 1600×1067 on
  this ARM64 machine. During drag the UI calls it with `maxEdge` 800.
- `NegadoctorMath.RenderNegativeView(src, maxEdge)`: the negative as captured, for display
  and sampling. It applies an auto exposure scale (99.5th percentile → 0.9) so a capture
  six stops under is still visible. Display only.
- `NegativeAnalysis.SampleFilmBase(src, region)` returns `FilmBaseSample`: the per-channel
  median in the region. It warns on: any channel ≥ 0.98 (clipped light), CV > 6% (not
  uniform: dust, edge, holder), and the sample being darker than the frame's 99th-percentile
  transmission (probably picture content, not clear film).
- `NegativeAnalysis.EstimateFilmBase(src, frameRect?)`: used when no sample exists. It takes
  the 99.5th percentile of non-clipped pixels outside the frame (or the whole image). The
  result is marked `IsEstimate`.
- `NegativeAnalysis.AutoTone(src, p, region)`: a port of darktable's GUI pickers
  `apply_auto_Dmax`, `apply_auto_offset`, `apply_auto_black` and `apply_auto_exposure`,
  run in the GUI's order on the crop region. Use robust 0.5/99.5 percentiles, as
  `render_negadoctor.py` did, instead of strict extrema. Read the 5.6.2 source; do not guess.
- `NegativeAnalysis.NeutralizeHighlights(src, p, region)` / `NeutralizeShadows(...)` are
  ports of `apply_auto_WB_high` / `apply_auto_WB_low`.
- `NegativeAnalysis.DetectFrame(src)` returns `(NormalizedRect rect, double confidence)?`.
  It uses the log transmission of a down-scaled image. Row and column profiles of median
  log-transmission, plus the gradient magnitude, find the longest interior run whose density
  departs from the bright rebate/backlight plateau. Clipped sprocket and backlight pixels are
  excluded. The result is inset by 1% and a low-confidence result is returned as null.
  Straighten is not attempted; the user does that.
- `NegativeAnalysis.Histogram(Bgra8Image)` returns RGB and luma bins plus counts of clipped
  shadows and highlights.
- `ColorBalanceControls`: a friendly bijection in log2 space for wb_high and wb_low.
  `i=(lr+lg+lb)/3`, `warmth=lb−lr`, `tint=lg−(lr+lb)/2`. Inverse: `lr=i−w/2−t/3`,
  `lb=i+w/2−t/3`, `lg=i+2t/3`. Intensity `i` is preserved when warmth or tint changes;
  results are clamped to [0.25, 2]. Sign check: + warmth must make the positive warmer.
  In normal positive-producing densities, raising wb_high brightens that channel; friendly
  highlight controls therefore reverse both raw coordinate signs. Shadows reverse the raw
  signs for negative `offset`, retain them for positive `offset`, and have no effect at zero.
  **+ warmth is warmer and + tint more magenta** in that operating regime. Outside it,
  highlight derivatives can reverse with density and offset, and clipping can suppress any
  change: no global sign mapping can guarantee the direction for every legal parameter set.
  The exact RGB controls remain authoritative; see the measured sign cases in §6.

### 3.4 darktable bridge (`DarktableCliNegativeRenderer`, internal templates)

- **Templates** are embedded resources derived from `lab-matched.xmp`, with camera- and
  image-specific modules removed: rawprepare, demosaic, colorin, colorout, gamma and flip
  are omitted so darktable applies each image's own defaults. The templates keep:
  temperature (only when the Light Balance is pinned), highlights (off), channelmixerrgb
  (off), exposure (off), sigmoid (off) and negadoctor. Two templates exist: `linear`
  (negadoctor disabled) and `positive`. `auto_presets_applied=1`. Keep the canonical `x`,
  `rdf` and `darktable` prefixes. Write with `XmlWriter` and do not round-trip through
  arbitrary DOM serializers.
- **Packing**: negadoctor v2 is `<i18f` little-endian: `film_stock, Dmin[4], wb_high[4],
  wb_low[4], D_max, offset, black, gamma, soft_clip, exposure`. The 4th channel of each
  array is written as 1.0, as darktable does. Verify temperature's modversion and struct
  layout against `src/iop/temperature.c` release-5.6.2 before packing. Unit test the round
  trip with the hex in `lab-matched.xmp`; it must decode to `lab-matched-settings.json`.
- **Invocation**: `ProcessStartInfo.ArgumentList`, never a shell string. Each render gets a
  private workspace under `<CacheRoot>\negative-lab\work\<guid>`, with **a copy of the
  source**, the XMP and the output all given as *relative* names and `WorkingDirectory` set
  to the workspace (absolute Windows output paths were mangled in the experiment).
  Arguments: `--icc-type LIN_REC2020|SRGB`, `--apply-custom-presets false`, `--core`,
  `--configdir <per-worker isolated dir>`, `--library :memory:`, `--disable-opencl`,
  `--conf write_sidecar_files=never`, `--conf plugins/imageio/format/jpeg/quality=95`, and
  `--conf plugins/imageio/format/tiff/bpp=16` for export. Add `--width/--height` only for
  previews. Set `OMP_NUM_THREADS` to the processor count minus 1. Capture stdout and stderr
  to a log in the workspace, include its tail in errors, apply a 15 min timeout, and on
  cancellation kill the process tree. Verify that the output exists and decodes. After
  every render, verify the source size and mtime are unchanged. Delete the workspace on
  success and keep it on failure, with the path logged.
- **Workers**: two isolated configdirs, each serialized. The *interactive* worker handles
  linear bases and proofs, with latest-wins cancellation of stale proofs. The *export*
  worker handles conversions. Never share a configdir concurrently.
- **Probe**: `darktable-cli --version` must parse to 5.6.x; keep the tested set in one list
  so it is easy to extend. Anything else is reported as unsupported with the found version.
  `PHOTOMANAGE_DARKTABLE_CLI` overrides discovery. Discovery order: env var, the
  `AppLocalSettings.DarktableCliPath` the user chose in the Lab, then
  `%ProgramFiles%\darktable\bin\darktable-cli.exe`. darktable is **user-installed, never
  bundled** (GPL; see ADR 0020).
- **Formats**: the linear basis is **PFM** (`.pfm`; rows are bottom-to-top, a negative
  scale means little-endian). Proofs are JPEG at long edge 1600. Exports are full
  resolution with `tiff bpp=16` and `--icc-type SRGB`.

### 3.5 Caches and persistence

- Linear basis: `<CacheRoot>\negative-lab\linear\<sha256>.pfm`. The key covers the source
  path, size and mtime, darktable version, template hash, Light Balance coefficients and
  long edge. Proof: `negative-lab\proof\<sha256>.jpg`, with the key extended by the
  parameter hash. Cap the whole `negative-lab` cache at 3 GB with LRU eviction. A cache is
  disposable; deleting it only costs time.
- The Catalog migration adds three tables:
  - `negative_conversion_settings(media_item_id PK, source_variant_id, settings_json, version, updated_utc)`
  - `film_profiles(id PK, name UNIQUE COLLATE NOCASE, profile_json, version, created_utc, updated_utc)`
  - `negative_conversion_outputs(output_variant_id PK, media_item_id, source_variant_id, settings_json, settings_hash, darktable_version, template_hash, created_utc)`
  These are app-owned working state, like saved Fujifilm recipes. They are not Photo
  Metadata. Losing them on a Catalog rebuild loses Lab work, never photos. Delete rows
  when their Media Item is removed.
- `IsChangedSinceExport` compares the hash of the current settings with the latest output
  row for that Photo.

### 3.6 Export pipeline (per frame, inside the Activity)

1. Revalidate the source RAW variant: it exists, and its size and mtime match the frame
   state when the export started.
2. Render a full-resolution 16-bit sRGB TIFF with darktable, using the positive template,
   the frame's Light Balance and the negadoctor parameters.
3. Convert `NegativeFrameGeometry` to a `PhotoEditTransform` in TIFF pixel space. Call the
   existing `IPhotoEditRenderer.RenderJpegEditAsync(tiff → staged.jpg, transform, quality 95)`.
4. Publish through the **same code path as `SavePhotoEditAsync`'s JPEG branch**. Refactor
   its tail into a shared private helper; do not copy it. That path provides
   collision-safe `<base>-edit.jpg` naming (recognized by Rescan via
   `NormalizeGeneratedEditBaseName`), copying Shared Photo Metadata from the source,
   `AppendDerivedPhotoVariantAsync` under a rendition mutation, and a thumbnail refresh.
   The output must not carry darktable history XMP, and its orientation must be normal.
5. Insert the `negative_conversion_outputs` row.
6. If `ReplacePreviousOutput` (default **on**) and an earlier Negative Lab output of this
   Photo still exists, recycle that variant through the existing Derived Photo Variant
   recycle command, **after** the new one is published. Only outputs recorded in
   `negative_conversion_outputs` are ever recycled.
7. Report progress. A failure stops only that frame; it is recorded with its message and
   the batch continues. Clean up staged files in every case.

The RAW, its sidecar and any `.RAF.xmp` are never written. Tests assert unchanged hashes
and that no new file appears beside the source except the published JPEG.

## 4. The Negative Lab window

Inherits Graphite & Glass (`Themes\DesignSystem.xaml`) and the Fuji Lab's approved structure.
The photo stays dominant, the filmstrip sits beneath it, a compact adjustment panel stays
visible, and groups are **always visible, not drawers**. Mode: *Operate*. The image is the
hero; chrome is quiet, dense and precise, and color in the chrome only marks state.

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ Negative Lab   DSCF6553 · 3 of 12      Film profile [Superia 400 · light pad ▾] ⋯  darktable 5.6.2 ● │
├──────────────────────────────────────────────────────────────┬──────────────────────────┤
│ [Negative|Positive|Split]  │ ✂ Crop  ◎ Film base  ⊕ Neutral │ J ▣  Fit  ●Live │  HISTOGRAM  (clip counts) │
│ ┌──────────────────────────────────────────────────────────┐ │  [ Auto tone ]  [ Reset ] │
│ │                                                          │ │ FILM BASE ········ ROLL  │
│ │                     photo stage                          │ │  Color | B&W              │
│ │      (crop overlay / pickers / split divider)            │ │  ■ swatch  R G B  ✓ clean │
│ │                                                          │ │  [Pick film base]         │
│ │                                                          │ │  Light  [Pinned · 6553 ▾] │
│ └──────────────────────────────────────────────────────────┘ │ COLOR ············ ROLL  │
│  contextual tool bar (crop: aspect 3:2 6×7 6×6 6×4.5 Free,   │  Highlights warmth ───●── │
│   Detect frame, ⟲90°, Mirror, straighten°)                   │  Highlights tint   ──●─── │
├──────────────────────────────────────────────────────────────┤  [Pick neutral highlight] │
│ 12 frames · 5 checked  [✓ all] [Sync…] [Copy][Paste]         │  Shadows warmth / tint    │
│ ┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐        [Export frame]       │  [Pick neutral shadow]    │
│ │☑ ││☑ ││▣ ││  ││  ││⚠ ││✓ ││↻ │        [Export checked (5)▾] │ TONE ············ FRAME  │
│ └──┘└──┘└──┘└──┘└──┘└──┘└──┘└──┘                              │  Exposure  Density        │
│                                                              │  Contrast  Blacks         │
│                                                              │  Highlight roll-off  Range│
│                                                              │ EXACT VALUES (negadoctor) │
├──────────────────────────────────────────────────────────────┴──────────────────────────┤
│ status line: Exporting 3/5 · DSCF6555 …  [Cancel]   |  messages / errors                │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

### 4.1 Opening

- Media Browser: add **Negative Lab** next to the Fujifilm Development Lab action (toolbar
  and Photo context menu), with the same `MainWindow` wiring and Saved events.
- The frames are the selected Photos if more than one is selected. Otherwise they are the
  current view's Photos, with the focused Photo selected. This matches how the Fuji Lab
  receives `MediaItems.ToArray()`. Frames without a RAW Photo Variant stay listed with a ⚠
  and an explanation, and cannot be converted. Reuse an open Lab window: activate it and
  replace its frames.

### 4.2 First run of a frame: something good in under ~4 s, then honest labels

When a frame has no saved settings:
- If a Film Profile is active in this Lab session, apply it.
- Otherwise run `DetectFrame` to propose a crop, `EstimateFilmBase` outside it (labeled
  **Estimated** in amber), then `AutoTone` on the crop.

The stage shows the app's existing Preview of the RAW, dimmed, with "Preparing negative…"
until the linear basis arrives, then switches to the Live Preview. A one-line coach mark
reads *"For accurate color, pick the film base: drag across clear film between frames."*
It is shown until a sample exists, then never again for that Photo.

### 4.3 Stage

- **View** segmented control: Negative / Positive / Split. Split has a draggable vertical
  divider: negative on the left, positive on the right. Holding **Space** temporarily
  shows the negative.
- **Render state chip**, always visible:
  - **Live** (neutral): the native port is showing.
  - **Rendering proof…** with a spinner.
  - **Proof** with a green dot: darktable's own render for exactly these settings is showing.
  The proof renders automatically after 1.5 s idle, cancelling any stale one; a "Proof
  automatically" toggle is in ⋯. Clicking the chip toggles between Live and Proof when
  both exist. Never show a proof whose parameter hash differs from the current settings.
- Tools, each also on a key, and mutually exclusive:
  - **Crop** (C) reuses `CropRotateOverlay`. Its contextual bar holds film-format aspect
    presets (3:2, 6×7, 6×6, 6×4.5, 4:5, Free), **Detect frame**, Rotate 90°, Mirror
    (frames shot emulsion-side) and a straighten slider with the overlay's straighten
    gesture. Outside the Crop tool the positive is shown cropped, and the Negative and
    Split views show the full capture with the crop outline.
  - **Film base** (B): drag a rectangle. A popover shows the swatch, RGB values,
    uniformity and any warnings, with *Use sample* / *Cancel*. A warning never blocks; it
    explains.
  - **Neutral** (N): drag a rectangle. Pick neutral highlight or neutral shadow; the
    default follows the region's luminance and the popover lets you switch.
- **Clipping overlay** (J): blue marks crushed shadows and red marks blown highlights on
  the positive. Zoom: Fit / 100% (Z), scroll to zoom, drag to pan. At 100% the Live
  Preview requests a higher-resolution basis (long edge 3200) for that frame on demand.

### 4.4 Inspector (fixed 340 px, scrolls; each group always visible)

Each group header carries a small scope tag: **ROLL** means it is usually shared across a
roll and is what *Sync* copies by default; **FRAME** means it is per photo. Every slider row
has a label, slider, NumberBox and reset on double-click. Dragging renders at 800 px; on
release, full size.

1. **Histogram** with clipped percentages, then **Auto tone** (A; runs `AutoTone` on the
   crop) and **Reset frame**.
2. **Film base · ROLL**:
   - Film type: Color / B&W.
   - Base swatch with RGB values and a status: "Sampled ✓", "Estimated" (amber) or a
     warning.
   - [Pick film base].
   - **Light balance**: "As shot", "Pinned (from DSCF6553)", or *Pin from this frame*,
     which reads the as-shot coefficients through ExifTool `WB_GRBLevels`. A tooltip
     explains this is the scanning light's white balance, not the film's. If checked frames
     have different as-shot white balance, show an amber hint suggesting a pin.
     Changing the Light Balance re-prepares the linear basis, then asks once:
     "Re-sample film base?" because the old Dmin no longer matches.
3. **Color · ROLL**: Highlights warmth and tint, [Pick neutral highlight]; Shadows warmth
   and tint, [Pick neutral shadow]. Range ±2 (log2), stepped 0.01.
4. **Tone · FRAME**: Exposure (print exposure, shown as EV −1…+1), Density (scan exposure
   bias), Contrast (paper grade), Blacks (paper black), Highlight roll-off (paper gloss),
   Range (Dmax).
5. **Exact values (negadoctor)**: a compact two-column grid of NumberBoxes for every
   negadoctor parameter, with darktable's names in tooltips, for power users and for
   matching a darktable session. Edits here and the friendly sliders stay in sync.

### 4.5 Filmstrip and batch

- A horizontal strip of frames with the Live Preview thumbnail of each **positive** (cropped).
  Thumbnails are prepared in the background in strip order, current frame first, with a
  quiet progress note: "Preparing frames 7/12". Each frame carries a checkbox for batch.
  Badges: edited ●, exported ✓, changed since export ↻, problem ⚠, export failed ✕ (tooltip
  with Retry).
- ←/→ switch frames. Settings are saved **automatically** after a 500 ms debounce and on
  frame switch or close; there is no Save button for settings.
- **Sync…** (flyout): checkboxes for Film base & light ✓, Color ✓, Tone ☐, Crop ☐, then
  *Sync to checked frames* / *Sync to all*. **Copy/Paste** (Ctrl+Shift+C/V) copy the same
  groups.
- **Film profile** picker (title bar): choosing a profile applies its ROLL groups (and its
  Tone defaults, if stored) to the current frame. If frames are checked, it offers *Apply to
  checked*. It shows "· modified" when the current frame differs. ⋯ menu: *Save current as
  profile…*, *Rename/Delete profile*, *Import darktable XMP…* (seeds a profile, e.g. the
  lab-matched XMP), *Compare with reference image…* (pins any JPEG beside the stage for
  lab-scan matching; display only), *Locate darktable…*, *Proof automatically*.
- **Export frame** (Ctrl+E) and **Export checked (n)**. The ▾ holds JPEG quality (default
  95) and *Replace previous Negative Lab output* (default on). Export progress replaces the
  status line, with Cancel. Per-frame failures stay visible in the strip. When an export
  succeeds the Lab raises `PhotoSaved`/`BatchPhotoSaved` so the Media Browser refreshes, and
  the Lab does **not** close.

### 4.6 States and copy

| State | Behavior |
|---|---|
| darktable missing | The stage becomes an explanatory panel: "Negative Lab converts RAW negatives with darktable's negadoctor. darktable 5.6 was not found." [Get darktable] [Locate darktable-cli.exe…]. The inspector is disabled. |
| darktable untested version | The same panel, naming the found version and the tested versions. |
| Frame without RAW | The stage shows "This Photo has no RAW Photo Variant. Negative conversion needs the RAW capture of the negative." The frame cannot be checked for export. |
| Preparing / basis failed | Dimmed preview with a spinner, or an error with [Retry] and the tail of darktable's log. |
| Proof failed | The chip shows "Proof failed" (amber) with a tooltip and keeps Live; editing is unaffected. |
| Export partially failed | Status: "Exported 4 of 5 · 1 failed", with the failed frame marked and Retry available. |
| Source changed during export | That frame fails with "The RAW changed since it was prepared. Prepare it again." |

All user-visible errors go through `RunUiAsync` / `ShowErrorAsync` / `AppStartupLog`. Closing
the window cancels proofs, preparation and export (exports already published stay).
AutomationIds use the `NegativeLab*` prefix. Keyboard: ←/→, Space, C, B, N, A, J, Z, Ctrl+E,
Ctrl+Shift+C/V, Esc (cancel tool). The Lab works at 1280×800; below that width the inspector
overlays the stage. Use theme brushes only. Chrome color appears only in state chips and badges.

## 5. Implementation phases

Each phase lands as its own commit(s) on `feature/negative-lab` with the build and tests green.

1. **Engine core:**
   - Domain types, `NegadoctorMath`, `NegativeAnalysis`, `ColorBalanceControls`, PFM reader.
   - XMP templates and packing, `INegativeRenderer` and `DarktableCliNegativeRenderer`.
   - The new console test project `tests\PhotoManage.Engine.NegativeConversion.Tests`.
   - **Evidence gates**, recorded in §6:
     1. The minimal template matches the full `lab-matched.xmp` stack on a sample RAF.
     2. The Live Preview matches the darktable proof.
     3. A pinned Light Balance matches As shot when it is pinned from the same frame.
2. **Engine integration:**
   - Catalog migration and repositories, caches, the facade methods.
   - `ConvertNegativesAsync`, the Activity kind, the publication refactor.
   - `EngineDependencySet`, and `AppServices` discovery plus the `AppLocalSettings` path.
   - Architecture tests, and integration tests (skipped unless darktable and
     `PHOTOMANAGE_NEGATIVE_SAMPLE_RAF` are present).
3. **UI:**
   - `NegativeLabWindow` per §4 and the MainWindow entry points.
   - `ActivityText` and `ActivityViewModel` kinds.
   - `winui-app-requirements.md` and `engine-requirements.md` entries.
4. **Review and finish**: code review, a visual finish pass against §4, and fixes.

## 6. Evidence log

Record measured results here as phases complete (numbers, sample file, darktable version).

### 2026-11-05: Engine phases 1 and 2

Measured with darktable **5.6.2 Windows ARM64**, on private copies of `DSCF6553.RAF`
(40,327,552 bytes). Neither the original nor its neighboring files were changed; existing
neighboring XMP files were hash-checked, and no new sidecar appeared.

Source SHA256: `528d28209ab18f850a778535e14f5e8742affa670aa5791886d683fed3b5104f`.
Template SHA256: `949fe0dd4c165698bdbc4b01e3a7e8193556affb9581f515492b595661c8626b`.
Basis/Proof: **1600×1066**; full-resolution 16-bit export: **7728×5152**.

All differences below use sRGB RGB absolute errors divided by 255.

| Evidence gate | Mean absolute error | 95th percentile | Result |
|---|---:|---:|---|
| Minimal positive vs untouched full `lab-matched.xmp` | 0.01191435 | 0.03529412 | **Qualified, not exact**: the original uses temperature preset 4's late camera-reference correction. |
| Minimal vs full history with equivalent pinned Light Balance | 0.00048630 | 0.00392157 | The removed camera/image modules do not materially change the equivalent pipeline. |
| Native Live Preview vs darktable Proof | 0.00125576 | 0.00784314 | **Pass**: 97.434158% of pixels have mean RGB absolute error `<3/255`, exceeding the required 95%. |
| Pinned coefficients vs As shot, linear basis | 0 | 0 (maximum also 0) | **Pass**, after selecting the isolated `none` workflow. |

The Native/Proof 95th percentile is also **0.00784314 when computed per pixel**, not just
over flattened channel errors. Pinning used ExifTool `WB_GRBLevels`, normalized from GRB:
**RGB = (433/302, 1, 776/302) = (1.4337748344, 1, 2.5695364238)**.
The original preset-4 correction also affects `colorin`; literal pinned coefficients use
temperature preset 2. That extra correction cannot be represented by the current
`LightBalance` record. XMP profile reads explicitly warn through `Light.Source`; they do
not claim to reproduce an arbitrary camera-reference history exactly.

The final Debug numeric run, including private-source copying and actual output decoding,
measured **6.143 s basis**, **6.229 s Proof**, **5.997 s full-history comparison**,
and **10.376 s full-resolution TIFF export**. Native math took **233.48 ms first call /
221.90 ms warm average** in that Debug run. Earlier quiet runs varied substantially:
**45.20 ms warm Debug**, and independent quiet **Release medians 70.99 / 67.76 ms**
at the actual 1600×1066 size. The ≤80 ms production target is met in Release, not
consistently in Debug. Optimizations retained byte-identical output; no approximate
color or density functions were introduced.
The final opt-in Release benchmark on **ARM64 / .NET 10.0.10 / 10 processors** recorded
**77.665 ms warm**, minimum **71.824 ms**, maximum **84.102 ms**, over **15 samples**,
and passed its 80 ms warm gate. BGRA SHA256:
`c0c8963017cb07bfb208c55425ed686c531e1f1a93a0fe9d91e8a5f36093de65`.

**Orientation:** omitting `flip` still applies the image default. Private RAF copies with
EXIF orientation 1 produced flags 0; orientation 6 produced flags 5 (CW90), with pixel
correspondence checked. Frame Crop is therefore expressed on the already-oriented output.

**Color-control signs:** raw log coordinates remain bijective; friendly highlight controls
reverse their signs in the normal density regime. For neutral transmission 0.3 and a
control increase of 0.2, measured sRGB8 deltas were:

| Offset | Highlights warmth RGB delta | Highlights tint | Shadows warmth | Shadows tint |
|---|---|---|---|---|
| −0.1 | (+5, 0, −3) | (+6, −5, +3) | (+1, 0, 0) | (+1, −1, +1) |
| +0.1 | (+6, −1, −5) | (+7, −6, +3) | (+1, 0, −1) | (+1, −2, +1) |

Shadow warmth increased linear positive R−B by **0.00425776** at offset −0.1 and
**0.00621549** at +0.1; zero offset produces exactly zero shadow-control effect.
Highlight derivatives reverse when `log10(transmission/base)/Dmax + offset*wb_low`
changes sign. A universal “always warmer” promise across every legal parameter set
would contradict the exact algorithm; the source-matching limits are covered by tests.
The film-stock codes in §3.1 were also corrected from the source: color **1**, B&W **0**.

**Implementation details:** JPEG and TIFF output verification includes actual decoding
(StbImageSharp 2.30.16 and BSD-style LibTiff.NET 2.4.660), not merely header inspection.
The 3 GB LRU budget covers linear bases, Proofs, and retained failed renderer workspaces.
Active renderer workspaces count toward the budget but hold a lease preventing eviction;
temporary in-flight growth is reconciled after export. Old diagnostics are disposable
under cache pressure. Publication reuses the Photo-edit JPEG tail, includes the current
Shared Photo Metadata (including RAW sidecar/pending working state), normalizes orientation
and sRGB tagging, and records provenance before recycling recorded older outputs.
Export initialization only seeds absent working settings; it never overwrites newer edits.
Clearing renditions also removes completed failed workspaces, while preserving leased workers.

The final Debug solution build passed without warnings or errors. Negative Lab's console
runner passed **48/48**, including real copied-RAF basis, Proof, and export integration.
All affected existing Engine runners, the UI/Fujifilm runners, and **5/5** architecture tests
passed. The additionally attempted Scale runner fails its combined metadata/tag/search
ordering assertion and then its pooled-SQLite cleanup; both failures reproduced on an
isolated archive of the unchanged pre-feature commit. That unrelated issue was not changed.

**Reproduction:** ordinary gated renderer integration needs only supported darktable and
`PHOTOMANAGE_NEGATIVE_SAMPLE_RAF` pointing to a private RAF copy:

```powershell
dotnet run --project .\tests\PhotoManage.Engine.NegativeConversion.Tests\PhotoManage.Engine.NegativeConversion.Tests.csproj -c Debug
```

Full numerical comparisons additionally require `PHOTOMANAGE_NEGATIVE_PYTHON` to point to
an existing Python interpreter with NumPy and Pillow; the command validates that prerequisite
before rendering and never falls back to the Microsoft Store alias:

```powershell
dotnet run --project .\tests\PhotoManage.Engine.NegativeConversion.Tests\PhotoManage.Engine.NegativeConversion.Tests.csproj -c Debug -- --evidence
```

`--benchmark-native` is an opt-in Release benchmark using
`PHOTOMANAGE_NEGATIVE_BASIS_PFM`. Successful evidence workspaces are removed; failed ones
retain diagnostics under the test project's `bin\evidence-work` directory.

### 2026-11-05: WinUI implementation and validation

`NegativeLabWindow` implements the separate Operate-mode window in §4, with concern-based
stage, inspector, tools, filmstrip, profiles, and export partials. MainWindow opens or
reuses it from the toolbar, action rail, details, and Photo context menu. Conversion,
sampling, detection, color coordinates, settings persistence, profiles, and publication
use the existing Engine surface; no Engine change was required. The Windows rendering
adapter exposes its existing edit geometry for identical Live/export crop processing.

The explicit Debug UI build and Debug solution build both completed with **0 warnings
and 0 errors**. The UI console runner passed **125 tests**, including eight Negative Lab
tests covering all 16 sync-group combinations, clipboard round trips, profiles and
first-run initialization, EV conversion, export badges, persistent coach dismissal,
latest-wins cancellation, autosave/error propagation, and Live/export crop geometry.
The Negative Lab Engine runner passed **48/48**, including real copied-RAF integration;
the Fujifilm Camera runner passed **21 tests**, and architecture tests passed **5/5**.

The actual Debug **win-x64** executable was launched with an isolated Catalog/cache and
private copies of `DSCF6553.RAF` and `DSCF6543.RAF`. Native Windows UI Automation opened
the Lab, waited for a matching darktable **5.6.2 Proof**, dragged a film-base rectangle
and confirmed it, verified that Density 0 disables shadow controls, changed tone values,
rotated the output, and exported a derived JPEG through the window. The Lab stayed open
with its Exported badge and status, and opening it again reused the same native window.
Film-base warnings wrapped within the inspector at the target window size.
The private source remained unchanged, and the
original DSCF6553 SHA256 still matched the value recorded above. Launched windows were
closed; appended startup logs contained no XAML/runtime exceptions from these operations.
The sample deliberately includes film edges and a nonuniform sampling warning: the
screen is evidence of the real tool and its honest labels, not a color-calibration claim.

Runtime logs and the **1280x800 logical-pixel** screenshot are session artifacts, not
repository fixtures:
`C:\Users\migue\.copilot\session-state\b5f98e3f-ba2b-467b-bd3c-351b1722b531\files\negative-lab-smoke\negative-lab-1280.png`.

**Implementation qualifications relative to §4:**

- Locating darktable persists `AppLocalSettings.DarktableCliPath` and explicitly asks
  for an app restart. The shared in-process Engine is not replaced underneath other
  open windows; discovery/re-probing happens on restart. The dialog also explains
  environment-variable precedence.
- The active Crop tool edits the full capture in source coordinates using the shared
  overlay. Its contextual output-orientation label and status explain that quarter-turn
  rotation and mirroring appear on leaving Crop; straighten remains visible while
  editing. Positive, Proof, and exported output apply the complete transformation.
  This avoids a second crop-coordinate model diverging from export geometry.
- The §4.2 four-second first-result target is not a new timing guarantee. Preparation
  uses the existing dimmed Preview and progress feedback; the measured §6 basis/Proof
  and Debug-versus-Release limitations still apply.

Light theme, touch, narrow overlay behavior, physical-device interaction, and every
failure-state dialog were not interactively exercised. The corresponding paths are
implemented, but the real-window smoke test is not a substitute for that coverage.

### 2026-11-05: RGB Capture Sets prototype and pass-through evidence

Implemented section 8 with opt-in RGB x3 grouping, data-based classification,
translation alignment, independent light normalization, version 3 settings, and
native positive export. White light templates and their hash remain unchanged.

**Camera-channel read:** checked darktable **release-5.6.2**
[`temperature.c`](https://github.com/darktable-org/darktable/blob/release-5.6.2/src/iop/temperature.c)
and [`colorin.c`](https://github.com/darktable-org/darktable/blob/release-5.6.2/src/iop/colorin.c).
Temperature is modversion **4**: four floats and an integer preset, with RGB
coefficients (1,1,1), unused fourth coefficient 0, and user-defined preset **2**.
Colorin is modversion **7**, a **1044-byte** parameter structure:
type, filename[512], intent, normalize, blue_mapping, type_work, filename_work[512].
Input type and working type are both **4 (linear Rec.2020)**; type_work starts at
byte **528**. Normalize/blue_mapping are zero and CLI output is LIN_REC2020.
Identical input/working/output profiles cancel the transforms, bypassing the camera
matrix. Demosaicing and spatial interpolation still run: these are reconstructed
camera-channel values, not bit-exact sensor samples.

Measured using private copies of `DSCF6553.RAF` and darktable **5.6.2**, never the
original or its sidecars. Initial default downsampling produced 21 negative camera
samples at 800x533 (range -0.007167554..1.0541956), versus 84 negatives with the
standard camera-matrix pipeline (range -0.031457663..1.3006048). RGB-only bilinear
export/warp interpolation removes that ringing; the final camera basis range was
**0.000608174..1.000001, zero negative samples**. A separate RGB cache salt prevents
reuse of White light or earlier ringing-prone bases.

An empirical red-only gain probe (1 -> 1.5, G/B unchanged) compared output against
independent channel scaling. Relative mean absolute errors were **R 0.0501%,
G 0.00869%, B 0.0328%**, all below the test's 0.1% threshold. Maximum absolute
differences were 0.0074324 / 0.0019541 / 0.0043480. This supports absence of material
camera-matrix mixing, **not exact zero cross-channel changes**: demosaic/interpolation
can remain gain-dependent.

**Copied-RAF plumbing, not color quality:** three copies of that same White light
RAF used a classification override that exists only in the evidence test. Full
camera-channel reads took 23.034 / 23.722 / 22.588 seconds; merge plus native
16-bit TIFF write/decode completed at **7728x5152**, with TIFF write/decode taking
28.402 seconds. The Windows photo-edit renderer also encoded and decoded a
**7728x5152 JPEG**. Synthetic Engine export tests separately verify normal JPEG
publication/provenance only on the anchor, current metadata, unchanged source bytes,
no metadata writes to any source, and rejection if any prepared source changes.
The real-copy test verifies original/sidecar hashes and no new neighboring files.
Original DSCF6553 SHA256 remains
`528d28209ab18f850a778535e14f5e8742affa670aa5791886d683fed3b5104f`.

**Validation:** Debug solution and explicit win-x64 UI builds passed with **0 warnings,
0 errors**. The Engine runner passed **66/66** with gates unset, then **67/67** with the
real White light gate enabled. Light Source Sample converged in three iterations
(residual 0.0000841); Print Finish (+1 EV, -50 highlights, +40 shadows) Live/Proof
mean DeltaE was 0.00164064, pixel P95 0.00524402, and native finished export decoded
at 7728x5152. This reaffirms the tested iteration-2 White light behavior.
The UI runner passed and architecture tests passed **5/5**. Synthetic RGB shifts
(0.4,-1.2), (-2.35,0.65), (5.2,3.4), and zero recovered every coordinate within
**0.1 pixel**; tests also cover grouping/classification, normalization, v3 round trips,
v1/v2 upgrades, and 16-bit precision/Live parity.

The actual Debug app opened White light (including a real Proof), switched to RGB,
showed one grouped card and the expected ambiguous-light explanation with export
disabled, and switched back. Final binaries were checked with native UI Automation
and 1280x800 screenshots; all launched windows were closed. UI fixture copies alone
were given distinct capture timestamps before Indexing so identical White light
captures would remain three Photos. No RGB classification override is available in
the production UI. Appended diagnostics contained the expected classified-set warning,
not an unhandled/XAML exception. An earlier fixture thumbnail-preparation failure is
not evidence of RGB conversion quality.

The self-contained backlight helper was exercised in the collaborative browser:
R/G/B/W produced exact primary/white backgrounds and Space stepped R -> G -> B.
Fullscreen/Wake Lock APIs were present; actual tablet Wake Lock, brightness behavior,
and fading timing in a foreground physical browser remain device checks.

Reproduce the plumbing check with `PHOTOMANAGE_NEGATIVE_SAMPLE_RAF` pointing to the
sample, a **fresh** `PHOTOMANAGE_NEGATIVE_RGB_EVIDENCE_ROOT`, and:
`dotnet run --project .\tests\PhotoManage.Engine.NegativeConversion.Tests\PhotoManage.Engine.NegativeConversion.Tests.csproj -c Debug -- --rgb-plumbing`.
Set `PHOTOMANAGE_NEGATIVE_RGB_TIFF` to its `rgb-positive.tif` and run the UI runner
for Windows JPEG encoding. Set `PHOTOMANAGE_NEGATIVE_RGB_SET` to a directory containing
exactly three real RGB RAFs to exercise classification without an override.
**No real RGB set was supplied; that gate was skipped.**

Prototype qualifications: no regrouping editor; translation-only registration;
reported residual is a spectral phase-fit proxy in basis pixels, not a measured
geometric error on real film. Existing positive/display math is reused without a
calibrated dye-to-display profile. Full export retains one selected channel at a time
(about 160 MB at 40 MP) plus the approximately 480 MB merged image; peak memory has
not been benchmarked. Saved RGB state preserves existing White light adjustments
on the anchor, but a subsequent White light edit/save replaces that anchor's RGB
working state. Physical RGB color quality, touch/light-theme and complete failure
interaction coverage remain unverified.

## 7. Iteration 2: Light Source Sample and Print Finish

User feedback after the first build: (1) set white balance by picking the **lamp light
seen through the sprocket holes / gaps between frames**; (2) an easy way to push and pull
exposure, plus **Shadows / Highlights**. Both are added without changing the darktable bridge
contract for negadoctor.

### 7.1 Light Source Sample (white balance from the backlight)

*Concept:* a region of unfiltered backlight (sprocket hole, gap between frames, holder edge).
Making it neutral is the most correct Light Balance for a scan: it removes the lamp's color
before negadoctor, so the film base and color controls only deal with the film.

- **Engine:** `ValueTask<LightSourceMeasurement> MeasureLightSourceAsync(MeasureLightSourceCommand(MediaItemId, SourceVariantId, LightBalance Current, NormalizedRect Region), IProgress<string>? , ct)`.
  - The linear basis is Rec.2020 *after* darktable's `colorin`, while Light Balance scales
    *camera* RGB before it, so a single ratio is not exact. Measure the per-channel median of
    non-clipped pixels in the region on a small basis (long edge 800), set
    `c' = c ⊙ (G/R, 1, G/B)`, normalize G = 1, re-render, re-measure. Iterate until the
    region is neutral within 0.5 % per channel, at most 4 iterations. Return the coefficients,
    the final residual and the iteration count. Cancellation-aware; reuses the basis cache.
  - Warnings (same pattern as `FilmBaseSample`): **ClippedLight** (any channel clipped in the
    capture; detect clipping robustly: plateau at the channel's image maximum, near-zero
    variance; document the method) **blocks** the measurement with the copy *"The light is
    clipped in this capture, so its color can't be measured. Pick a dimmer spot, or capture
    the next roll 1 stop darker."* **NotLight** (region not clearly brighter than the film
    base / looks like film) and **NonUniform** warn but allow use.
  - Result: `LightBalance(coefficients, Source: "Light source · DSCF6553")`. Add a pure,
    tested `NegativeAnalysis.SampleLightSource(NegativeLinearImage, NormalizedRect)` for
    the measurement statistics and warnings.
- **After a Light Balance change**, the film base is **re-measured automatically** at the
  stored `BaseSample.Region` on the new basis (or re-estimated when it was an estimate). The
  status line says *"Film base re-measured for the new light."* This replaces the
  "Re-sample film base?" prompt.
- **UI:** a new stage tool **Light** (key **L**) beside Film base, with the same
  drag-rectangle interaction. Its popover shows a swatch of the light as captured, the
  warnings, and the actions **Use for all frames** (primary, because a roll shares one lamp)
  and **This frame only**. While measuring, the popover shows "Measuring light… (2/4)".
  The inspector group is renamed **LIGHT & FILM BASE · ROLL**. Light row: a status
  (*As shot* / *Camera white balance (DSCF6553)* / *Light source ✓ (DSCF6553)*) plus
  [Pick light source] and a ⋯ with *Use camera white balance* / *Reset to as shot*. The
  first-run coach mark becomes: *"Pick the light through a sprocket hole (L), then clear
  film between frames (B)."*

### 7.2 Print Finish (Exposure, Highlights, Shadows)

*Concept:* global tone adjustments PhotoManage applies **after** negadoctor, identically in the
Live Preview, the Proof and the export. negadoctor stays the faithful print model; the Print
Finish is the easy "push/pull" layer.

- **Domain:** `NegativePrintFinish(double ExposureEv, double Highlights, double Shadows)`,
  with `ExposureEv` in [−3, +3] and the others in [−100, +100]; `Neutral = (0,0,0)`.
  `NegativeConversionSettings` gains `Finish` and becomes **Version 2**. Version 1 is read
  as Version 2 with a neutral finish; this is an explicit, tested upgrade. Unknown versions
  are still rejected. The settings hash, IsChangedSinceExport and the proof cache key
  include the finish.
- **Math:** a public pure function `NegativePrintFinishMath.Apply(...)` over **linear sRGB in
  [0,1]**, which is the space after the Live Preview's Rec.2020→sRGB clamp and after decoding
  darktable's sRGB TIFF.
  - Work on luminance Y (Rec.709 weights) in log2 space around mid-grey 0.18. The exposure
    gain is 2^EV.
  - Shadows and Highlights are smooth weights: full strength in deep shadows / bright
    highlights, fading to 0 at mid-grey. Full strength is about ±1.5 EV at ±100.
  - Then a soft shoulder above 0.8, so a push rolls off instead of hard clipping.
  - Apply as a ratio-preserving gain `RGB·Y'/Y`. If a channel exceeds 1, desaturate toward
    Y' so the hue is kept.
  - Required properties, each tested:
    - neutral settings return the input **bit-identical**, and the export skips the stage
      entirely;
    - the curve is strictly monotonic for every legal setting;
    - Exposure +1 EV doubles mid-grey;
    - Shadows at +100 does not move pixels above mid-grey by more than 2 %, and Highlights
      likewise below it;
    - zero allocation per pixel.
- **Live Preview:** `NegadoctorMath.RenderPositive` takes the finish (or a separate call
  inside the same pass). It must stay within the 80 ms Release budget at 1600 px.
- **Proof:** darktable renders the proof as a 16-bit TIFF (or the existing JPEG when the
  finish is neutral). The Engine applies the finish and writes the proof image the UI
  shows. Choose the smallest encoder dependency (for example StbImageWriteSharp) or return
  pixels; document the choice.
- **Export:** darktable renders the 16-bit sRGB TIFF. The Engine decodes it (LibTiff.NET),
  applies the finish in linear light and writes a 16-bit TIFF, which then goes to
  `IPhotoEditRenderer` unchanged. A neutral finish skips this stage.
- **UI (TONE · FRAME, top of the group):**
  - **Exposure** row: a slider (−3…+3 EV, label in stops such as "+⅔ EV"), with **−** and
    **+** buttons beside it that step ⅓ stop. Keys **[** and **]** step ⅓ stop; with Shift
    they step 1 stop.
  - **Highlights** and **Shadows** rows (−100…+100).
  - **Contrast** (paper grade) stays.
  - The remaining negadoctor controls move to a following group, **PRINT · FRAME (advanced)**:
    Print exposure, Density, Blacks, Highlight roll-off, Range. It is always visible but placed
    after Tone, followed by Exact values.
  - **Auto tone** fits the print (negadoctor) and leaves the finish alone. **Reset frame**
    resets everything.
  - Sync's "Tone" box includes the finish. A clipping overlay (J) helps when pushing.
- **Fix from the first build:** the two COLOR slider pairs get sub-headings **Highlights**
  and **Shadows**, which the screenshot showed were missing.

## 8. Prototype: RGB Capture Sets (three shots per frame)

Status: **prototype**. Keep it simple; harden later if the results justify it.

Implemented on 2026-11-05; synthetic and copied-RAW evidence is in section 6.

**Capture a set:** keep the camera/film/tablet fixed on a tripod, with manual focus,
aperture and ISO. Open `tools\rgb-backlight\index.html` on the tablet, keep brightness
constant, and disable Night Shift/color filters, adaptive brightness and auto-lock.
Shoot three consecutive RAWs per negative under R, G, then B, with no intervening
or missing shots. Adjust shutter time per color (blue may need longer); avoid clipping.
Include bare backlight through a sprocket hole and a clear-film margin. Select complete
sets, open the Lab and choose RGB x3; grouping sorts Capture Time then filename,
while detection determines the actual light order. Sample bare light first, then clear
film. Missing shots shift subsequent triplets: fix the selection and reopen, rather
than expecting automatic regrouping.

The user photographs each negative frame three times on a tablet screen showing pure red, green
and blue (tripod, same focus, manual exposure, blue usually longer). Merging the three gives
per-dye-layer transmission, like a film scanner. This avoids most camera color crosstalk.

**UI**
- The Lab gets a capture-mode toggle in the title row: **White light | RGB ×3**. It is
  persisted in `AppLocalSettings`. White light is today's behavior, unchanged.
- In RGB ×3 mode:
  - The selected Photos are sorted by capture time (then file name) and grouped into
    consecutive triplets. Each triplet is **one frame** in the filmstrip, labeled
    `DSCF7001–7003`, with a small `RGB` badge.
  - Each shot's color is detected from its own data: the dominant camera channel, with a
    ratio ≥ 2 over the others. Frames with an incomplete or ambiguous triplet get ⚠ and an
    explanation and cannot be exported. There is no manual regrouping UI yet.
  - Frame state lines show the detected mapping and alignment shifts, for example
    `R DSCF7001 · G DSCF7002 · B DSCF7003 · aligned (R +0.4, −1.2 px; B …)`.
  - The **Light tool** samples the bare light (sprocket hole) in the merged image. Each
    channel is then divided by its own light value; no iterative loop is needed. Camera
    white balance and Light Balance options are hidden in this mode.
  - Film base, Color, Tone, Print Finish, Crop, Sync and Export all work unchanged.
  - The render chip shows **Live (RGB)** and there is no darktable Proof in this mode.

**Engine**
- Read each shot through darktable **without color processing**: temperature coefficients
  (1,1,1) and a pass-through input profile (verify colorin's params and modversion in
  release-5.6.2), output as linear PFM. Take the channel matching that shot's light. If true
  pass-through is not achievable, document what was used and its effect.
- Alignment: translation-only sub-pixel phase correlation of R and B against G, computed
  at basis resolution and scaled for full resolution, then bilinear resampling. Report the
  shifts and a quality score, and warn above 0.5 px of residual misalignment.
- Normalization: divide by the light-sample value per channel. Without a light sample,
  use each channel's robust 99.9th percentile and label it **Estimated**.
- The merged result is a `NegativeLinearImage`, so `NegadoctorMath`, `NegativeAnalysis`
  and the Print Finish work as-is.
- **Export in RGB mode does not use darktable for the positive.** It renders full
  resolution with the native `NegadoctorMath` (parity with darktable is proven in §6) plus
  the Print Finish, writes a 16-bit sRGB TIFF, then goes through the usual
  `IPhotoEditRenderer` and publication path. The output is a Derived Photo Variant of the
  **first shot's** Photo (the anchor). Memory: stream one channel per shot (about 160 MB
  each at 7728×5152).
- Settings: Version 3 adds an optional
  `RgbCapture(Guid Red, Guid Green, Guid Blue, alignment, light sample)` stored on the
  anchor. Versions 1 and 2 upgrade on read.

**Capture helper**
- `tools\rgb-backlight\index.html` is a self-contained full-screen page for the tablet.
  Keys **R/G/B/W** show a color; **Space** steps R→G→B. It shows no UI chrome and keeps
  the screen on where the browser allows it.

**Tests**
- Synthetic coverage: triplet grouping and color classification, phase-correlation shift
  recovery, normalization, settings v3 round trip and upgrade, and RGB export with a fake
  renderer. The source RAWs must never be written.
- A real gated test runs when `PHOTOMANAGE_NEGATIVE_RGB_SET` points to a folder with
  three RAFs.
