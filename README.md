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
manual focus (M), single-shot drive (S), fixed exposure/white balance and a writable
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

Open **Roll workflow** after connecting a camera, or explicitly select **Demo camera**
to try the entire workflow without hardware. Demo windows, files, previews and
exports stay labelled **DEMO / SYNTHETIC**; the synthetic renderer is never a fallback
for real captures.

Choose the film kind and White or RGB mode, then **New roll** and a parent folder.
The app creates a uniquely named roll folder. The capture mode is fixed after the
first reference; use separate rolls for different modes.

1. **References:** capture **NO-FILM** illumination, then a **FILM BASE** reference
   using clear/unexposed developed film. Positive/reversal film needs no film-base
   reference. RGB references contain three separate exposures. Keep camera exposure,
   white balance, focus, display brightness, geometry and lighting fixed.
2. **Capture:** advance/focus the film and capture numbered frames. White is one
   exposure; RGB is one complete triplet. RAW+JPEG partners remain grouped together.
   Each complete frame is saved first, then automatically rendered as a positive
   on the right, without leaving Capture. Previews are limited to 1600 pixels on
   the long edge; exports retain full cropped resolution. RAW decoding still takes
   time, especially on the first frame. If previewing fails or is canceled, the
   saved capture stays in the roll: use **Render Preview**, not another exposure.
   Illumination stays on the left through every phase; its size and color cannot
   change during an unfinished exposure/download. Reopening a real camera lease
   requires fresh references before adding captures.
3. **Develop:** use **Pick backlight WB** to click exposed illumination with no film,
   or **Pick film base** to click clear/unexposed developed film. Each picker switches
   to the negative/source view; **Show negative** also lets you inspect it manually.
   Picks average a 5 x 5 patch of the underlying reference-normalized linear input,
   not the displayed JPEG. Backlight sampling replaces positive-output channel gains
   to correct residual tint after light-reference normalization; film-base sampling
   replaces measured-base multipliers. Both are saved as unaccepted recipe revisions
   and automatically re-render the positive. These roll-wide settings do not replace
   the required reference captures or change camera white balance.
   In **Develop settings**, adjust rendering exposure, channel
   gains, measured film-base multipliers, black/white levels and crop, then
   **Render Preview**. Crop out dark film-holder borders before measuring the base.
   **Accept this preview** saves an immutable recipe revision; editing any setting
   requires another preview. These controls do not change camera exposure.
4. **Export:** process missing/failed frames using the identified accepted revision.
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
Roll manifests currently use absolute paths: keep the roll at its original location
when resuming it.

Recipes currently apply to the whole roll; per-frame overrides are not implemented.
Original captures and references are never deleted automatically. A rendered TIFF
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
matching camera/exposure metadata across the selected frame and its references.

Signed scene-linear values from sensor noise/profile matrices are preserved in
32-bit intermediates; negadoctor applies its density-domain floor, rather than the
decoder clipping or offsetting RAW data. Generated linear input caches live under
`Working`; real originals stay in `captures`.

Real exports use ProPhoto RGB TIFF masters and sRGB JPEG companions with embedded
ICC profiles. Positive/reversal film skips inversion; B&W negatives use monochrome
inversion.

Allow substantial working space: one 40-megapixel float RGB intermediate is about
480 MB, and a 16-bit TIFF master about 240 MB before compression. Export is sequential.

Negatives start from measured film-base values and a version-pinned negadoctor
print recipe, followed by supervised preview/approval. **Import calibrated XMP**
optionally imports negadoctor print settings; it must use negadoctor v2, the standard
module order, and no other enabled history modules. Ordinary full RAW-development
XMPs are not interchangeable with this normalized-linear-input template. Film Light
owns input normalization, exposure, levels, gains and crop.

Sequential RGB reconstruction divides each selected primary channel by its matching
no-film reference **before** inversion; it does not HDR-average or independently
invert the three images. It assumes fixed exposure and registration. Spectral
crosstalk, physical alignment, film color accuracy and real X-T5 operation still
require hardware qualification; synthetic validation does not certify them.

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

The four-phase native workflow smoke check uses an explicitly synthetic camera,
exercises preview invalidation/acceptance, TIFF/JPEG export, missing-output retry
and reopening, and leaves its demo roll under `artifacts`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\WorkflowSmokeTest.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\WorkflowSmokeTest.ps1 -CaptureMode Rgb
```

Processing tests execute actual negadoctor when darktable 5.6.2 is installed at
`C:\Program Files\darktable\bin\darktable-cli.exe`, or when
`FILMLIGHT_TEST_DARKTABLE` points to its executable. Otherwise those integration
cases are explicitly skipped, not reported as real processing.
Optional public RAW fixture checks use `FILMLIGHT_TEST_RAF`,
`FILMLIGHT_TEST_SIGNED_RAF` and `FILMLIGHT_TEST_LOSSY_RAF`; those tests also explicitly
skip when their fixtures are unavailable.

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
