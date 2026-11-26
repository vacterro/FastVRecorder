$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'PublishSafety.psm1') -Force

Push-Location $root
try {
    foreach ($folder in @('src', 'tests')) {
        $path = Join-Path $root $folder
        if (-not (Test-Path $path)) { continue }
        $buildDirs = @(Get-ChildItem $path -Directory -Recurse |
            Where-Object { $_.Name -in @('bin', 'obj') } |
            Sort-Object { $_.FullName.Length } -Descending)
        foreach ($directory in $buildDirs) {
            if (Test-ReparsePoint $directory.FullName) { continue }
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        }
    }

    $removed = Remove-OwnedStaging -RepoRoot $root
    Write-Host "Build output removed. Owned staging directories removed: $removed." -ForegroundColor Green
    Write-Host 'dist/FastVRecorder and every portable runtime/config/recording directory were left untouched.'
}
finally {
    Pop-Location
}
