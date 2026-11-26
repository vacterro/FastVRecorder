$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot 'PublishSafety.psm1'
Import-Module $module -Force

$script:passed = 0
$script:failed = 0
$script:skipped = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        $script:passed++
        Write-Host "PASS: $Message" -ForegroundColor Green
    }
    else {
        $script:failed++
        Write-Host "FAIL: $Message" -ForegroundColor Red
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Message)
    try {
        & $Action | Out-Null
        $script:failed++
        Write-Host "FAIL (no throw): $Message" -ForegroundColor Red
    }
    catch {
        $script:passed++
        Write-Host "PASS (refused): $Message" -ForegroundColor Green
    }
}

function Skip-Test {
    param([string]$Message)
    $script:skipped++
    Write-Host "SKIP: $Message" -ForegroundColor Yellow
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $directory = Split-Path -Path $Path -Parent
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Set-Content -LiteralPath $Path -Value $Text -Encoding UTF8
}

function Hash-Of {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Write-Manifest {
    param([string]$Target, [System.Collections.IDictionary]$Files, [int]$Schema = 1)
    $payload = [ordered]@{ schema = $Schema; files = $Files }
    $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Target 'publish-manifest.json') -Encoding UTF8
}

function New-MiniFixture {
    param([string]$Root)
    $repo = Join-Path $Root 'repo'
    $target = Join-Path $repo 'dist\FastVRecorder'
    $staging = Join-Path $Root 'staging'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Write-TextFile (Join-Path $target 'FastVRecorder.exe') 'old-exe'
    Write-TextFile (Join-Path $staging 'FastVRecorder.exe') 'new-exe'
    return @{ Repo = $repo; Target = $target; Staging = $staging }
}

