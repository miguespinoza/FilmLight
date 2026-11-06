# darktable-cli + negadoctor processing investigation

Date: 2026-11-06
Baseline: darktable **5.6.2** (tag `release-5.6.2`, commit `20891f6c6fa7be995e5bf9dff9d1ee5062051773`, released 2026-10-04)
Scope: Can the downloaded X-T5 scan transactions (`Scan-<id>`, white or R/G/B RAF/JPG) be turned headlessly into Lightroom-editable positive masters + JPG with `darktable-cli` and `negadoctor`, after a light-only calibration?
Method: Only official darktable docs, source code at the release tag, release/distribution pages, and first-party Fujifilm/Adobe pages. Nothing was installed or executed, and no private SDK material was shared. Line numbers are approximate and refer to the tag.

## Decision summary

1. **Use the white-light single-frame path with darktable.** `darktable-cli` + negadoctor can batch-convert X-T5 RAFs headlessly. It **needs a pre-built XMP sidecar or style** that already holds the negadoctor and white-balance parameters. The CLI does no film-base, Dmin, or scene auto-calibration. [fact: S3, S6]
2. **Calibration is a one-time GUI step per roll or stock.** In the darktable GUI, do white balance from the light-only frame, then use negadoctor's pickers for Dmin (unexposed base) and D_max/offset/WB (scene). Save the result as an XMP template or a style and reuse it from the CLI. The app could write negadoctor params itself, but that is an unproven proposal tied to darktable's version-specific XMP param encoding. [fact + proposal]
3. **darktable cannot assemble R/G/B sequential frames.** "Create HDR" is a weighted *average* of same-scene raws into a single CFA DNG. It is GUI-only and not a channel assembler. `darktable-cli` has no merge option. RGB mode would need an external reconstruction step that writes a linear float RGB TIFF/EXR, which negadoctor can then read. [fact: S8, S9; reconstruction = proposal]
4. **Master format: 16-bit TIFF in a wide profile is the Lightroom-safe default.** 32-bit float TIFF also imports into Lightroom Classic. Both are **rendered, display-referred positives**: inversion, print gamma, and soft clip are baked in. Neither is a raw or reversible conversion. darktable has **no DNG export**. [fact: S12, S13, S16]
5. **Don't use lossy-compressed RAF.** darktable lists "Fujifilm lossy RAFs" as unsupported. Set the X-T5 to UNCOMPRESSED or LOSSLESS COMPRESSED. [fact: S14, S15]
6. **Both Windows x64 and Windows-on-ARM64 installers exist** (5.6.0+). The ARM64 build targets Windows 11. The ARM64 WinUI app could call a native ARM64 `darktable-cli.exe`. [fact: S1, S2, S17]
7. **Delete negative captures only after validation, and keep the calibration bundle.** Keep the light-only frame(s), base frame, XMP/style, darktable version/checksum, and `--verbose` history log. A rendered positive cannot be re-inverted with different choices later.

Evidence labels: **fact** = verified in the cited primary source; **inference** = derived from source/docs but not executed; **proposal** = design suggestion for this app; **unverified** = needs a local or hardware test.

## Citation key

