param(
    [string]$Runtime = 'win-x64',
    [string]$Output = 'dist\FastVRecorder',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipTests) {
        Write-Host 'Running the test suite before publishing...' -ForegroundColor Cyan
        dotnet test FastVRecorder.sln -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
    }

    $target = Join-Path $root $Output
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }

    Write-Host "Publishing self-contained portable build to $Output..." -ForegroundColor Cyan
    dotnet publish src\FastVRecorder.App\FastVRecorder.App.csproj `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $target
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    # Portable overrides: ship the original Wintage themes next to the executable.
    if (Test-Path (Join-Path $root 'themes')) {
        Copy-Item (Join-Path $root 'themes') (Join-Path $target 'themes') -Recurse -Force
    }

    Copy-Item (Join-Path $root 'README.md') $target -Force
    Copy-Item (Join-Path $root 'CHANGELOG.md') $target -Force
    New-Item -ItemType Directory -Path (Join-Path $target 'Recordings') -Force | Out-Null

    Write-Host "Published: $target" -ForegroundColor Green
    Write-Host 'Portable layout: FastVRecorder.exe, media/, themes/, Recordings/, config/ (created on first run).'
}
finally {
    Pop-Location
}
