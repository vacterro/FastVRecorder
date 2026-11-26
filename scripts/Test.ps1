param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $arguments = @('test', 'FastVRecorder.sln', '-c', $Configuration, '--nologo')
    if ($NoBuild) { $arguments += '--no-build' }
    Write-Host "Running tests ($Configuration)..." -ForegroundColor Cyan
    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
    Write-Host "All tests passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
