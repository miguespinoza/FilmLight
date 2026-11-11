param(
    [string]$AppPath = "$PSScriptRoot\..\artifacts\FilmLight-arm64\FilmLight.exe",
    [string]$OutputParent = "$PSScriptRoot\..\artifacts\WorkflowSmoke-$([Guid]::NewGuid().ToString('N'))",
    [ValidateSet('White', 'Rgb')][string]$CaptureMode = 'White',
    [switch]$ProbePreviewErrors,
    [switch]$ProbeReconnect,
    [switch]$ProbeProgress,
    [string]$RawPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WorkflowWindows {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@

function Find-Control([string]$Name, $Scope = $script:window, [switch]$ById) {
    $property = if ($ById) { [System.Windows.Automation.AutomationElement]::AutomationIdProperty }
        else { [System.Windows.Automation.AutomationElement]::NameProperty }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        $property, $Name)
    return $Scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Control([string]$Name, $Scope = $script:window, [switch]$Enabled, [switch]$ById) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $control = Find-Control $Name $Scope -ById:$ById
        if ($null -ne $control -and (!$Enabled -or $control.Current.IsEnabled)) { return $control }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    $names = @($Scope.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } |
        Where-Object { $_ } | Select-Object -Unique -First 55)
    throw "Missing control: $Name. Controls: $($names -join '; ')"
}

function Invoke-Button([string]$Name, $Scope = $script:window) {
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $control = $Scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $control -and $control.Current.IsEnabled) { break }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $control -or !$control.Current.IsEnabled) { throw "Missing or disabled button: $Name" }
    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Wait-Text([string]$Prefix, [int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $controls = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($control in $controls) {
            $name = $control.Current.Name
            if ($null -ne $name -and $name.StartsWith($Prefix)) { return $name }
            if ($null -ne $name -and $name.StartsWith('Operation failed:')) { throw $name }
        }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    $names = @($controls | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique -First 55)
    throw "Timed out waiting for '$Prefix'. Controls: $($names -join '; ')"
}

function Wait-AppWindow([string]$Title) {
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Title)))
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($process.HasExited) { throw "App exited: $($process.ExitCode)" }
        $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $candidate -and !$candidate.Current.IsOffscreen) {
            [WorkflowWindows]::SetForegroundWindow([IntPtr]$candidate.Current.NativeWindowHandle) | Out-Null
            return $candidate
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Missing app window: $Title"
}

function Select-SavedRoll([string]$Path, [switch]$Add, [int]$ExpectedPictures = -1) {
    $list = Wait-Control 'SavedRolls' -ById
    if ($Add) {
        $edit = Wait-Control 'Existing roll folder path'
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Path)
        Invoke-Button 'Add existing roll'
        Wait-Control 'Resume roll' -Enabled | Out-Null
    }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $item = @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
        Where-Object { $_.Current.Name.Contains($Path) } | Select-Object -First 1)
    if ($item.Count -ne 1) { throw "Roll is missing from the in-app library: $Path" }
    $name = $item[0].Current.Name
    if ($name -notlike '*Created * | Updated *') { throw "Roll dates are missing: $name" }
    if ($ExpectedPictures -ge 0 -and $name -notlike "*$ExpectedPictures pictures*") {
        throw "Incorrect picture count in roll picker: $name"
    }
    $item[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Button 'Resume roll'
}

function Read-Roll {
    return Get-Content -LiteralPath $script:manifest -Raw | ConvertFrom-Json
}

