# Film Light

A native WinUI 3 light table for Windows tablets. The light area sits above a dark,
touch-sized bottom toolbar. Roll workflow splits the workspace equally: illumination
on the left, capture/positive preview on the right, with a full-width bottom toolbar.
Runs offline without browser UI.

## Use

Start in fullscreen white mode. White defaults to full RGB white, labelled nominal
5000 K; warmer/cooler controls apply an approximate tint, not calibrated temperature.
Use **Setup** to open Windows Display settings. Manually disable Night light,
adaptive brightness/color and HDR, and set a fixed display brightness.
The app keeps the display awake while focused and does not change system display settings.

In RGB mode, choose 1–120 seconds per color with the minus/plus buttons. Start gives
a three-second countdown, then holds pure red, green and blue for the selected interval.
One set is the default; enable Repeat for continuous cycling. Completion holds blue.
Pause freezes the light; restarting begins a fresh set after the countdown.
Losing focus pauses the sequence. Escape pauses and exits fullscreen.
Settings are saved under `%LOCALAPPDATA%\FilmLight`.

The timer does **not** trigger the camera. Align your intervalometer so exposures
fall near the middle of each hold, leaving time around transitions. Disable automatic
exposure and white balance. Test for display flicker, uneven illumination and visible pixels.
Repeated sequences can drift relative to an independent camera timer.

## USB camera capture

Click **Camera**, select the SDK13410 root folder and a save folder, confirm camera
setup, then **Connect USB**. The default SDK location is
`%USERPROFILE%\Downloads\SDK13410`; photos default to `Pictures\FilmLight`.
This implementation targets the **Fujifilm X-T5**. Use USB TETHER SHOOTING AUTO,
single-shot drive (S), fixed exposure/white balance and a writable
card. Close other tether apps and do not press the camera shutter during app captures.
Select RAW, JPEG or RAW+JPEG on the camera; HEIF is explicitly unsupported.

The bottom toolbar has a persistent camera indicator: disconnected (gray), detection
and capture activity (amber), ready (green), or fault (red). It shows SDK loading,
USB discovery, detected model/firmware, light settling, triggering, waiting for
exposure/processing, downloading RAW/JPEG, and saving. Idle connections are checked
every two seconds; loss of connection faults the capture path rather than leaving
a stale ready indicator. Click Camera to initiate USB detection/connection.

Exposure stays on the camera: set shutter speed, aperture, ISO, and white balance
using its controls. Film Light selects and verifies camera-priority tethering and
uses only `ReleaseEx` for triggering; it never sets exposure values. You can adjust
them between sets, but keep them fixed across the three exposures of one RGB set.
The original tethering priority is restored on clean disconnect.
Automatic image downloads remain enabled.

Autofocus or manual focus is the user's choice. Film Light neither requires nor
changes the camera's focus mode or lens position. Camera-side focus/release
priority can still reject a shutter operation; that SDK error is shown explicitly.

Connected RGB mode shows **AUTO** instead of interval controls. After the countdown,
the app settles each color for 250 ms, performs half/full press, waits for the
camera queue, and downloads the complete selected format set before the next color.
RAW/JPEG partners are checked against their Fujifilm MakerNote shot number.
Both SHORT and LONG shot-number encodings are supported, including the X-T5's
SHORT encoding.
White mode also offers **Capture white**.

Completed transactions appear under `Scan-<id>` in the save folder. Each channel has
a directory containing original `.RAF`/`.JPG` files and transaction metadata.
Camera-card recording is enabled for the selected formats and its original setting
is restored on clean disconnect. Tablet download success does not independently
certify the camera card's write; verify card copies on hardware.
Preexisting queued images and canceled complete captures are preserved in `Recovery`.
Incomplete/failed transfers remain in `.pending`, never advertised as successful scans.
The SDK cannot download old files from the camera card itself.

Pause waits for an already-triggered capture and its downloads to finish before
unlocking light controls. After an uncertain capture/USB error, the light controls
remain locked. Verify the camera is idle, use **Re-arm** to disconnect, then reconnect.
There is no automatic retry/reshoot or silent fallback to the timer after a camera fault.
The queue deadline is two minutes; the helper watchdog is three minutes.
Do not use bulb or exposures/processing that exceed those limits.

