# PROTOTYPE - throwaway launcher for the Develop panel variants (see FilmLight\RollWorkflowWindow.DevelopPrototype.cs).
# Builds Debug (the prototype is excluded from Release) and starts it with the chosen variant.
# Switch variants in the app with the yellow pill at the bottom or the Left/Right keys.
param(
    [ValidateSet('A', 'B', 'C', 'Current')][string]$Variant = 'A',
    [ValidateSet('ARM64', 'x64')][string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' })
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
# Run from the build output: a Debug *publish* of this unpackaged WinUI app crashes at startup.
& dotnet build "$root\FilmLight\FilmLight.csproj" -c Debug -r $runtime "-p:Platform=$Platform" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Prototype build failed.' }
$folder = Join-Path $root "FilmLight\bin\$Platform\Debug\net10.0-windows10.0.26100.0\$runtime"
if (!(Test-Path "$folder\CameraHost\FilmLight.CameraHost.exe")) {
    & dotnet publish "$root\FilmLight.CameraHost\FilmLight.CameraHost.csproj" -c Release -r win-x64 -o "$folder\CameraHost" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Camera helper publish failed.' }
}
$env:FILMLIGHT_PROTOTYPE_DEVELOP = $Variant
Start-Process "$folder\FilmLight.exe"
