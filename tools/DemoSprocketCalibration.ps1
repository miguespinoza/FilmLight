param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\SprocketDemo-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))"
)
$ErrorActionPreference = 'Stop'
$inputPath = (Resolve-Path -LiteralPath $InputDirectory).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $PSScriptRoot 'FilmLight.CalibrationDemo\FilmLight.CalibrationDemo.csproj'
& dotnet run --project $project --configuration Release -- $inputPath $outputPath
if ($LASTEXITCODE -ne 0) { throw "Calibration demo failed (exit $LASTEXITCODE). See $outputPath\report.json." }
Write-Output "Gallery: $outputPath\index.html"
