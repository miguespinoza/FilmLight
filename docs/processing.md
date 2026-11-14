# Film processing

FilmLight owns both capture and conversion. The processing code was migrated from
PhotoManage's Negative Lab; there is no reference to PhotoManage's assemblies,
Catalog, Media Item IDs, Activities, or rendering host. Its existing Fujifilm
Development Lab is unrelated and remains in PhotoManage.

## Use

After capturing, choose **Process film** in the roll toolbar. Select a completed,
non-discarded white-light frame or RGB frame. A camera JPEG is never substituted
for an original RAF; use uncompressed or lossless-compressed RAF on the X-T5.
Install darktable 5.6.x separately. `FILMLIGHT_DARKTABLE_CLI` overrides discovery.
An unsupported or missing renderer produces an actionable error.

The uncropped preview uses a cached float basis, not the camera thumbnail.
**Sample light source** measures a bare, unclipped light area in a white-light
capture. **Sample film base** measures clear, unexposed film instead; these are
different calibrations. Both pickers sample a small region around the click.
Changing Light Balance remeasures the film base. An initial film base is explicitly
an estimate; use a clear rebate for deliberate calibration. **Auto tone within
crop** measures scene density and print response, rather than blindly inverting
an 8-bit JPEG. Paper controls, highlight color balance, and linear-light Print
Finish remain independently editable. Crop coordinates and rotation affect export;
the preview deliberately stays uncropped so sampling is possible.

Per-frame settings are saved atomically in `<roll>\.processing\<capture-id>.json`.
Batch export is opt-in and shares film/light/color calibration only across the
same capture kind. Existing per-frame tone, Print Finish and crop remain separate;
new frames receive their own auto tone. Only select a batch recorded with the
same film and acquisition setup.

Each export creates a unique `<roll>\Positives\Positive-<capture-id>-<id>` directory:

- `positive.tif`: full-resolution, unsigned 16-bit, profile-tagged sRGB master.
- `positive.jpg`: quality-95 sRGB delivery copy encoded by Windows.
- `conversion.json`: exact settings, source/reference paths and fingerprints,
  SHA-256 hashes, renderer/template identity, RGB alignment/normalization and warnings.

Original RAFs, reference captures, camera JPEGs, metadata sidecars and `roll.json`
are never modified or deleted. TIFF/JPEGs are rendered positives, not reversible
RAWs. Keep the originals and calibration record. Source changes invalidate a
prepared export. Output is staged privately and published only after decoding
and validation; collisions never overwrite an earlier conversion. Failure or
cancellation retains an unpublished `.pending` directory for diagnosis, while
already published batch outputs remain available. **Cancel conversion** terminates
owned darktable work; closing the dialog waits for cancellation to finish.

## Pipeline

`NegativeProcessor` is the roll-processing module. It consumes explicit
`RollCapture` channel assignments, not filename ordering or guessed triples.
`INegativeRenderer` is the replaceable renderer seam. The implementation uses
argument lists, source copies, private XMP and isolated darktable configuration,
bounded processes and cancellation. The bridge pins negadoctor v2, temperature
v4 and RGB pass-through colorin v7 to darktable 5.6.x. darktable remains an
external installation, not a bundled or linked GPL dependency.

White light: RAF -> darktable demosaic/input profile -> float linear Rec.2020
PFM basis -> native negadoctor/display transform for interaction. Export instead
uses full-resolution darktable negadoctor -> sRGB TIFF -> linear-light Print Finish
-> crop/rotation -> TIFF master and JPEG. The original C# math, analysis, PFM,
history-packing and precision regression cases now run in `FilmLight.Tests`.
`RenderProofAsync` remains available on the renderer seam for reference comparisons;
the compact processing dialog currently shows native previews, not automatic Proofs.

RGB: independently decode each RAF with unity temperature and pass-through camera
channels; verify the explicit R/G/B labels; extract one camera channel per
illuminant; phase-correlate red and blue against green; resample and normalize
with the roll's light-only reference medians. Without references, a clearly warned
99.9th-percentile estimate is used. Preview normalization and alignment are reused,
scaled to full-resolution sampling, during native positive TIFF export.

**RGB remains experimental.** The reconstruction is not calibrated colorimetry,
spectral crosstalk correction, per-pixel flat-field correction, or affine/focus
compensation. Uncertain registration and uneven illumination are reported. Export
requires explicit acknowledgement. Real independently captured RGB sets and
white-light comparisons still need photographic quality review; copied RAFs,
synthetic tests and the original evidence do not establish that quality.

## Source evidence

[Original design and measurements](photo-manage-negative-lab.md) and
[original research](negative-scan-processing-research.md) preserve the earlier
investigation. They are historical evidence, not claims that FilmLight's new
host integration or RGB color accuracy has been verified on those captures.

## Validation

Run `dotnet test .\FilmLight.Tests\FilmLight.Tests.csproj`. The optional native
end-to-end check accepts an existing self-contained build and a real scan RAF:

```powershell
powershell.exe -NoProfile -File .\tools\Test-Processing.ps1 `
    -AppPath .\artifacts\FilmLight-arm64\FilmLight.exe -RawPath C:\Scans\frame.RAF
```

It copies the RAW into a private temporary roll, launches an owned app process
with `FILMLIGHT_APPDATA_ROOT`, and checks preview, TIFF/JPEG publication, provenance
and unchanged source bytes without changing the user's settings or original roll.
Its temporary evidence directory is printed for inspection. This validates the
white-light host integration, not real RGB color quality.

Add `-DialogOnly` for the focused dialog-opening regression. It expands the
adjustment controls, closes the dialog and checks that the copied RAW is unchanged,
without exporting. The unpackaged publish must include `FilmLight.pri`; omitting
this merged resource index causes WinUI to fail fast while loading the `Expander`
default style (`0xC000027B`, underlying XAML error `0x880008B0`), before RAW preparation.
The project explicitly stages this generated file and rejects publication if it
is missing.
