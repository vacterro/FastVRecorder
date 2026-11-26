Set-StrictMode -Version 3.0

$script:ProtectedTopLevel = @('config', 'Recordings')
$script:PreservedAssetDirs = @('media', 'themes', 'languages')
$script:ForbiddenTargetSegments = @('src', 'tests', '.git', '.saipen')
$script:TransactionDirName = '.fvr-update'
$script:StagingMarkerName = '.fvr-staging.json'
$script:StagingLockName = '.fvr-staging.lock'
$script:ReservedDeviceNames = @(
    'CON', 'PRN', 'AUX', 'NUL', 'CLOCK$',
    'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
    'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9',
    ('COM' + [char]0x00B9), ('COM' + [char]0x00B2), ('COM' + [char]0x00B3),
    ('LPT' + [char]0x00B9), ('LPT' + [char]0x00B2), ('LPT' + [char]0x00B3)
)
$script:FaultHook = $null

function Set-PublishFaultHook {
    <#
    Test-only deterministic fault injection. The hook receives a stage name and a detail
    string and may throw to simulate a crash at that point. Never set in production.
    #>
    param([scriptblock]$Hook)
    $script:FaultHook = $Hook
}

function Invoke-FaultPoint {
    param([string]$Point, [string]$Detail = '')
    if ($null -ne $script:FaultHook) {
        & $script:FaultHook $Point $Detail
    }
}

