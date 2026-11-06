# Local Fujifilm SDK investigation for the Surface RGB scanning app

Investigated 2026-11-05/06. SDK root: `C:\Users\migue\Downloads\SDK13410`.

**Final report. Camera confirmed by user, 2026-11-05: Fujifilm X-T5 running the newest firmware.** Accept latest installed firmware as the user's stated configuration; its numeric version was not independently read from the camera and is not invented here. The X-T5 is explicitly supported, with support introduced in SDK 1.25.0.0; this local manual does not specify a numeric minimum X-T5 firmware. The recommended starting point remains USB tethering through one x64 SDK owner, with a native ARM64 UI communicating through the camera adapter. This is a supported-camera match, not verified Surface runtime compatibility. [User confirmations, 2026-11-05; M pp.24,29; B; A1–A2]

## Decision summary

- **The SDK has the necessary still-camera trigger and RAW/JPEG transfer primitives, but a successful shutter call is not a completed capture.** Both full-press APIs return immediately; Fujifilm explicitly directs callers to poll the camera image buffer for shooting completion. A scan adapter must additionally download and persist every required file before returning paths. [M pp.126–139, §§4.1.7.2–4.1.8.5]
- **This package does not supply native Windows ARM64 DLLs.** Its Windows `64bit` DLLs are AMD64, not ARM64; `32bit` DLLs are I386. An ordinary ARM64 .NET WinUI process cannot load the AMD64 SDK in-process. Use an x64 SDK-owning helper if retaining a native ARM64 UI, or publish the whole app x64 as the simpler initial integration experiment. Neither alternative is Fujifilm-proven on this Surface. [B, A1–A2; M p.25 §1.1.1]
- **Tablet plus camera-card retention is documented.** `XSDK_SetMediaRecord` controls additional card recording, independently of downloading the camera's volatile transfer queue. Values are RAW+JPEG `1`, RAW `2`, JPEG `3`, OFF `4`—OFF is not zero. Query capabilities and read back the actual setting; do not confuse this with image-quality selection. [M p.42 §1.5; pp.183–185 §4.1.11; p.1012 §7.1; H lines 2236–2242]
- **Serialize the SDK globally and make captures transactional.** Fujifilm prohibits concurrent calls both within one process and across processes. One outstanding shutter transaction, exclusive queue ownership, RAW/JPEG pair validation, and no success-shaped partial downloads are the safest seam. [M p.34 §1.3; pp.134–139 §4.1.8; p.1012 §7.1; design in §8 below]
- **Two release gates remain:** pass hardware/Surface acceptance tests with the confirmed X-T5/latest-firmware configuration; recover the actual applicable SDK license/redistribution agreement, which was not found in this local package. Log the numeric firmware during connection rather than blocking this report on another question. Do not label the SDK runtime-ready or redistribution-cleared. [User confirmations; M pp.29–30 §1.2; local inventory L]

Timer-first implementation need not change. Later, its real camera seam should mean “this channel's exposure and complete requested files are finished,” not “a timer expired” or “the shutter API returned.” This is an application recommendation grounded in the immediate-return and queue-transfer contracts above, not a feature supplied by Fujifilm.

## 1. Evidence scope and citation key

**Evidence labels:** **SDK fact** means a statement in these local official files or their binary metadata; **platform fact** means separately identified Microsoft primary-source/host evidence; **design/inference** means the proposed application behavior; **unverified** means camera/Surface execution or a missing contract has not been established.

Every local relative filename below resolves under `C:\Users\migue\Downloads\SDK13410`. PDF references are **1-based PDF pages**, matching the printed page numbers in the sections inspected.