function Install-Fixture {
    param([hashtable]$Fixture)
    Install-PortablePackage -Staging $Fixture.Staging -Target $Fixture.Target -RepoRoot $Fixture.Repo | Out-Null
}

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ('fvr-publish-safety-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $sandbox 'repo'
$target = Join-Path $repo 'dist\FastVRecorder'
$staging = Join-Path $repo 'dist\.staging\publish-test'
$external = Join-Path $sandbox 'external-target'

try {
    # ---- manifest path contract (unit) ------------------------------------------------
    Assert-Throws { Assert-ManifestRelativePath '..\escape.txt' } 'parent traversal rejected'
    Assert-Throws { Assert-ManifestRelativePath 'sub/../../escape.txt' } 'mixed-separator traversal rejected'
    Assert-Throws { Assert-ManifestRelativePath 'C:\Windows\evil.dll' } 'rooted path rejected'
    Assert-Throws { Assert-ManifestRelativePath '\absolute.dll' } 'slash-rooted path rejected'
    Assert-Throws { Assert-ManifestRelativePath '\\server\share\evil.dll' } 'UNC path rejected'
    Assert-Throws { Assert-ManifestRelativePath 'stream.dll:payload' } 'alternate data stream rejected'
    Assert-Throws { Assert-ManifestRelativePath 'CON.txt' } 'reserved device name rejected'
    Assert-Throws { Assert-ManifestRelativePath 'LPT1\file.txt' } 'reserved device directory rejected'
    Assert-Throws { Assert-ManifestRelativePath 'dir\file.' } 'trailing-dot segment rejected'
    Assert-Throws { Assert-ManifestRelativePath 'dir\ file.txt' } 'leading-space segment rejected'
    Assert-Throws { Assert-ManifestRelativePath 'dir\bad?.txt' } 'reserved character rejected'
    Assert-Throws { Assert-ManifestRelativePath 'dir\\double.txt' } 'empty segment rejected'
    Assert-Throws { Assert-ManifestRelativePath '' } 'empty path rejected'
    Assert-True (Test-ManifestRelativePath 'docs\USER_GUIDE.md') 'normal relative path accepted'
    Assert-True (Test-ManifestRelativePath 'media/start.wav') 'forward separators accepted'
    Assert-True (Test-ManifestRelativePath 'themes\goldendefault.json') 'asset path accepted'
    Assert-Throws { Assert-ManifestRelativePath 'dir\short~1.txt' } 'short-name alias rejected'

    # ---- fixture: a runtime directory full of user data -------------------------------
    New-Item -ItemType Directory -Path (Join-Path $target 'config') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $target 'Recordings\sub') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $target 'themes') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $target 'media') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $target 'languages') -Force | Out-Null
    Write-TextFile (Join-Path $target 'config\settings.json') '{"schemaVersion":2,"user":"keep"}'
    Write-TextFile (Join-Path $target 'Recordings\keep.wav') 'RIFF-user-wav'
    Write-TextFile (Join-Path $target 'Recordings\keep.mp3') 'ID3-user-mp3'
    Write-TextFile (Join-Path $target 'Recordings\sub\deep.wav') 'RIFF-deep'
    Write-TextFile (Join-Path $target 'Recordings\active.wav.part') 'partial-bytes'
    Write-TextFile (Join-Path $target 'Recordings\active.wav.recovery.json') '{"schemaVersion":1}'
    Write-TextFile (Join-Path $target 'unknown-user.bin') 'unknown-user-file'
    Write-TextFile (Join-Path $target 'themes\custom-user.json') '{"slug":"custom-user"}'
    Write-TextFile (Join-Path $target 'media\custom-user.wav') 'RIFF-custom-cue'
    Write-TextFile (Join-Path $target 'media\old-default.wav') 'RIFF-default-v1'
    Write-TextFile (Join-Path $target 'languages\de.json') '{"strings":{"Common.Open":"Benutzerdefiniert"}}'
    Write-TextFile (Join-Path $target 'FastVRecorder.exe') 'old-exe'
    Write-TextFile (Join-Path $target 'old.dll') 'old-obsolete'
    Write-TextFile (Join-Path $target 'modified.dll') 'old-modified'

    Write-Manifest -Target $target -Files ([ordered]@{
            'FastVRecorder.exe' = (Hash-Of (Join-Path $target 'FastVRecorder.exe'))
            'old.dll' = (Hash-Of (Join-Path $target 'old.dll'))
            'modified.dll' = (Hash-Of (Join-Path $target 'modified.dll'))
        })
    Write-TextFile (Join-Path $target 'modified.dll') 'modified-after-manifest'

    $sentinel = Join-Path $sandbox 'sentinel'
    New-Item -ItemType Directory -Path $sentinel -Force | Out-Null
    Write-TextFile (Join-Path $sentinel 'sentinel.txt') 'do-not-touch'
    $junctionPath = Join-Path $target 'external-junction'
    New-Item -ItemType Junction -Path $junctionPath -Target $sentinel | Out-Null

    $protected = @(
        'config\settings.json',
        'Recordings\keep.wav',
        'Recordings\keep.mp3',
        'Recordings\sub\deep.wav',
        'Recordings\active.wav.part',
        'Recordings\active.wav.recovery.json',
        'unknown-user.bin',
        'themes\custom-user.json',
        'media\custom-user.wav',
        'languages\de.json',
        'modified.dll'
    )
    $before = @{}
    foreach ($relative in $protected) {
        $before[$relative] = Hash-Of (Join-Path $target $relative)
    }

    # ---- fixture: generated staging package ------------------------------------------
    New-Item -ItemType Directory -Path (Join-Path $staging 'media') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $staging 'themes') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $staging 'languages') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $staging 'docs') -Force | Out-Null
    Write-TextFile (Join-Path $staging 'FastVRecorder.exe') 'new-exe'
    Write-TextFile (Join-Path $staging 'FastVRecorder.Core.dll') 'new-core'
    Write-TextFile (Join-Path $staging 'README.md') 'new-readme'
    Write-TextFile (Join-Path $staging 'VERSION') '0.0.2'
    Write-TextFile (Join-Path $staging 'media\start.wav') 'RIFF-new-start'
    Write-TextFile (Join-Path $staging 'media\old-default.wav') 'RIFF-default-v1'
    Write-TextFile (Join-Path $staging 'themes\goldendefault.json') '{"slug":"goldendefault"}'
    Write-TextFile (Join-Path $staging 'themes\custom-user.json') '{"slug":"overwritten-by-default"}'
    Write-TextFile (Join-Path $staging 'languages\en.json') '{"strings":{"Common.Open":"Open"}}'
    Write-TextFile (Join-Path $staging 'docs\USER_GUIDE.md') 'new-guide'

    # ---- install and preserve --------------------------------------------------------
    Install-PortablePackage -Staging $staging -Target $target -RepoRoot $repo | Out-Null

    foreach ($relative in $protected) {
        $after = Hash-Of (Join-Path $target $relative)
        Assert-True ($before[$relative] -eq $after) "user data unchanged: $relative"
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $target 'FastVRecorder.exe') -Raw) -match 'new-exe') 'application binary replaced'
    Assert-True ((Get-Content -LiteralPath (Join-Path $target 'README.md') -Raw) -match 'new-readme') 'root documentation replaced'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $target 'old.dll'))) 'unmodified obsolete owned artifact removed'
    Assert-True (Test-Path -LiteralPath (Join-Path $target 'modified.dll')) 'modified obsolete artifact preserved'
    Assert-True ((Get-Content -LiteralPath (Join-Path $target 'themes\custom-user.json') -Raw) -match 'custom-user') 'user theme not overwritten'
    Assert-True ((Get-Content -LiteralPath (Join-Path $target 'languages\de.json') -Raw) -match 'Benutzerdefiniert') 'user language override not overwritten'
    Assert-True ((Get-Content -LiteralPath (Join-Path $sentinel 'sentinel.txt') -Raw) -match 'do-not-touch') 'junction target untouched'
    Assert-True (Test-Path -LiteralPath (Join-Path $target 'publish-manifest.json')) 'owned artifact manifest written'
    $writtenManifest = Get-Content -Raw -LiteralPath (Join-Path $target 'publish-manifest.json') | ConvertFrom-Json
    $manifestKeys = @($writtenManifest.files.PSObject.Properties.Name)
    Assert-True ($manifestKeys -contains 'media\old-default.wav') 'identical unowned asset adopted into manifest'
    Assert-True (Test-Path -LiteralPath (Join-Path $target '.fvr-update\publish.lock')) 'durable publish lock retained'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $target '.fvr-update\journal.json'))) 'journal removed after success'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $target '.fvr-update\backup'))) 'generation backup removed after success'

    # second publish is repeatable and still preserves user data
    Install-PortablePackage -Staging $staging -Target $target -RepoRoot $repo | Out-Null
    foreach ($relative in $protected) {
        Assert-True ($before[$relative] -eq (Hash-Of (Join-Path $target $relative))) "second publish preserves: $relative"
    }

    # a now-owned asset is updated by the next package; unowned modified assets still survive
    Write-TextFile (Join-Path $staging 'media\old-default.wav') 'RIFF-default-v2'
    Install-PortablePackage -Staging $staging -Target $target -RepoRoot $repo | Out-Null
    Assert-True ((Get-Content -LiteralPath (Join-Path $target 'media\old-default.wav') -Raw) -match 'RIFF-default-v2') 'adopted owned asset updated'
    Assert-True ((Get-Content -LiteralPath (Join-Path $target 'media\custom-user.wav') -Raw) -match 'RIFF-custom-cue') 'unowned user cue still preserved'

    # ---- safe target policy ----------------------------------------------------------
    Assert-Throws { Assert-SafeTarget -Target $repo -RepoRoot $repo } 'repository root target refused'
    Assert-Throws { Resolve-PublishTarget -Output '..\escape' -RepoRoot $repo } 'relative traversal outside repo refused'
    Assert-Throws { Assert-SafeTarget -Target (Join-Path $repo 'src\FastVRecorder.App') -RepoRoot $repo } 'source-tree target refused'
    Assert-Throws { Assert-SafeTarget -Target (Join-Path $repo 'dist\.staging\publish-inside') -RepoRoot $repo } 'target inside staging area refused'

    $nonRuntime = Join-Path $sandbox 'not-a-runtime'
    New-Item -ItemType Directory -Path $nonRuntime -Force | Out-Null
    Write-TextFile (Join-Path $nonRuntime 'personal.txt') 'personal-data'
    Assert-Throws { Assert-SafeTarget -Target $nonRuntime -RepoRoot $repo } 'non-empty non-runtime target refused'

    $junctionTarget = Join-Path $sandbox 'junction-target'
    New-Item -ItemType Junction -Path $junctionTarget -Target $sentinel | Out-Null
    Assert-Throws { Assert-SafeTarget -Target $junctionTarget -RepoRoot $repo } 'reparse-point target refused'
    Assert-True ((Get-Content -LiteralPath (Join-Path $sentinel 'sentinel.txt') -Raw) -match 'do-not-touch') 'sentinel survives refused target'

    # ---- source/target overlap --------------------------------------------------------
    $overlap = New-MiniFixture -Root (Join-Path $sandbox 'overlap-a')
    Assert-Throws { Install-PortablePackage -Staging $overlap.Staging -Target (Join-Path $overlap.Staging 'inner') -RepoRoot $overlap.Repo } 'target inside staging refused'
    $overlap2 = New-MiniFixture -Root (Join-Path $sandbox 'overlap-b')
    $insideStaging = Join-Path $overlap2.Target 'staging-inside'
    New-Item -ItemType Directory -Path $insideStaging -Force | Out-Null
    Write-TextFile (Join-Path $insideStaging 'FastVRecorder.exe') 'inside-exe'
    Assert-Throws { Install-PortablePackage -Staging $insideStaging -Target $overlap2.Target -RepoRoot $overlap2.Repo } 'staging inside target refused'

    # ---- fresh, spaced and unicode targets -------------------------------------------
    $fresh = Join-Path $sandbox 'fresh target ünïcode'
    Install-PortablePackage -Staging $staging -Target $fresh -RepoRoot $repo | Out-Null
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh 'FastVRecorder.exe')) 'fresh target with spaces/unicode created'
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh 'Recordings')) 'fresh target gets Recordings directory'

    $absoluteOutside = Join-Path $external 'portable'
    Install-PortablePackage -Staging $staging -Target $absoluteOutside -RepoRoot $repo | Out-Null
    Assert-True (Test-Path -LiteralPath (Join-Path $absoluteOutside 'FastVRecorder.exe')) 'absolute external target installed'

    # ---- an owned staging lease is metadata, not package content ----------------------
    $leaseStaging = New-OwnedStaging -RepoRoot $repo
    Write-TextFile (Join-Path $leaseStaging.Path 'FastVRecorder.exe') 'lease-exe'
    $leaseTarget = Join-Path $sandbox 'lease-target'
    Install-PortablePackage -Staging $leaseStaging.Path -Target $leaseTarget -RepoRoot $repo | Out-Null
    Close-OwnedStaging -Staging $leaseStaging
    Remove-OwnedStaging -RepoRoot $repo | Out-Null
    Assert-True (Test-Path -LiteralPath (Join-Path $leaseTarget 'FastVRecorder.exe')) 'owned staging content installed'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $leaseTarget '.fvr-staging.lock'))) 'staging lease is not published'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $leaseTarget '.fvr-staging.json'))) 'staging marker is not published'
    $leaseManifest = Get-Content -Raw -LiteralPath (Join-Path $leaseTarget 'publish-manifest.json') | ConvertFrom-Json
    $leaseKeys = @($leaseManifest.files.PSObject.Properties.Name)
    Assert-True ($leaseKeys -notcontains '.fvr-staging.lock') 'lease file is absent from the manifest'
    Assert-True ($leaseKeys -notcontains '.fvr-staging.json') 'marker file is absent from the manifest'

    # ---- damaged manifests fail closed ------------------------------------------------
    $corrupt = New-MiniFixture -Root (Join-Path $sandbox 'corrupt')
    Set-Content -LiteralPath (Join-Path $corrupt.Target 'publish-manifest.json') -Value '{ not valid json' -Encoding UTF8
    $corruptBefore = Hash-Of (Join-Path $corrupt.Target 'FastVRecorder.exe')
    Assert-Throws { Install-Fixture $corrupt } 'corrupted manifest refuses the publication'
    Assert-True ($corruptBefore -eq (Hash-Of (Join-Path $corrupt.Target 'FastVRecorder.exe'))) 'corrupted manifest leaves the runtime untouched'

    $traversal = New-MiniFixture -Root (Join-Path $sandbox 'traversal')
    $traversalSentinel = Join-Path $sandbox 'traversal-sentinel.txt'
    Write-TextFile $traversalSentinel 'outside-data'
    Write-Manifest -Target $traversal.Target -Files ([ordered]@{
            'FastVRecorder.exe' = (Hash-Of (Join-Path $traversal.Target 'FastVRecorder.exe'))
            '..\..\traversal-sentinel.txt' = (Hash-Of $traversalSentinel)
        })
    Assert-Throws { Install-Fixture $traversal } 'manifest parent traversal refuses before mutation'
    Assert-True ((Get-Content -LiteralPath $traversalSentinel -Raw) -match 'outside-data') 'traversal sentinel untouched'

    $caseAlias = New-MiniFixture -Root (Join-Path $sandbox 'case-alias')
    Write-Manifest -Target $caseAlias.Target -Files ([ordered]@{
            'App.dll' = ('A' * 64)
            'a.dll' = ('B' * 64)
        })
    Assert-Throws { Install-Fixture $caseAlias } 'case-insensitive manifest duplicate refused'

    $badHash = New-MiniFixture -Root (Join-Path $sandbox 'bad-hash')
    Write-Manifest -Target $badHash.Target -Files ([ordered]@{
            'FastVRecorder.exe' = 'XYZ'
        })
    Assert-Throws { Install-Fixture $badHash } 'non-SHA256 manifest digest refused'

    $rootedManifest = New-MiniFixture -Root (Join-Path $sandbox 'rooted')
    Write-Manifest -Target $rootedManifest.Target -Files ([ordered]@{
            'C:\Windows\System32\drivers\etc\hosts' = ('C' * 64)
        })
    Assert-Throws { Install-Fixture $rootedManifest } 'rooted manifest entry refused'

    # ---- unowned and user-modified collisions are refused ----------------------------
    $unowned = New-MiniFixture -Root (Join-Path $sandbox 'unowned')
    Write-TextFile (Join-Path $unowned.Target 'extra.bin') 'user-owned-extra'
    Write-TextFile (Join-Path $unowned.Staging 'extra.bin') 'package-extra'
    # the executable is identical so the first refusal is the unowned collision
    Copy-Item -LiteralPath (Join-Path $unowned.Staging 'FastVRecorder.exe') -Destination (Join-Path $unowned.Target 'FastVRecorder.exe') -Force
    Assert-Throws { Install-Fixture $unowned } 'unowned file collision refused while no manifest exists'
    Assert-True ((Get-Content -LiteralPath (Join-Path $unowned.Target 'extra.bin') -Raw) -match 'user-owned-extra') 'unowned file bytes preserved'

    $modifiedOwned = New-MiniFixture -Root (Join-Path $sandbox 'modified-owned')
    Write-TextFile (Join-Path $modifiedOwned.Target 'app.dll') 'original-owned'
    Write-TextFile (Join-Path $modifiedOwned.Staging 'app.dll') 'package-owned'
    Write-Manifest -Target $modifiedOwned.Target -Files ([ordered]@{
            'FastVRecorder.exe' = (Hash-Of (Join-Path $modifiedOwned.Target 'FastVRecorder.exe'))
            'app.dll' = (Hash-Of (Join-Path $modifiedOwned.Target 'app.dll'))
        })
    Write-TextFile (Join-Path $modifiedOwned.Target 'app.dll') 'user-edited-owned'
    Assert-Throws { Install-Fixture $modifiedOwned } 'user-modified owned collision refused'
    Assert-True ((Get-Content -LiteralPath (Join-Path $modifiedOwned.Target 'app.dll') -Raw) -match 'user-edited-owned') 'user-modified bytes preserved'

    # ---- reparse points cannot redirect writes or deletes ----------------------------
    $symlinkSupported = $true
    try {
        New-Item -ItemType SymbolicLink -Path (Join-Path $sandbox 'symlink-probe') -Target $sentinel -ErrorAction Stop | Out-Null
    }
    catch {
        $symlinkSupported = $false
    }

    $junctionDest = New-MiniFixture -Root (Join-Path $sandbox 'junction-dest')
    New-Item -ItemType Junction -Path (Join-Path $junctionDest.Target 'extern') -Target $sentinel | Out-Null
    Write-TextFile (Join-Path $junctionDest.Staging 'extern\sentinel.txt') 'package-overwrite'
    Write-Manifest -Target $junctionDest.Target -Files ([ordered]@{
            'FastVRecorder.exe' = (Hash-Of (Join-Path $junctionDest.Target 'FastVRecorder.exe'))
            'extern\sentinel.txt' = (Hash-Of (Join-Path $sentinel 'sentinel.txt'))
        })
    Assert-Throws { Install-Fixture $junctionDest } 'destination under a junction refused'
    Assert-True ((Get-Content -LiteralPath (Join-Path $sentinel 'sentinel.txt') -Raw) -match 'do-not-touch') 'junction destination never overwritten'

    if ($symlinkSupported) {
        $fileLink = New-MiniFixture -Root (Join-Path $sandbox 'file-link')
        $linkTarget = Join-Path $sandbox 'link-target.dll'
        Write-TextFile $linkTarget 'linked-content'
        $linkPath = Join-Path $fileLink.Target 'linked.dll'
        New-Item -ItemType SymbolicLink -Path $linkPath -Target $linkTarget | Out-Null
        Write-TextFile (Join-Path $fileLink.Staging 'linked.dll') 'package-content'
        Write-Manifest -Target $fileLink.Target -Files ([ordered]@{
                'FastVRecorder.exe' = (Hash-Of (Join-Path $fileLink.Target 'FastVRecorder.exe'))
                'linked.dll' = (Hash-Of $linkTarget)
            })
        Assert-Throws { Install-Fixture $fileLink } 'destination file symlink refused'
        Assert-True ((Get-Content -LiteralPath $linkTarget -Raw) -match 'linked-content') 'file symlink target untouched'

        $manifestLink = New-MiniFixture -Root (Join-Path $sandbox 'manifest-link')
        $manifestTarget = Join-Path $sandbox 'manifest-real.json'
        Write-TextFile $manifestTarget '{"schema":1,"files":{}}'
        New-Item -ItemType SymbolicLink -Path (Join-Path $manifestLink.Target 'publish-manifest.json') -Target $manifestTarget | Out-Null
        Assert-Throws { Install-Fixture $manifestLink } 'manifest symlink refused'
    }
    else {
        Skip-Test 'destination file symlink refused (symlink privilege unavailable)'
        Skip-Test 'manifest symlink refused (symlink privilege unavailable)'
    }

    # ---- concurrent publish lock ------------------------------------------------------
    $locked = New-MiniFixture -Root (Join-Path $sandbox 'locked')
    Write-Manifest -Target $locked.Target -Files ([ordered]@{
            'FastVRecorder.exe' = (Hash-Of (Join-Path $locked.Target 'FastVRecorder.exe'))
        })
    New-Item -ItemType Directory -Path (Join-Path $locked.Target '.fvr-update') -Force | Out-Null
    $lockStream = [System.IO.File]::Open(
        (Join-Path $locked.Target '.fvr-update\publish.lock'),
        [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        Assert-Throws { Install-Fixture $locked } 'concurrent publish refused by the target lock'
        Assert-True ((Get-Content -LiteralPath (Join-Path $locked.Target 'FastVRecorder.exe') -Raw) -match 'old-exe') 'locked target unchanged'
    }
    finally {
        $lockStream.Dispose()
    }
    Install-Fixture $locked
    Assert-True ((Get-Content -LiteralPath (Join-Path $locked.Target 'FastVRecorder.exe') -Raw) -match 'new-exe') 'publish proceeds after the lock is released'

    # ---- rollback after an interrupted publication ------------------------------------
    function New-TransactionFixture {
        param([string]$Root)
        $fixture = New-MiniFixture -Root $Root
        Write-TextFile (Join-Path $fixture.Target 'old.dll') 'old-obsolete'
        Write-TextFile (Join-Path $fixture.Staging 'new.dll') 'new-file'
        Write-Manifest -Target $fixture.Target -Files ([ordered]@{
                'FastVRecorder.exe' = (Hash-Of (Join-Path $fixture.Target 'FastVRecorder.exe'))
                'old.dll' = (Hash-Of (Join-Path $fixture.Target 'old.dll'))
            })
        return $fixture
    }

    $faultCopy = New-TransactionFixture -Root (Join-Path $sandbox 'fault-copy')
    $oldManifestText = Get-Content -LiteralPath (Join-Path $faultCopy.Target 'publish-manifest.json') -Raw
    Set-PublishFaultHook -Hook { param($point, $detail) if ($point -eq 'copy' -and $detail -eq 'FastVRecorder.exe') { throw 'injected copy fault' } }
    try {
        Assert-Throws { Install-Fixture $faultCopy } 'copy fault propagates'
    }
    finally {
        Set-PublishFaultHook -Hook $null
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $faultCopy.Target 'FastVRecorder.exe') -Raw) -match 'old-exe') 'failed first copy restored the executable'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $faultCopy.Target 'new.dll'))) 'failed first copy created no new artifact'
    Assert-True ((Get-Content -LiteralPath (Join-Path $faultCopy.Target 'publish-manifest.json') -Raw) -eq $oldManifestText) 'failed copy restored the manifest'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $faultCopy.Target '.fvr-update\journal.json'))) 'failed copy cleaned the journal'
    Install-Fixture $faultCopy
    Assert-True ((Get-Content -LiteralPath (Join-Path $faultCopy.Target 'FastVRecorder.exe') -Raw) -match 'new-exe') 'retry after rollback succeeds'

    $faultMid = New-TransactionFixture -Root (Join-Path $sandbox 'fault-mid')
    Set-PublishFaultHook -Hook { param($point, $detail) if ($point -eq 'copy' -and $detail -eq 'new.dll') { throw 'injected mid-copy fault' } }
    try {
        Assert-Throws { Install-Fixture $faultMid } 'mid-copy fault propagates'
    }
    finally {
        Set-PublishFaultHook -Hook $null
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $faultMid.Target 'FastVRecorder.exe') -Raw) -match 'old-exe') 'mid-copy fault restored the overwritten executable'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $faultMid.Target 'new.dll'))) 'mid-copy fault removed the partially published file'
    Assert-True (Test-Path -LiteralPath (Join-Path $faultMid.Target 'old.dll')) 'mid-copy fault kept the obsolete owned artifact'

    $faultManifest = New-TransactionFixture -Root (Join-Path $sandbox 'fault-manifest')
    Set-PublishFaultHook -Hook { param($point, $detail) if ($point -eq 'before-manifest') { throw 'injected manifest fault' } }
    try {
        Assert-Throws { Install-Fixture $faultManifest } 'manifest fault propagates'
    }
    finally {
        Set-PublishFaultHook -Hook $null
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $faultManifest.Target 'FastVRecorder.exe') -Raw) -match 'old-exe') 'manifest fault rolled the executable back'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $faultManifest.Target 'new.dll'))) 'manifest fault removed the new artifact'

    $faultDelete = New-TransactionFixture -Root (Join-Path $sandbox 'fault-delete')
    Set-PublishFaultHook -Hook { param($point, $detail) if ($point -eq 'delete' -and $detail -eq 'old.dll') { throw 'injected delete fault' } }
    try {
        Assert-Throws { Install-Fixture $faultDelete } 'delete fault propagates'
    }
    finally {
        Set-PublishFaultHook -Hook $null
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $faultDelete.Target 'old.dll')) 'delete fault kept the obsolete owned artifact'
    Assert-True ((Get-Content -LiteralPath (Join-Path $faultDelete.Target 'FastVRecorder.exe') -Raw) -match 'old-exe') 'delete fault rolled the executable back'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $faultDelete.Target 'new.dll'))) 'delete fault removed the new artifact'

    # ---- direct repair of a durable journal ------------------------------------------
    $repair = New-MiniFixture -Root (Join-Path $sandbox 'repair')
    Write-TextFile (Join-Path $repair.Target 'a.dll') 'old-a'
    Write-TextFile (Join-Path $repair.Target 'b.dll') 'newly-written-b'
    $oldManifest = @{ schema = 1; files = [ordered]@{ 'FastVRecorder.exe' = (Hash-Of (Join-Path $repair.Target 'FastVRecorder.exe')); 'a.dll' = (Hash-Of (Join-Path $repair.Target 'a.dll')) } }
    Write-TextFile (Join-Path $repair.Target 'publish-manifest.json') ($oldManifest | ConvertTo-Json -Depth 5)
    $transactionDir = Join-Path $repair.Target '.fvr-update'
    $backupDir = Join-Path $transactionDir 'backup'
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $backupDir '0001.bak') -Value 'old-a' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $backupDir 'manifest.bak') -Value ($oldManifest | ConvertTo-Json -Depth 5) -Encoding UTF8
    $journal = @{
        schema = 1
        phase = 'prepared'
        target = $repair.Target
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $true
        items = @(
            @{ relative = 'a.dll'; action = 'copy'; existed = $true; backup = '0001.bak'; newHash = (Hash-Of (Join-Path $repair.Target 'a.dll')) },
            @{ relative = 'b.dll'; action = 'copy'; existed = $false; backup = $null; newHash = (Hash-Of (Join-Path $repair.Target 'b.dll')) }
        )
    }
    Write-TextFile (Join-Path $transactionDir 'journal.json') ($journal | ConvertTo-Json -Depth 8)
    Repair-InterruptedPublish -Target $repair.Target
    Assert-True ((Get-Content -LiteralPath (Join-Path $repair.Target 'a.dll') -Raw) -match 'old-a') 'journal repair restored the previous bytes'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $repair.Target 'b.dll'))) 'journal repair removed the unowned new file'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $transactionDir 'journal.json'))) 'journal repair removed the journal'
    Assert-True (-not (Test-Path -LiteralPath $backupDir)) 'journal repair removed the backup generation'

    # ---- reserved package paths and orphan backup recovery ----------------------------
    $reservedStage = New-MiniFixture -Root (Join-Path $sandbox 'reserved-stage')
    Write-TextFile (Join-Path $reservedStage.Staging 'publish-manifest.json') '{"schema":1,"files":{}}'
    Assert-Throws { Install-Fixture $reservedStage } 'staging cannot carry the tool manifest'
    Assert-True ((Get-Content -LiteralPath (Join-Path $reservedStage.Target 'FastVRecorder.exe') -Raw) -match 'old-exe') 'reserved staging path left the target untouched'

    $reservedManifest = New-MiniFixture -Root (Join-Path $sandbox 'reserved-manifest')
    Write-Manifest -Target $reservedManifest.Target -Files ([ordered]@{
            'publish-manifest.json' = ('A' * 64)
        })
    Assert-Throws { Install-Fixture $reservedManifest } 'manifest cannot own the tool manifest'

    $orphan = New-MiniFixture -Root (Join-Path $sandbox 'orphan-backup')
    Write-Manifest -Target $orphan.Target -Files ([ordered]@{
            'FastVRecorder.exe' = (Hash-Of (Join-Path $orphan.Target 'FastVRecorder.exe'))
        })
    New-Item -ItemType Directory -Path (Join-Path $orphan.Target '.fvr-update\backup') -Force | Out-Null
    Write-TextFile (Join-Path $orphan.Target '.fvr-update\backup\0001.bak') 'orphan-backup'
    Install-Fixture $orphan
    Assert-True ((Get-Content -LiteralPath (Join-Path $orphan.Target 'FastVRecorder.exe') -Raw) -match 'new-exe') 'orphan backup does not block a publication'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $orphan.Target '.fvr-update\backup'))) 'orphan backup generation cleaned'

    # ---- crafted journals cannot escape or touch protected paths ----------------------
    $journalEscape = New-MiniFixture -Root (Join-Path $sandbox 'journal-escape')
    $escapeTx = Join-Path $journalEscape.Target '.fvr-update'
    New-Item -ItemType Directory -Path (Join-Path $escapeTx 'backup') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $escapeTx 'backup\0001.bak') -Value 'backup-bytes' -Encoding UTF8
    $outsideBytes = Join-Path $sandbox 'outside-restore.txt'
    Write-TextFile $outsideBytes 'OUTSIDE-CONTENT'
    Write-TextFile (Join-Path $escapeTx 'journal.json') (@{
        schema = 1
        phase = 'prepared'
        target = $journalEscape.Target
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $false
        items = @(
            @{ relative = 'FastVRecorder.exe'; action = 'copy'; existed = $true; backup = '..\..\..\outside-restore.txt' }
        )
    } | ConvertTo-Json -Depth 8)
    Assert-Throws { Repair-InterruptedPublish -Target $journalEscape.Target } 'journal backup traversal refused'
    Assert-True ((Get-Content -LiteralPath (Join-Path $journalEscape.Target 'FastVRecorder.exe') -Raw) -match 'old-exe') 'traversal repair left the target untouched'

    $journalProtected = New-MiniFixture -Root (Join-Path $sandbox 'journal-protected')
    $protectedTx = Join-Path $journalProtected.Target '.fvr-update'
    New-Item -ItemType Directory -Path (Join-Path $protectedTx 'backup') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $protectedTx 'backup\0001.bak') -Value '{}' -Encoding UTF8
    Write-TextFile (Join-Path $protectedTx 'journal.json') (@{
        schema = 1
        phase = 'prepared'
        target = $journalProtected.Target
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $false
        items = @(
            @{ relative = 'config\settings.json'; action = 'copy'; existed = $true; backup = '0001.bak' }
        )
    } | ConvertTo-Json -Depth 8)
    Assert-Throws { Repair-InterruptedPublish -Target $journalProtected.Target } 'journal rollback into a protected path refused'

    $journalHash = New-MiniFixture -Root (Join-Path $sandbox 'journal-hash')
    $hashTx = Join-Path $journalHash.Target '.fvr-update'
    New-Item -ItemType Directory -Path (Join-Path $hashTx 'backup') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $hashTx 'backup\0001.bak') -Value 'wrong-preimage' -Encoding UTF8
    Write-TextFile (Join-Path $hashTx 'journal.json') (@{
        schema = 1
        phase = 'prepared'
        target = $journalHash.Target
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $false
        items = @(
            @{ relative = 'FastVRecorder.exe'; action = 'copy'; existed = $true; backup = '0001.bak'; hash = ('A' * 64) }
        )
    } | ConvertTo-Json -Depth 8)
    Assert-Throws { Repair-InterruptedPublish -Target $journalHash.Target } 'journal backup hash mismatch refused'

    # ---- plan/apply TOCTOU ------------------------------------------------------------
    $toctou = New-TransactionFixture -Root (Join-Path $sandbox 'toctou')
    $script:toctouTarget = Join-Path $toctou.Target 'FastVRecorder.exe'
    Set-PublishFaultHook -Hook {
        param($point, $detail)
        if ($point -eq 'before-backup') {
            Add-Content -LiteralPath $script:toctouTarget -Value 'edited-during-publication'
        }
    }
    try {
        Assert-Throws { Install-Fixture $toctou } 'concurrent modification during a publication refused'
    }
    finally {
        Set-PublishFaultHook -Hook $null
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $toctou.Target 'FastVRecorder.exe') -Raw) -match 'edited-during-publication') 'concurrent edit survived the refused publication'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $toctou.Target '.fvr-update\journal.json'))) 'TOCTOU refusal left no journal'

    # ---- a file edited during the copy phase is never rolled back over ----------------
    $lateEdit = New-TransactionFixture -Root (Join-Path $sandbox 'late-edit')
    $script:lateEditTarget = Join-Path $lateEdit.Target 'FastVRecorder.exe'
    Set-PublishFaultHook -Hook {
        param($point, $detail)
        if ($point -eq 'copy' -and $detail -eq 'FastVRecorder.exe') {
            Add-Content -LiteralPath $script:lateEditTarget -Value 'late-edit'
        }
    }
    try {
        Assert-Throws { Install-Fixture $lateEdit } 'file changed after its backup refused'
    }
    finally {
        Set-PublishFaultHook -Hook $null
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $lateEdit.Target 'FastVRecorder.exe') -Raw) -match 'late-edit') 'post-backup edit survived rollback'
    Assert-True (Test-Path -LiteralPath (Join-Path $lateEdit.Target 'old.dll')) 'untouched obsolete artifact kept while the edit failed'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $lateEdit.Target 'new.dll'))) 'no new artifact after the refused copy'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $lateEdit.Target '.fvr-update\journal.json'))) 'late-edit refusal cleaned the journal'

    # ---- journal manifest-backup hash and reserved destinations ----------------------
    $manifestHashJournal = New-MiniFixture -Root (Join-Path $sandbox 'manifest-hash-journal')
    $mhTx = Join-Path $manifestHashJournal.Target '.fvr-update'
    New-Item -ItemType Directory -Path (Join-Path $mhTx 'backup') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $mhTx 'backup\0001.bak') -Value 'x' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $mhTx 'backup\manifest.bak') -Value '{"schema":1,"files":{}}' -Encoding UTF8
    Write-TextFile (Join-Path $mhTx 'journal.json') (@{
        schema = 1
        phase = 'prepared'
        target = $manifestHashJournal.Target
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $true
        manifestHash = ('A' * 64)
        items = @(
            @{ relative = 'FastVRecorder.exe'; action = 'copy'; existed = $false; backup = $null }
        )
    } | ConvertTo-Json -Depth 8)
    Assert-Throws { Repair-InterruptedPublish -Target $manifestHashJournal.Target } 'journal manifest-backup hash mismatch refused'

    $reservedJournal = New-MiniFixture -Root (Join-Path $sandbox 'reserved-journal')
    $rjTx = Join-Path $reservedJournal.Target '.fvr-update'
    New-Item -ItemType Directory -Path (Join-Path $rjTx 'backup') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $rjTx 'backup\0001.bak') -Value 'x' -Encoding UTF8
    Write-TextFile (Join-Path $rjTx 'journal.json') (@{
        schema = 1
        phase = 'prepared'
        target = $reservedJournal.Target
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $false
        items = @(
            @{ relative = '.fvr-update\journal.json'; action = 'copy'; existed = $false; backup = $null }
        )
    } | ConvertTo-Json -Depth 8)
    Assert-Throws { Repair-InterruptedPublish -Target $reservedJournal.Target } 'journal tool-reserved destination refused'

    # ---- rollback never overwrites bytes this publication did not write ---------------
    $unrecorded = New-MiniFixture -Root (Join-Path $sandbox 'unrecorded-edit')
    Write-TextFile (Join-Path $unrecorded.Target 'a.dll') 'old-a'
    $unrecordedPre = Hash-Of (Join-Path $unrecorded.Target 'a.dll')
    $unrecordedTx = Join-Path $unrecorded.Target '.fvr-update'
    New-Item -ItemType Directory -Path (Join-Path $unrecordedTx 'backup') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $unrecordedTx 'backup\0001.bak') -Value 'old-a' -Encoding UTF8
    Write-TextFile (Join-Path $unrecorded.Target 'a.dll') 'user-edit-after-backup'
    Write-TextFile (Join-Path $unrecordedTx 'journal.json') (@{
        schema = 1
        phase = 'prepared'
        target = $unrecorded.Target
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $false
        items = @(
            @{ relative = 'a.dll'; action = 'copy'; existed = $true; backup = '0001.bak'; hash = $unrecordedPre }
        )
    } | ConvertTo-Json -Depth 8)
    Repair-InterruptedPublish -Target $unrecorded.Target
    Assert-True ((Get-Content -LiteralPath (Join-Path $unrecorded.Target 'a.dll') -Raw) -match 'user-edit-after-backup') 'unrecorded copy output is preserved as a user edit'

    $appeared = New-MiniFixture -Root (Join-Path $sandbox 'appeared-user-file')
    $appearedTx = Join-Path $appeared.Target '.fvr-update'
    New-Item -ItemType Directory -Path (Join-Path $appearedTx 'backup') -Force | Out-Null
    Write-TextFile (Join-Path $appeared.Target 'user-new.dll') 'user-created-file'
    Write-TextFile (Join-Path $appearedTx 'journal.json') (@{
        schema = 1
        phase = 'prepared'
        target = $appeared.Target
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        manifestExisted = $false
        items = @(
            @{ relative = 'user-new.dll'; action = 'copy'; existed = $false; backup = $null }
        )
    } | ConvertTo-Json -Depth 8)
    Repair-InterruptedPublish -Target $appeared.Target
    Assert-True ((Get-Content -LiteralPath (Join-Path $appeared.Target 'user-new.dll') -Raw) -match 'user-created-file') 'a file the journal cannot prove it wrote is never deleted'

    # a failure on one entry must not roll back an edit made on a different entry
    $multi = New-TransactionFixture -Root (Join-Path $sandbox 'late-edit-other-entry')
    $script:multiTarget = Join-Path $multi.Target 'FastVRecorder.exe'
    Set-PublishFaultHook -Hook {
        param($point, $detail)
        if ($point -eq 'copy' -and $detail -eq 'new.dll') {
            Add-Content -LiteralPath $script:multiTarget -Value 'edit-other-entry'
            throw 'injected late failure'
        }
    }
    try {
        Assert-Throws { Install-Fixture $multi } 'failure on another entry refused the publication'
    }
    finally {
        Set-PublishFaultHook -Hook $null
    }
    Assert-True ((Get-Content -LiteralPath (Join-Path $multi.Target 'FastVRecorder.exe') -Raw) -match 'edit-other-entry') 'edit on a different entry survived rollback'
    Assert-True (Test-Path -LiteralPath (Join-Path $multi.Target 'old.dll')) 'obsolete artifact kept after the refused publication'

    # ---- staging ownership markers ----------------------------------------------------
    $runtimeKeep = Join-Path $target 'Recordings\keep.wav'
    $stagingRoot = Join-Path $repo 'dist\.staging'
    New-Item -ItemType Directory -Path (Join-Path $stagingRoot 'publish-unmarked') -Force | Out-Null
    Write-TextFile (Join-Path $stagingRoot 'publish-unmarked\junk.txt') 'junk'
    $foreign = Join-Path $stagingRoot 'publish-foreign'
    New-Item -ItemType Directory -Path $foreign -Force | Out-Null
    Write-TextFile (Join-Path $foreign '.fvr-staging.json') ('{"schema":1,"repoRoot":"C:\\elsewhere","nonce":"publish-foreign"}')
    $validStaging = New-OwnedStaging -RepoRoot $repo
    $stagingName = Split-Path -Path $validStaging.Path -Leaf
    Assert-True (Test-Path -LiteralPath (Join-Path $validStaging.Path '.fvr-staging.json')) 'owned staging carries its marker'
    Assert-Throws {
        $probe = [System.IO.File]::Open(
            (Join-Path $validStaging.Path '.fvr-staging.lock'),
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        $probe.Dispose()
    } 'owned staging lock is exclusive while active'
    $removed = Remove-OwnedStaging -RepoRoot $repo
    Assert-True ($removed -eq 0) 'active staging is not cleaned by another publish'
    Close-OwnedStaging -Staging $validStaging
    $removed = Remove-OwnedStaging -RepoRoot $repo
    Assert-True ($removed -ge 1) 'released owned staging is cleaned'
    Assert-True (-not (Test-Path -LiteralPath $validStaging.Path)) 'released staging directory removed'
    Assert-True (Test-Path -LiteralPath (Join-Path $stagingRoot 'publish-unmarked')) 'staging without a marker is never removed'
    Assert-True (Test-Path -LiteralPath $foreign) 'staging marker from another repository is never removed'
    Assert-True (Test-Path -LiteralPath $runtimeKeep) 'cleanup leaves runtime recordings untouched'
}
finally {
    try { Remove-Item -LiteralPath $sandbox -Recurse -Force } catch { }
}

Write-Host ""
Write-Host "Publish safety: $($script:passed) passed, $($script:failed) failed, $($script:skipped) skipped." -ForegroundColor Cyan
if ($script:failed -gt 0) { exit 1 }
exit 0
