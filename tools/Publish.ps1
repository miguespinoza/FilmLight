param(
    [ValidateSet('ARM64', 'x64')][string]$Platform = 'ARM64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$folder = Join-Path $root "artifacts\FilmLight-$($Platform.ToLowerInvariant())"
& dotnet publish "$root\FilmLight\FilmLight.csproj" -c Release -r $runtime "-p:Platform=$Platform" -o $folder --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
& dotnet publish "$root\FilmLight.CameraHost\FilmLight.CameraHost.csproj" -c Release -r win-x64 -o "$folder\CameraHost" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Camera helper publish failed.' }
Write-Output "Launch: $folder\FilmLight.exe"