| Key | Exact primary source |
|---|---|
| M | `MANUAL\SDK_ProgrammingReference.pdf`, API Reference revision **1.34.1.0**, Jan.28,2026, 1,012 pages; cover p.1, revision history p.24. |
| H | `HEADERS\XAPI.H`; the common Windows C ABI and constants. Its introductory version comment is **1.10.0.0**, not the package/API DLL version (lines 1–8). |
| O | `HEADERS\XAPIOpt.H`; optional/model-dependent constants. |
| RB | `SAMPLES\Windows\ReleaseButton\ReleaseButton\CameraControl.cpp`; shutter/queue/cancellation example. |
| HF | `SAMPLES\Windows\HotFolder\HotFolder\MainDlg.cpp`; queue downloading and filesystem writes. |
| CS | `SAMPLES\Windows\Multiple\MultipleCameraControlSampleTool\FujiSDK.cs`; supplied C# structures, dynamic loading and Cdecl delegates. |
| CC | `SAMPLES\Windows\Multiple\MultipleCameraControlSampleTool\CameraControl.cs`; mutex-controlled SDK calls. |
| XF | `SAMPLES\Windows\XFileName\XFileName.cpp` and `XFileName.h`; MakerNote identification example. |
| B | Read-only PE COFF Machine fields, normal/delay import directories, and Windows `FileVersionInfo` for **every** DLL in `REDISTRIBUTABLES\Windows\32bit` and `REDISTRIBUTABLES\Windows\64bit`. Exact results summarized in §3 and §7. |
| L | Read-only recursive local package inventory, contents of the sample ReadMe files, and member-name inspection of all seven supplied ZIP and nine TAR.GZ archives; findings in §7.3. Negative findings are bounded to these sources, not to Fujifilm's download portal or agreements supplied separately. |
| A1 | Microsoft, [Arm64EC — Interoperability](https://learn.microsoft.com/en-us/windows/arm/arm64ec): an Arm64 process can only load Arm64 binaries; x64/Arm64EC interoperability is a different process/ABI arrangement. Directly fetched/read by the coordinating investigation session on 2026-11-05; the delegated local-SDK investigator used no web tools. |
| A2 | Microsoft, [How x86 and x64 emulation work on Arm](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-x86-emulation): Windows 11 x86/x64 emulation is user-mode; drivers must be native ARM64. Same provenance as A1. |
| A3 | Microsoft, [.NET cancellation in managed threads](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads): cancellation is cooperative, not forced termination. Same provenance as A1. |
| HOST | Coordinating session's independent local `dotnet --info` verification, 2026-11-05: Windows `10.0.26300`, RID `win-arm64`, SDK `10.0.302`, host/runtime `10.0.10` ARM64, other architectures **None**. This is host evidence, not an SDK support statement. |

The PDF was text-extracted with PDF.js because no usable Python or PDF command-line extractor was available; fonts emitted warnings, so columnar capability tables were not treated as reliable model mappings from flattened text. Relevant headers and prose were checked separately. Inspection scripts/text/dependencies were confined to the specified session artifact folder and removed after the report was written. No SDK calls, camera tests, app edits, SDK edits, browser research, other agents, or workflows were performed.

Source fingerprints, SHA-256:

```text
MANUAL\SDK_ProgrammingReference.pdf
72BD5D8096F2AA7F8A18E74DE8FD4C77110CAEA89E009A6FCB88F943043C7C54
REDISTRIBUTABLES\Windows\64bit\XAPI.dll
F8B75BD9B76E195B817142C95B2D0CCBECA3DDD9053C6723EEE47DA592B99CA9
REDISTRIBUTABLES\Windows\64bit\FTLPTP.dll
0563EDDA388B4FE6D55158D99CCBF8B8730D23F90F8B1CFDBE4388381E4FED64
REDISTRIBUTABLES\Windows\64bit\FTLPTPIP.dll
1E49651BFC5933E0F1A1C0C299E9904150B55BA7A6BE0D0FAA504415EACB261B
```

## 2. Camera compatibility: do not assume the user's model

### 2.1 Exact documented list and version matrix

**SDK facts.** The following is the current supported-camera list, with support-introduction versions from the revision history. A blank firmware threshold means **this local reference does not state a numeric minimum**, not “every firmware works.” Fujifilm asks users to upgrade to latest firmware. [M p.29 §1.2; p.24 Revision History]

| Camera | SDK support introduction recorded locally | Firmware qualification recorded locally |
|---|---|---|
| X-T3 | 1.20.0.0 initial release in this history | No numeric minimum stated |
| X-T4 | 1.20.0.0 | No numeric minimum stated |
| X-Pro3 | 1.20.0.0 | No numeric minimum stated |
| GFX 50S | 1.20.0.0 | Firmware **1.00 or 1.01: USB only**; Wi-Fi exception is firmware-qualified |
| GFX 50R | 1.20.0.0 | No numeric minimum stated |
| GFX 100 | 1.20.0.0 | No numeric minimum stated |
| GFX100S | 1.20.0.0 | No numeric minimum stated |
| X-S10 | 1.21.0.0 | **2.00 or later** |
| GFX50S II | 1.22.0.0 | No numeric minimum stated |
| X-H2S | 1.23.0.1 | No numeric minimum stated |
| X-H2 | 1.24.0.0 | No numeric minimum stated |
| X-T5 | 1.25.0.0 | No numeric minimum stated |
| X-S20 | 1.27.0.0 | No numeric minimum stated |
| GFX100 II | 1.28.0.0 | No numeric minimum stated |
| GFX100S II | 1.29.0.0 | No numeric minimum stated |
| X-M5 | 1.31.0.0 | No numeric minimum stated |
| GFX100RF | 1.33.0.0 | No numeric minimum stated |
| GFX ETERNA 55 | 1.34.0.0 | Filmmaking exception; **not a supported solution for this RAW/JPEG still-scan requirement** |

The GFX 50S wording does not give a precise newer-firmware Wi-Fi minimum; do not invent one. The Wi-Fi section expressly excludes firmware 1.00/1.01. GFX ETERNA 55 has no still-image shooting/volatile still-image storage, and some “mandatory” common APIs are unavailable; its exception list omits `XSDK_ReadImage`, `XSDK_DeleteImage`, and media-record control. [M pp.29–30 §§1.2–1.2.2; p.42 §1.5; pp.46,53 §3; `HEADERS\GFXETERNA55.h` lines 182–183 marks model-dependent Set/GetMediaRecord parameters unsupported]

Older X-T1/X-T2/X-Pro2 names appear in some model-dependent reference tables, but they are **not** in the package's current supported-camera list. Those table headings must not expand the compatibility claim. [M p.29 §1.2 versus pp.330–331 §§4.2.5.4–4.2.5.5]

**Configuration resolved for this report: X-T5 with newest firmware, as reported by the user.** No further model/firmware question is required to finish the investigation. Use USB tethering for the initial integration proof; RAW+JPEG on tablet and card is a useful acceptance-test configuration, not a newly imposed product requirement. Make the final required formats explicit in adapter configuration before scanning. Log the numeric body/lens firmware and focus configuration during connection/acceptance testing; SDK getters exist for body/lens versions. [User confirmations; M pp.107,109 §§4.1.5.4,4.1.5.6; design recommendation]

### 2.2 Version identifiers are not interchangeable

The document/package is 1.34.1.0; revision 1.34.1.0 corrects document errors after 1.34.0.0 added ETERNA 55. Supplied `XAPI.dll` file/product version is **1.17.0.0**, transport libraries have their own versions, and H's old comment says 1.10.0.0. These do not show a missing DLL merely because their numbers differ. At actual startup log `XSDK_GetVersionString`, body firmware, DLL versions/hashes and the model; do not silently substitute old sample DLLs. [M pp.1,24,34–35; p.102 §4.1.4.3; H lines 1–8; B version resources]

## 3. Windows ARM64 and deployment choices

### 3.1 What is actually supplied

**Binary facts:** both Windows folders contain `XAPI.dll`, `FTLPTP.dll`, `FTLPTPIP.dll`, `FF0000API.dll` through `FF0022API.dll`, and `XSDK.DAT`—26 DLLs plus one data file per architecture. All 26 `32bit` DLLs have COFF Machine **0x014C (I386)**; all 26 `64bit` DLLs have **0x8664 (AMD64)**. No Windows ARM64 DLL directory or ARM64 Machine binary occurs in these redistributable folders. For reproducibility, `32bit\XAPI.dll` has `e_lfanew=0x108`, Machine at file offset `0x10C`; `64bit\XAPI.dll` has `e_lfanew=0x118`, Machine at `0x11C`. [B; local filenames above; M pp.34–35 §1.4.1]

The Windows reference specifies **Intel CPUs**, x86/x64 libraries, Windows 7/8.1/10 and Windows 11, with testing under Japanese Windows and Visual Studio 2017 C++. It does **not** state support/testing for Surface ARM64, .NET 10, WinUI 3 or Windows-on-ARM emulation. Linux ARMv8 and Android arm64-v8a artifacts are different operating-system binaries, not a Windows ARM64 solution. [M p.25 §1.1.1; pp.27–28 §§1.1.3–1.1.4; `REDISTRIBUTABLES\Linux\Linux64Armv8.tar.gz`; `REDISTRIBUTABLES\Android\arm64-v8a\XAPI.so`]

### 3.2 Candidate fallback architectures

**Platform facts:** ordinary native ARM64 processes cannot load AMD64 DLLs. Arm64EC is not a switch that makes ordinary ARM64 .NET P/Invoke accept these DLLs. Windows 11 can emulate x64/x86 user-mode applications, but any necessary kernel driver must be ARM64. This establishes possible process architecture arrangements, **not camera-driver compatibility or successful SDK operation**. [A1–A2]

| Candidate | Practical arrangement | Status and trade-off |
|---|---|---|
| Native ARM64 UI + x64 helper | ARM64 WinUI/.NET host sends channel/request ID over local IPC. A single `win-x64` helper owns the full AMD64 SDK, session, queue, downloads and transaction. It returns completed paths, not native pointers/handles. | Recommended if native UI is a hard requirement. Adds IPC and deployment work but isolates native hangs/failures. OS-feasible inference; Fujifilm/Surface runtime unverified. [A1–A2; M p.34 §1.3] |
| Entire app x64 | Publish WinUI/.NET and all its native dependencies x64; call SDK in the emulated x64 process. | Simplest first SDK/camera proof and fewer moving parts. Sacrifices native ARM64 execution. Appropriate x64 WinUI/Windows App SDK and .NET deployment dependencies still need validation; not proved by this SDK. [A2; M p.25 §1.1.1] |
| Entire app x86 | Use I386 SDK with x86 process emulation. | Technically another user-mode fallback, but not preferred for high-resolution RAW buffer allocation and address-space constraints; no evidence of a benefit over x64. [A2; H lines 117–125; design inference] |
| Native ARM64 UI directly P/Invoking supplied `64bit` DLLs | Architecture mismatch. | **Not viable ordinary in-process interop.** [A1; B] |

HOST reports no installed x64 .NET runtime. A managed x64 helper/whole app consequently needs an x64 runtime deployment or a self-contained `win-x64` publication; a native C++ helper instead needs its own chosen native runtime arrangement. This is a deployment inference from HOST, not proof that installing a runtime solves camera transport. A self-contained .NET publish also does not automatically establish that all WinUI/Windows App SDK dependencies are present. [HOST; A1–A2]

Run only one SDK-owning process. A helper plus another SDK-using sample, or two helpers making concurrent calls, violates Fujifilm's stated limit. Our own cross-process mutex cannot serialize an unrelated third-party tether application that does not honor it; close such tools during operation and tests. [M p.34 §1.3; design consequence]

## 4. Transport and camera setup

**USB SDK facts.** All compatible models support USB tethered shooting. Select the camera's tethered connection mode rather than USB card-reader/RAW-development/webcam operation. The reference gives PC SHOOT AUTO, USB AUTO or USB TETHER SHOOTING AUTO; specifically X-H2S uses `NETWORK/USB SETTING MENU > SELECT CONNECTION SETTING > USB TETHER SHOOTING AUTO`. Exact menus for an unspecified camera must come from that camera's Owner's Manual/New Feature Guide, which is not included here. ETERNA 55's always-available USB control is an exception, not useful evidence for still scans. [M pp.29–30 §1.2.1]

**Network SDK facts.** Wi-Fi tethering is documented except GFX 50S firmware 1.00/1.01. X-H2S/X-H2/X-T5/X-S20 setup is delegated to their Network and USB Settings manuals. Other models' examples configure automatic/manual IPv4 assignment, access-point setup, then WIRELESS FIXED/WIRELESS TETHER SHOOTING FIXED and verify the camera's IP. Detection can search the local segment or use specified IPv4 addresses. This is local tethering, not evidence of cloud or Bluetooth capture support. [M pp.30–32 §1.2.2; p.77 §4.1.2.1]

The common discovery constants are USB **0x1**, local Wi-Fi/network **0x10**, specified IPv4 **0x20**, combined by bitwise OR. `XSDK_Detect(interface, ipFilterOrNull, modelFilterOrNull, &count)` builds a camera list; `XSDK_Append` updates/enumerates its entries. `XSDK_OpenEx` accepts a null-terminated ASCII `ENUM:<index>` or `IPv4:<address>`, returns a camera handle and connection-mode bitmap, and takes `pOption=NULL`. Require `XSDK_DSC_MODE_TETHER=0x1`; RAW-development `0x2`, backup/restore `0x4`, and webcam `0x8` are not interchangeable scan sessions. [H lines 258–267,2318–2324; M pp.77,85–89 §§4.1.2–4.1.3.1]

The camera-list structure can report framework `USB`, `ETHER` or `IP`; this and TCP/IP library presence show a transport vocabulary, **not** a complete Ethernet-adapter/model/accessory compatibility matrix. Wired Ethernet, accessory requirements, firewall ports, network security and tablet network reliability remain model-specific/unverified. [H lines 52–59; M p.86 §4.1.2.2; B `FTLPTPIP.dll`]

**Recommendation:** start acceptance testing over direct USB with an adequate data cable and power, then qualify network separately if required. Do not infer USB driver readiness from a connected cable or successful DLL loading. [M pp.25,29–32; A2; design inference]

## 5. Shutter acceptance, exposure completion and readiness

### 5.1 Exact shutter APIs and mode selection

The common signatures are:

```c
long XSDK_Release(
    XSDK_HANDLE hCamera, long lReleaseMode,
    long* plShotOpt, long* plAfStatus);
long XSDK_ReleaseEx(
    XSDK_HANDLE hCamera, long lReleaseMode,
    long* plShotOpt, long* plAfStatus);
```

**SDK facts:** `Release` operates in **PC priority**; `ReleaseEx` operates in **camera priority**. Query the matching `XSDK_CapRelease`/`XSDK_CapReleaseEx` before choosing operations. **Both require half-press before full-press**, and both full-press calls return immediately. `plShotOpt` is an IN/OUT shot-count parameter, not a future or completion handle; `plAfStatus` reports AF/custom-WB status where available, not download completion. `XSDK_COMPLETE=0` and `XSDK_ERROR=-1`. [H lines 2296–2299,2344–2350; M pp.124–132 §§4.1.7.1–4.1.7.4]

Use a capability-confirmed two-step single-shot sequence:

| Priority | Half-press | Full-press and release |
|---|---|---|
| PC | `XSDK_Release(..., XSDK_RELEASE_S1ON=0x0200, ...)` | `XSDK_Release(..., XSDK_RELEASE_S2_S1OFF=0x0304, ...)` |
| Camera | `XSDK_ReleaseEx(..., XSDK_RELEASE_EX_S1_ON=0x00010000, ...)` | `XSDK_ReleaseEx(..., XSDK_RELEASE_EX_S2_ON_S2_OFF_S1_OFF=0x00020003, ...)` |

Set the intended count to one and evaluate AF/error policy before full-press. These exact combinations are used in the supplied ReleaseButton sample; do not assume “SHOOT” or a combined Ex constant removes the reference's half-press requirement. [H lines 273–355; RB lines 728–758,868–896; M pp.126,131]

Legacy cameras listed in §1.7 require the distinction between camera-controlled and PC-controlled operation; default power-on mode is camera priority. Recent models support PC priority mainly for compatibility and can use camera-priority control. ETERNA is camera-priority-only. Query `CapPriorityMode`, `GetPriorityMode`, and release capabilities rather than assuming that the unspecified camera accepts Ex. [M p.44 §1.7; pp.110–113 §4.1.6; RB lines 393–396,736–745]

### 5.2 What each signal actually establishes

| Observation | Documented meaning | What it does **not** establish |
|---|---|---|
| Full-press returns `XSDK_COMPLETE` | Trigger API succeeded; returns immediately | End of exposure, image downloaded, or file saved |
| `GetReleaseStatus` | Bitmap of operations/locks: S1, BULB, AF, AEL/AFL/WBL, SHOOTING | A general “one exposure completed” callback or disk completion |
| `SHOOTING` bit `0x0100` | Manual explicitly calls this **burst photography in progress** | Do not reinterpret it as a universal single-shot exposure-completion bit |
| `GetBufferCapacity` | Number of frames shot and total frame capacity | Identity of all RAW/JPEG objects, their ordering, or local files |
| Successful `ReadImageInfo` with non-NONE format | Information for **top of camera queue**, including size and format | Bytes have not yet been downloaded; object may be stale or unrelated |
| Successful `ReadImage` | Captured queue-head bytes copied to caller buffer; entry removed | Durable tablet file, complete RAW+JPEG pair, or verified card write |
| Successful checked file writes/flush/close and transaction publication | Application-established local persistence | This is adapter behavior, not an SDK callback |

Sources: [M pp.126,131,133–139 §§4.1.7–4.1.8; H lines 368–394,2353–2357; HF lines 703–709,793–822].

Fujifilm's prescribed completion evidence is polling `GetBufferCapacity` or `ReadImageInfo`. **No separate timestamped end-of-exposure signal/capture-completed callback is documented in the inspected common capture ABI.** That negative finding is bounded to H's common declarations and these reference sections; it does not deny unrelated camera features. For this scan, successful retrieval of a newly correlated still image is the practical documented completion evidence; a precise physical shutter/illumination boundary must be measured on hardware if required. [M pp.126,131; H lines 2314–2425; unverified physical timing]

“Ready” also is not one universal bit. Priority, shooting/playback, mode, drive, focus, buffer capacity and camera busy state are different dimensions. Check `GetMode` and `GetDriveMode`, and physically select still/single mode where necessary: mechanical dials/switches override software, `SetDriveMode`/`SetMode` cannot override certain mechanical selections, and some `GetDriveMode` responses are coarse compatibility values. A helper's application `Ready` state must therefore include verified configuration, queue ownership and no unresolved prior shot, not just a nonbusy getter. [M pp.43–44 §§1.6–1.7; pp.114–123 §§4.1.6.4–4.1.6.9]

`XSDK_GetErrorDetails` is a **busy-error diagnostic**, called after `GetErrorNumber` reports `RUNNING_OTHER_FUNCTION`; its flags include shooting, self-timer, live view, recording and untransferred images. Do not promote it to an unconditional readiness/exposure-event API. [M p.101 §4.1.4.2; H lines 2251–2261]

## 6. RAW/JPEG downloads, all objects, pairing and card copies

### 6.1 Queue transfer, not card-filesystem enumeration

**SDK facts:** tethered stills enter camera **volatile storage**. The SDK can automatically copy them to the camera card, but **card files/data cannot be accessed through the SDK in tethered shooting modes**. There is no common capture API here equivalent to “enumerate every file on the SD card.” “Download all objects” for this adapter must mean drain the relevant transfer queue by repeatedly reading its head; not list the entire card. [M p.42 §1.5; pp.134–139 §4.1.8; H lines 2352–2357]

For each queue head:

1. Call `XSDK_ReadImageInfo(hCamera, &info)`. Distinguish an API error from successful empty-queue reporting: `(info.lFormat & 0x00FF)==XSDK_IMAGEFORMAT_NONE`, value **5**.
2. Mask orientation separately. RAW is **1**, JPEG **7**, live JPEG **4**, HEIF **0x12**. A live JPEG is not the required still JPEG.
3. Validate positive, plausible `lDataSize`; allocate a buffer of that size; call `XSDK_ReadImage(hCamera, data, size)`. On success, the queue entry has already been consumed.
4. Persist the returned bytes to a transaction-private file, check the whole byte count and close/flush successfully, then repeat `ReadImageInfo`.

These SDK contracts are explicit; size bounds, staging and checked persistence are application safeguards. [M pp.134–139; H lines 117–126,377–394,2354–2357; HF lines 679–709,805–820]

**Never call `DeleteImage` after successful `ReadImage`.** ReadImage already removes the head; DeleteImage would remove the next object, potentially the JPEG/RAW partner. `ReadPreview` is different: it returns a JPEG preview where supported and does **not** consume the image; it cannot satisfy a RAW/full-JPEG download requirement. [M pp.136–138 §§4.1.8.2–4.1.8.4]

No chunked/stream-to-filename API or returned actual-byte-count is present in these common ReadImage declarations. They accept one caller buffer and its allocated size, returning success/error. Thus memory budgeting and complete-buffer integrity matter for large GFX RAW images. Failure-consumption semantics, interrupted-read recoverability and internal blocking timeout are not specified here. [H lines 2354–2357; M p.137; unverified failure semantics]

### 6.2 RAW+JPEG pairing

The reference recommends the same output basename for the two images and, more importantly, states that the RAW and JPEG members of a shot share a **MakerNote sequential file number**. If card recording is enabled, MakerNote also carries the card filename. The supplied XFileName parser reads sequential-number tag **0x1438** and media-path tag **0x1439**. [M p.137 §4.1.8.3; p.1012 §7.1; XF lines 244–275,288–331]

**Design:** correlate by camera identity + session epoch + MakerNote shot number, then by expected format. Name tablet files with scan/request ID and R/G/B channel, with `.RAF` and `.JPG` sharing a basename. Do not use queue adjacency, internal filename alone, arrival order, wall-clock timestamp, or “first two objects” as the sole pairing proof. The local manual does not guarantee globally unique counters across camera resets, wraparound, firmware changes or reconnects; do not compare counters as permanently monotonic without qualification. [M p.1012; H lines 117–126; inference/unverified lifetime guarantees]

The SDK does not provide RAW conversion. Download original RAW/JPEG bytes; any later decoding/scan processing is a separate capability. [M p.25 §1]

Treat the XFileName source as an example to harden, not a complete trusted parser: the manual limits it to little-endian and says it is not error-free; source returns “no information” when metadata is absent. Validate every metadata offset and length before dereferencing. Missing pair identity should cause an explicit unverified/failed transaction or a separately hardware-qualified fallback—not silent guessing. [M p.1012 §7.1; XF lines 234–275,288–331]

### 6.3 Exact save-mode values and restrictions

`XSDK_CapMediaRecord` → `XSDK_SetMediaRecord` → `XSDK_GetMediaRecord` controls **card recording during tethering**:

| Value | Constant | Camera-card copies |
|---|---|---|
| `0x0001` | `XSDK_MEDIAREC_RAWJPEG` | RAW and JPEG, subject to supported camera configuration |
| `0x0002` | `XSDK_MEDIAREC_RAW` | RAW |
| `0x0003` | `XSDK_MEDIAREC_JPEG` | JPEG |
| `0x0004` | `XSDK_MEDIAREC_OFF` | No additional card recording |

H also aliases `RAWJPEGHEIF` to `RAWJPEG` and `JPEGHEIF` to `JPEG`; the numeric mode alone is not proof that the compressed still is JPEG rather than HEIF. [H lines 2236–2242; M pp.183–185 §4.1.11]

To retain both tablet and card copies, enable the desired supported media-record mode **before** shutter and still download all required queue entries. For tablet-only, use supported `OFF=4`, still performing ReadImage. The reference connects enabled MediaRecord to card copies; transfer consumption affects volatile queue storage, not an API request to delete card files. Card retention therefore does not replace or complete tablet downloading. [M p.42 §1.5; p.137 §4.1.8.3; p.1012 §7.1; design sequencing]

This is **not** the same property as the camera's output image quality. For producing RAW+JPEG on the tablet, configure/read back model-dependent `ImageQuality` and, where applicable, JPEG/HEIF format separately. O defines RAW `1`, JPEG Fine `2`, JPEG Normal `3`, RAW+Fine `4`, RAW+Normal `5`, SuperFine `6`, RAW+SuperFine `7`; supported values are model-dependent. For example, `HEADERS\X-T5.h` lines 36–38 defines Cap/Set/GetImageQuality parameter shapes; that is an illustrative source, not an assumption that the user's camera is X-T5. [O lines 570–581; M pp.330–332 §§4.2.5.4–4.2.5.5; H lines 2422–2425]

**Restrictions/unknowns:** common MediaRecord APIs are documented for compatible still cameras with Set/Get noted as SDK 1.1+, and capabilities must be queried. This package does not provide a numeric firmware-by-model-by-card-mode matrix or a promise of identical slot behavior, card-less behavior or mode persistence after reconnect. ETERNA is excluded as explained in §2. RAW-only/JPEG-only card mode versus independently configured RAW+JPEG transfer behavior must be tested, not inferred from names. Required card retention must also fail explicitly on card-full/write-error; a successful tablet download is not independent proof of a completed card write. [M pp.183–185; pp.42,53; p.1005 §5; H lines 2288–2289]

## 7. Native interop, lifecycle, dependencies and redistribution

### 7.1 Threading, lifecycle and marshaling

- **Serialization is mandatory across all SDK functions and cameras**, not merely per camera. Fujifilm explicitly prohibits simultaneous calls from a single process and from multiple processes. HF protects read-info/read-image and device checks with a critical section; CC uses a mutex. A sole SDK-owning helper/worker is the simplest controlled implementation. [M p.34 §1.3; HF lines 679–709,852–873; CC lines 143–182]
- **Lifecycle:** load XAPI → `XSDK_Init(HMODULE)` → Detect/Append → OpenEx → session APIs → Close → wait **at least 600 ms** → Exit → unload. Init takes XAPI's loaded HMODULE to locate lower-layer libraries when files are not beside the executable. SDK state names are Loaded S0, Initialized S1, Detected S2, Session S3; Exit is allowed in S1/S2, so close sessions first. [M pp.25,45,75–77,89,96,98 §§1.1.1,2,4.1.1–4.1.3; CS lines 980–987 passes module handle]
- **Do not call `PowerOFF` as routine disposal.** It closes the session and powers down the camera, requiring explicit restart action; normal teardown should use Close. Never unload while a native call or download is still in flight. [M pp.98–99 §§4.1.3.3–4.1.3.4; inference on safe lifetime]
- **Calling convention:** Windows H defines exports as `long` and `extern "C"` with no WINAPI/stdcall annotation; the supplied C# delegates explicitly use **Cdecl**. Windows native `long` is 32-bit here, reflected by `int` parameters/returns in CS; do not map it to C# `long`/Int64 because the DLL folder says 64bit. Native `unsigned long` data size maps to `uint`. [H lines 18–26,2310–2357,2439–2482; CS lines 682–764]
- **Handles:** H uses Windows HANDLE/HINSTANCE, and CS uses `IntPtr`. Map camera/image/library handles to pointer-sized values; camera handles are SDK-owned and closed with `XSDK_Close`, not blindly with Win32 `CloseHandle`. `hImage` appears in the actual header and supplied C# image structure although the manual's printed struct omits it. Follow the actual ABI, not that incomplete manual struct. No image-handle release function is documented in this acquisition API. [H lines 18–21,117–126,2322–2324; CS lines 56–68,694–701; M p.134]
- **Packing:** H explicitly uses `#pragma pack(1)` for structs. ImageInformation contains fixed 32-byte name + six 32-bit fields + pointer: calculated size **60 bytes x86, 64 bytes x64**. CameraList is four 256-byte arrays plus a 1-byte C++ bool: calculated packed size **1,025 bytes**. DeviceInformation is calculated **1,857 bytes**. Add native/managed size/offset tests before using interop. CS's CameraList declaration lacks Pack=1 even though other structs have it; do not copy its default array stride into new bindings without correcting/testing the header layout. [H lines 52–76,115–127; CS lines 18–87; calculated ABI consequences]
- **Strings:** OpenEx explicitly requires null-terminated ASCII; header strings are fixed native `char` arrays/LPSTR, not UTF-16 arrays. Prefer fixed byte buffers with explicit encoding/termination, ASCII/ANSI input marshaling and correctly allocated output buffers. VersionString requires 256 bytes. C# `char` is not inherently a native byte; do not rely on casual `char[]` conversions in sample declarations. Final tablet output paths are .NET/filesystem paths, not a SDK filename argument. [M pp.89,102; H lines 52–76,117–126,2319–2334; CS lines 18–68,688–719]
- **Optional properties:** `XSDK_CapProp/SetProp/GetProp` are **variadic** C functions and depend on the actual model's API codes/parameter shapes. Avoid a generic guessed P/Invoke; use explicitly verified typed bindings or a small typed x64 native shim. Keep timer-first free of this complexity; basic trigger/download can use common APIs once hardware configuration is correct. [H lines 2422–2425; M p.46 §3; pp.330–332; `HEADERS\X-T5.h` lines 36–38]

The relevant SDK calls return success/error directly; trigger completion and queue arrival are polled rather than delivered through a documented common capture callback. Native ReadImage is a direct caller-buffer operation, with no CancellationToken/cancel-handle parameter. A worker off the WinUI UI thread is a design recommendation; maximum native blocking time and general thread affinity are not specified in these contracts. Do not equate “async .NET wrapper” with an interruptible native SDK call. [M pp.126,131,134–139; H lines 2344–2357; A3]

**COM/apartment caveat:** `FTLPTP.dll` imports COM functions, HotFolder initializes COM with `CoInitialize(NULL)` in `SAMPLES\Windows\HotFolder\HotFolder\HotFolder.cpp` line 83, and the C# sample entry point has `[STAThread]` in `SAMPLES\Windows\Multiple\MultipleCameraControlSampleTool\Program.cs` line 11. These are evidence of sample initialization, **not** an explicit requirement that every SDK call must execute on one STA or that a message pump is mandatory. A dedicated owner thread with deliberate COM initialization is conservative; qualify its apartment/pump behavior on hardware rather than asserting an undocumented SDK threading contract. [B; sample locations above]

### 7.2 DLLs, WPD transport, drivers and runtimes

Deploy the matched architecture's complete redistributable set together, including `XSDK.DAT`. The Windows reference says related DLLs must be in the same folder as XAPI; it recommends LoadLibrary/GetProcAddress and finally FreeLibrary. Model modules are loaded beneath XAPI, so absence of a static import from XAPI to FFxxxx does not make those DLLs optional. Prefer the official REDISTRIBUTABLES folder over DLL copies embedded in sample output directories. [M pp.25,34–35,46 §3; CS lines 449–476,508–510]

**Read-only binary findings (both architectures):**

| Local DLL(s) | Normal PE imports; delay-import lists empty |
|---|---|
| `REDISTRIBUTABLES\Windows\{32bit,64bit}\FF0000API.dll` through `FF0022API.dll` | `KERNEL32.dll`, `USER32.dll`, `VERSION.dll` |
| `...\XAPI.dll` | `KERNEL32.dll`, `USER32.dll`, `ole32.dll`, `VERSION.dll` |
| `...\FTLPTP.dll` | `KERNEL32.dll`, `USER32.dll`, `ole32.dll`; imports include `CreateFileW`, `CoCreateInstance` |
| `...\FTLPTPIP.dll` | `KERNEL32.dll`, `ole32.dll`, `WS2_32.dll`, `IPHLPAPI.DLL` |

Here `{32bit,64bit}` denotes the two exact directories inspected, not a literal filename. Findings are from B's PE Import and Delay Import directories; dynamically loaded dependencies/COM servers are outside that static result.

Windows version resources identify `FTLPTP.dll` as **“FUJIFILM PTP Transport Library for USB(WPD Version)”**, version **3.3.3.0**; `FTLPTPIP.dll` as TCPIP transport, **1.3.2.3**; XAPI **1.17.0.0**. FF module versions match M's redistributable list, ranging from 1.0.0.0 to 1.4.0.0; retain the matching set rather than assuming all modules must share XAPI's version. [B version resources; M pp.34–35]

**Boundary:** WPD wording is real DLL metadata, not a speculative conclusion from a generic device class. It still does not prove which camera/USB driver binds on this Surface, which COM components will load under x64 emulation, or that an inbox driver suffices. No Windows `.inf`, `.sys`, camera-driver installer, or redistributable-runtime installer was found in this package inventory. Do **not** claim a required Fujifilm vendor-driver installation or declare “driver-free” based on that absence. `NODRIVER` is described simply as “No camera found,” so that error name alone also is not a driver-install prescription. [B; L; M p.1005 §5; A2]

No `VCRUNTIME`, `MSVCP`, MFC or UCRT DLL appears in the inspected normal/delay imports of the SDK DLLs. This narrows their apparent direct runtime dependencies; it does not prove there are no dynamic/transitive prerequisites or runtime needs for a rebuilt helper/WinUI app. The HotFolder sample's project uses v120 and release `MultiThreaded` runtime settings (`SAMPLES\Windows\HotFolder\HotFolder\HotFolder.vcxproj` lines 28–49,157,187); that is sample-build evidence, not an obligation to install VS2013 or a VC runtime for this SDK. [B; exact project ranges above]

### 7.3 License and redistribution: explicit missing source

**Found:** M labels §1.4's named Windows DLL/data files “Redistributable Files”; source headers contain Fujifilm copyright notices. M is marked CONFIDENTIAL, and sample ReadMe files are project-overview text rather than an SDK agreement. [M pp.34–35 §1.4.1; H lines 1–8; `SAMPLES\Windows\HotFolder\HotFolder\ReadMe.txt` lines 1–14; `SAMPLES\Windows\ReleaseButton\ReleaseButton\ReadMe.txt` lines 1–14]

**Not found locally:** a license/EULA/terms document establishing commercial-use permissions, permitted packaging, attribution/notice duties, distribution territory, sublicensing constraints, source/sample redistribution, or other enforceable SDK redistribution conditions. No license-like member names were found when inspecting:

- `REDISTRIBUTABLES\macOS\SDK_13400.zip`;
- `SAMPLES\Android\HotFolder\HotFolder.zip`, `Liveview\LiveView.zip`, `ZoomPos\ZoomPos.zip`;
- `SAMPLES\macOS\LiveView\LiveView.zip`, `Multiple\MultipleCameraControl.zip`, `ZoomPos\ZoomPos.zip`;
- `REDISTRIBUTABLES\Linux\Linux32Armv7.tar.gz`, `Linux64Armv8.tar.gz`, `Linux64PC.tar.gz`;
- `SAMPLES\Linux\HotFolder\HotFolder.tar.gz`, `Multiple\MultipleCameraControl.tar.gz`, `ZoomPos\ZoomPos.tar.gz`;
- `SAMPLES\Linux\Liveview\Linux32ARMv7\LiveViewTool.tar.gz`, `Linux64ARMv8\LiveViewTool.tar.gz`, `Linux64PC\LiveViewTool.tar.gz`.

Paths in grouped bullets retain their displayed parent directory. The recursive unpacked inventory also contained no separately named license/EULA/COPYING/terms file. This is a bounded negative inventory finding, not a legal determination or proof that no separately accepted agreement exists. Archive member names were inspected; unrelated platform source trees were not exhaustively legally audited. [L]

**Release action:** obtain the agreement accepted when downloading/receiving SDK13410, or Fujifilm's applicable redistribution terms, and record its version and required notices/packaging before distribution. The reference's redistributable list identifies candidate deployable files; it does **not** let this investigation invent the missing obligations or declare the package unrestricted. No warranty analysis is needed for the engineering decision. [M §1.4; L; unresolved legal permission]

## 8. Proposed simplest reliable camera seam

Everything in this section is **design**, not an SDK-provided managed adapter. Its constraints derive from global serialization, immediate-return shutter, volatile FIFO acquisition, MakerNote pairing and cooperative cancellation. [M pp.34,42,126–139,1012; H lines 2344–2357; A3]

Conceptual contract:

```text
CaptureAsync(channel: R|G|B, cancellationToken)
    -> complete IReadOnlyList<string> of final tablet paths
    OR cancellation/error; never a success result containing a partial set
```

The channel tags the application transaction; it is not a Fujifilm shutter parameter. The scan orchestrator controls illumination/settling and must retain the channel/scan lease until the adapter's transaction or recovery has ended. A timer simulator may implement the same orchestration seam during timer-first development, but cannot advertise real camera files or camera completion.

### 8.1 Owner and state machine

```text
Unloaded → Initialized → Detected → Open → Configure/Quarantine → Ready
Ready → AcquireLease → Preflight → HalfPress → FullPressAccepted
      → AwaitNewImage → DownloadExpectedSet → Validate/Commit → Ready
Any uncertain shot/read/disconnect → Recovering or Faulted (never directly Ready)
Any teardown → wait for in-flight work → Close → ≥600 ms → Exit → Unloaded
```

1. **One owner, one capture at a time.** One dedicated SDK executor (in the x64 helper or whole x64 app) serializes every native call. Hold a separate **transaction lease** through exposure, all downloads and cleanup; serialize R, then G, then B. A per-call lock alone is insufficient because another read thread could consume our RAW/JPEG between calls. Pause live view and any background queue consumer. Scan-level orchestration must prevent the next light channel from activating while cleanup still owns a possibly exposing camera. [M p.34; queue contract pp.134–139; design consequence]
2. **Configure and preflight before shutter.** Verify selected camera identity/firmware, TETHER mode, priority/release capabilities, still/single mode, desired output quality and JPEG-versus-HEIF setting, supported/read-back card mode, available memory/disk space and no unresolved prior shot. Physical dial positions require operator setup where software cannot change them. Do not change save mode after firing as a readiness probe. [M pp.29,43–44,89,114–123,183–185,330–332; design]
3. **Establish a clean ownership boundary.** Download preexisting queue entries to a clearly separate quarantine location before a new scan, rather than silently deleting potentially valuable images. Record their identities/counters and reset only the application's session epoch when reconnecting. Empty queue is necessary, but a quiet interval alone cannot prove an old exposure will not arrive late. If a previous exposure's disposition is unknown, remain Recovering/Faulted until it is resolved or a hardware-qualified/manual re-arm establishes a clean boundary. Never assign its late arrival to the next channel. [M pp.42,134–139,1012; unverified delayed-arrival guarantees]
4. **Check cancellation before half/full press.** If canceled before full press, release any held half-press using the capability-confirmed OFF operation and return cancellation with no success paths. If still active, perform the two-step shutter sequence, check each return/error and AF policy, then record “trigger accepted.” Do not retry an ambiguous full-press blindly: an error/transport timeout might still accompany an exposure on hardware. [M pp.126,131; H lines 292–355; RB lines 59–99; A3; design on ambiguous acceptance]
5. **Await a new still image, not merely idle.** Poll ReadImageInfo under a bounded transaction deadline; distinguish NONE, transient BUSY and fatal error. Use buffer/status diagnostics as secondary context. The image must belong to this session/request's clean post-trigger boundary and expected MakerNote identity. Do not require observing a short-lived SHOOTING transition that the SDK only documents for bursts. [M pp.126,131,133–139,1005,1012]
6. **Download the entire expected set.** RAW mode requires one RAW; JPEG mode one still JPEG; RAW+JPEG both for the same MakerNote shot number. Drain/process each relevant queue head; never stop at the first image, first empty observation between partners, or a frame count interpreted as an object count. An unexpected shot ID/duplicate/wrong format is a correlation failure, not an extra success file. Quarantine unrelated objects where safely downloadable; do not delete unseen user images just to advance the queue. [M pp.134–139,1012; design stricter than sample]
7. **Publish only complete data.** Validate metadata/file signatures and expected sizes; write each file to private staging, check full persistence, then publish a complete transaction only after every required member passes. Same-volume renames can stage individual files, but multiple renames are not an atomic group: use private directory/commit bookkeeping and return paths only after the group is committed. On any failure, withhold all success paths; retain/quarantine artifacts for recovery without presenting them as a complete scan. [HF lines 703–709,793–822; design filesystem transaction]
8. **Re-arm after verified completion.** Confirm no held controls, unprocessed transaction members or unresolved shot remain before releasing the lease. Only then may the orchestrator switch from R to G or G to B. SDK/card configuration can persist unpredictably across power/reconnect; revalidate rather than reuse cached assumptions. [M pp.126–139,183–185; design/unverified persistence]

The official ReleaseButton sample is **not** this seam: its polling loop calls `DeleteImage` instead of downloading (RB lines 612–626), and its post-trigger “completion” path tries `SetMediaRecord(RAWJPEG)` as a busy probe (RB lines 902–926). The HotFolder example demonstrates downloads but allocates/writes one queue entry at a time and increments an application sequence per entry; it does not establish RAW+JPEG transactional pairing. Some write/rename errors only have comments, so it must not be copied as a success-safe persistence layer. [HF lines 698–709,760–820; RB cited ranges]

### 8.2 Cancellation, deadlines, errors and recovery

- **Cancellation after shutter is not physical rollback.** A .NET token cannot forcibly interrupt ReadImage or un-expose a sensor. Prefer to finish/drain the already-triggered transaction into canceled/quarantine storage under an internal cleanup deadline, never return its paths as success, and keep the camera/channel lease held until safe recovery. The caller may see cancellation earlier only if orchestration separately knows cleanup is still busy and cannot start another channel. [A3; H lines 2344–2357; design]
- Fujifilm's `XSDK_RELEASE_CANCEL=0x000F` and `XSDK_RELEASE_EX_CANCEL=0x00000010` are described as canceling **long-time exposure** in progress. Use them only when capability-supported and appropriate; they do not promise cancellation of every single-shot exposure or transfer. Always reconcile possible produced images afterward. [M pp.125,129; H lines 309,344; RB lines 87–99]
- Use phase deadlines for preflight/AF, image arrival, transfer and cleanup, plus one overall transaction deadline. Initial poll delays may be tens/hundreds of milliseconds, but choose actual limits from measured exposure, processing and USB/network throughput—not an arbitrary short timer. Managed deadlines bound when to declare fault; they cannot bound a hung native call that has not returned. [M pp.126,131,137; A3; design]
- Immediately capture `XSDK_GetErrorNumber` and, when indicated, GetErrorDetails **inside the same serialization scope** after a failed call; it describes the last call. Record API code, error, phase, request/channel, identities and diagnostic flags. [M pp.100–101 §§4.1.4.1–4.1.4.2]
- BUSY `0x1006`, FORCEMODE_BUSY `0x1016`, RUNNING_OTHER_FUNCTION `0x1017` can justify bounded retries/diagnostics for appropriate nonshutter operations. COMMUNICATION `0x2001`/TIMEOUT `0x2002` may mean disconnect/power-off. SHOOT_ERROR `0x1008`, FRAME_FULL `0x1009`, card write `0x2004` and card full `0x2005` need explicit policies; none should become successful empty/partial path lists. Unsupported/ABI/load-library errors should fail setup, not loop indefinitely. [H lines 2266–2292; M pp.86,1005]
- `CapForceMode`/`SetForceMode(SHOOT)` can recover certain operating-mode busy conditions, but are not a general “reset camera safely” or “cancel download” API. Query support and avoid using force-mode changes to discard an unresolved capture. [M pp.186–187 §4.1.12; p.1005 §5]
- On disconnect/uncertain transfer, stop accepting captures. ReadImage already consumes data on success, and failure consumption is undocumented, so no general “retry the same head and succeed” guarantee exists. Close when possible, honor 600 ms, Exit/unload, rediscover and match camera identity, then reconfigure/quarantine. Detect regenerates indexes; do not persist `ENUM:0` as camera identity. [M pp.86–87,98,137; design]
- An x64 helper permits a last-resort watchdog to terminate **that owned helper** if it is irrecoverably stuck; this bounds the UI's wait, not the camera's physical operation. Restarting a helper does not prove volatile images vanished or that a prior shutter never fired. Keep recovery/quarantine and manual re-arm available, without returning partial success or automatically reshooting an ambiguous exposure. [A3; M pp.42,126–139; design]

## 9. Unresolved facts and hardware acceptance gates

None of these checks was executed in this investigation.

| Gate/test | Required result or fact to resolve |
|---|---|
| **X-T5 body firmware and capabilities** | User confirms X-T5 with newest firmware. Log its numeric firmware through the SDK on connection; verify compatible still/tether behavior and actual returned priority/release/media/image-quality capabilities. Listed model support alone is insufficient to certify the hardware transaction. [User confirmations; M pp.29–30,107,110–132,183–185] |
| **Surface x64 USB proof** | On the target Surface/OS, run a controlled x64 SDK owner with only the matching redistributables. Verify load/init/detect/open, actual bound ARM64 USB/WPD driver and COM operation, error/version logging. Record driver/provider/version instead of inferring it. No assumption of vendor or inbox-driver sufficiency. [B; A2; M p.25] |
| **Architecture deployment** | Verify x64 .NET/self-contained runtime and x64 WinUI dependencies as applicable. For helper architecture verify IPC security, request IDs, single owner, crash/hang isolation and path publication. Run on a clean target without developer tools. [HOST; A1–A2; design] |
| **Shutter-to-completion timing** | Measure half-press/AF, full-press acceptance, physical exposure, first image availability, final pair arrival and completed tablet write. Test longest intended shutter, processing options, focus failure and single/burst/dial mistakes. Establish illumination safety; absent callback cannot be replaced by a fixed delay claim. [M pp.126,131,133–139] |
| **RAW/JPEG object behavior** | Test RAW, JPEG and RAW+JPEG; orientation flags; HEIF-enabled camera configurations; actual object order; counter metadata on both files; late partner arrivals; no duplicate/missing partner or first-empty premature success. Confirm all requested files can be opened/decoded downstream. [H lines 377–394; M pp.134–139,1012] |
| **Stale/unsolicited capture filtering** | Seed old queue entries, simulate a manual camera-button shot, cancel mid-exposure, restart/reconnect and attempt next R/G/B. Verify no stale file is attributed to a new channel, ambiguous extras fail safely and no undocumented counter lifetime assumptions. [M pp.42,44,1012; design] |
| **Card retention matrix** | Test each supported OFF/RAW/JPEG/RAWJPEG mode with output-quality combinations, no card, full/write-protected card and relevant slots. Physically inspect camera-card copies after each shot. Verify tablet downloads remain complete and error semantics are recorded. Resolve whether required card-write completion can be certified by available metadata/status. [M pp.183–185,1005,1012] |
| **Repeated RGB scan load** | Run many R→G→B transactions with channel leases, unique pairs, no queue growth/format intermixing, stable memory and disk use, no simultaneous SDK calls, and no result before final bytes are committed. [M pp.34,134–139; design] |
| **Cancellation/fault injection** | Cancel before half-press, between presses, during exposure, between RAW/JPEG, during native read and disk write. Unplug USB/power off at each phase; inject disk-full/rename failure. Verify no success-shaped partial result, no automatic ambiguous reshoot and no early light switch. Measure actual native timeout/hang behavior. [A3; M pp.98,126–139,1005; design] |
| **ABI/lifecycle qualification** | Assert all required struct sizes/offsets, fixed-string and bool widths, Cdecl/int/uint/IntPtr bindings; run repeated Init/Close/≥600ms/Exit cycles on the selected owner thread/apartment. Test recovery without unloading in-flight code. [H lines 18–127,2314–2425; CS lines 682–764; M pp.75–76,98] |
| **Network, if required** | Obtain the actual model's networking manual/accessory details, verify IPv4 discovery/open and permissions/firewall, then repeat timing/pairing/cancellation/disconnect tests under weak/reconnected networking. The local reference does not identify every Ethernet accessory or port requirement. [M pp.30–32,77,85–89] |
| **Distribution permission** | Obtain applicable Fujifilm SDK agreement and resolve exact redistribution/notice obligations before shipping. Local redistributable naming is insufficient to fill missing legal terms. [M pp.34–35; L] |

**Bottom line:** research is complete for the user's X-T5 with newest firmware. Preserve timer-first development; the later implementation should first prove one x64 USB trigger-and-complete-download transaction on the Surface, recording firmware automatically. Keep the UI native ARM64 by placing that serialized transaction in one x64 helper; its adapter accepts channel and CancellationToken and returns final file paths only after the complete required image set is downloaded and saved. Hardware qualification and redistribution permission are remaining implementation/release gates, not unanswered questions blocking this report. The sources establish a credible engineering route, **not runtime readiness, a universal firmware matrix, or cleared redistribution rights**.