An x64 helper owns all SDK calls while the WinUI app remains native ARM64.
It uses redirected local process streams, one SDK-owner mutex and serialized
transactions. SDK DLLs are loaded directly from your local package, not copied into
the app or repository. Obtain the applicable redistribution agreement before
shipping SDK binaries. Logs are under `%LOCALAPPDATA%\FilmLight\Logs`.
See `docs\fujifilm-sdk-investigation.md` for primary-source references and remaining
hardware acceptance gates.

## Roll workflow

Open **Roll workflow** and use **Connect USB camera** there, connect in the main
window first, or explicitly select **Demo camera**
to try the entire workflow without hardware. Demo windows, files, previews and
exports stay labelled **DEMO / SYNTHETIC**; the synthetic renderer is never a fallback
for real captures.

Choose the film kind and White or RGB mode, then **New roll**.
The app automatically creates a uniquely named folder under `Pictures\FilmLight\Rolls`;
there is no folder-selection step. **Open / resume** shows an in-app roll library
with creation/update dates, picture counts, film kind, capture mode and workflow phase.
RGB triplets and RAW+JPEG pairs count as one picture; reference exposures are excluded.
Demo rolls are clearly labelled, and unavailable rolls show their error.
For an older roll outside the library, paste its full folder path under
**Older roll stored elsewhere** and choose **Add existing roll** once; the app
remembers it without moving its files. Metadata stays in each roll's `roll.json`,
with linked folder paths in `Pictures\FilmLight\Rolls\linked-rolls.json`; no database is required.
The capture mode is fixed after the
first picture; use separate rolls for different modes. There is no separate
calibration or reference-photo step.

If the camera needs to be released, confirm it is physically idle using the
checkbox, then **Confirm idle / release camera**. Use **Connect USB camera** in
the same roll window to reconnect; there is no need to close the workflow.
This also works after a camera fault once idle is confirmed. Captures, numbering,
preview, picture picks and recipe edits stay in the open roll. Reconnection keeps
the current phase and each frame's saved light/film picks. Exposure and focus may
change between frames: calibration is not reused from another photograph.
Canceling or failing a connection leaves the roll open for retry. Camera controls
are disabled during exposures, downloads, processing and connection setup.

