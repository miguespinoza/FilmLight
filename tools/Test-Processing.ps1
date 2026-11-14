param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$RawPath,
    [switch]$DialogOnly
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$root = Join-Path ([IO.Path]::GetTempPath()) "FilmLight-processing-smoke-$([Guid]::NewGuid().ToString('N'))"
$roll = Join-Path $root 'roll'
$captureId = [Guid]::NewGuid().ToString('N')
$capture = Join-Path $roll "Scan-$captureId\White"
New-Item -ItemType Directory -Path $capture -Force | Out-Null
$raw = Join-Path $capture 'sample.RAF'
Copy-Item -LiteralPath (Resolve-Path $RawPath) -Destination $raw
$before = (Get-FileHash -LiteralPath $raw -Algorithm SHA256).Hash
ConvertTo-Json -Depth 8 -InputObject @(@{Id=$captureId;Kind=2;Channels=@{'3'=@($raw)};FrameNumber=0;Discarded=$false}) |
    Set-Content -LiteralPath (Join-Path $roll 'roll.json') -Encoding UTF8
@{Temperature=5000;Interval=5;Repeat=$false;ActiveRollDirectory=$roll} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $root 'settings.json') -Encoding UTF8

function Find-Control([string]$Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Enabled([string]$Name, [int]$Seconds = 120) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ($process.HasExited) { throw "Film Light exited: $($process.ExitCode)" }
        $control = Find-Control $Name
        if ($null -ne $control -and $control.Current.IsEnabled) { return $control }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Control did not become ready: $Name"
}
function Invoke-Button([string]$Name) {
    $control = Wait-Enabled $Name
    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$previous = $env:FILMLIGHT_APPDATA_ROOT
$env:FILMLIGHT_APPDATA_ROOT = $root
Write-Output "Processing evidence: $root"
$process = $null
try {
    $process = Start-Process -FilePath (Resolve-Path $AppPath) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $script:window) { break }
        if ($process.HasExited) { throw "Film Light exited: $($process.ExitCode)" }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $script:window) { throw 'No Film Light window appeared.' }
    Invoke-Button 'Process film'
    if ($DialogOnly) {
        Wait-Enabled 'Done' -Seconds 15 | Out-Null
        $expander = Wait-Enabled 'Conversion, Print Finish and crop' -Seconds 15
        $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            if ($process.HasExited) { throw "Film Light exited: $($process.ExitCode)" }
            $rotation = Find-Control 'Rotation (degrees)'
            if ($null -ne $rotation) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $rotation) { throw 'Expanded adjustment controls did not appear.' }
        Invoke-Button 'Done'
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            if ($process.HasExited) { throw "Film Light exited: $($process.ExitCode)" }
            if ($null -eq (Find-Control 'Done')) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -ne (Find-Control 'Done')) { throw 'Process film dialog did not close.' }
        if ((Get-FileHash -LiteralPath $raw -Algorithm SHA256).Hash -ne $before) { throw 'Capture RAW changed.' }
        Write-Output "PASS: Process film dialog opened, adjustments expanded and dialog closed; source unchanged. Evidence: $root"
        return
    }
    Wait-Enabled 'Export positive' | Out-Null
    $settings = Join-Path $roll ".processing\$captureId.json"
    if (!(Test-Path -LiteralPath $settings)) { throw "Frame preparation did not save settings. Inspect $root" }
    $bounds = $script:window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$bounds.Left, [int]$bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $root 'processing.png'))
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    Invoke-Button 'Export positive'
    $deadline = [DateTime]::UtcNow.AddMinutes(4)
    do {
        $outputs = @(Get-ChildItem (Join-Path $roll 'Positives') -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name.StartsWith('Positive-') })
        if ($outputs.Count -gt 0) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($outputs.Count -ne 1) { throw "No complete export. Inspect $root" }
    foreach ($name in @('positive.tif','positive.jpg','conversion.json')) {
        $file = Get-Item -LiteralPath (Join-Path $outputs[0].FullName $name)
        if ($file.Length -eq 0) { throw "Empty output: $name" }
    }
    if ((Get-FileHash -LiteralPath $raw -Algorithm SHA256).Hash -ne $before) { throw 'Capture RAW changed.' }
    Invoke-Button 'Done'
    Write-Output "PASS: native preview, original RAF, full-resolution TIFF/JPEG, provenance and source immutability. Evidence: $root"
}
finally {
    $env:FILMLIGHT_APPDATA_ROOT = $previous
    if ($null -ne $process -and !$process.HasExited) { Stop-Process -Id $process.Id }
}
