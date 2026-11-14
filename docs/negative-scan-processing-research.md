# Local negative-scan conversion: Fujifilm RAF and darktable

Research checked **2026-10-24**. Scope: external primary sources only; no repository integration investigation or executable/image-quality evaluation. Development-branch source describes current implementation, not necessarily a released binary.

## Recommendation

**Updated input assumption:** a Fujifilm camera RAW (`.RAF`) photograph of a film negative, not primarily a scanner TIFF.

For this input, first validate an **optional, locally installed darktable external-process backend**: darktable handles supported RAF decoding/demosaicing and color input processing, negadoctor performs the negative conversion, and `darktable-cli` renders the resulting positive from a darktable-generated XMP history. No GPL module needs to be embedded or translated to prove this workflow. This recommendation is conditional on the exact camera/compression mode, installed CLI, and a reproducible calibration workflow. [1,9–12]

Retain an independently implemented float density core as an **alternative** if external-app installation or darktable-controlled recipe authoring is unacceptable. Magick.NET remains infrastructure, not a proven RAF/film conversion solution. That alternative also requires its own validated RAW decoding path.

**No camera, hardware, installer, executable, or image-quality execution was performed.** Commands below are verified against official documentation, not tested invocations. There is no claim of automatic calibration, interactive performance, or matching photographic output.

## RAF feasibility and Windows availability

- darktable's camera table, generated from **5.6.2**, lists Fujifilm models including X-T3, X-T5, X-H2, X-H2S, X-Pro3, and X100VI with RawSpeed decoding. This supports an X-Trans-camera workflow, not a promise that every Fujifilm model or every RAF encoding works. **Fujifilm lossy RAF is explicitly unsupported**; inspect the exact camera and compression mode, then try a real file. Missing WB presets or noise profiles are distinct from missing basic decoder support. [11]
- negadoctor consumes **linear RGB after input profiling**, not the sensor mosaic. Its inversion therefore follows darktable's RAF decoding/demosaicing rather than requiring PhotoManage to implement X-Trans demosaicing first. The user's proposed route is technically supported by these documented stages; success for the user's unprovided camera file is still unverified. [1,2,11]
- The official release API reported **5.6.2** as latest on this check. Its official assets include **`darktable-5.6.2-win64.exe` (Windows x64)** and **`darktable-5.6.2-woa64.exe` (Windows on ARM64)**. ARM64 is therefore not merely an assumed emulation route. The separate `arm64.dmg` is macOS, not Windows. [12]
- Package availability does **not** prove that `darktable-cli.exe` is installed at a particular path or works on the target machine. Discover the actual executable, inspect its help/version, and smoke-test a RAF. Do not hard-code a guessed install directory or require x64 emulation when a native ARM64 package is available.

## Documented external-process invocation

The stable **5.6** CLI manual specifies:

```text
darktable-cli <input file> [<xmp file>] <output file>
              [--width <max width>] [--height <max height>]
              [--hq <true|false>] [--upscale <true|false>]
              [--style <style name>] [--style-overwrite]
              [--apply-custom-presets <true|false>]
              [--core <darktable options>]
```

The middle positional argument is the XMP history, **not an output sidecar**. If omitted, darktable searches for an XMP belonging to the input. `--library <path>` **before** `--core` instead selects a library as the history source; it is not the same role as the core library-isolation option. Everything after `--core` is interpreted as a darktable core option. [9,10]

### Full-resolution positive, 16-bit TIFF

Illustrative PowerShell invocation; `$cli` must already contain the discovered absolute executable path. Create the three job directories first; paths below are examples, not files created during research. The XMP must have been authored in the pinned darktable version with negadoctor enabled, the desired crop, and an explicit output profile.

```powershell
& $cli '.\scan.RAF' '.\recipes\scan.RAF.xmp' '.\positive.tif' `
    --hq true --upscale false --apply-custom-presets false `
    --core --configdir '.\dt-job\config' `
    --cachedir '.\dt-job\cache' --tmpdir '.\dt-job\work' `
    --library ':memory:' --disable-opencl `
    --conf 'plugins/imageio/format/tiff/bpp=16'
