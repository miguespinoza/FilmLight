param(
    [string]$AppPath = "$PSScriptRoot\..\artifacts\FilmLight-arm64\FilmLight.exe",
    [switch]$ProbeCamera,
    [switch]$SkipPixels
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LightPixels {
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
}
'@

function Assert-Light([uint32]$Expected) {
    if ($SkipPixels) { return }
    Start-Sleep -Milliseconds 80
    $bounds = $script:window.Current.BoundingRectangle
    $dc = [LightPixels]::GetDC([IntPtr]::Zero)
    try {
        foreach ($x in @(0.1, 0.5, 0.9)) {
            foreach ($y in @(0.1, 0.5, 0.8)) {
                $pixel = [LightPixels]::GetPixel($dc,
                    [int]($bounds.Left + $bounds.Width * $x),
                    [int]($bounds.Top + $bounds.Height * $y))
                if ($pixel -ne $Expected) {
                    throw "Light pixel mismatch at ($x, $y): expected $Expected, got $pixel; window bounds $bounds; DC $dc"
                }
            }
        }
    }
    finally { [LightPixels]::ReleaseDC([IntPtr]::Zero, $dc) | Out-Null }
}

function Find-Control([string]$Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Control([string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $control = Find-Control $Name
        if ($null -ne $control) { return $control }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Missing control: $Name"
}

function Invoke-Button([string]$Name) {
    $control = Wait-Control $Name
    if (!$control.Current.IsEnabled) { throw "Disabled button: $Name" }
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Wait-Text([string]$Prefix, [int]$TimeoutSeconds = 12) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $controls = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($control in $controls) {
            $name = $control.Current.Name
            if ($null -ne $name -and $name.StartsWith($Prefix)) { return $name }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for '$Prefix'"
}

$process = Start-Process -FilePath (Resolve-Path $AppPath) -PassThru
try {
    if ($SkipPixels) { Write-Output 'SKIP: screen-pixel checks explicitly disabled; exercising UI Automation only.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($process.HasExited) { throw "App exited with code $($process.ExitCode)" }
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $script:window) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $script:window) { throw 'No app window appeared.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $bounds = $script:window.Current.BoundingRectangle
        if (!$script:window.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0 -and $bounds.Left -gt -10000) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Wait-Text 'Uniform white' | Write-Output
    Wait-Text 'CAMERA ' | Write-Output
    foreach ($name in @('New roll', 'Open folder', 'Undo / retake', 'Saved color progress')) {
        $control = Wait-Control $name
        if ($control.Current.IsOffscreen) { throw "Bottom roll control is offscreen: $name" }
        $bounds = $control.Current.BoundingRectangle
        $windowBounds = $script:window.Current.BoundingRectangle
        if ($bounds.Top -lt $windowBounds.Top + $windowBounds.Height * 0.65) {
            throw "Roll control is not in the bottom bar: $name"
        }
    }
    Invoke-Button 'Reset'
    Assert-Light 0xFFFFFF
    Invoke-Button ([string][char]0x2212)
    Assert-Light 0xFAFDFF
    Invoke-Button 'Reset'
    Invoke-Button 'RGB'
    Invoke-Button 'Reset'
    for ($i = 0; $i -lt 4; $i++) { Invoke-Button ([string][char]0x2212) }
    Wait-Text '1 s' | Write-Output
    Invoke-Button 'Start RGB'
    Wait-Text 'Starting in' | Write-Output
    Wait-Text 'Red ' | Write-Output
    Assert-Light 0x0000FF
    Wait-Text 'Green ' | Write-Output
    Assert-Light 0x00FF00
    Wait-Text 'Blue ' | Write-Output
    Assert-Light 0xFF0000
    Wait-Text 'Set 1 complete' | Write-Output
    Invoke-Button 'Start RGB'
    Wait-Text 'Red ' | Write-Output
    Invoke-Button 'Pause'
    Wait-Text 'Paused. Light held' | Write-Output
    Start-Sleep -Seconds 2
    Wait-Text 'Paused. Light held' 1 | Out-Null
    Assert-Light 0x0000FF
    Invoke-Button 'Reset'
    Invoke-Button 'White'
    Invoke-Button 'Reset'
    Wait-Text '5000 K' | Write-Output
    Invoke-Button 'Exit full screen'
    Invoke-Button 'Setup'
    Wait-Text 'Before scanning' | Write-Output
    Invoke-Button 'Done'
    if ($ProbeCamera) {
        Invoke-Button 'Camera'
        Wait-Text 'Connect Fujifilm X-T5' | Write-Output
        $confirm = Wait-Control 'Camera is idle and configured; no other tether app is running.'
        $confirm.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Invoke-Button 'Connect USB'
        Wait-Text 'Camera not connected' 30 | Write-Output
        Wait-Text 'Camera connection failed:' | Write-Output
        Wait-Text ('CAMERA ' + [char]0x00B7 + ' No USB camera detected') | Write-Output
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $cameraButton = Find-Control 'Camera'
            if ($null -ne $cameraButton -and $cameraButton.Current.IsEnabled) { break }
            Start-Sleep -Milliseconds 50
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $cameraButton -or !$cameraButton.Current.IsEnabled) {
            throw 'Camera controls did not recover after failed connection.'
        }
    }
    Write-Output 'PASS: WinUI launch, white controls, RGB sequence, pause, fullscreen and setup.'
}
finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