function Pick-SourcePoint([double]$X, [double]$Y, [int]$ExpectedRevisions) {
    if (![WorkflowWindows]::SetForegroundWindow([IntPtr]$script:window.Current.NativeWindowHandle)) {
        throw 'Cannot foreground the workflow for the native picker check.'
    }
    $image = Wait-Control 'Captured frame preview'
    $bounds = $image.Current.BoundingRectangle
    $scale = [Math]::Min($bounds.Width / 384, $bounds.Height / 256)
    $px = $bounds.Left + ($bounds.Width - 384 * $scale) / 2 + $X * 384 * $scale
    $py = $bounds.Top + ($bounds.Height - 256 * $scale) / 2 + $Y * 256 * $scale
    $original = New-Object WorkflowWindows+Point
    if (![WorkflowWindows]::GetCursorPos([ref]$original)) { throw 'Cannot read cursor position for native picker check.' }
    try {
        if (![WorkflowWindows]::SetCursorPos([int]$px, [int]$py)) { throw 'Cannot position pointer over source image.' }
        $actual = New-Object WorkflowWindows+Point
        if (![WorkflowWindows]::GetCursorPos([ref]$actual)) { throw 'Cannot verify pointer position.' }
        [uint32]$targetProcess = 0
        [WorkflowWindows]::GetWindowThreadProcessId([WorkflowWindows]::WindowFromPoint($actual), [ref]$targetProcess) | Out-Null
        if ($actual.X -ne [int]$px -or $actual.Y -ne [int]$py -or $targetProcess -ne $process.Id) {
            throw "Picker target is obscured or offscreen: expected $px, $py in PID $($process.Id); actual $($actual.X), $($actual.Y) in PID $targetProcess."
        }
        [WorkflowWindows]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [WorkflowWindows]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    }
    finally { [WorkflowWindows]::SetCursorPos($original.X, $original.Y) | Out-Null }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (@((Read-Roll).Recipes).Count -eq $ExpectedRevisions) { break }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    if (@((Read-Roll).Recipes).Count -ne $ExpectedRevisions) {
        $state = @($script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } |
            Where-Object { $_ } | Select-Object -Unique -First 55)
        throw "Source picker did not save its calibration revision. Point $px, $py; image bounds $bounds. Controls: $($state -join '; ')"
    }
    Wait-Text 'Preview rendered for the exact current recipe' | Write-Output
}

function Wait-CompletedExports([int]$Count) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        if (@((Read-Roll).Exports | Where-Object State -eq 2).Count -eq $Count) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Expected $Count completed export history records."
}

function Assert-ActivityStopped {
    $ring = Find-Control 'WorkflowActivityIndicator' -ById
    if ($null -ne $ring -and !$ring.Current.IsOffscreen) {
        throw 'Activity indicator remained visible after the operation finished.'
    }
}