```

Omitting width/height requests no CLI downscale cap: export the full processed frame, subject to crop/orientation and the recipe. Validate actual output dimensions. TIFF bit depth is explicitly set rather than inherited from the last GUI export setting. `--disable-opencl` is a conservative optional CPU-first smoke-test choice, not a performance recommendation. [9,10]

### Downscaled converted preview

Use the **same RAF and XMP**, but a different output and an explicit size bound:

```powershell
& $cli '.\scan.RAF' '.\recipes\scan.RAF.xmp' '.\preview.png' `
    --width 1600 --height 1600 --hq true --upscale false `
    --apply-custom-presets false `
    --core --configdir '.\dt-job\config' `
    --cachedir '.\dt-job\cache' --tmpdir '.\dt-job\work' `
    --library ':memory:' --disable-opencl `
    --conf 'plugins/imageio/format/png/bpp=16'
```

These bounds constrain the **rendered positive**, not extraction of the embedded camera JPEG. They do not establish cheap incremental rendering or real-time slider latency: each CLI launch is a new export. Start serialized; isolate directories per concurrent job. For output profile overrides, the documented CLI provides `--icc-type`, `--icc-file`, and `--icc-intent`; query `--help icc-type` / `--help icc-intent` in the pinned installation instead of inventing enum values. [9]

### Isolation and styles

- `--core --configdir ...` separates configuration from the user's normal darktable setup; core `--library ':memory:'` avoids retaining an image library. Config/cache/work paths should be dedicated application/job locations. This is **not a filesystem sandbox** or proof that no sidecar gets written: test side effects on a staged RAF copy first. Never overwrite PhotoManage-owned metadata sidecars with a darktable processing recipe. [10,13]
- `--apply-custom-presets false` avoids loading `data.db`, which the manual explicitly associates with concurrent CLI operation. It also makes `--style` unavailable. An XMP-driven first implementation avoids this style database dependency. [9]
- A style invocation uses `--style 'installed style name'` **before** `--core`, plus `--core --configdir '<dedicated style configuration>'`. This is a name in that configuration, **not a `.dtstyle` filename**. Ensure the style has been imported there using darktable, enable custom presets, and serialize access initially. By default styles append to history; `--style-overwrite` replaces it and must only be used with a deliberately complete style. [9]

## Reproducible negadoctor settings: supported boundary and blocker

1. **First supported recipe-authoring method:** open the RAF in the pinned darktable GUI, set illumination WB and camera/input profile, choose the recommended linear working space, disable conflicting tone modules, sample clean unexposed film base, then tune negadoctor density/cast/print controls. Save/retain darktable's XMP history and any external profile dependencies. Replay that XMP through the same-version CLI. A separate unexposed film reference is documented when the border is unavailable. [1,9,13]
2. Record the executable version, camera/model and compression mode, XMP content/hash, profile files, explicit export settings, and calibration provenance. A roll recipe can be a useful starting point only under compatible capture/development conditions; it is not a universal stock profile.
3. **There are no documented `--negadoctor-dmin`/density-slider CLI flags in the examined interface.** `--conf` exports format configuration; it is not documented as a generic image-module parameter API. The supported public route here is darktable-generated history/style, not arbitrary numeric parameter injection. [9]
4. Release source stores `darktable:params` as an encoded **binary blob**, alongside `darktable:modversion`, operation name, blending parameters, and other history data. This is not a stable JSON-style control surface. The examined negadoctor source also contains parameter-version migration code. Hand-constructing or byte-patching those blobs is a version-coupled adapter project, **not a verified supported API**; do not present it as an already solved mapping from native PhotoManage sliders. [2,14]
5. Pin darktable and recipe authoring versions together and regression-test upgrades. Release 5.6.2 warns that updated configuration/library data cannot be reused with 5.4. Backward migration code does not promise byte-for-byte output stability across versions. Keep dedicated configuration backups and render fixtures. [12,14]

**Consequent MVP boundary:** an “open/calibrate in darktable, then render/apply saved recipe” workflow is feasible from documented interfaces. Fully native interactive film-base picking and per-slider darktable updates remain **unimplemented technical hypotheses** until a supported authoring bridge or explicitly maintained version-pinned serialization adapter is demonstrated.

## Verified alternatives

| Route | Proven capability | Reuse implications / assessment |
|---|---|---|
| Own density-domain core | darktable demonstrates transmission-to-log-density processing, channel corrections, and a separate paper/tone stage; RawTherapee demonstrates channel-dependent reciprocal-power inversion. [1–4] | A small numerical core is technically plausible in portable .NET. Its calibration, color fidelity, performance, and acceptable defaults remain untested. Implement independently rather than transplanting GPL source. |
| darktable **negadoctor + CLI** | Supports scanned or camera-photographed negatives, color/B&W, sampled film base, density range, exposure bias, separate shadow/highlight color correction, paper gamma, exposure, and highlight compression. Input is documented as **linear RGB**; output is nonlinear/display-referred. [1] Float implementation follows input profiling. [2] CLI accepts RAW input plus XMP and writes a positive rendition; official x64/Windows ARM64 packages exist. [9,12] | **Preferred first RAF feasibility spike as an optional installed external process**, not module embedding. Host controls process and artifact exchange; darktable owns RAW development and negadoctor history. Calibration authoring/native slider mapping and real-file execution are still blockers. GPL distribution review remains necessary if bundling. |
| RawTherapee **Film Negative** | Current manual supports **non-RAW** images and Bayer/X-Trans RAW, explicitly excluding other RAW types. Controls expose a green reference exponent and red/blue ratios; two unclipped neutral spots of different brightness help estimate ratios. [3] Current source applies negative channel powers to float RGB, reference input/output scaling, optional median-based estimates, and output clipping. [4] | This is C++ integrated with RawTherapee image sources, profiles, processing parameters, and compatibility handling—not a demonstrated standalone .NET API. Source is GPL-3.0-or-later. [4] Useful reference for per-channel slope calibration; not a proven drop-in engine. |
| **Magick.NET** / ImageMagick | Maintained .NET binding with documented .NET 10 support, Windows x64/ARM64 and other host packages, Q16/Q16-HDRI variants, and native components supplied by its packages. Wrapper README identifies Apache-2.0 licensing. [5] ImageMagick documents explicit linear/nonlinear handling and color profiles. [6] | Appropriate lower-integration-cost infrastructure candidate for TIFF/profile handling around an owned core. **Not a verified film-specific conversion library**, nor necessarily a lightweight binary dependency. Confirm selected package's floating-point pixel round-trip, ICC behavior, precision, runtime assets, and redistribution notices before choosing it. Do not choose Q8 for the processing path. |

No cited source establishes universal film-stock profiles or proves comparative output quality. RawTherapee's stock-specific default suggestions are not treated as universal calibration. [3]

## Input contract: linear light is not the same as 16-bit

- **Camera RAW:** require actual sensor decoding rather than the embedded JPEG preview; account for black level, white level/scaling, CFA/demosaicing, illumination white balance, camera characterization, and orientation before declaring a linear RGB buffer suitable for the selected algorithm. LibRaw exposes RAW pixels and relevant metadata and provides basic conversion; its authors explicitly do not position that conversion as a maintained production-quality rendering pipeline. [7] This is prerequisite work, not something a negative inversion automatically supplies.
- **Capture calibration:** negadoctor recommends unclipped expose-to-the-right capture, white balance from the light source without film, a camera input matrix, and an unexposed film area for the base sample. [1] Avoid channel clipping, including in the orange base.
- **Scanner TIFF:** request uninverted, high-bit-depth RGB with known transfer function and input characterization. A 16-bit TIFF can still be gamma-encoded or contain baked-in correction. ImageMagick explicitly distinguishes *declaring* pixels linear from actually converting them, and requires an appropriate profile to communicate linear color in TIFF. [6] For an unknown scanner response or baked automatic corrections, physical density recovery is **not established**; offer an explicitly approximate path or reject it for the calibrated MVP.
- **Rendered/JPEG input:** current RawTherapee documentation permits non-RAW input, but does not prove that every rendered scan preserves transmission information. [3] Decoding a known transfer curve cannot recover clipping or undo an unknown scanner tone curve. Prefer 16-bit/channel TIFF input and float processing; reserve 8-bit encoding for final previews/exports.
- **Color-management order matters:** darktable explicitly recommends linear Rec.2020 or an appropriate emulsion profile and disabling base curve/filmic during this workflow. [1] RawTherapee source includes input-space versus working-space handling for its sampling path. [4] Do not assume matrix/profile conversion commutes with per-channel logs. Choose and record one channel basis for calibration and conversion; verify it experimentally.

## Alternative owned pipeline — technical hypothesis

The following is a design proposal informed by [1–4], **not a port, a complete physical film model, or a validated recipe**:

1. Decode to normalized float RGB with explicit input profile/transfer information. Linearize correctly; avoid hidden automatic tone/exposure adjustments.
2. Sample clean, unexposed film in the **same processing basis** as image pixels. Use robust regional channel statistics; exclude dust, scratches, holders, and clipped samples. Allow a separate base reference capture under unchanged acquisition conditions.
3. Let `T[c]` be linear pixel transmission and `B[c]` the measured base transmission. Compute relative density:
   `D[c] = -log10(max(T[c], epsilon) / max(B[c], epsilon))`.
   This removes the measured channel-wise base offset in density space; it is not subtraction of an orange RGB color in encoded space. Epsilon, invalid pixels, samples brighter than the base, and noise need explicit handling.
4. Apply bounded per-channel density slope/offset controls, ideally aided by genuine neutral shadow/highlight samples. Base normalization addresses the measured mask, **not necessarily dye crosstalk, aging, illumination mismatch, or all color casts**. darktable's additional correction controls and RawTherapee's channel exponents illustrate why one base sample is insufficient as a general color model. [1,3,4]
5. Map corrected densities to a positive with a documented monotone mapping, followed by separate exposure, black point, contrast, and soft highlight roll-off. A reciprocal-power mapping can be expressed through density exponentiation; that mathematical equivalence does not establish equivalence to either complete application.
6. Convert the resulting positive through an explicit output/display profile and transfer curve. Keep working floats until final encoding; tag a 16-bit TIFF export with its actual profile. No second accidental gamma application, and no unexplained per-channel clipping.

Record the base sample, channel basis/profile, density coefficients, tone settings, and algorithm version as the reproducible conversion settings. A “roll calibration” should mean shared acquisition/calibration settings—not a claim that one film stock always has the same response.

## Incremental RAF MVP and acceptance gates

1. **Resolve real blockers:** exact Fujifilm model and RAF compression; target Windows architecture; installed executable/version/CLI presence; supported output decoder/profile display path; and who authors calibration. Reject unsupported lossy RAF instead of promising all `.RAF` files work. [11,12]
2. **One-photo proof:** on actual target hardware, calibrate a representative RAF in darktable, retain the generated XMP, and export both bounded preview and uncapped 16-bit TIFF using dedicated configuration. Verify dimensions, profile, source immutability, process exit/errors, and absence of unexpected metadata/catalog interference.
3. **Reproducibility:** repeat with the same recipe/version and compare GUI versus CLI output. Measure startup/export time, cancellation behavior, memory, CPU/OpenCL differences, and target-architecture execution. No such measurements have been made here.
4. **Batch only after calibration works:** copy compatible roll settings with per-frame review. Test dense/thin negatives, genuine neutral references, missing borders, clipped channels, and aged film. No automatic “perfect color” or universal stock preset.
5. **Native-slider decision gate:** if authoring outside PhotoManage is unacceptable, first prove a supported automation/parameter bridge. Otherwise choose the alternative owned core, beginning with known-linear TIFF fixtures and then a separately validated RAF decoder such as LibRaw. Do not quietly substitute unsupported XMP byte patching for this decision.

## Licensing and distribution checklist

These are engineering considerations, **not legal guarantees**:

- Both examined conversion source files are **GPL-3.0-or-later**. Copying/translating their implementation creates licensing considerations; language translation is not an avoidance strategy. Review obligations before shipping derived code or bundling either application. A subprocess boundary alone is not a legal conclusion. [2,4]
- The proposed first integration invokes a **user-installed external darktable executable**, rather than copying module code. That changes the engineering/distribution scope but is not a blanket legal exemption. Bundling darktable, modifying it, or redistributing profiles/native dependencies requires an explicit license/notice/source-offer review as applicable.
- Magick.NET identifies **Apache-2.0** for its wrapper; that does not establish the license inventory of bundled ImageMagick/native codecs, delegates, profiles, or runtimes. Inventory the exact package artifacts and their notices before distribution. [5]
- LibRaw offers a choice of **LGPL-2.1 or CDDL-1.0**; choose and review the applicable path, native build contents, modification/source obligations, and any optional components separately. [7] Do not infer that every demosaicing add-on shares the same terms.
- Pin released dependency versions and native architectures; floating upstream branches are research evidence, not dependency pins. No packages were installed and no runtime behavior was tested for this note.

## Primary sources

Initial eight sources, followed by six primary sources for the RAF/CLI follow-up. Stable 5.6 CLI documentation is used for invocation claims; development docs/source remain explicitly identified.

1. [darktable manual — negadoctor](https://docs.darktable.org/usermanual/development/en/module-reference/processing-modules/negadoctor/) — input contract, calibration, processing controls, working profile, and tone-stage guidance.
2. [darktable `negadoctor.c`, master](https://github.com/darktable-org/darktable/blob/master/src/iop/negadoctor.c) ([fetched source](https://raw.githubusercontent.com/darktable-org/darktable/master/src/iop/negadoctor.c)) — algorithm, float buffers, host dependencies, GPL notice.
3. [RawPedia — Film Negative](https://rawpedia.pixls.us/film_negative/) — current supported inputs, neutral-spot workflow, exponent controls; page reports modification on 2026-02-08. The former `rawpedia.rawtherapee.com/Film_Negative` URL returned 404; use this verified location.
4. [RawTherapee `filmnegativeproc.cc`, dev](https://github.com/RawTherapee/RawTherapee/blob/dev/rtengine/filmnegativeproc.cc) ([fetched source](https://raw.githubusercontent.com/RawTherapee/RawTherapee/dev/rtengine/filmnegativeproc.cc)) — reference scaling, channel powers, medians, clipping, space selection, GPL notice.
5. [Magick.NET README, main](https://github.com/dlemstra/Magick.NET/blob/main/Readme.md) ([fetched source](https://raw.githubusercontent.com/dlemstra/Magick.NET/main/Readme.md)) — framework/platform/package support, native deployment model, wrapper license.
6. [ImageMagick — Color Management](https://imagemagick.org/color-management/) — linearization versus declaration, profiles, and TIFF linear-color metadata caveat.
7. [LibRaw — About / Licensing](https://www.libraw.org/about#licensing) — decoder scope, limitations of built-in rendering, license alternatives.
8. [LibRaw — API Notes](https://www.libraw.org/docs/API-notes.html) — decoded RAW versus postprocessed buffers, metadata-dependent behavior, native runtime and thread/memory considerations.
9. [darktable 5.6 manual — darktable-cli](https://docs.darktable.org/usermanual/5.6/en/special-topics/program-invocation/darktable-cli/) — exact positional arguments, styles/custom presets, size bounds, ICC and export configuration.
10. [darktable manual — core binary options](https://docs.darktable.org/usermanual/development/en/special-topics/program-invocation/darktable/) — configuration/cache/work directory overrides, in-memory library, OpenCL disable, version query.
11. [Official camera support table](https://www.darktable.org/resources/camera-support/) — 5.6.2 model/decoder coverage and explicit Fujifilm lossy-RAF exclusion.
12. [Official release 5.6.2](https://github.com/darktable-org/darktable/releases/tag/release-5.6.2) ([release API and complete asset list](https://api.github.com/repos/darktable-org/darktable/releases/latest)) — verified Windows `win64` and `woa64` installers, checksums, release version, configuration-upgrade caveat. “Latest” is time-sensitive; the named release is the pin.
13. [darktable manual — sidecar files](https://docs.darktable.org/usermanual/development/en/overview/sidecar-files/sidecar/) — generated processing history, RAW read-only behavior, database-versus-sidecar precedence, current optional DTDATA dependency for per-pixel edits. Keep the MVP free of such mask dependencies.
14. [darktable 5.6.2 `src/common/exif.cc`](https://github.com/darktable-org/darktable/blob/release-5.6.2/src/common/exif.cc) ([fetched source](https://raw.githubusercontent.com/darktable-org/darktable/release-5.6.2/src/common/exif.cc)) — `dt_exif_xmp_encode` binary encoding and history `params`/`modversion` fields, especially lines 3127 onward and 4813–4849.
