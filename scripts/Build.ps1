param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "Restoring FastVRecorder ($Configuration)..." -ForegroundColor Cyan
    dotnet restore FastVRecorder.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

    Write-Host "Building FastVRecorder ($Configuration)..." -ForegroundColor Cyan
    dotnet build FastVRecorder.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

    Write-Host "Build succeeded." -ForegroundColor Green
}
finally {
    Pop-Location
}
