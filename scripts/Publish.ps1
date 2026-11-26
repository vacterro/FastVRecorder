param(
    [string]$Runtime = 'win-x64',
    [string]$Output = 'dist\FastVRecorder',
    [switch]$SkipTests,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'PublishSafety.psm1') -Force

Push-Location $root
$staging = $null
$stagingLease = $null
try {
    $target = Resolve-PublishTarget -Output $Output -RepoRoot $root
    Assert-SafeTarget -Target $target -RepoRoot $root | Out-Null
    Assert-RuntimeNotRunning -Target $target

    if (-not $SkipTests) {
        Write-Host 'Running the test suite before publishing...' -ForegroundColor Cyan
        dotnet test FastVRecorder.sln -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
    }

    Remove-OwnedStaging -RepoRoot $root | Out-Null
    $stagingLease = New-OwnedStaging -RepoRoot $root
    $staging = $stagingLease.Path

    Write-Host "Publishing to isolated staging: $staging" -ForegroundColor Cyan
    dotnet publish src\FastVRecorder.App\FastVRecorder.App.csproj `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    foreach ($document in @('README.md', 'CHANGELOG.md', 'VERSION', 'THIRD-PARTY-NOTICES.md')) {
        $source = Join-Path $root $document
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $staging -Force }
    }

    # Runtime redistribution notices must come from the SDK that produced this package, never
    # from a manual release step.
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
    foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $noticeSource = Join-Path $dotnetRoot $notice
        if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) {
            throw "Runtime notice is missing next to the .NET host: $noticeSource"
        }
        Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $staging $notice) -Force
    }
    $docs = Join-Path $root 'docs'
    if (Test-Path -LiteralPath $docs) {
        Copy-Item -LiteralPath $docs -Destination (Join-Path $staging 'docs') -Recurse -Force
    }
    $screenshots = Join-Path $root 'media\screenshots'
    if (Test-Path -LiteralPath $screenshots) {
        Copy-Item -LiteralPath $screenshots -Destination (Join-Path $staging 'media\screenshots') -Recurse -Force
    }

    # The packaged documentation must not contain links that resolve only in the repository.
    $markdown = @(Join-Path $staging 'README.md') + @(Get-ChildItem -LiteralPath (Join-Path $staging 'docs') -Filter *.md -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    foreach ($document in $markdown) {
        $documentDir = Split-Path -Parent $document
        $text = Get-Content -LiteralPath $document -Raw
        foreach ($match in [regex]::Matches($text, '\]\(([^)]+)\)|<img[^>]+src="([^"]+)"')) {
            $link = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
            if ($link -match '^(https?:|mailto:|#)') { continue }
            $relative = ($link -split '#')[0]
            if ([string]::IsNullOrWhiteSpace($relative)) { continue }
            $resolved = [IO.Path]::GetFullPath((Join-Path $documentDir $relative))
            if (-not (Test-Path -LiteralPath $resolved)) {
                throw "Packaged documentation link is broken: $document -> $link"
            }
        }
    }

    foreach ($required in @('FastVRecorder.exe', 'FastVRecorder.Core.dll', 'media\start.wav', 'media\screenshots\recorder.png', 'languages\en.json', 'themes\goldendefault.json', 'docs\USER_GUIDE.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) {
            throw "Staging validation failed: missing $required."
        }
    }
    if ((Test-Path -LiteralPath (Join-Path $staging 'config')) -or
        (Test-Path -LiteralPath (Join-Path $staging 'Recordings'))) {
        throw 'Staging validation failed: generated package contains runtime data.'
    }

    Install-PortablePackage -Staging $staging -Target $target -RepoRoot $root | Out-Null
    Write-Host "Published safely: $target" -ForegroundColor Green
    Write-Host 'Existing config/, Recordings/, custom themes, languages and cue assets were preserved.'
}
finally {
    if ($stagingLease) {
        Close-OwnedStaging -Staging $stagingLease
    }
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        if ($KeepStaging) {
            Write-Host "Staging kept for diagnosis: $staging" -ForegroundColor Yellow
        }
        else {
            try { Remove-TreeSafely -Path $staging } catch { }
        }
    }
    Pop-Location
}