1. **Capture:** capture a picture with exposed backlight and clear/unexposed film
   visible around the image. White is one
   exposure; RGB is one complete triplet. RAW+JPEG partners remain grouped together.
   Each complete frame is saved first, then automatically rendered as a positive
   on the right, without leaving Capture. Before its first preview, the app runs
   the sprocket/clear-film finder on that frame and saves its own calibration.
   Returning to a saved frame restores its own picks and recipe, not those from
   the last photograph. If detection fails, the preview is explicitly labelled
   uncalibrated/provisional and includes the diagnostic error; it never borrows
   another frame's samples.
   To override detection, click **Pick backlight WB**, then exposed light with no
   film over it, or **Pick film base**, then clear/unexposed developed film.
   Each override affects only this frame. Each picker switches to the negative/source view;
   **Show negative** also lets you inspect it manually. Positive/reversal film
   needs only the light pick.
   Picks average a 5 x 5 patch from the full-resolution linear input, not the
   displayed JPEG. Manual pick coordinates use the recipe crop fractions; patches
   stop at sensor boundaries, not crop edges. A manual backlight pick that contains clipped values is still
   saved with its red marker, but is labelled **WB correction skipped**. It counts
   as the light pick for this frame; its RGB values are not used to normalize
   the input or alter darktable's processing settings. The film-base pick and
   other develop settings still apply. Pick an unclipped backlight patch again
   to restore measured normalization. Dark/invalid picks and clipped film-base
   picks are still rejected. Manual picks are saved immediately for this frame only.
   Picks remain saved after reopening or reconnecting; automatic calibration
   means no routine manual picking is required. **Reset picks** reruns automatic
   detection for the selected frame. Previous
   saved revisions and outputs remain intact.
   **Auto-pick light + base** explicitly reruns the same automatic finder: it looks for
   at least three evenly spaced sprocket holes near an edge of the
   uncropped RAW, with the film strip roughly horizontal or vertical. It measures
   robust median RGB in the hole interiors (bare backlight) and in clear film
   between adjacent holes, avoiding their edges. Invalid individual patches are
   excluded; a row still needs at least three valid, regularly spaced holes.
   Overlapping hypotheses on one edge form one physical row, whose RGB is the
   median of its valid patches; only distinct rows are checked for conflicts.
   The marker identifies a representative patch, with geometric tie-breaking.
   Detection uses a reduced-size
   linear image. Automatic color measurements use the 2560-pixel linear RAW
   proxy, never screenshot/JPEG colors. Samples with a peak channel of 0.9 or
   higher are checked for clipping/dark pixels at full resolution, retaining the
   detector's shared patch rule and 5% tolerance; samples below that threshold need no full decode.
   Manual picks always measure full-resolution pixels. Clipped automatic backlight
   picks use the same marker-only behavior as manual picks: film-base and develop
   settings still apply, but measured WB/backlight normalization is skipped with
   an explicit warning. An RGB value at or above 1 is treated conservatively as
   out of range; it does not, by itself, prove sensor saturation.
   Internally, the calibration
   module runs an ordered step list: sprocket detection then clipping verification
   for automatic picks, or a manual-pick adapter for manual picks. No neural network
   or additional runtime is required.
   Clipped film-base, dark, nonuniform or conflicting measurements are rejected without changing
   existing picks; use manual picking when perforations are absent, obscured or
   substantially rotated. If backlight values are clipped/out of range, a manual
   marker-only backlight pick remains available; changing the develop exposure
   slider cannot recover measured channel ratios. Detection failure alone does
   not require another exposure.
   Auto-pick logs candidate-hole counts, row rejection stages, rejected patch RGB
   ranges and selected samples under `Working\calibration-diagnostics` in the roll.
   Failures show the diagnostic log path and leave existing picks untouched.
   Automatic picks are saved for this frame and used for its previews and exports.
   Crop and other develop controls
   are preserved; the saved samples belong only to this frame. Positive film
   uses only the backlight sample. Partially cropped holes are supported when
   enough of their interiors remain visible. Geometric localization has been
   demonstrated on real RAF previews; RAW color accuracy still needs qualification.
   The preview overlays a **red marker for backlight WB** and a **blue marker for
   film base**, on both positive and negative/source views. These are display-only
   annotations, never burned into preview/export files. They track window size
   and crop, and appear only on the frame where the samples were taken. Picks
   outside the current crop are hidden with an explanation; reduce the crop to
   inspect them. New automatic and manual picks retain their original image
   coordinates when saved/reopened. Older picks still process normally but need
   to be sampled again to record unambiguous marker geometry.
   Previews are limited to 1600 pixels on
   the long edge and saved as lossless 8-bit sRGB PNGs with embedded ICC profiles;
   the source view remains a JPEG. Exports retain full cropped resolution and
   their existing ProPhoto TIFF master plus sRGB JPEG companion.
   Calibration and develop share a cancellable, step-agnostic runner.
   The develop module runs ordered reconstruction, preview crop, inversion,
   controls and encoding steps, selected by Preview or Export intent. Darktable
   previews launch only the inversion adapter, not output encoders. Unchanged
   normalized pixels and effective XMP reuse a bounded disk cache under
   `Working\.develop-cache`, including across processor instances; exposure,
   gains and levels then need no new inversion. Exports always run fresh, with
   one darktable inversion launch and in-process output encoding; they no longer
   write `positive-linear.tif` or `output-positive.xmp`.
   The WinUI-free `FrameWorkflow` owns per-frame drafts, prepare/preview/picks,
   saved settings and resumable export/publication. The window only
   displays results and owns camera, illumination and UI operation state.
   Each workflow keeps one processor and one shared, lazily created
   `FrameSources` cache; opening another roll or changing the darktable path
   disposes that workflow. `FrameSources` decodes a 2560-pixel linear proxy for ordinary previews,
   warms it after capture, and uses full resolution for tight crops, exports and
   manual picks and near-clip automatic verification. RAW decoding still takes
   time, especially on the first frame. If previewing fails or is canceled, the
   saved capture stays in the roll: use **Render Preview**, not another exposure.
   While work runs, the preview area shows an animated activity ring, the current
   stage (RAW decode/cache, negative inversion, TIFF/JPEG encoding or loading),
   and total/stage elapsed time. The frame is explicitly marked as saved before
   conversion starts. No guessed percentage or remaining time is shown;
   **Pause / cancel safely** remains available. The indicator clears on completion,
   cancellation or error, leaving the result/error in the status area.
   The frame selector includes its saved file format. Switching the camera to RAW
   does not change older JPEG frames; capture a new RAW
   frame in **1. Capture**. JPEG-only previews show an actionable error instead
   of leaving the status on Processing.
   Illumination stays on the left through every phase; its size and color cannot
   change during an unfinished exposure/download.
