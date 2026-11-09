param(
    [string]$AppPath = "$PSScriptRoot\..\artifacts\FilmLight-arm64\FilmLight.exe",
    [string]$OutputParent = "$PSScriptRoot\..\artifacts\WorkflowSmoke-$([Guid]::NewGuid().ToString('N'))",
    [ValidateSet('White', 'Rgb')][string]$CaptureMode = 'White'
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
    throw "Missing control: $Name"
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
    $image = Wait-Control 'Captured frame preview'
    $bounds = $image.Current.BoundingRectangle
    $scale = [Math]::Min($bounds.Width / 384, $bounds.Height / 256)
    $px = $bounds.Left + ($bounds.Width - 384 * $scale) / 2 + $X * 384 * $scale
    $py = $bounds.Top + ($bounds.Height - 256 * $scale) / 2 + $Y * 256 * $scale
    $original = New-Object WorkflowWindows+Point
    if (![WorkflowWindows]::GetCursorPos([ref]$original)) { throw 'Cannot read cursor position for native picker check.' }
    try {
        if (![WorkflowWindows]::SetCursorPos([int]$px, [int]$py)) { throw 'Cannot position pointer over source image.' }
        [WorkflowWindows]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [WorkflowWindows]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    }
    finally { [WorkflowWindows]::SetCursorPos($original.X, $original.Y) | Out-Null }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (@((Read-Roll).Recipes).Count -eq $ExpectedRevisions) { break }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    if (@((Read-Roll).Recipes).Count -ne $ExpectedRevisions) { throw 'Source picker did not save its calibration revision.' }
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

$OutputParent = [System.IO.Path]::GetFullPath($OutputParent)
[System.IO.Directory]::CreateDirectory($OutputParent) | Out-Null
$process = Start-Process -FilePath (Resolve-Path $AppPath) -PassThru
try {
    $script:window = Wait-AppWindow 'Film Light'
    Invoke-Button 'Roll workflow'
    $script:window = Wait-AppWindow 'Film Light - Roll workflow'
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

    Invoke-Button 'Capture NO-FILM reference'
    Wait-Text 'LightReference 0 saved' | Write-Output
    Invoke-Button 'Capture FILM BASE reference'
    Wait-Text 'FilmBaseReference 0 saved' | Write-Output
    Invoke-Button '2. Capture'
    Wait-Text 'Capture frames:' | Write-Output
    Invoke-Button 'Capture frame 1'
    Wait-Text 'Frame 1 saved' | Write-Output
    Invoke-Button 'Capture frame 2'
    Wait-Text 'Frame 2 saved' | Write-Output
    Wait-Text 'SYNTHETIC - Frame 2 - positive preview' | Write-Output
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
    Wait-Text 'Click an exposed backlight patch' | Write-Output
    Pick-SourcePoint (3.0 / 384) .5 1
    Invoke-Button 'Pick film base'
    Wait-Text 'Click clear/unexposed developed film' | Write-Output
    Pick-SourcePoint (12.0 / 384) .5 2
    $picked = @((Read-Roll).Recipes)[-1]
    if ($picked.Accepted -or [Math]::Abs($picked.RedGain - 1) -gt .001 -or
        [Math]::Abs($picked.FilmBaseRed - 1) -gt .001) {
        throw 'Synthetic backlight/base sampling did not save the expected unaccepted calibration.'
    }
    $captured = Read-Roll
    if ($captured.Captures.Count -ne 4 -or $captured.Exports.Count -ne 0) {
        throw 'Capture phase did not retain two references/two frames separately from processing.'
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

    Invoke-Button '3. Develop'
    Invoke-Button 'Render Preview'
    Wait-Text 'Preview rendered for the exact current recipe' | Write-Output
    if (!(Wait-Control 'Accept this preview').Current.IsEnabled) { throw 'Successful preview did not enable acceptance.' }
    Invoke-Button 'Develop settings'
    Invoke-Button 'Increase Exposure EV'
    Wait-Text 'Unaccepted edits.' | Write-Output
    if ((Wait-Control 'Accept this preview').Current.IsEnabled) { throw 'Edited recipe was accepted without a new preview.' }
    Invoke-Button 'Render Preview'
    Wait-Text 'Preview rendered for the exact current recipe' | Write-Output
    Invoke-Button 'Accept this preview'
    Wait-Text 'Accepted immutable recipe' | Write-Output
    Invoke-Button '4. Export'
    Invoke-Button 'Export missing / failed'
    Wait-Text 'Batch finished: 2/2 complete, 0 failed.' 60 | Write-Output
    $exported = Read-Roll
    $complete = @($exported.Exports | Where-Object State -eq 2)
    if ($complete.Count -ne 2) { throw 'Both frames were not recorded as complete exports.' }
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
    Invoke-Button 'Export missing / failed'
    Wait-Text 'Batch finished: 2/2 complete, 0 failed.' 60 | Write-Output
    if (@((Read-Roll).Exports | Where-Object State -eq 2).Count -ne 3) {
        throw 'Resume unnecessarily duplicated successful exports.'
    }
    Write-Output "PASS: split capture/auto-preview, mouse WB/base pickers, four-phase DEMO workflow, preview invalidation/acceptance, TIFF/JPEG jobs, missing-output retry, reopen, unchanged captures. Artifacts: $rollRoot"
}
finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