$OutputParent = [System.IO.Path]::GetFullPath($OutputParent)
[System.IO.Directory]::CreateDirectory($OutputParent) | Out-Null
if ($ProbePreviewErrors -or $ProbeReconnect -or $ProbeProgress) {
    Add-Type -AssemblyName System.Drawing
    $fixtureRoot = Join-Path $OutputParent $(if ($ProbeProgress) { 'RawProgressFixture' } else { 'JpegRejectionFixture' })
    [System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $id = [Guid]::NewGuid()
    $directory = Join-Path $fixtureRoot "captures\$($id.ToString('N'))"
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    if ($ProbeProgress) {
        if (!$RawPath -or [System.IO.Path]::GetExtension($RawPath) -ine '.RAF') {
            throw '-ProbeProgress requires -RawPath pointing to an existing supported RAF.'
        }
        $path = Join-Path $directory 'White.RAF'
        Copy-Item -LiteralPath $RawPath -Destination $path
        $originalHash = (Get-FileHash -LiteralPath $path).Hash
    }
    else {
        $path = Join-Path $directory 'White.JPG'
        $bitmap = New-Object System.Drawing.Bitmap 4, 4
        try { $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg) }
        finally { $bitmap.Dispose() }
    }
    $captures = @(@{
        Id = $id; Number = 1; Purpose = 2; Mode = 0; Files = @(@{ Channel = 3; Path = $path })
        IsDemo = $false; CapturedAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o')
    })
    $recipes = @()
    if ($ProbeReconnect) {
        $recipes = @(@{
            Id = [Guid]::NewGuid(); Revision = 1; FilmKind = 0; XmpTemplatePath = $null
            ExposureEv = 0; RedGain = 1; GreenGain = 1; BlueGain = 1
            FilmBaseRed = 1; FilmBaseGreen = 1; FilmBaseBlue = 1; BlackPoint = 0; WhitePoint = 1
            CropLeft = 0; CropTop = 0; CropRight = 0; CropBottom = 0; FrameId = $id
            PictureCalibration = @{
                Light = @{ FrameId = $id; X = .1; Y = .5; Red = .8; Green = .7; Blue = .6 }
                FilmBase = @{ FrameId = $id; X = .2; Y = .5; Red = .6; Green = .3; Blue = .2 }
            }
        })
    }
    @{
        SchemaVersion = 2; Id = [Guid]::NewGuid()
        Name = $(if ($ProbeProgress) { 'Saved RAW progress fixture - no camera connection' } else { 'JPEG rejection test fixture - not camera photographs' })
        FilmKind = 0; IsDemo = $false; Phase = 2; Captures = $captures
        Recipes = $recipes; Exports = @(); IntermediatesTrashed = $false
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'roll.json') -Encoding UTF8
}
$process = Start-Process -FilePath (Resolve-Path $AppPath) -PassThru
try {
    $script:window = Wait-AppWindow 'Film Light'
    Invoke-Button 'Roll workflow'
    $script:window = Wait-AppWindow 'Film Light - Roll workflow'
    if ($ProbePreviewErrors -or $ProbeReconnect -or $ProbeProgress) {
        Invoke-Button 'Open / resume'
        $edit = Wait-Control 'Existing roll folder path'
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('relative-invalid-roll')
        Invoke-Button 'Add existing roll'
        Wait-Text 'Cannot add roll:' | Write-Output
        if ((Wait-Control 'Resume roll').Current.IsEnabled) { throw 'Invalid roll path enabled Resume.' }
        Select-SavedRoll $fixtureRoot -Add
        Wait-Text 'Resumed ' | Write-Output
        if ($ProbeProgress) {
            $illumination = (Wait-Control 'Illumination area').Current.BoundingRectangle
            Invoke-Button 'Render Preview'
            Wait-Text 'Decoding RAW' 120 | Write-Output
            $ring = Wait-Control 'WorkflowActivityIndicator' -ById
            if ($ring.Current.IsOffscreen) { throw 'RAW decoding has no visible activity indicator.' }
            $windowBounds = $script:window.Current.BoundingRectangle
            if ($ring.Current.BoundingRectangle.Left -lt $windowBounds.Left + $windowBounds.Width / 2) {
                throw 'Progress feedback overlaps the illumination area.'
            }
            $elapsedCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'WorkflowActivityElapsed')
            $clock = $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $elapsedCondition)
            if ($null -eq $clock -or $clock.Current.IsOffscreen) { throw 'Elapsed time is not visible during RAW decoding.' }
            $before = $clock.Current.Name
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            do {
                Start-Sleep -Milliseconds 200
                $after = $clock.Current.Name
            } while ($after -eq $before -and [DateTime]::UtcNow -lt $deadline)
            if ($before -notlike 'Elapsed * | Current stage *' -or $after -eq $before) {
                throw 'Elapsed time does not advance while RAW decoding is running.'
            }
            if ((Wait-Control 'Render Preview').Current.IsEnabled -or !(Wait-Control 'Pause / cancel safely').Current.IsEnabled) {
                throw 'Processing must disable duplicate renders while leaving safe cancellation available.'
            }
            Wait-Text 'Preview rendered for the exact current recipe' 180 | Write-Output
            Wait-Control 'Render Preview' -Enabled | Out-Null
            Assert-ActivityStopped
            if ((Wait-Control 'Illumination area').Current.BoundingRectangle -ne $illumination) {
                throw 'Progress feedback changed the illumination geometry.'
            }
            Invoke-Button 'Render Preview'
            Wait-Text 'Loading cached RAW' 120 | Write-Output
            Invoke-Button 'Pause / cancel safely'
            Wait-Text 'Paused.' 30 | Write-Output
            Wait-Control 'Render Preview' -Enabled | Out-Null
            Assert-ActivityStopped
            if ((Get-FileHash -LiteralPath $path).Hash -ne $originalHash) { throw 'Processing changed the original RAW input.' }
            Write-Output 'PASS: visible RAW stages, advancing elapsed time, stable illumination, successful completion and safe cancellation without a stuck activity indicator.'
            return
        }
        if ($ProbeReconnect) {
            $manifestPath = Join-Path $fixtureRoot 'roll.json'
            $before = (Get-FileHash -LiteralPath $manifestPath).Hash
            $handle = $script:window.Current.NativeWindowHandle
            foreach ($attempt in 1..2) {
                Invoke-Button 'Connect USB camera'
                Wait-Control 'Connect Fujifilm X-T5' | Out-Null
                Invoke-Button 'Cancel'
                Wait-Text 'Camera connection canceled.' | Write-Output
                if ($script:window.Current.NativeWindowHandle -ne $handle) { throw 'Reconnect replaced the roll window.' }
                if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne $before) { throw 'Canceled reconnect changed the saved roll.' }
                Wait-Control 'Connect USB camera' -Enabled | Out-Null
                $selection = (Wait-Control 'Representative frame selection').GetCurrentPattern(
                    [System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
                if ($selection.Count -ne 1 -or $selection[0].Current.Name -notlike 'Frame 1 -*') {
                    throw 'Reconnect did not retain the selected frame.'
                }
            }
            $helper = Join-Path (Split-Path (Resolve-Path $AppPath).Path -Parent) 'CameraHost\FilmLight.CameraHost.exe'
            if (!(Test-Path -LiteralPath $helper)) {
                Invoke-Button 'Connect USB camera'
                $confirmation = Wait-Control 'Camera is idle and configured; no other tether app is running.'
                $confirmation.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
                Invoke-Button 'Connect USB'
                $failure = Wait-Text 'Operation failed:'
                if ($failure -notlike '*Camera helper is missing*') { throw "Unexpected connection failure: $failure" }
                Wait-Control 'Connect USB camera' -Enabled | Out-Null
                if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne $before) { throw 'Failed reconnect changed the saved roll.' }
                Write-Output $failure
            }
            Invoke-Button 'Close workflow'
            $script:window = Wait-AppWindow 'Film Light'
            Invoke-Button 'Camera'
            Wait-Control 'Connect Fujifilm X-T5' | Out-Null
            Invoke-Button 'Cancel'
            Write-Output 'PASS: in-roll camera setup can be canceled and retried without closing or changing the roll; parent camera setup still works.'
            return
        }
        foreach ($attempt in 1..2) {
            Invoke-Button 'Render Preview'
            $failure = Wait-Text 'Operation failed:'
            if ($failure -notmatch 'JPEG' -or $failure -notmatch 'RAW') {
                throw "Preview did not expose the actionable input error: $failure"
            }
            if (!(Wait-Control 'Render Preview').Current.IsEnabled) { throw 'Preview failure left the workflow locked.' }
            Assert-ActivityStopped
            Write-Output $failure
        }
        Write-Output 'PASS: JPEG-only preview rejection is visible and retryable, not stuck showing Processing.'
        return
    }
    Invoke-Button 'Open / resume'
    Wait-Control 'SavedRolls' -ById | Out-Null
    Invoke-Button 'Cancel'
    Invoke-Button 'Demo camera'
    Wait-Text 'DEMO / SYNTHETIC - ' | Write-Output
    if ($CaptureMode -eq 'Rgb') {
        $combo = Wait-Control 'Capture mode'
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $item = Wait-Control 'Rgb'
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    }
    Invoke-Button 'New roll'
    $created = Wait-Text 'Created '
    Write-Output $created
    if ($created -notmatch '^Created (.+)\. (?:Capture a picture|Remove film)') { throw "Missing automatically created roll path: $created" }
    $rollRoot = $Matches[1]
    $libraryRoot = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'FilmLight\Rolls'
    if ((Split-Path $rollRoot -Parent) -ine $libraryRoot) { throw "Roll was not created in the managed library: $rollRoot" }
    $script:manifest = Join-Path $rollRoot 'roll.json'
    if (!(Read-Roll).CreatedAt) { throw 'New roll has no persistent creation date.' }

    if ($null -ne (Find-Control 'Capture NO-FILM reference') -or $null -ne (Find-Control '1. References')) {
        throw 'Separate reference calibration is still visible.'
    }
    Invoke-Button '1. Capture'
    Wait-Text 'Capture frames:' | Write-Output
    Invoke-Button 'Capture frame 1'
    Wait-Text 'Frame 1 saved' | Write-Output
    Wait-Text 'SYNTHETIC - Frame 1 - positive preview' | Write-Output
    $previewBounds = (Wait-Control 'Captured frame preview').Current.BoundingRectangle
    $toolbarBounds = (Wait-Control 'Workflow toolbar').Current.BoundingRectangle
    $windowBounds = $script:window.Current.BoundingRectangle
    if ($previewBounds.Left -lt $windowBounds.Left + $windowBounds.Width / 2 - 3) {
        throw 'Capture preview is not confined to the right half.'
    }
    if ([Math]::Abs($toolbarBounds.Width - $windowBounds.Width) -gt 3) {
        throw 'Bottom toolbar does not span the full window width.'
    }
    Invoke-Button 'Pick backlight WB'
    Wait-Text 'Click exposed backlight (no film).' | Write-Output
    Pick-SourcePoint (3.0 / 384) .5 2
    Invoke-Button 'Pick film base'
    Wait-Text 'Click clear/unexposed developed film' | Write-Output
    Pick-SourcePoint (12.0 / 384) .5 3
    $picked = @((Read-Roll).Recipes)[-1]
    if (!$picked.PictureCalibration.Light -or !$picked.PictureCalibration.FilmBase) {
        throw 'The two mouse picks were not saved for the selected frame.'
    }
    $savedPicks = $picked.PictureCalibration | ConvertTo-Json -Compress
    $origin = (Read-Roll).Captures[0].Id
    if ($picked.PictureCalibration.Light.FrameId -ne $origin -or $picked.PictureCalibration.FilmBase.FrameId -ne $origin) {
        throw 'Picture picks do not identify their source frame.'
    }
    Invoke-Button 'Capture frame 2'
    Wait-Text "Frame 2 saved. Positive preview uses this frame's own" | Write-Output
    Wait-Text 'SYNTHETIC - Frame 2 - positive preview' | Write-Output
    $captured = Read-Roll
    $firstRecipe = @($captured.Recipes | Where-Object FrameId -eq $origin)[-1]
    $secondRecipe = @($captured.Recipes | Where-Object FrameId -eq $captured.Captures[1].Id)[-1]
    if (($firstRecipe.PictureCalibration | ConvertTo-Json -Compress) -ne $savedPicks -or
        $secondRecipe.PictureCalibration.Light.FrameId -ne $captured.Captures[1].Id) {
        throw 'Capturing another frame changed or shared frame-local picture picks.'
    }
    if ($captured.Captures.Count -ne 2 -or $captured.Exports.Count -ne 0 -or
        @($captured.Captures | Where-Object Purpose -ne 2).Count -ne 0) {
        throw 'Capture should require only two pictures, with no separate references.'
    }
    $expectedMode = if ($CaptureMode -eq 'Rgb') { 1 } else { 0 }
    $expectedChannels = if ($CaptureMode -eq 'Rgb') { @(0, 1, 2) } else { @(3) }
    foreach ($capture in $captured.Captures) {
        if ($capture.Mode -ne $expectedMode -or $capture.Files.Count -ne $expectedChannels.Count -or !$capture.IsDemo) {
            throw "Expected $CaptureMode captures, not another mode or incomplete triplets."
        }
        $actualChannels = @($capture.Files | ForEach-Object Channel | Sort-Object)
        if (($actualChannels -join ',') -ne ($expectedChannels -join ',')) {
            throw 'Capture light channels do not match the selected mode.'
        }
    }
    $inputHashes = @{}
    foreach ($capture in $captured.Captures) {
        foreach ($file in $capture.Files) { $inputHashes[$file.Path] = (Get-FileHash -LiteralPath $file.Path).Hash }
    }

    Invoke-Button '2. Develop'
    Invoke-Button 'Render Preview'
    Wait-Text 'Preview rendered for the exact current recipe' | Write-Output
    if ($null -ne (Find-Control 'Accept this preview')) { throw 'Obsolete acceptance button is still present.' }
    Invoke-Button 'Develop settings'
    Invoke-Button 'Increase Exposure EV'
    Wait-Text 'Settings saved for this frame.' | Write-Output
    Invoke-Button '3. Export'
    Invoke-Button 'Export missing / failed'
    Wait-Text 'Batch finished: 2/2 complete, 0 failed.' 60 | Write-Output
    $exported = Read-Roll
    $complete = @($exported.Exports | Where-Object State -eq 2)
    if ($complete.Count -ne 2) { throw 'Both frames were not recorded as complete exports.' }
    $secondExport = @($complete | Where-Object FrameId -eq $captured.Captures[1].Id)[-1]
    $exportedRecipe = @($exported.Recipes | Where-Object {
        $_.Id -eq $secondExport.RecipeId -and $_.Revision -eq $secondExport.RecipeRevision
    })[0]
    if ($exportedRecipe.ExposureEv -ne .25) { throw 'Export ignored the latest saved, unpreviewed settings.' }
    foreach ($item in $complete) {
        foreach ($path in $item.OutputFiles) {
            if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt 100) {
                throw "Missing/empty output: $path"
            }
        }
    }

    $removed = @($complete[0].OutputFiles | Where-Object { [System.IO.Path]::GetExtension($_) -eq '.jpg' })[0]
    if (!$removed) { throw 'Expected a JPEG companion to exercise retry.' }
    [System.IO.File]::Delete($removed)
    Invoke-Button 'Export missing / failed'
    Wait-CompletedExports 3
    Wait-Text 'Batch finished: 2/2 complete, 0 failed.' 60 | Write-Output
    $retried = Read-Roll
    if (@($retried.Exports | Where-Object State -eq 2).Count -ne 3) { throw 'Missing output was not regenerated in a new job.' }
    foreach ($entry in $inputHashes.GetEnumerator()) {
        if ((Get-FileHash -LiteralPath $entry.Key).Hash -ne $entry.Value) { throw "Capture input changed: $($entry.Key)" }
    }

    Invoke-Button 'Close workflow'
    $script:window = Wait-AppWindow 'Film Light'
    Invoke-Button 'Roll workflow'
    $script:window = Wait-AppWindow 'Film Light - Roll workflow'
    Invoke-Button 'Open / resume'
    Select-SavedRoll $rollRoot -ExpectedPictures 2
    Invoke-Button 'Use Demo camera'
    Wait-Text 'Resumed ' | Write-Output
    Wait-Text 'SIMULATED / SYNTHETIC EXPORTS' | Write-Output
    $reopenedFirst = @((Read-Roll).Recipes | Where-Object FrameId -eq $origin)[-1]
    if (($reopenedFirst.PictureCalibration | ConvertTo-Json -Compress) -ne $savedPicks) {
        throw 'Reopening the roll did not restore the picture picks.'
    }
    Invoke-Button '1. Capture'
    Invoke-Button 'Render Preview'
    Wait-Text 'Preview rendered for the exact current recipe' | Write-Output
    Invoke-Button '3. Export'
    Invoke-Button 'Export missing / failed'
    Wait-Text 'Batch finished: 2/2 complete, 0 failed.' 60 | Write-Output
    if (@((Read-Roll).Exports | Where-Object State -eq 2).Count -ne 3) {
        throw 'Resume unnecessarily duplicated successful exports.'
    }
    Write-Output "PASS: reference-free capture/auto-preview, frame-local light/film picks, latest-settings TIFF/JPEG jobs without acceptance, missing-output retry, reopen, unchanged captures. Artifacts: $rollRoot"
}
finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