function Get-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) { return $full }
    return $full.TrimEnd(
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
    $prefix = $rootFull
    if (-not $prefix.EndsWith([string][System.IO.Path]::DirectorySeparatorChar)) {
        $prefix = $prefix + [System.IO.Path]::DirectorySeparatorChar
    }
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
    $prefix = $r
    if (-not $prefix.EndsWith([string][System.IO.Path]::DirectorySeparatorChar)) {
        $prefix = $prefix + [System.IO.Path]::DirectorySeparatorChar
    }
    return $p.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
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

function Get-ManifestPathError {
    <#
    Strict relative-path contract for every path taken from (or written to) a publish
    manifest. Returns $null for a valid relative path, otherwise a short reason.
    #>
    param([string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative)) { return 'is empty' }
    if ($Relative.Length -gt 2000) { return 'is longer than 2000 characters' }
    if ($Relative.StartsWith('\\')) { return 'is a UNC device path' }
    if ($Relative.StartsWith('/') -or $Relative.StartsWith('\')) { return 'is rooted' }
    if ([System.IO.Path]::IsPathRooted($Relative)) { return 'is rooted' }
    if ($Relative -match '^[A-Za-z]:') { return 'carries a drive prefix' }
    foreach ($character in $Relative.ToCharArray()) {
        $code = [int]$character
        if ($code -lt 32 -or $code -eq 127) { return 'contains a control character' }
    }
    if ($Relative -match '[<>:"|?*]') { return 'contains a Windows-reserved character' }
    if ($Relative.Contains('~')) { return 'contains a short-name alias marker' }
    $segments = $Relative -split '[\\/]'
    foreach ($segment in $segments) {
        if ($segment.Length -eq 0) { return 'contains an empty path segment' }
        if ($segment -eq '.' -or $segment -eq '..') { return 'contains a relative path segment' }
        if ($segment.Length -gt 255) { return 'contains a segment longer than 255 characters' }
        if ($segment -ne $segment.Trim()) { return 'contains leading or trailing whitespace in a segment' }
        if ($segment.EndsWith('.')) { return 'contains a segment ending in a dot' }
        $base = ($segment -split '\.', 2)[0]
        if ($base -ne $base.Trim()) { return "contains an ambiguous device-like segment '$segment'" }
        if ($script:ReservedDeviceNames -contains $base.ToUpperInvariant()) {
            return "names the reserved Windows device '$segment'"
        }
    }
    return $null
}

function Test-ManifestRelativePath {
    param([string]$Relative)
    return ($null -eq (Get-ManifestPathError $Relative))
}

function Assert-ManifestRelativePath {
    param([string]$Relative)
    $problem = Get-ManifestPathError $Relative
    if ($null -ne $problem) {
        throw "Manifest path '$Relative' $problem; refusing to use it."
    }
}

function Test-Sha256Hex {
    param([string]$Value)
    return ($Value -match '^[0-9A-Fa-f]{64}$')
}

function Resolve-ManifestDestination {
    <# Canonical destination for a manifest-relative path, proven inside the target. #>
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$Relative)
    Assert-ManifestRelativePath $Relative
    $targetFull = Get-NormalizedPath $Target
    $candidate = Get-NormalizedPath (Join-Path $targetFull $Relative)
    if ($candidate.Equals($targetFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest path '$Relative' resolves to the target directory itself."
    }
    if (-not (Test-PathWithin -Path $candidate -Root $targetFull)) {
        throw "Manifest path '$Relative' escapes the authorized publication root."
    }
    return $candidate
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
    if ($t.Equals([System.IO.Path]::GetPathRoot($t), [System.StringComparison]::OrdinalIgnoreCase)) {
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
    $stagingRoot = Join-Path $r 'dist\.staging'
    if (Test-PathWithin -Path $t -Root $stagingRoot) {
        throw "Refusing a publish target inside the private staging area: $t"
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
        $children = @(Get-ChildItem -LiteralPath $t -Force -ErrorAction Stop |
            Where-Object { $_.Name -ne $script:TransactionDirName })
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
        $running = @(Get-Process -Name 'FastVRecorder' -ErrorAction SilentlyContinue)
    }
    catch {
        $running = @()
    }
    foreach ($process in $running) {
        $path = $null
        try { $path = $process.Path } catch { $path = $null }
        if ([string]::IsNullOrEmpty($path)) {
            # The requirement is explicit: an uninspectable process could be running from the
            # target, and publishing over a live installation must never be a guess.
            throw "A FastVRecorder process (PID $($process.Id)) is running but its executable path cannot be read. Close it before publishing."
        }
        if (Test-PathWithin -Path $path -Root $t) {
            throw "FastVRecorder is running from the target directory. Close it before publishing."
        }
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-DurableText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text)
    $temporary = $Path + '.tmp-' + [Guid]::NewGuid().ToString('N')
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $stream = [System.IO.File]::Open(
        $temporary,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
    try {
        if (Test-Path -LiteralPath $Path) {
            $backup = $Path + '.old-' + [Guid]::NewGuid().ToString('N')
            [System.IO.File]::Replace($temporary, $Path, $backup)
            if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
        }
        else {
            [System.IO.File]::Move($temporary, $Path)
        }
    }
    finally {
        try { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } } catch { }
    }
}

function Read-PublishManifest {
    <#
    Returns a case-insensitive name -> SHA-256 table, or $null when no manifest exists.
    A present but unreadable, malformed or contract-violating manifest is an ERROR: a
    damaged manifest must never be silently interpreted as "no ownership information"
    or as permission for legacy destructive behavior.
    #>
    param([Parameter(Mandatory = $true)][string]$Target)
    $path = Join-Path $Target 'publish-manifest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    if (Test-ReparsePoint $path) {
        throw "publish-manifest.json is a reparse point; refusing to trust it."
    }
    try {
        $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($text)) { throw 'the file is empty' }
        $document = $text | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "publish-manifest.json is damaged and cannot be trusted: $($_.Exception.Message)"
    }
    if ($null -eq $document) {
        throw 'publish-manifest.json is damaged: the document is null.'
    }
    $filesProperty = $document.PSObject.Properties['files']
    if ($null -eq $filesProperty -or $null -eq $filesProperty.Value) {
        throw 'publish-manifest.json is damaged: no files table.'
    }
    if ($filesProperty.Value -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'publish-manifest.json is damaged: the files table is not an object.'
    }
    if ($null -ne $document.PSObject.Properties['schema']) {
        $schema = [string]$document.schema
        if ($schema -ne '1') {
            throw "publish-manifest.json declares unsupported schema '$schema'."
        }
    }
    $files = @{}
    foreach ($property in $filesProperty.Value.PSObject.Properties) {
        Assert-ManifestRelativePath $property.Name
        if ($property.Name -eq 'publish-manifest.json' -or $property.Name -match '^\.fvr-update([\\/]|$)') {
            throw "publish-manifest.json lists the tool-reserved path '$($property.Name)'."
        }
        $hash = [string]$property.Value
        if (-not (Test-Sha256Hex $hash)) {
            throw "publish-manifest.json entry '$($property.Name)' does not carry a SHA-256 digest."
        }
        if ($files.ContainsKey($property.Name)) {
            throw "publish-manifest.json lists '$($property.Name)' more than once (case-insensitive duplicate)."
        }
        $files[$property.Name] = $hash.ToUpperInvariant()
    }
    return $files
}

function Write-PublishManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][hashtable]$Files)
    $path = Join-Path $Target 'publish-manifest.json'
    $payload = [ordered]@{
        schema = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        files = $Files
    }
    $json = $payload | ConvertTo-Json -Depth 5
    Assert-NoReparsePath (Split-Path -Path $path -Parent)
    if (Test-Path -LiteralPath $path) {
        if (Test-ReparsePoint $path) {
            throw "Refusing to replace a publish manifest that is a reparse point: $path"
        }
    }
    Write-DurableText -Path $path -Text $json
}

function Test-ProtectedRelativePath {
    param([Parameter(Mandatory = $true)][string]$Relative)
    Assert-ManifestRelativePath $Relative
    $segments = $Relative -split '[\\/]'
    return ($script:ProtectedTopLevel -contains $segments[0])
}

