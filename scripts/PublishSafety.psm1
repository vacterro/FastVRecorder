Set-StrictMode -Version 3.0

$script:ProtectedTopLevel = @('config', 'Recordings')
$script:PreservedAssetDirs = @('media', 'themes', 'languages')
$script:ForbiddenTargetSegments = @('src', 'tests', '.git', '.saipen')

function Get-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}


function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path)
    $rootFull = Get-NormalizedPath $Root
    $pathFull = Get-NormalizedPath $Path
    if ($pathFull.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { return '' }
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$pathFull' is not inside '$rootFull'."
    }
    return $pathFull.Substring($prefix.Length)
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root)
    $p = Get-NormalizedPath $Path
    $r = Get-NormalizedPath $Root
    if ($p.Equals($r, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $p.StartsWith(
        $r + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-ReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path -Force
    return (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $current = Get-NormalizedPath $Path
    while ($current) {
        if (Test-ReparsePoint $current) {
            throw "Refusing path through a reparse point or junction: $current"
        }
        $parent = Split-Path -Path $current -Parent
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
}


function Resolve-PublishTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Output,
        [Parameter(Mandatory = $true)][string]$RepoRoot)
    if ([System.IO.Path]::IsPathRooted($Output)) {
        return Get-NormalizedPath $Output
    }
    $candidate = Get-NormalizedPath (Join-Path (Get-NormalizedPath $RepoRoot) $Output)
    if (-not (Test-PathWithin -Path $candidate -Root $RepoRoot)) {
        throw "A relative publish output must stay inside the repository; refusing to escape with '$Output'."
    }
    return $candidate
}

function Assert-SafeTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$RepoRoot)

    $t = Get-NormalizedPath $Target
    $r = Get-NormalizedPath $RepoRoot

    if (Test-Path -LiteralPath $t -PathType Leaf) {
        throw "The publish target is a file, not a directory: $t"
    }
    if ($t -eq [System.IO.Path]::GetPathRoot($t)) {
        throw "Refusing to publish to a drive root: $t"
    }
    if ($t.Equals($r, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to publish over the repository root: $t"
    }
    if (Test-PathWithin -Path $r -Root $t) {
        throw "Refusing a target that contains the repository root: $t"
    }
    $distRoot = Join-Path $r 'dist'
    if ($t.Equals((Get-NormalizedPath $distRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to publish directly into the dist container: $t"
    }
    if (Test-PathWithin -Path $t -Root $r) {
        $relative = Get-RelativePath -Root $r -Path $t
        $first = ($relative -split '[\\/]')[0]
        if ($script:ForbiddenTargetSegments -contains $first) {
            throw "Refusing to publish inside protected repository area '$first': $t"
        }
    }

    Assert-NoReparsePath $t

    if (Test-Path -LiteralPath $t) {
        $children = @(Get-ChildItem -LiteralPath $t -Force -ErrorAction Stop)
        if ($children.Count -gt 0) {
            if (-not (Test-Path -LiteralPath (Join-Path $t 'FastVRecorder.exe') -PathType Leaf)) {
                throw "The target directory is not empty and does not look like a FastVRecorder installation: $t"
            }
        }
    }

    return $t
}

function Assert-RuntimeNotRunning {
    param([Parameter(Mandatory = $true)][string]$Target)
    $t = Get-NormalizedPath $Target
    try {
        $running = Get-Process -Name 'FastVRecorder' -ErrorAction SilentlyContinue
    }
    catch {
        $running = @()
    }
    foreach ($process in @($running)) {
        $path = $null
        try { $path = $process.Path } catch { }
        if ($path -and (Test-PathWithin -Path $path -Root $t)) {
            throw "FastVRecorder is running from the target directory. Close it before publishing."
        }
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Read-PublishManifest {
    param([Parameter(Mandatory = $true)][string]$Target)
    $path = Join-Path $Target 'publish-manifest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $document = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $document.files) { return $null }
        $files = @{}
        foreach ($property in $document.files.PSObject.Properties) {
            $files[$property.Name] = [string]$property.Value
        }
        return $files
    }
    catch {
        return $null
    }
}

function Write-PublishManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][hashtable]$Files)
    $path = Join-Path $Target 'publish-manifest.json'
    $temporary = $path + '.tmp'
    $payload = [ordered]@{
        schema = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        files = $Files
    }
    $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary -Encoding UTF8
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
    Move-Item -LiteralPath $temporary -Destination $path -Force
}

function Test-ProtectedRelativePath {
    param([Parameter(Mandatory = $true)][string]$Relative)
    $segments = $Relative -split '[\\/]'
    return ($script:ProtectedTopLevel -contains $segments[0])
}

function Test-PreservedAssetRelativePath {
    param([Parameter(Mandatory = $true)][string]$Relative)
    $segments = $Relative -split '[\\/]'
    return ($script:PreservedAssetDirs -contains $segments[0])
}

function Install-PortablePackage {
    param(
        [Parameter(Mandatory = $true)][string]$Staging,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$RepoRoot)

    $stagingFull = Get-NormalizedPath $Staging
    $targetFull = Assert-SafeTarget -Target $Target -RepoRoot $RepoRoot
    Assert-NoReparsePath $stagingFull
    Assert-RuntimeNotRunning $targetFull

    if (-not (Test-Path -LiteralPath (Join-Path $stagingFull 'FastVRecorder.exe') -PathType Leaf)) {
        throw "Staging does not contain FastVRecorder.exe: $stagingFull"
    }

    $oldManifest = Read-PublishManifest -Target $targetFull
    New-Item -ItemType Directory -Path $targetFull -Force | Out-Null

    $newManifest = @{}
    $stagingFiles = @(Get-ChildItem -LiteralPath $stagingFull -Recurse -File -Force)
    foreach ($file in $stagingFiles) {
        $relative = Get-RelativePath -Root $stagingFull -Path $file.FullName
        if (Test-ProtectedRelativePath $relative) { continue }

        $destination = Join-Path $targetFull $relative
        if ((Test-PreservedAssetRelativePath $relative) -and (Test-Path -LiteralPath $destination)) {
            # Themes, language packs and cue assets may be user-modified. Replace a file only
            # when the previous manifest proves it is an unmodified shipped default.
            $oldHash = $null
            if ($null -ne $oldManifest -and $oldManifest.ContainsKey($relative)) {
                $oldHash = $oldManifest[$relative]
            }
            $currentHash = Get-FileSha256 $destination
            if ($null -eq $oldHash) {
                # A file installed before manifests existed. Adopt it only when it is byte-identical
                # to the shipped asset; otherwise it is user-modified and must be preserved.
                $stagingHash = Get-FileSha256 $file.FullName
                if (-not $currentHash.Equals($stagingHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
            }
            elseif (-not $currentHash.Equals($oldHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
        }

        Assert-NoReparsePath (Split-Path -Path $destination -Parent)
        $parent = Split-Path -Path $destination -Parent
        if (-not [string]::IsNullOrEmpty($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $newManifest[$relative] = Get-FileSha256 $destination
    }

    if ($null -ne $oldManifest) {
        foreach ($relative in $oldManifest.Keys) {
            if ($newManifest.ContainsKey($relative)) { continue }
            if (Test-ProtectedRelativePath $relative) { continue }
            if (Test-PreservedAssetRelativePath $relative) { continue }
            $destination = Join-Path $targetFull $relative
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { continue }
            Assert-NoReparsePath $destination
            $current = Get-FileSha256 $destination
            if ($current -eq $oldManifest[$relative]) {
                Remove-Item -LiteralPath $destination -Force
            }
        }
    }

    New-Item -ItemType Directory -Path (Join-Path $targetFull 'Recordings') -Force | Out-Null
    Write-PublishManifest -Target $targetFull -Files $newManifest
    return $targetFull
}

function Remove-OwnedStaging {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)
    $stagingRoot = Join-Path (Get-NormalizedPath $RepoRoot) 'dist\.staging'
    if (-not (Test-Path -LiteralPath $stagingRoot)) { return 0 }
    Assert-NoReparsePath $stagingRoot
    $removed = 0
    foreach ($directory in @(Get-ChildItem -LiteralPath $stagingRoot -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($directory.Name -notlike 'publish-*') { continue }
        if (Test-ReparsePoint $directory.FullName) { continue }
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        $removed++
    }
    return $removed
}

Export-ModuleMember -Function @(
    'Get-NormalizedPath',
    'Get-RelativePath',
    'Test-PathWithin',
    'Test-ReparsePoint',
    'Assert-NoReparsePath',
    'Assert-SafeTarget',
    'Resolve-PublishTarget',
    'Assert-RuntimeNotRunning',
    'Get-FileSha256',
    'Install-PortablePackage',
    'Remove-OwnedStaging'
)