2. **Develop (optional):** use the bottom console's sliders and color pad to adjust
   rendering exposure, channel gains, film-base multipliers, levels and crop.
   Adjustments show an instant approximation followed by an automatic exact preview;
   no Apply or acceptance step is needed. Double-click a slider to reset it, or
   double-click the color pad to return to neutral. Icon buttons have hover tooltips;
   **Advanced develop settings** contains XMP import and darktable selection.
   Pick light and film before cropping them out of the picture. Edits save immutable
   recipe revisions, including when leaving Develop or closing before a render finishes.
   These controls do not change camera exposure.
3. **Export:** process missing/failed frames using each frame's current saved settings,
   even if the latest edit has not been previewed. Unedited frames follow the
   immediately previous frame's develop controls, including later changes, until
   manually overridden. The first frame starts from defaults. Calibration samples
   are always measured independently and never inherited.
   Incomplete calibration remains an explicit warning on provisional previews/exports.
   A successful export is a validated 16-bit TIFF master and JPEG companion.
   Pause/cancel retains inputs and diagnostic artifacts. Missing outputs can be
   regenerated without overwriting existing files.

**Open positives folder** opens the roll's `Positives` directory (`SYNTHETIC-Positives`
for demo rolls). It contains only positive TIFF/JPEG images and recipe metadata.
Processing intermediates, effective XMPs and CLI logs stay separately in `Processing`
and `Previews`. After validated positive publication, the three generated full-size
processing TIFFs are removed; logs, effective recipes and original captures are kept.
Preview intermediates and reusable linear decode caches remain available.
`roll.json`, `captures` and `recipes` retain the acquisition and
calibration history; **Open / resume** restores the roll.
Developing and exporting already captured frames do not require a connected camera.
Roll manifests currently use absolute paths: keep the roll at its original location
when resuming it.

Calibration and overrides are frame-scoped. Repicking one frame does not change
another frame's calibration. Develop-control edits propagate only through
following frames that have not been manually overridden; existing exports remain
intact until explicitly re-exported. Version 1 roll manifests remain
readable; their approval flags are discarded, and the next save writes version 2.
Older roll-wide recipes remain as history, not defaults for other frames. Existing
frame-specific recipes retain their controls as local overrides. New frames follow
their previous sibling's controls until manually edited.
Opening/rendering/exporting through the workflow creates frame-scoped recipes and measures missing calibration
from each frame. Original captures and legacy references are never deleted
automatically. A rendered TIFF
is not reversible RAW and cannot replace retained captures for future reinversion.

### Real image processing

The standard `Program Files\darktable\bin\darktable-cli.exe` location is detected
automatically. Otherwise select **darktable-cli.exe** from a **darktable 5.6.2**
installation using **Develop settings**, available before the first frame.
The native ARM64 darktable build requires Windows 11.
This version is pinned because the negadoctor parameter encoding is version-specific.
Use **RAW or RAW+JPEG** with X-T5 **UNCOMPRESSED or LOSSLESS COMPRESSED** RAF.
JPEG-only and lossy-compressed RAF inputs are rejected rather than processed as if
they were linear RAW. The decoder uses CFA black/white correction, demosaicing and
the camera's standard input matrix, with fixed unity processing white balance and
no automatic exposure or tone mapping. Camera settings are never changed. It checks
matching camera/exposure metadata within an RGB triplet. Separate frames need not
have matching exposure or focus settings, because each supplies its own light and
film-base measurements. The legacy separate-reference processing path retains its
reference-exposure checks; new workflow edits do not use that path.