| Key | Source |
|---|---|
| S1 | darktable 5.6.2 release notes + SHA-256: https://github.com/darktable-org/darktable/releases/tag/release-5.6.2 |
| S2 | darktable install page: https://www.darktable.org/install/ |
| S3 | darktable-cli manual 5.6: https://docs.darktable.org/usermanual/5.6/en/special-topics/program-invocation/darktable-cli/ |
| S4 | CLI source: `src/cli/main.c` @ `release-5.6.2` — https://github.com/darktable-org/darktable/blob/release-5.6.2/src/cli/main.c |
| S5 | negadoctor manual: https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/negadoctor/ |
| S6 | negadoctor source: `src/iop/negadoctor.c` @ `release-5.6.2` |
| S7 | white balance source: `src/iop/temperature.c` @ `release-5.6.2` |
| S8 | lighttable "selected image(s) → create HDR": https://docs.darktable.org/usermanual/5.6/en/module-reference/utility-modules/lighttable/selected-image/ |
| S9 | HDR merge source: `src/control/jobs/control_jobs.c` `_control_merge_hdr_process` @ `release-5.6.2` (~L468–632) |
| S10 | composite module: https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/composite/ ; source `src/iop/overlay.c` @ `release-5.6.2` |
| S11 | blend modes: https://docs.darktable.org/usermanual/5.6/en/darkroom/masking-and-blending/blend-modes/ |
| S12 | export conversion: `src/imageio/imageio.c` @ `release-5.6.2` (~L1449–1460); `src/imageio/format/tiff.c`, `exr.cc`; format list `src/imageio/format/` |
| S13 | supported input formats: https://docs.darktable.org/usermanual/5.6/en/overview/supported-file-formats/ |
| S14 | camera support (5.6.2): https://www.darktable.org/resources/camera-support/ ; RawSpeed `data/cameras.xml` (https://github.com/darktable-org/rawspeed/blob/develop/data/cameras.xml) |
| S15 | Fujifilm X-T5 manual, RAW RECORDING: https://fujifilm-dsc.com/en/manual/x-t5/menu_shooting/image_quality_setting/index.html |
| S16 | Adobe Lightroom Classic supported formats (updated 2026-04-16): https://helpx.adobe.com/lightroom-classic/desktop/introduction-to-lightroom-classic/supported-file-formats.html |
| S17 | Packaging: `cmake/darktable-packaging.cmake`, `packaging/windows/darktable.iss.in`, `src/cli/CMakeLists.txt` @ `release-5.6.2`; `.github/workflows/nightly.yml` (master) |
| S18 | darktable program invocation (`--core` options): https://docs.darktable.org/usermanual/5.6/en/special-topics/program-invocation/darktable/ ; OpenCL: https://docs.darktable.org/usermanual/5.6/en/special-topics/opencl/activate-opencl/ |
| S19 | master commit `18c3f67ea240f78f866e7e87d3254aa1fb37c46b` (2026-11-01) "eaw, negadoctor: compute exp() accurately on the CPU" |

## 1. darktable-cli usage (5.6.2)

**Syntax (fact, S3/S4):**

```
darktable-cli <input file|folder> [<xmp file>] <output file|folder>
  [--width N] [--height N] [--hq 0|1] [--upscale 0|1]
  [--style <name>] [--style-overwrite] [--apply-custom-presets 0|1]
  [--out-ext <ext>] [--import <file|dir>] [--library <db>]
  [--icc-type <type>] [--icc-file <file>] [--icc-intent <intent>]
  [--verbose] [--core <darktable core options>]
```

Behaviors that matter here (fact, S4):

- Unless `--library` (new in 5.6.0) is given, the CLI runs with an **in-memory library** (`--library :memory:`) and `--conf write_sidecar_files=never`. Inputs and XMPs are never modified.
- An explicit `<xmp>` argument is read with `dt_exif_xmp_read()`. Without one, `<input>.xmp` is auto-detected. If no XMP and no library are found, it prints "no XMP sidecar file nor library path provided, using default settings". **Default settings do not include negadoctor**, so the output would be an unconverted negative.
- `--style` needs `--core --configdir <dir>`, because styles live in that directory's `data.db`. Styles **append** to the XMP history by default, and `--style-overwrite` replaces it. `--apply-custom-presets 0` allows multiple module instances but **disables `--style`**.
- If the output file already exists, it is **not overwritten**; the new file gets a uniquified name. The app should write to a fresh folder or delete before rerunning.
- `--verbose` prints the history stack applied to the first image. Capture this output as the audit log.
- `--bpp` is documented as unsupported. Bit depth and compression are set with `--core --conf`:
  - TIFF: `plugins/imageio/format/tiff/bpp` = 8|16|32. Other TIFF keys: `compress` = 0 none / 1 deflate / 2 deflate+predictor (float predictor for 32-bit), plus `compresslevel` and `shortfile`. `shortfile` writes grayscale only if the channels are nearly equal.
  - EXR: `plugins/imageio/format/exr/bpp` = 16|32, `compression` = 0–8.
  - JPEG: `plugins/imageio/format/jpeg/quality` = 5–100.
- `--icc-type`: `NONE` (default, meaning "use the history's output color profile"), `FILE`, `SRGB`, `ADOBERGB`, `LIN_REC709`, `LIN_REC2020`, `REC709`, `PROPHOTO_RGB`, `DISPLAY_P3`, `XYZ`, `LAB`, and others. `--icc-intent`: `PERCEPTUAL` | `RELATIVE_COLORIMETRIC` | `SATURATION` | `ABSOLUTE_COLORIMETRIC`. TIFF output embeds the ICC profile (S12).
- `--core --disable-opencl` forces the CPU path (S18).

