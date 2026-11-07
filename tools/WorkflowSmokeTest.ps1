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
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@

function Find-Control([string]$Name, $Scope = $script:window) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Control([string]$Name, $Scope = $script:window) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $control = Find-Control $Name $Scope
        if ($null -ne $control) { return $control }
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

function Select-Folder([string]$Path) {
    $condition = [System.Windows.Automation.Condition]::TrueCondition
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $picker = $null
    do {
        if ($null -ne (Find-Control 'Select Folder' $script:window)) {
            $picker = $script:window
            break
        }
        $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        foreach ($candidate in $candidates) {
            $owner = [WorkflowWindows]::GetWindow([IntPtr]$candidate.Current.NativeWindowHandle, 4)
            for ($depth = 0; $owner -ne [IntPtr]::Zero -and $depth -lt 8; $depth++) {
                if ($owner -eq [IntPtr]$script:window.Current.NativeWindowHandle) { break }
                $owner = [WorkflowWindows]::GetWindow($owner, 4)
            }
            if ($owner -eq [IntPtr]$script:window.Current.NativeWindowHandle) {
                $picker = $candidate
                break
            }
        }
        if ($null -ne $picker) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $picker) {
        $appState = @($script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } |
            Where-Object { $_ } | Select-Object -Unique -First 35)
        $details = @($candidates | Where-Object {
            $_.Current.ProcessId -eq $process.Id -or $_.Current.Name -match '^(Select|Choose|Pick).*folder'
        } | ForEach-Object {
            "$($_.Current.Name) [class=$($_.Current.ClassName), pid=$($_.Current.ProcessId), handle=$($_.Current.NativeWindowHandle), owner=$([WorkflowWindows]::GetWindow([IntPtr]$_.Current.NativeWindowHandle, 4))]"
        })
        throw "No folder picker owned by workflow handle $($script:window.Current.NativeWindowHandle) appeared. Candidates: $($details -join '; '). App state: $($appState -join '; ')"
    }
    $editCondition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Folder:')))
    $edit = $picker.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    if ($null -eq $edit) {
        $names = @($picker.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } |
            Where-Object { $_ } | Select-Object -Unique -First 45)
        throw "Folder picker has no Folder edit. Controls: $($names -join '; ')"
    }
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Path)
    Invoke-Button 'Select Folder' $picker
}

function Read-Roll {
    return Get-Content -LiteralPath $script:manifest -Raw | ConvertFrom-Json
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
    Select-Folder $OutputParent
    Wait-Text 'Created ' | Write-Output
    $rolls = @(Get-ChildItem -LiteralPath $OutputParent -Filter roll.json -Recurse)
    if ($rolls.Count -ne 1) { throw 'Expected exactly one newly created roll manifest.' }
    $script:manifest = $rolls[0].FullName
    $rollRoot = $rolls[0].DirectoryName

    Invoke-Button 'Capture NO-FILM reference'
    Wait-Text 'LightReference 0 saved' | Write-Output
    Invoke-Button 'Capture FILM BASE reference'
    Wait-Text 'FilmBaseReference 0 saved' | Write-Output
    Invoke-Button '2. Capture'
    Wait-Text 'Capture the entire roll' | Write-Output
    Invoke-Button 'Capture frame 1'
    Wait-Text 'Frame 1 saved' | Write-Output
    Invoke-Button 'Capture frame 2'
    Wait-Text 'Frame 2 saved' | Write-Output
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
    Select-Folder $rollRoot
    Invoke-Button 'Use Demo camera'
    Wait-Text 'Resumed ' | Write-Output
    Wait-Text 'SIMULATED / SYNTHETIC EXPORTS' | Write-Output
    Invoke-Button 'Export missing / failed'
    Wait-Text 'Batch finished: 2/2 complete, 0 failed.' 60 | Write-Output
    if (@((Read-Roll).Exports | Where-Object State -eq 2).Count -ne 3) {
        throw 'Resume unnecessarily duplicated successful exports.'
    }
    Write-Output "PASS: native four-phase DEMO workflow, preview invalidation/acceptance, TIFF/JPEG jobs, missing-output retry, reopen, unchanged captures. Artifacts: $rollRoot"
}
finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