Signed scene-linear values from sensor noise/profile matrices are preserved in
32-bit intermediates; negadoctor applies its density-domain floor, rather than the
decoder clipping or offsetting RAW data. Generated linear input caches live under
`Working\camera-input-cache`; real originals stay in `captures`. Proxy and full
tiers use separate content-addressed entries. Receipts validate size, modification
time, dimensions and exposure without rehashing the decoded image on each read.
Full-resolution input uses lossless float32 RGB PFM with bulk, row-flipped reads;
the 2560-pixel proxy stays float32 TIFF for ICC and EXIF validation. A full decode
first obtains the same RAF's proxy receipt (decoding the proxy if needed), then
checks the full image's aspect, dimensions and finite samples against it.
Validated darktable outputs are published without rewriting or hashing.
The module retains eight recent proxies and one full image; consumers receive
defensive copies. Decodes are serialized with queued proxies ahead of full work,
and canceling a preview does not cancel a shared decode. Cleanup attempts to remove
partial jobs older than an hour and retain only ten failed decode jobs for diagnosis.
Locked or access-denied abandoned jobs are left for a later cleanup attempt with a
trace warning, rather than blocking previews or exports.

Real exports use uncompressed 16-bit **linear ProPhoto RGB** TIFF masters and
quality-95, **4:4:4** sRGB JPEG companions with embedded ICC profiles. The TIFF
profile matches darktable 5.6.2's `PROPHOTO_RGB` colorants, D50 white and measured
gamma-1 TRC (not gamma 1.8). Shared Rec2020 output color math handles conversion;
Windows WIC encodes the JPEG without chroma subsampling or an extra dependency.
Positive/reversal film skips inversion; B&W negatives use monochrome inversion.

Allow substantial working space: one 40-megapixel float RGB intermediate is about
480 MB, and a 16-bit TIFF master about 240 MB before compression. Frames export
sequentially; each frame's TIFF/JPEG encoders run concurrently with scanline-sized
managed buffers, not another full-resolution converted image.
On the real 40 MP X-T5 RAF fixture, a representative warm-source export dropped
from 58.13 s to 32.43 s; cold full-resolution source preparation added 14.05 s.
The two encode launches plus float intermediate took 20.55 s, versus 1.44 s for
parallel in-process encoding (2.30 s sequential). Inversion timing varies with
machine load: repeated paired exports saved 11.6-25.7 s overall, while the
encoding stages consistently saved about 19 s. TIFF fidelity gates measured max
1/65535 on real and signed, out-of-gamut synthetic inputs; decoded JPEG
mean/p99/max differences were
0.155/2/12 (real) and 0.157/3/9 (synthetic). Preview keeps its existing code path
and cold/controls-only/base-change launch counts of 1/0/1.

Negatives start from measured film-base values and a version-pinned negadoctor
print recipe, followed by supervised preview/approval. **Import calibrated XMP**
optionally imports negadoctor print settings; it must use negadoctor v2, the standard
module order, and no other enabled history modules. Ordinary full RAW-development
XMPs are not interchangeable with this normalized-linear-input template. Film Light
owns input normalization, exposure, levels, gains and crop.

Sequential RGB reconstruction divides each selected primary channel by the matching
component of an unclipped picked light **before** inversion; a clipped manual
light pick leaves those channels unnormalized. Film-base transmission comes
from the picked clear-film patch. It does not HDR-average or independently
invert the three images. Point picks provide global normalization, not spatial
flatfield correction. It assumes fixed exposure and registration. Spectral
crosstalk, physical alignment, film color accuracy and real X-T5 operation still
require hardware qualification; synthetic validation does not certify them.

## Sprocket annotation demo

On Windows with the .NET 10 SDK:

```powershell
.\tools\DemoSprocketCalibration.ps1 -InputDirectory 'C:\scans\roll' -OutputDirectory '.\artifacts\SprocketDemo'
```

This runs the shared sprocket geometry detector on each RAF's embedded JPEG
(or a supplied JPEG/PNG/TIFF), without changing originals or their sidecars.
It writes annotated PNGs (up to 1800 pixels on the long edge), a thumbnail
`index.html` gallery, and `report.json` with normalized coordinates and failures.
**Red** marks bare light inside a hole; **blue** marks film between holes.
Missing or unreliable sprocket pairs, including blank-light references with no
film visible, receive a warning rather than fabricated dots. The script analyzes
the selected folder only, not its subfolders.

