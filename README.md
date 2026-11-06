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
