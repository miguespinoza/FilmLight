# Film Light

A native WinUI 3 light table for Windows tablets. The untouched light area fills
the screen above a dark, touch-sized bottom toolbar. Runs offline without browser UI.

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

Click **Camera**, select the SDK13410 root folder if needed, confirm camera
setup, then **Connect USB**. The default SDK location is
`%USERPROFILE%\Downloads\SDK13410`. Film Light automatically creates a unique
`Roll-<date>-<id>` folder under `Pictures\FilmLight` (or your saved roll location);
no folder picker is required. **Open folder** opens the current roll in Explorer.
**New roll** creates another folder without deleting the previous roll's photos.
A healthy connected camera reconnects to the new roll automatically.
Changing the roll location affects future rolls only.
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
Before triggering and after the last download, it requires the shutter controls
to be inactive and the transfer queue to remain empty for 500 ms. Temporary SDK
busy responses and active shutter status restart that idle window instead of
faulting the scan; the light stays held while waiting. The idle camera indicator
also reports active shutter controls as busy, not ready.
RAW/JPEG partners are checked against their Fujifilm MakerNote shot number.
White mode also offers **Capture white**.

In connected RGB mode, **Capture references** saves one RGB reference set for the
current roll; **Capture frame** saves subsequent RGB sets separately. References
are optional and are never reset by a camera failure or disconnect. Roll metadata,
saved references and completed colors are restored when the app restarts.
After a failed/paused set, **Resume capture** skips already-saved colors and keeps
the same scan ID. Keep the film position, exposure and lighting settings unchanged
when resuming. Timer-only mode still starts a fresh set.

The compact bottom progress bar shows the current reference/frame, camera stage,
and saved RGB colors; it advances only when a color's complete files are saved.
A horizontal strip adds small labelled thumbnails as each capture arrives,
including partial frames. JPEG files or the embedded JPEG preview in RAF files
provide the thumbnails; originals are not modified.

Select a thumbnail and use **Undo / retake** to reject that frame or reference set
and queue a fresh capture of all its colors. Retaking an older frame keeps its
frame number and leaves later frames and references intact. **Cancel frame** or
**Cancel references** stops an active set, waits for an in-flight exposure/download
to finish, then queues a fresh retake rather than resuming the saved colors.
Retaking does not trigger the shutter until you press Capture again. Rejected
captures remain on disk and are marked discarded in `roll.json`, not counted as
valid frames/references. After a camera fault, re-arm before undoing or capturing.

Completed transactions appear under `Scan-<id>` in the roll folder. Each channel has
a directory containing original `.RAF`/`.JPG` files and transaction metadata.
Camera-card recording is enabled for the selected formats and its original setting
is restored on clean disconnect. Tablet download success does not independently
certify the camera card's write; verify card copies on hardware.
Preexisting queued images and canceled complete captures are preserved in `Recovery`.
Incomplete/failed transfers remain in `.pending`, never advertised as successful scans.
The SDK cannot download old files from the camera card itself.

Pause waits for an already-triggered capture and its downloads to finish before
unlocking light controls. After an uncertain capture/USB error, the light controls
remain locked. Verify the camera is idle, then use **Re-arm**, confirm idle and
choose **Reconnect**. You can also choose **Disconnect** without reconnecting.
Then use **Resume capture**; reference photos and completed colors do not need to
be captured again. There is no automatic retry/reshoot or silent fallback to the
timer after a camera fault. A canceled in-flight capture stays in Recovery and is
not treated as a completed color.
Cancellation before triggering interrupts busy polling without taking a shot.
After triggering, cancellation waits for downloads and the idle window before
unlocking controls. Shutter calls and image downloads are never blindly retried:
an error may occur after an exposure or queue consumption has already started.
The full capture deadline (readiness, exposure, transfer and final idle wait) is
two minutes; the helper watchdog is three minutes.
Do not use bulb or exposures/processing that exceed those limits.

An x64 helper owns all SDK calls while the WinUI app remains native ARM64.
It uses redirected local process streams, one SDK-owner mutex and serialized
transactions. SDK DLLs are loaded directly from your local package, not copied into
the app or repository. Obtain the applicable redistribution agreement before
shipping SDK binaries. Logs are under `%LOCALAPPDATA%\FilmLight\Logs`.
See `docs\fujifilm-sdk-investigation.md` for primary-source references and remaining
hardware acceptance gates.

## Film processing

After capture, choose **Process film** to preview and convert original RAFs using
the processing pipeline moved from PhotoManage's Negative Lab. Install darktable
5.6.x separately (`FILMLIGHT_DARKTABLE_CLI` can select its executable).
Light-source and film-base sampling, per-frame tone/Print Finish, crop/rotation,
and opt-in batch export produce a 16-bit sRGB TIFF, JPEG and reproducibility record
under the roll's `Positives` folder. Originals and references remain untouched.
RGB reconstruction uses the roll's explicit capture sets and references and
remains **experimental**, with acknowledgement required before export.
See [processing details and limitations](docs/processing.md).

## Build and run

Requires Windows 10 2004 or newer, .NET 10 SDK and Windows SDK 10.0.26100.

```powershell
.\tools\Publish.ps1
.\artifacts\FilmLight-arm64\FilmLight.exe
```

For an Intel Surface, use `.\tools\Publish.ps1 -Platform x64`.
The publish folder includes the .NET and Windows App SDK runtimes; keep its files together.
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