This is a **location/geometry demonstration**, not color calibration: preview
clipping is allowed for locating holes, but automatic RAW calibration still
rejects clipped color samples. Manual clipped backlight picks are marker-only
and skip WB correction. Do not feed JPEG colors into negadoctor. No network
service or darktable installation is required for this visual demo.

## Build and run

Requires Windows 10 2004 or newer, .NET 10 SDK and Windows SDK 10.0.26100.

```powershell
.\tools\Publish.ps1
.\artifacts\FilmLight-arm64\FilmLight.exe
```

For an Intel Surface, use `.\tools\Publish.ps1 -Platform x64`.
The publish folder includes the .NET and Windows App SDK runtimes; keep its files together.
It must also include the generated `FilmLight.pri` resource index. The project
explicitly stages it during publish: without it, WinUI can terminate with
`0xC000027B` while loading control styles, even though the main window opens.
This is an unpackaged desktop app, not a PWA or MSIX installer.

```powershell
dotnet test .\FilmLight.Tests\FilmLight.Tests.csproj
```

The optional native UI smoke check launches the app, exercises its controls and
closes that process. Run it with Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\SmokeTest.ps1
```

With the camera disconnected, `-ProbeCamera` also exercises actual SDK startup and
the no-camera error path. `-SkipPixels` explicitly disables screen-pixel checks when
the desktop is unavailable to GDI; UI Automation checks still run.

The three-phase native workflow smoke check uses an explicitly synthetic camera,
exercises reference-free capture, frame-local mouse picks and reopen,
the default develop console, pending slider edits, TIFF/JPEG export, missing-output retry,
automatic folder creation and the metadata-rich resume dialog. It leaves its demo
roll under `Pictures\FilmLight\Rolls`; diagnostic fixtures stay under `artifacts`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\WorkflowSmokeTest.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\WorkflowSmokeTest.ps1 -CaptureMode Rgb
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\WorkflowSmokeTest.ps1 -ProbePreviewErrors
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\WorkflowSmokeTest.ps1 -ProbeReconnect
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\WorkflowSmokeTest.ps1 -ProbeProgress -RawPath 'C:\Scans\frame.RAF'
```

The preview-error probe uses generated JPEG-only test inputs, requires darktable at
its standard installation path, and never connects to the camera.
The progress probe copies the supplied supported RAW into an isolated fixture and
checks visible conversion stages, a live elapsed timer, completion and cancellation.
It requires darktable at its standard path and never connects to the camera.
The reconnect probe checks setup cancellation/retry with a saved roll open and
does not touch camera hardware; physical reconnect still needs hardware acceptance.

Processing tests execute actual negadoctor when darktable 5.6.2 is installed at
`C:\Program Files\darktable\bin\darktable-cli.exe`, or when
`FILMLIGHT_TEST_DARKTABLE` points to its executable. Otherwise those integration
cases are explicitly skipped, not reported as real processing.
Optional public RAW fixture checks use `FILMLIGHT_TEST_RAF`,
`FILMLIGHT_TEST_SIGNED_RAF` and `FILMLIGHT_TEST_LOSSY_RAF`; those tests also explicitly
skip when their fixtures are unavailable.
`FILMLIGHT_TEST_SPROCKET_RAF` optionally supplies a film scan with out-of-range
backlight values and valid clear film to replay automatic marker-only calibration.

## Camera-control seam

`FilmLight.Core.ICameraCapture` is the camera-control seam. Supply an adapter to
`ScanSequence` and connect its completion callback to image storage/UI. The adapter
receives the displayed channel, waits for display settling, triggers exposure, and
returns downloaded file paths only after exposure and transfer have finished.
The next color cannot appear before that task finishes. Errors stop the sequence;
cancellation must ensure the camera cannot expose later under another color.
`CameraClient` supplies this adapter through the x64 `FilmLight.CameraHost`.
Without a connected camera, the existing timer-only sequence remains available.
No Fujifilm binaries are included.

## License

MIT. See [LICENSE](LICENSE). The Fujifilm SDK is not covered and must be obtained separately under Fujifilm's terms.