**Command shapes (proposal; flags verified against S3/S4, not executed).** `<DT>` is the pinned darktable install. `<cfg>` is a private, app-owned config dir.

```powershell
# 16-bit TIFF master, ProPhoto, deflate+predictor, explicit template XMP
& "<DT>\bin\darktable-cli.exe" "Scan-123\frame.RAF" "templates\roll42-negadoctor.xmp" "out\Scan-123\frame.tif" `
  --icc-type PROPHOTO_RGB --icc-intent RELATIVE_COLORIMETRIC --verbose `
  --core --configdir "<cfg>" --disable-opencl `
  --conf plugins/imageio/format/tiff/bpp=16 --conf plugins/imageio/format/tiff/compress=2

# 32-bit float TIFF (Lightroom Classic imports 32-bit TIFF)
#   same, with: --conf plugins/imageio/format/tiff/bpp=32

# Delivery JPG, sRGB, same template
& "<DT>\bin\darktable-cli.exe" "Scan-123\frame.RAF" "templates\roll42-negadoctor.xmp" "out\Scan-123\frame.jpg" `
  --icc-type SRGB --core --configdir "<cfg>" --disable-opencl --conf plugins/imageio/format/jpeg/quality=95

# Style variant (style must exist in <cfg>\data.db; replaces history)
& "<DT>\bin\darktable-cli.exe" "Scan-123\frame.RAF" "out\Scan-123\frame.tif" `
  --style "roll42-negadoctor" --style-overwrite --core --configdir "<cfg>" --disable-opencl `
  --conf plugins/imageio/format/tiff/bpp=16
```

A template XMP whose history includes **crop** applies the same crop to every frame. Keep framing out of the template, or use a per-frame XMP (proposal).

## 2. negadoctor: what it does, and what the CLI cannot do

**Model (fact, S5/S6).** Params v2 (`DT_MODULE_INTROSPECTION(2, …)`):
- `film_stock` {black and white = 0, color = 1}
- `Dmin[4]`, `wb_high[4]`, `wb_low[4]`
- `D_max`, `offset`, `black`, `gamma`, `soft_clip`, `exposure`

The pipeline is Cineon-like:

1. density = log10(Dmin / max(in, 2⁻³²))
2. corrected by `wb_high`/`wb_low`/`offset`/`D_max`
3. print curve (`black`, `exposure`)
4. `gamma`
5. highlight `soft_clip`

Module flags include `INCLUDE_IN_STYLES` and `ONE_INSTANCE`. Built-in presets are "color film" (Dmin 1.13/0.49/0.27, D_max 1.6, gamma 4) and "black and white film" (Dmin 1, D_max 2.2, gamma 5). Input is linear scene RGB; output is non-linear and display-referred (S5).

**No CLI auto-calibration (fact, S6).** Every "auto" in negadoctor is a **GUI colour-picker callback**: `apply_auto_Dmin`, `apply_auto_Dmax`, `apply_auto_offset`, `apply_auto_WB_low`, `apply_auto_WB_high`, `apply_auto_black`, `apply_auto_exposure` (~L635–805, dispatched from `color_picker_apply`). Each one writes the measured values into the params/history. `process()` only applies the stored params. The CLI has no pickers, so:

- Without a negadoctor XMP or style, the CLI does **no** inversion.
- With a template, it applies the **template's fixed** Dmin/D_max/WB to every frame. It does **not** re-measure the film base or each frame's scene range.

**Recommended calibration recipe (fact = manual guidance S5; sequencing = proposal):**

