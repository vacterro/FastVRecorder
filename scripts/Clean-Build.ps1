$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    foreach ($folder in @('src', 'tests')) {
        $path = Join-Path $root $folder
        if (-not (Test-Path $path)) { continue }
        Get-ChildItem $path -Directory -Recurse |
            Where-Object { $_.Name -in @('bin', 'obj') } |
            Remove-Item -Recurse -Force
    }

    $dist = Join-Path $root 'dist'
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

    Write-Host 'Build output and dist/ removed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