function Test-PreservedAssetRelativePath {
    param([Parameter(Mandatory = $true)][string]$Relative)
    Assert-ManifestRelativePath $Relative
    $segments = $Relative -split '[\\/]'
    return ($script:PreservedAssetDirs -contains $segments[0])
}

function Copy-FileAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string]$ExpectedHash = $null,
        [switch]$EnforcePreimage)
    $parent = Split-Path -Path $Destination -Parent
    Assert-NoReparsePath $parent
    if (-not [string]::IsNullOrEmpty($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporary = Join-Path $parent ('.fvr-new-' + [Guid]::NewGuid().ToString('N'))
    try {
        [System.IO.File]::Copy($Source, $temporary, $false)
        if ($EnforcePreimage) {
            # PowerShell binds -ExpectedHash $null to '', so an empty string is the absent case.
            if (-not [string]::IsNullOrEmpty($ExpectedHash)) {
                if (-not (Test-Path -LiteralPath $Destination -PathType Leaf)) {
                    throw "'$Destination' disappeared while the publication was running."
                }

                if (Test-ReparsePoint $Destination) {
                    throw "Refusing to overwrite a reparse point: $Destination"
                }

                $liveHash = Get-FileSha256 $Destination
                if (-not $liveHash.Equals($ExpectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "'$Destination' changed while the publication was running."
                }
            }
            elseif (Test-Path -LiteralPath $Destination) {
                throw "'$Destination' appeared while the publication was running."
            }
        }
        if (Test-Path -LiteralPath $Destination) {
            if (Test-ReparsePoint $Destination) {
                throw "Refusing to overwrite a reparse point: $Destination"
            }
        }
        if (Test-Path -LiteralPath $Destination) {
            $backup = Join-Path $parent ('.fvr-old-' + [Guid]::NewGuid().ToString('N'))
            [System.IO.File]::Replace($temporary, $Destination, $backup)
            if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
        }
        else {
            [System.IO.File]::Move($temporary, $Destination)
        }
    }
    finally {
        try { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } } catch { }
    }
}

function Invoke-WithPublishLock {
    <# Exclusive OS-level lock. A second publisher (or the same process again) is refused. #>
    param(
        [Parameter(Mandatory = $true)][string]$LockPath,
        [Parameter(Mandatory = $true)][scriptblock]$Action)
    $parent = Split-Path -Path $LockPath -Parent
    if (-not [string]::IsNullOrEmpty($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $stream = $null
    try {
        $stream = [System.IO.File]::Open(
            $LockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch {
        throw "Another publish operation is already in progress (lock: $LockPath)."
    }
    try {
        & $Action
    }
    finally {
        $stream.Dispose()
    }
}

function Write-UpdateJournal {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionDir,
        [Parameter(Mandatory = $true)][hashtable]$Journal)
    Write-DurableText -Path (Join-Path $TransactionDir 'journal.json') -Text ($Journal | ConvertTo-Json -Depth 8)
}

function Repair-InterruptedPublish {
    <#
    Roll a previous, unfinished publication back to its previous generation. The journal and
    its backups are the only authority; a damaged journal fails closed instead of guessing.
    #>
    param([Parameter(Mandatory = $true)][string]$Target)
    $transactionDir = Join-Path $Target $script:TransactionDirName
    $journalPath = Join-Path $transactionDir 'journal.json'
    if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) { return }
    if (Test-ReparsePoint $journalPath) {
        throw "The update journal is a reparse point; refusing to use it: $journalPath"
    }
    try {
        $journal = Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The update journal is damaged and cannot be repaired automatically: $journalPath. Inspect $transactionDir manually."
    }
    $backupDir = Join-Path $transactionDir 'backup'
    $phase = [string]$journal.phase
    if ($phase -eq 'committed') {
        if (Test-Path -LiteralPath $backupDir) { Remove-TreeSafely -Path $backupDir }
        Remove-Item -LiteralPath $journalPath -Force
        return
    }
    if ($phase -ne 'prepared') {
        throw "The update journal declares an unknown phase '$phase'; refusing to guess a repair strategy."
    }
    if ($null -eq $journal.items) {
        throw "The update journal carries no mutation plan; manual repair is required: $journalPath"
    }
    $validated = New-Object System.Collections.ArrayList
    foreach ($item in @($journal.items)) {
        if ($null -eq $item -or [string]::IsNullOrWhiteSpace([string]$item.relative)) {
            throw "The update journal carries a malformed entry; manual repair is required: $journalPath"
        }

        $relative = [string]$item.relative
        if (Test-ProtectedRelativePath $relative) {
            throw "The update journal targets the protected path '$relative'; refusing to repair it automatically."
        }

        if ($relative -eq 'publish-manifest.json' -or $relative -match '^\.fvr-update([\\/]|$)') {
            throw "The update journal targets the tool-reserved path '$relative'; manual repair is required."
        }

        $backupPath = $null
        if ([bool]$item.existed) {
            $backupName = [string]$item.backup
            if ($backupName -notmatch '^[0-9]{4}\.bak$') {
                throw "The update journal names an invalid backup '$backupName'; manual repair is required."
            }

            $backupPath = Join-Path $backupDir $backupName
            if (-not (Test-PathWithin -Path $backupPath -Root $backupDir)) {
                throw "The update journal backup escapes the backup directory; manual repair is required."
            }

            if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
                throw "The update journal references a missing backup for '$relative'; manual repair is required."
            }

            if (Test-ReparsePoint $backupPath) {
                throw "The update journal backup is a reparse point; manual repair is required."
            }

            $hashProperty = $item.PSObject.Properties['hash']
            if ($null -ne $hashProperty -and -not [string]::IsNullOrWhiteSpace([string]$hashProperty.Value)) {
                $backupHash = Get-FileSha256 $backupPath
                if (-not $backupHash.Equals([string]$hashProperty.Value, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "The backup for '$relative' does not match the journaled pre-image; manual repair is required."
                }
            }
        }

        $actionProperty = $item.PSObject.Properties['action']
        $action = if ($null -ne $actionProperty) { [string]$actionProperty.Value } else { 'copy' }
        if ($action -ne 'copy' -and $action -ne 'delete') {
            throw "The update journal declares an unknown action '$action'; manual repair is required."
        }

        $hashValue = $null
        $hashPropertyItem = $item.PSObject.Properties['hash']
        if ($null -ne $hashPropertyItem) { $hashValue = [string]$hashPropertyItem.Value }

        $newHashValue = $null
        $newHashProperty = $item.PSObject.Properties['newHash']
        if ($null -ne $newHashProperty) { $newHashValue = [string]$newHashProperty.Value }

        $null = $validated.Add([pscustomobject]@{
            relative = $relative
            action = $action
            existed = [bool]$item.existed
            backup = $backupPath
            hash = $hashValue
            newHash = $newHashValue
        })
    }
    if ([bool]$journal.manifestExisted -and
        -not (Test-Path -LiteralPath (Join-Path $backupDir 'manifest.bak') -PathType Leaf)) {
        throw 'The update journal references a missing manifest backup; manual repair is required.'
    }

    $skipRollback = @()
    $skipProperty = $journal.PSObject.Properties['skipRollback']
    if ($null -ne $skipProperty -and $null -ne $skipProperty.Value) {
        $skipRollback = @($skipProperty.Value | ForEach-Object { [string]$_ })
    }

    Write-Warning "Recovering an interrupted publication in $Target (journal phase '$phase')."
    foreach ($item in $validated) {
        $relative = $item.relative
        if ($skipRollback -contains $relative) {
            Write-Warning "Leaving '$relative' untouched: it changed after its backup was captured and rolling it back would destroy that edit."
            continue
        }

        $destination = Resolve-ManifestDestination -Target $Target -Relative $relative
        $exists = Test-Path -LiteralPath $destination -PathType Leaf
        if ($exists -and (Test-ReparsePoint $destination)) {
            throw "Refusing to touch a reparse point during rollback: $destination"
        }

        $currentHash = $null
        if ($exists) { $currentHash = Get-FileSha256 $destination }

        if ($item.action -eq 'delete') {
            if (-not $exists) {
                if ($null -ne $item.backup) {
                    Copy-FileAtomic -Source $item.backup -Destination $destination
                }
            }
            elseif ([string]::IsNullOrEmpty($item.hash) -or
                -not $currentHash.Equals($item.hash, [System.StringComparison]::OrdinalIgnoreCase)) {
                Write-Warning "Leaving '$relative' untouched: it changed after the backup and the deletion never happened."
            }

            continue
        }

        if ($item.existed) {
            if (-not $exists) {
                if ($null -ne $item.backup) {
                    Copy-FileAtomic -Source $item.backup -Destination $destination
                }
            }
            elseif (-not [string]::IsNullOrEmpty($item.hash) -and
                $currentHash.Equals($item.hash, [System.StringComparison]::OrdinalIgnoreCase)) {
                # Still the verified pre-image: nothing to roll back.
            }
            elseif (-not [string]::IsNullOrEmpty($item.newHash) -and
                $currentHash.Equals($item.newHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                if ($null -ne $item.backup) {
                    Copy-FileAtomic -Source $item.backup -Destination $destination
                }
            }
            else {
                Write-Warning "Leaving '$relative' untouched: its current bytes are neither this publication's output nor the journaled pre-image."
            }
        }
        else {
            if (-not $exists) { continue }
            if (-not [string]::IsNullOrEmpty($item.newHash) -and
                $currentHash.Equals($item.newHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $destination -Force
            }
            else {
                Write-Warning "Leaving '$relative' untouched: it was not written by this publication."
            }
        }
    }

    $manifestPath = Join-Path $Target 'publish-manifest.json'
    $manifestBackup = Join-Path $backupDir 'manifest.bak'
    if ([bool]$journal.manifestExisted) {
        if (-not (Test-Path -LiteralPath $manifestBackup -PathType Leaf)) {
            throw 'The update journal references a missing manifest backup; manual repair is required.'
        }

        $manifestHashProperty = $journal.PSObject.Properties['manifestHash']
        if ($null -ne $manifestHashProperty -and -not [string]::IsNullOrWhiteSpace([string]$manifestHashProperty.Value)) {
            $manifestBackupHash = Get-FileSha256 $manifestBackup
            if (-not $manifestBackupHash.Equals([string]$manifestHashProperty.Value, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'The manifest backup does not match the journaled pre-image; manual repair is required.'
            }
        }

        Copy-FileAtomic -Source $manifestBackup -Destination $manifestPath
    }
    elseif (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        Assert-NoReparsePath $manifestPath
        Remove-Item -LiteralPath $manifestPath -Force
    }

    if (Test-Path -LiteralPath $backupDir) { Remove-TreeSafely -Path $backupDir }
    Remove-Item -LiteralPath $journalPath -Force
    Write-Warning 'Interrupted publication rolled back to the previous generation.'
}

function New-OwnedStaging {
    <#
    Create a staging directory that proves its ownership: a marker file naming this
    repository (plus a nonce matching the directory) and an exclusive liveness lock held
    for as long as the publisher works in it.
    #>
    param([Parameter(Mandatory = $true)][string]$RepoRoot)
    $repository = Get-NormalizedPath $RepoRoot
    $stagingRoot = Join-Path $repository 'dist\.staging'
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    Assert-NoReparsePath $stagingRoot
    $name = 'publish-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $directory = Join-Path $stagingRoot $name
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
    $lease = [System.IO.File]::Open(
        (Join-Path $directory $script:StagingLockName),
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    $marker = [ordered]@{
        schema = 1
        repoRoot = $repository
        nonce = $name
        pid = $PID
        createdAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $directory $script:StagingMarkerName),
        ($marker | ConvertTo-Json -Depth 4),
        (New-Object System.Text.UTF8Encoding($false)))
    return [pscustomobject]@{ Path = $directory; Lease = $lease }
}

function Close-OwnedStaging {
    param([Parameter(Mandatory = $true)]$Staging)
    if ($null -ne $Staging.Lease) {
        try { $Staging.Lease.Dispose() } catch { }
        $Staging.Lease = $null
    }
}

function Remove-OwnedStaging {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)
    $repository = Get-NormalizedPath $RepoRoot
    $stagingRoot = Join-Path $repository 'dist\.staging'
    if (-not (Test-Path -LiteralPath $stagingRoot)) { return 0 }
    Assert-NoReparsePath $stagingRoot
    $removed = 0
    foreach ($directory in @(Get-ChildItem -LiteralPath $stagingRoot -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($directory.Name -notlike 'publish-*') { continue }
        if (Test-ReparsePoint $directory.FullName) { continue }
        $markerPath = Join-Path $directory.FullName $script:StagingMarkerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { continue }
        if (Test-ReparsePoint $markerPath) { continue }
        try {
            $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        }
        catch { continue }
        if ($null -eq $marker) { continue }
        if ([string]$marker.repoRoot -ne $repository) { continue }
        if ([string]$marker.nonce -ne $directory.Name) { continue }
        $lease = $null
        try {
            $lease = [System.IO.File]::Open(
                (Join-Path $directory.FullName $script:StagingLockName),
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
        }
        catch {
            # Another publisher holds the liveness lock: the directory is active.
            continue
        }
        $lease.Dispose()
        Remove-TreeSafely -Path $directory.FullName
        $removed++
    }
    return $removed
}

function Get-PublicationPlan {
    <#
    Read-only preflight: validate every input and derive the exact mutation plan plus the
    next manifest. Nothing on disk is changed here; a refusal leaves the target untouched.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Staging,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][AllowNull()][hashtable]$OldManifest)
    $stagingFull = Get-NormalizedPath $Staging
    $targetFull = Get-NormalizedPath $Target
    $newManifest = @{}
    $plan = New-Object System.Collections.ArrayList
    $seenRelative = @{}
    $stagingFiles = @(Get-ChildItem -LiteralPath $stagingFull -Recurse -File -Force)
    foreach ($file in $stagingFiles) {
        $relative = Get-RelativePath -Root $stagingFull -Path $file.FullName
        Assert-ManifestRelativePath $relative
        if ($file.DirectoryName -eq $stagingFull -and
            ($file.Name -eq $script:StagingMarkerName -or $file.Name -eq $script:StagingLockName)) {
            # Ownership metadata belongs to the staging area, never to the portable package.
            continue;
        }
        if ($relative -eq 'publish-manifest.json' -or $relative -match '^\.fvr-update([\/]|$)') {
            throw "Staging carries the tool-reserved path '$relative'."
        }
        if ($seenRelative.ContainsKey($relative)) {
            throw "Staging carries '$relative' more than once (case-insensitive collision)."
        }
        $seenRelative[$relative] = $true
        if (Test-ProtectedRelativePath $relative) { continue }
        $destination = Resolve-ManifestDestination -Target $targetFull -Relative $relative
        Assert-NoReparsePath $destination
        $stagingHash = Get-FileSha256 $file.FullName
        $expectedHash = $null
        $destinationExists = Test-Path -LiteralPath $destination -PathType Leaf
        if ($destinationExists) {
            $currentHash = Get-FileSha256 $destination
            if (Test-PreservedAssetRelativePath $relative) {
                # Themes, language packs and cue assets may be user-modified. Replace a file
                # only when the previous manifest proves it is an unmodified shipped default.
                $oldHash = $null
                if ($null -ne $OldManifest -and $OldManifest.ContainsKey($relative)) {
                    $oldHash = $OldManifest[$relative]
                }
                if ($null -eq $oldHash) {
                    if (-not $currentHash.Equals($stagingHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                        continue
                    }
                }
                elseif (-not $currentHash.Equals($oldHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
            }
            elseif ($currentHash.Equals($stagingHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                $newManifest[$relative] = $stagingHash
                continue
            }
            elseif ($null -eq $OldManifest) {
                throw "Refusing to overwrite '$relative': no trusted publish manifest exists, so ownership cannot be proven. Delete or rename the file to continue."
            }
            elseif (-not $OldManifest.ContainsKey($relative)) {
                throw "Refusing to overwrite '$relative': it is not owned by the previous manifest. Delete or rename the file to continue."
            }
            elseif (-not $currentHash.Equals($OldManifest[$relative], [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to overwrite user-modified file '$relative': it does not match the previous manifest, so the local change would be lost. Delete or rename it to continue."
            }
            $expectedHash = $currentHash
        }
        if ($newManifest.ContainsKey($relative)) {
            throw "Staging carries '$relative' more than once (case-insensitive collision)."
        }
        $newManifest[$relative] = $stagingHash
        $null = $plan.Add([pscustomobject]@{
            relative = $relative
            action = 'copy'
            source = $file.FullName
            expectedHash = $expectedHash
            stagingHash = $stagingHash
        })
    }

    if ($null -ne $OldManifest) {
        foreach ($relative in @($OldManifest.Keys)) {
            if ($newManifest.ContainsKey($relative)) { continue }
            if (Test-ProtectedRelativePath $relative) { continue }
            if (Test-PreservedAssetRelativePath $relative) { continue }
            $destination = Resolve-ManifestDestination -Target $targetFull -Relative $relative
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { continue }
            Assert-NoReparsePath $destination
            $current = Get-FileSha256 $destination
            if ($current.Equals($OldManifest[$relative], [System.StringComparison]::OrdinalIgnoreCase)) {
                $null = $plan.Add([pscustomobject]@{
                    relative = $relative
                    action = 'delete'
                    source = $null
                    expectedHash = $current
                })
            }
        }
    }

    return [pscustomobject]@{ Plan = $plan; Manifest = $newManifest }
}

function Remove-TreeSafely {
    <# Deletes a directory tree this tool owns without ever following a reparse point. #>
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (Test-ReparsePoint $Path) {
        Remove-Item -LiteralPath $Path -Force
        return
    }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Remove-Item -LiteralPath $Path -Force
        return
    }
    foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) {
        if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Remove-Item -LiteralPath $child.FullName -Force
        }
        elseif ($child.PSIsContainer) {
            Remove-TreeSafely -Path $child.FullName
        }
        else {
            Remove-Item -LiteralPath $child.FullName -Force
        }
    }
    Remove-Item -LiteralPath $Path -Force
}

function Invoke-PublicationTransaction {
    <#
    Apply a preflighted plan under a durable journal. Every destination is either the
    previous bytes or the new bytes; any failure rolls the whole publication back.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)]$Prepared)
    $targetFull = Get-NormalizedPath $Target
    $transactionDir = Join-Path $targetFull $script:TransactionDirName
    $backupDir = Join-Path $transactionDir 'backup'
    $journalPath = Join-Path $transactionDir 'journal.json'

    # Repair already ran under the lock. A backup tree without a journal is an orphan from a
    # failure before the journal was written and would wedge every later publish.
    if (Test-Path -LiteralPath $backupDir) {
        if (Test-Path -LiteralPath $journalPath) {
            throw "Refusing to start a publication while an unrepaired journal exists: $journalPath"
        }
        Remove-TreeSafely -Path $backupDir
    }

    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    Assert-NoReparsePath $backupDir

    $items = New-Object System.Collections.ArrayList
    $index = 0
    $manifestPath = Join-Path $targetFull 'publish-manifest.json'
    $manifestExisted = Test-Path -LiteralPath $manifestPath -PathType Leaf
    $journal = @{
        schema = 1
        phase = 'prepared'
        target = $targetFull
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $manifestExisted
        items = @()
    }

    try {
        Invoke-FaultPoint -Point 'before-backup' -Detail ''
        foreach ($entry in @($Prepared.Plan)) {
            $index++
            $destination = Resolve-ManifestDestination -Target $targetFull -Relative $entry.relative
            $existed = Test-Path -LiteralPath $destination -PathType Leaf
            if ($null -ne $entry.expectedHash) {
                if (-not $existed) {
                    throw "'$($entry.relative)' disappeared while the publication was being prepared; aborting before any change."
                }

                $liveHash = Get-FileSha256 $destination
                if (-not $liveHash.Equals([string]$entry.expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "'$($entry.relative)' changed while the publication was being prepared; aborting before any change."
                }
            }
            elseif ($existed) {
                throw "Refusing to overwrite '$($entry.relative)': it appeared after the publication was prepared."
            }

            $backupName = $null
            $sourceHash = $null
            if ($existed) {
                $backupName = ('{0:D4}.bak' -f $index)
                $backupPath = Join-Path $backupDir $backupName
                $sourceHash = Get-FileSha256 $destination
                if (-not $sourceHash.Equals([string]$entry.expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "'$($entry.relative)' changed while the publication was being prepared; aborting before any change."
                }

                [System.IO.File]::Copy($destination, $backupPath, $false)
                $backupHash = Get-FileSha256 $backupPath
                if (-not $sourceHash.Equals($backupHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "Could not preserve '$($entry.relative)' before replacing it."
                }

                # Flush the backup: the journal that references it is flushed too, so a power
                # loss must never leave a journal pointing at a partial backup.
                $stream = [System.IO.File]::Open(
                    $backupPath,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite,
                    [System.IO.FileShare]::None)
                try { $stream.Flush($true) } finally { $stream.Dispose() }
            }

            $null = $items.Add([pscustomobject]@{
                relative = $entry.relative
                action = $entry.action
                existed = $existed
                backup = $backupName
                hash = $sourceHash
                newHash = $null
            })
        }

        if ($manifestExisted) {
            Assert-NoReparsePath $manifestPath
            $manifestBackup = Join-Path $backupDir 'manifest.bak'
            [System.IO.File]::Copy($manifestPath, $manifestBackup, $false)
            $journal.manifestHash = Get-FileSha256 $manifestBackup
            $stream = [System.IO.File]::Open(
                $manifestBackup,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            try { $stream.Flush($true) } finally { $stream.Dispose() }
        }

        $journal.items = @($items)
        Write-UpdateJournal -TransactionDir $transactionDir -Journal $journal
    }
    catch {
        # Nothing was mutated; drop the partial backup generation so the next attempt starts clean.
        if (Test-Path -LiteralPath $backupDir) { Remove-TreeSafely -Path $backupDir }
        if (Test-Path -LiteralPath $journalPath) { Remove-Item -LiteralPath $journalPath -Force }
        throw
    }

    $transactionSucceeded = $false
    try {
        foreach ($entry in @($Prepared.Plan)) {
            if ($entry.action -eq 'copy') {
                Invoke-FaultPoint -Point 'copy' -Detail $entry.relative
                $destination = Resolve-ManifestDestination -Target $targetFull -Relative $entry.relative
                try {
                    if (-not (Get-FileSha256 $entry.source).Equals([string]$entry.stagingHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                        throw "The staged file for '$($entry.relative)' changed while the publication was running."
                    }

                    Copy-FileAtomic -Source $entry.source `
                        -Destination $destination `
                        -ExpectedHash $entry.expectedHash `
                        -EnforcePreimage
                }
                catch {
                    # The destination changed after its backup was captured. Rolling it back
                    # would destroy that newer edit, so record it as exempt from rollback.
                    $skip = @()
                    if ($journal.ContainsKey('skipRollback')) { $skip = @($journal['skipRollback']) }
                    if ($skip -notcontains $entry.relative) { $skip += $entry.relative }
                    $journal['skipRollback'] = @($skip)
                    Write-UpdateJournal -TransactionDir $transactionDir -Journal $journal
                    throw
                }

                # Record the verified staging hash as this transaction's output. Re-reading the
                # live destination here would adopt a concurrent write into the journal and let a
                # later rollback destroy it.
                foreach ($item in $items) {
                    if ($item.relative -eq $entry.relative -and $item.action -eq 'copy') {
                        $item.newHash = $entry.stagingHash
                    }
                }

                $journal['items'] = @($items)
                Write-UpdateJournal -TransactionDir $transactionDir -Journal $journal
            }
        }
        Invoke-FaultPoint -Point 'before-manifest' -Detail ''
        Write-PublishManifest -Target $targetFull -Files $Prepared.Manifest
        foreach ($entry in @($Prepared.Plan)) {
            if ($entry.action -ne 'delete') { continue }
            Invoke-FaultPoint -Point 'delete' -Detail $entry.relative
            $destination = Resolve-ManifestDestination -Target $targetFull -Relative $entry.relative
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { continue }
            Assert-NoReparsePath $destination
            if ($null -ne $entry.expectedHash) {
                $liveHash = Get-FileSha256 $destination
                if (-not $liveHash.Equals([string]$entry.expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $skip = @()
                    if ($journal.ContainsKey('skipRollback')) { $skip = @($journal['skipRollback']) }
                    if ($skip -notcontains $entry.relative) { $skip += $entry.relative }
                    $journal['skipRollback'] = @($skip)
                    Write-UpdateJournal -TransactionDir $transactionDir -Journal $journal
                    throw "'$($entry.relative)' changed during the publication; rolling back."
                }
            }

            Remove-Item -LiteralPath $destination -Force
        }
        $journal.phase = 'committed'
        Write-UpdateJournal -TransactionDir $transactionDir -Journal $journal
        $transactionSucceeded = $true
    }
    finally {
        if ($transactionSucceeded) {
            if (Test-Path -LiteralPath $backupDir) { Remove-TreeSafely -Path $backupDir }
            if (Test-Path -LiteralPath $journalPath) { Remove-Item -LiteralPath $journalPath -Force }
        }
        elseif (Test-Path -LiteralPath $backupDir) {
            Write-Warning 'Publication failed; rolling back to the previous generation.'
            Repair-InterruptedPublish -Target $targetFull
        }
        else {
            if (Test-Path -LiteralPath $journalPath) { Remove-Item -LiteralPath $journalPath -Force }
        }
    }
}

function Install-PortablePackage {
    param(
        [Parameter(Mandatory = $true)][string]$Staging,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$RepoRoot)

    $stagingFull = Get-NormalizedPath $Staging
    $targetFull = Assert-SafeTarget -Target $Target -RepoRoot $RepoRoot
    Assert-NoReparsePath $stagingFull
    if (Test-PathWithin -Path $targetFull -Root $stagingFull) {
        throw "Refusing a target inside the staging directory: $targetFull"
    }
    if (Test-PathWithin -Path $stagingFull -Root $targetFull) {
        throw "Refusing a staging directory inside the target: $stagingFull"
    }
    Assert-RuntimeNotRunning $targetFull

    if (-not (Test-Path -LiteralPath (Join-Path $stagingFull 'FastVRecorder.exe') -PathType Leaf)) {
        throw "Staging does not contain FastVRecorder.exe: $stagingFull"
    }

    New-Item -ItemType Directory -Path $targetFull -Force | Out-Null
    $transactionDir = Join-Path $targetFull $script:TransactionDirName
    New-Item -ItemType Directory -Path $transactionDir -Force | Out-Null
    Assert-NoReparsePath $transactionDir

    return Invoke-WithPublishLock -LockPath (Join-Path $transactionDir 'publish.lock') -Action {
        Repair-InterruptedPublish -Target $targetFull
        $oldManifest = Read-PublishManifest -Target $targetFull
        $prepared = Get-PublicationPlan -Staging $stagingFull -Target $targetFull -OldManifest $oldManifest
        Invoke-PublicationTransaction -Target $targetFull -Prepared $prepared

        $recordings = Join-Path $targetFull 'Recordings'
        New-Item -ItemType Directory -Path $recordings -Force | Out-Null
        return $targetFull
    }
}

Export-ModuleMember -Function @(
    'Get-NormalizedPath',
    'Get-RelativePath',
    'Test-PathWithin',
    'Test-ReparsePoint',
    'Assert-NoReparsePath',
    'Get-ManifestPathError',
    'Test-ManifestRelativePath',
    'Assert-ManifestRelativePath',
    'Test-Sha256Hex',
    'Resolve-ManifestDestination',
    'Assert-SafeTarget',
    'Resolve-PublishTarget',
    'Assert-RuntimeNotRunning',
    'Get-FileSha256',
    'Read-PublishManifest',
    'Write-PublishManifest',
    'Install-PortablePackage',
    'Repair-InterruptedPublish',
    'Set-PublishFaultHook',
    'Remove-TreeSafely',
    'New-OwnedStaging',
    'Close-OwnedStaging',
    'Remove-OwnedStaging',
    'Invoke-WithPublishLock'
)
