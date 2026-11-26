param(
    [string]$Source = 'dist\FastVRecorder',
    [string]$ReleaseDir = 'release',
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [switch]$KeepTemporary
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'PublishSafety.psm1') -Force

function Get-PackageRelativePath {
    param([string]$Base, [string]$Path)
    $baseFull = [IO.Path]::GetFullPath($Base).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($baseFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$Path' is outside '$Base'."
    }
    return $pathFull.Substring($baseFull.Length)
}

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)][IO.Stream]$Stream)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '') }
    finally { $sha.Dispose() }
}

Push-Location $root
$temporary = $null
try {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "VERSION '$Version' is not a plain semantic version."
    }

    $propsPath = Join-Path $root 'Directory.Build.props'
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $propsVersion = [string]$props.Project.PropertyGroup.Version
    if ($propsVersion -ne $Version) {
        throw "VERSION ($Version) and Directory.Build.props ($propsVersion) disagree."
    }

    $sourcePath = [IO.Path]::GetFullPath((Join-Path $root $Source))
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
        throw "Published source folder does not exist: $sourcePath. Run scripts\Publish.ps1 first."
    }
    if (Test-ReparsePoint $sourcePath) {
        throw "Published source folder is a reparse point: $sourcePath"
    }

    $manifest = Read-PublishManifest -Target $sourcePath
    if ($null -eq $manifest) {
        throw "publish-manifest.json is missing from $sourcePath; refusing to package unverified output."
    }

    $manifestPath = Join-Path $sourcePath 'publish-manifest.json'
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($property in $manifest.GetEnumerator()) {
        $relative = [string]$property.Key
        $expected = [string]$property.Value
        $full = Resolve-ManifestDestination -Target $sourcePath -Relative $relative
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Manifest file is missing: $relative"
        }
        if (Test-ReparsePoint $full) {
            throw "Manifest file is a reparse point: $relative"
        }
        $actual = Get-FileSha256 $full
        if (-not $actual.Equals($expected, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Manifest hash mismatch for $relative (expected $expected, got $actual)."
        }
        $files.Add($relative) | Out-Null
    }
    foreach ($optional in @('publish-manifest.json')) {
        if (Test-Path -LiteralPath (Join-Path $sourcePath $optional) -PathType Leaf) {
            $files.Add($optional) | Out-Null
        }
    }

    $fileSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $files) { $fileSet.Add($relative) | Out-Null }

    # Only manifest-owned files may be shipped. Runtime data, transaction journals and staging
    # metadata are excluded by construction; anything else is development-only output.
    $runtimeData = @('config', 'Recordings', 'logs', 'history.json')
    foreach ($file in Get-ChildItem -LiteralPath $sourcePath -File -Recurse -Force) {
        $relative = Get-PackageRelativePath -Base $sourcePath -Path $file.FullName
        if ($fileSet.Contains($relative)) { continue }
        if ($relative -match '^(\.fvr-update|\.publish-staging)(\\|/|$)' -or
            $relative -match '^(config|Recordings|logs)(\\|/|$)' -or
            $relative -match '(\.part|\.recovered|\.unrecoverable\.json|\.tmp-|\.lock)$') {
            continue
        }
        throw "Unowned file would be packaged: $relative"
    }

    # Ordinal ordering, not Sort-Object: the culture-aware comparer differs between Windows
    # PowerShell 5.1 (NLS) and PowerShell 7 (ICU), which reorders entries and changes the ZIP.
    $sorted = [string[]]$files.ToArray()
    [Array]::Sort($sorted, [System.StringComparer]::Ordinal)
    if ($null -eq $temporary) {
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('fvr-package-' + [Guid]::NewGuid().ToString('N'))
    }
    New-Item -ItemType Directory -Path $temporary -Force | Out-Null
    $staging = Join-Path $temporary 'stage'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $fixedTime = [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
    foreach ($relative in $sorted) {
        $destination = Join-Path $staging $relative
        $directory = Split-Path -Parent $destination
        if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
        Copy-Item -LiteralPath (Resolve-ManifestDestination -Target $sourcePath -Relative $relative) -Destination $destination -Force
        $item = Get-Item -LiteralPath $destination
        # libarchive stores mtime, atime and ctime in the ZIP extra field; all three must be
        # fixed or two runs of the same content produce different bytes.
        $item.CreationTimeUtc = $fixedTime
        $item.LastWriteTimeUtc = $fixedTime
        $item.LastAccessTimeUtc = $fixedTime
    }

    # bsdtar (shipped with Windows) is used instead of .NET's Deflate so the archive bytes do
    # not depend on whether the script runs on PowerShell 5.1 or 7 (different Deflate versions
    # produced different ZIPs for identical content).
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tar -PathType Leaf)) {
        $tar = (Get-Command tar -ErrorAction Stop).Source
    }
    $listPath = Join-Path $temporary 'files.txt'
    $list = $sorted | ForEach-Object { $_.Replace('\', '/') }
    Set-Content -LiteralPath $listPath -Value $list -Encoding ASCII
    $zipTemporary = Join-Path $temporary 'package.zip'
    Push-Location $staging
    try {
        & $tar --format=zip -cf $zipTemporary -T $listPath
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }

    # Verify the archive this run produced, entry by entry, against the manifest. The source
    # check above proves the inputs; only this pass proves the bytes that will ship.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expected = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
    foreach ($property in $manifest.GetEnumerator()) {
        $expected[([string]$property.Key).Replace('\', '/')] = [string]$property.Value
    }
    $expected['publish-manifest.json'] = Get-FileSha256 $manifestPath
    $archive = [IO.Compression.ZipFile]::OpenRead($zipTemporary)
    try {
        if ($archive.Entries.Count -ne $expected.Count) {
            throw "Packaged entry count $($archive.Entries.Count) does not match the manifest count $($expected.Count)."
        }
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ([string]::IsNullOrEmpty($name) -or $name.Contains('\')) {
                throw "Packaged entry name is invalid: '$name'"
            }
            if (-not $expected.ContainsKey($name)) {
                throw "Packaged entry is not manifest-owned: '$name'"
            }
            if (-not $seen.Add($name)) {
                throw "Packaged entry appears more than once: '$name'"
            }
            $stream = $entry.Open()
            try { $actual = Get-StreamSha256 $stream }
            finally { $stream.Dispose() }
            if (-not $actual.Equals($expected[$name], [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Packaged bytes do not match the manifest for '$name' (expected $($expected[$name]), got $actual)."
            }
        }
        if ($seen.Count -ne $expected.Count) {
            throw "Packaged archive is missing manifest entries ($($seen.Count) of $($expected.Count))."
        }
    }
    finally {
        $archive.Dispose()
    }

    $releaseRoot = if ([IO.Path]::IsPathRooted($ReleaseDir)) {
        [IO.Path]::GetFullPath($ReleaseDir)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $root $ReleaseDir))
    }
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    $name = "FastVRecorder-$Version-$Runtime.zip"
    $destination = Join-Path $releaseRoot $name
    Move-Item -LiteralPath $zipTemporary -Destination $destination -Force

    $hash = (Get-FileSha256 $destination).ToUpperInvariant()
    $size = (Get-Item -LiteralPath $destination).Length

    # Keep the checksum ledger append-only: replace only this archive's entries.
    $sumsPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
    $lines = if (Test-Path -LiteralPath $sumsPath) { @(Get-Content -LiteralPath $sumsPath) } else { @() }
    $exeLabel = "FastVRecorder.exe (inside $Version archive)"
    $lines = @($lines | Where-Object {
        $_ -notmatch ([regex]::Escape($name) + '$') -and
        $_ -notmatch ([regex]::Escape($exeLabel) + '$')
    })
    $newLines = @("$hash  $name")
    $exePath = Join-Path $sourcePath 'FastVRecorder.exe'
    if (Test-Path -LiteralPath $exePath -PathType Leaf) {
        $newLines += "$(Get-FileSha256 $exePath)  $exeLabel"
    }
    $lines = $newLines + $lines
    [IO.File]::WriteAllLines($sumsPath, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "Packaged $name" -ForegroundColor Green
    Write-Host "  entries: $($sorted.Count)"
    Write-Host "  bytes:   $size"
    Write-Host "  sha256:  $hash"
    Write-Host "  path:    $destination"
}
finally {
    if ($temporary -and (Test-Path -LiteralPath $temporary)) {
        if ($KeepTemporary) {
            Write-Host "Temporary package kept: $temporary" -ForegroundColor Yellow
        }
        else {
            try { Remove-TreeSafely -Path $temporary } catch { }
        }
    }
    Pop-Location
}