1. Shoot the light-only frame at the capture settings, with exposure ETTR on the light and no clipping. In the GUI, set white balance "from image area" on it (S5), so the white illuminant becomes neutral.
2. Shoot or include unexposed film base (the leader or the rebate between frames). Use the negadoctor Dmin picker on it. The manual explicitly allows measuring Dmin on another frame and pasting it.
3. On a representative frame, set D_max/offset, then the print WB/black/exposure pickers. Use the input profile's standard matrix and the linear Rec2020 working profile. Disable filmic, sigmoid, and base curve per S5, which avoids double tone mapping.
4. Save the result as a style (in the app's `<cfg>`) or an XMP template, and use it for the roll. Allow per-frame overrides (exposure/black) as a later GUI pass in Lightroom on the 16-bit master. That is cheaper than per-frame darktable edits.

**White balance determinism (fact, S7; recommendation = inference).** The temperature module stores coefficients in params. Its modes are as-shot, spot, user, camera-reference D65, and "as shot to reference" (`D65_LATE`). `D65_LATE` lets color calibration depend on each image's as-shot chroma. For a fixed light source, use **spot/user WB from the light-only frame**, with color calibration either disabled or in a fixed (non-as-shot-adaptive) mode. Camera-controlled auto WB on the X-T5 is irrelevant to the RAF data, but it does change the "as shot" metadata. The XMP records `auto_presets_applied`, so a GUI-created template should block darktable's default auto-applied presets. Confirm with `--verbose` (unverified).

## 3. Film types

| Material | darktable handling | Status |
|---|---|---|
| Color negative (C-41) | negadoctor `film_stock = color` with Dmin from the orange base, per-channel WB high/low | fact (S5/S6) |
| B&W negative | negadoctor `film_stock = black and white`. Uses `Dmin[0]` for all channels, and `wb_high` remains for toning. Monochrome capture still goes through demosaic; optional `shortfile` grayscale TIFF output | fact (S6, S12) |
| Positive / reversal (E-6, slides) | **No negadoctor slide mode** (film_stock has only B&W/color). Treat as a normal positive raw: fixed WB from the light-only frame, exposure, and a scene→display transform (sigmoid/filmic/AgX), with no inversion | fact (no mode); recipe = proposal |

## 4. RAF / X-T5 support

- **Supported (fact, S14).** The camera-support page (based on 5.6.2) lists the Fujifilm X-T5 with the RawSpeed decoder, white-balance presets, and a noise profile. RawSpeed `cameras.xml` has X-T5 entries for the default and `mode="compressed"` RAF (6×6 X-Trans CFA, black 1020, white 16383).
- **Lossy RAF is unsupported (fact, S1, S14).** Both the 5.6.0 and 5.6.2 notes list "Fujifilm lossy RAFs" as unsupported. The X-T5 offers UNCOMPRESSED, LOSSLESS COMPRESSED, and COMPRESSED (lossy) (S15). **Lock RAW RECORDING to uncompressed or lossless** and validate it in the app (proposal). Also prefer 14-bit/full-resolution RAF; HR/pixel-shift is a separate, unrelated path.
- JPEGs from the camera are already rendered (tone curve, film simulation, 8-bit). They are useful for preview only, not for negadoctor input (inference).

## 5. Windows distribution (x64 / ARM64)

- **Fact (S1, S2):** 5.6.2 ships `darktable-5.6.2-win64.exe` (SHA-256 `7f7a1eabcd0a1b548dcfacac842c8203fc85a4175697fb09f90bb891c8931edd`) and `darktable-5.6.2-woa64.exe` "Microsoft Windows on ARM" (SHA-256 `bcb700de8275f894c29ae4ccb5f600412b269bef1a581eecce9bbed697421fcc`). woa64 first appears with 5.6.0; 5.4.0 had none.
- **Fact (S17):** The project's nightly workflow states that win64 needs Windows 10+ and woa64 needs Windows 11+. `darktable-cli` is installed into `bin\` with the main application component, so it ships in the standard installer. The CMake ARM64 path notes "Native NSIS is currently unavailable", and the release uses an Inno Setup script (`darktable.iss.in`).
- **Unverified:** the exact installed path, whether a silent/per-user install is supported by the Inno script, and OpenCL availability on ARM64 GPUs. That last one doesn't matter if `--disable-opencl` is used.
- **Proposal:** Don't bundle darktable (GPL-3.0) into the app. Have the user install the pinned version, detect it, verify the version (`darktable-cli --version`) and the installer checksum, and invoke it as a separate process. The app's x64 SDK helper is unaffected because darktable runs as its own process for either architecture.

## 6. RGB sequential capture: not supported natively

**HDR merge is not channel assembly (fact, S8, S9).** "Create HDR" is a lighttable GUI action that only accepts raws. `_control_merge_hdr_process` requires mosaiced `uint16` raws of identical size and orientation. It normalizes each photosite by EXIF exposure/aperture/ISO and accumulates a **weighted average** (photon-count weighting with a saturation envelope) into one CFA float DNG (`…-hdr.dng`). Fed R-, G-, and B-lit frames, it would average three differently lit exposures of the same CFA. It would not put R in R, G in G, and B in B. `darktable-cli` exposes no HDR or merge option (S3/S4).

**The composite + channel-blend workaround is not viable (fact + inference, S10, S11).** The `composite` module overlays another **library image**, referenced by `imgid` and full path. It is set by drag-and-drop from the filmstrip and rendered via `dt_dev_image()` into an **8-bit** ARGB Cairo buffer (`uint8_t` cache). The manual also states it is imported at 8 bits. Blend modes do include "RGB red/green/blue channel". However, the 8-bit overlay, the library-ID dependence, and the CLI's in-memory library together make this unusable for calibrated masters (inference).

**darktable has no flat-field module** in the 5.6 module reference (https://docs.darktable.org/usermanual/5.6/en/module-reference/processing-modules/). Lens-correction vignetting is a profile-based lens model, not a per-pixel illumination map (inference). Per-pixel light-only normalization is therefore also external.

**External reconstruction requirements (proposal/inference):**

1. Use RAF linear data, not the JPEG. Do black-level subtraction (X-T5 black 1020 per RawSpeed, but read it per file) and saturation checks.
2. Shoot each channel at a **fixed manual exposure** (shutter/ISO/aperture/focus locked across the set). The current *camera-controlled* exposure varies between R/G/B frames and must either be recorded and normalized exactly, or replaced with locked exposure for RGB mode.
3. Take light-only flat fields per illuminant, at the same settings. Transmission per channel is T_c = (S_c − black) / (L_c − black), or normalize to the film base so Dmin ≈ 1.
4. Choose a camera channel per illuminant: the R channel from the red frame, and so on, or a calibrated weighted sum. Remove crosstalk (camera channel overlap, and broad emitter spectra vs. dye absorbance) with a measured 3×3 matrix. Density-based scanning standards such as ISO 5-3 (Status M) define narrowband density responses; cite as context only.
5. Fix registration (sub-pixel shifts, longitudinal CA / per-wavelength focus and magnification) and illuminant temporal stability (PWM/flicker, warm-up).
6. Write a **linear, 3-channel float TIFF (32-bit) or EXR** with a declared linear profile. darktable reads 16/32-bit float TIFF and EXR (S13). Then run it through negadoctor using a template built on a reconstructed light-only/base sample, with the input profile set to that declared linear profile (unverified: input-profile handling for such TIFFs must be tested).
7. This step is an app/tool responsibility, not darktable's. Reconstruction quality must be validated against the white-light path before trusting RGB mode.

## 7. Export precision and Lightroom interoperability

**What darktable writes (fact, S12):**
- Formats in 5.6.2: avif, copy, exr, heif, j2k, jpeg, jxl, pdf, pfm, png, ppm, tiff, webp, xcf. There is **no DNG export**.
- 16-bit integer output = `round(clamp(v·65535, 0, 65535))`: values outside [0,1] after output-profile conversion are clipped.
- Float output (TIFF 32-bit, half-float, EXR) is written **unclamped**, with `SAMPLEFORMAT_IEEEFP`. The ICC profile is embedded in TIFF.

**What Lightroom Classic reads (fact, S16):** 8/16/32-bit TIFF, and DNG including 32-bit, up to 65,000 px per side or 512 MP. Lightroom's own TIFF export is 8/16-bit only. EXR is **not listed**; that is an absence, not an explicit exclusion.

**Edit headroom vs. reversibility (inference):**
- negadoctor's output is already a positive "print": clamped at 0 and soft-clipped asymptotically toward 1. A float master therefore gains **precision and out-of-gamut values after profile conversion, not real highlight headroom**.
- A **16-bit TIFF in ProPhoto RGB** (or linear Rec2020 via `--icc-type`) gives ample grading room in Lightroom and the broadest compatibility. 32-bit float TIFF is a valid option if the larger files are acceptable; the app should test Lightroom Classic's handling.
- Neither is "raw-like" or reversible. Inversion, Dmin/WB, print gamma, and soft clip are permanent. Lightroom treats the TIFF as a rendered image, so camera profiles and raw WB are not available.
- If re-inversion flexibility matters later, the **negative RAF + calibration bundle is the only true master**. Deleting it is the trade-off the workflow is choosing.

**Recommended outputs (proposal):** `frame.tif` (16-bit, ProPhoto, deflate+predictor) as the Lightroom master, and `frame.jpg` (sRGB, q≈95) for delivery. Keep `frame.exr` or a 32-bit TIFF only if a downstream need is proven.

## 8. Deterministic, versioned pipeline

Proposals, grounded in the facts cited:

- **Pin the binary.** Record the darktable version string, the installer SHA-256 (S1), and the architecture (win64/woa64). Refuse to run on a mismatch, or re-validate.
- **Private config dir.** Always pass `--core --configdir <app-owned dir>` so user darktablerc/styles/presets cannot leak in. Explicitly set every `--conf` key used for export.
- **Explicit XMP per run.** Pass the template path. Don't rely on `<input>.xmp` auto-detection, which a stray sidecar could hijack (S4). Store the template next to the outputs with its hash.
- **CPU only.** `--disable-opencl`. darktable states CPU/GPU results are identical "except for some rounding errors" (S18). Before commit S19, negadoctor's soft clip measurably differed between CPU and OpenCL (≈1.5 M pixels, max ΔE 4.6 on a test image).
- **Version upgrades change pixels.** S19 (master, post-5.6.2, expected in 5.8) changes **negadoctor CPU output above the soft-clip threshold**. Module param versions can also migrate XMPs. On upgrade, re-run a golden-frame regression test and version the templates.
- **Audit log.** Save `--verbose` output (the applied history stack) and the exact command line per transaction.
- **Order-independent naming.** The CLI uniquifies names instead of overwriting, so write to a fresh, transaction-scoped folder and verify the expected filenames exist.
- **Avoid adaptive modules in the template.** Use no as-shot-dependent WB/color calibration, no auto-applied presets, and no exposure "compensate camera exposure". Camera-controlled exposure varies per frame, so per-frame brightness differences will pass through to the positives unless exposure is locked or normalized. That is fine for white-light scans with Lightroom grading, but it must be acknowledged (inference).

## 9. Disposable negative captures: gates before deletion

Proposed validation before deleting RAF/JPG:

1. darktable-cli exit code is 0, the outputs exist, and the TIFF has the expected dimensions, bit depth, embedded ICC, and 3 channels (or grayscale by design).
2. Clipping stats: fraction of pixels at 0 / 65535 is below a threshold; there is no all-black or all-white frame (catches a missing negadoctor).
3. `--verbose` history contains `negadoctor` and the expected template hash.
4. A thumbnail review in the app or Lightroom.
5. **Always retain** the light-only frame(s), the film-base frame, the template XMP/style, the darktable version/checksum, and the command log per roll. They are small and make the run reproducible for the kept masters.

## 10. Proven vs. proposal

| Claim | Status |
|---|---|
| darktable-cli applies an XMP/style headlessly; exports TIFF 8/16/32, EXR, JPEG with ICC selection | **Proven** (S3, S4, S12) |
| negadoctor auto-measurements happen only via GUI pickers; CLI applies stored params | **Proven** (S6) |
| negadoctor has color and B&W modes; no reversal mode | **Proven** (S6) |
| X-T5 uncompressed/lossless RAF supported; lossy RAF unsupported | **Proven** (S1, S14, S15) |
| Official win64 + woa64 installers include darktable-cli; woa64 needs Win11 | **Proven** (S1, S2, S17) |
| HDR merge = weighted average to CFA DNG; no CLI merge; no RGB channel assembly | **Proven** (S8, S9, S3) |
| No DNG export; 16-bit export clamps, float doesn't | **Proven** (S12) |
| Lightroom Classic imports 16/32-bit TIFF; EXR not listed | **Proven** (S16) |
| GUI template → reusable for a whole roll with fixed light source | Proposal (manual-backed, S5) |
| App-generated negadoctor params in XMP without GUI | Proposal; version-fragile |
| External RGB reconstruction → linear float TIFF/EXR → negadoctor | Proposal; unverified |
| `--disable-opencl` + pinned version gives bit-identical reruns | Inference; verify with golden-frame hash test |
| Exact install path, silent install, ARM64 runtime behavior | Unverified (no install performed) |

## Open gates (hardware / local validation)

1. Install the pinned 5.6.2 (x64 and/or woa64) and confirm the `darktable-cli.exe` path, `--version`, and that the commands in §1 run as written.
2. Build one GUI template from a real light-only + base + scene set. Run it via CLI twice and compare hashes, CPU only.
3. Confirm the template blocks auto-presets and as-shot-adaptive modules (inspect `--verbose`).
4. Test 16-bit vs 32-bit TIFF import and editing in Lightroom Classic.
5. RGB mode: prototype the reconstruction offline and compare it with the white-light path on the same frame before any app work.
