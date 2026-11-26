$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot 'PublishSafety.psm1'
Import-Module $module -Force

$script:passed = 0
$script:failed = 0

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

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ('fvr-publish-safety-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $sandbox 'repo'
$target = Join-Path $repo 'dist\FastVRecorder'
$staging = Join-Path $repo 'dist\.staging\publish-test'
$external = Join-Path $sandbox 'external-target'

try {
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
    Write-TextFile (Join-Path $target 'languages\de.json') '{"strings":{"Common.Open":"Benutzerdefiniert"}}'
    Write-TextFile (Join-Path $target 'FastVRecorder.exe') 'old-exe'
    Write-TextFile (Join-Path $target 'old.dll') 'old-obsolete'
    Write-TextFile (Join-Path $target 'modified.dll') 'old-modified'

    $oldManifest = [ordered]@{
        schema = 1
        files = [ordered]@{
            'FastVRecorder.exe' = (Hash-Of (Join-Path $target 'FastVRecorder.exe'))
            'old.dll' = (Hash-Of (Join-Path $target 'old.dll'))
            'modified.dll' = (Hash-Of (Join-Path $target 'modified.dll'))
        }
    }
    $oldManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'publish-manifest.json') -Encoding UTF8
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

    # second publish is repeatable and still preserves user data
    Install-PortablePackage -Staging $staging -Target $target -RepoRoot $repo | Out-Null
    foreach ($relative in $protected) {
        Assert-True ($before[$relative] -eq (Hash-Of (Join-Path $target $relative))) "second publish preserves: $relative"
    }

    # ---- safe target policy ----------------------------------------------------------
    Assert-Throws { Assert-SafeTarget -Target $repo -RepoRoot $repo } 'repository root target refused'
    Assert-Throws { Resolve-PublishTarget -Output '..\escape' -RepoRoot $repo } 'relative traversal outside repo refused'
    Assert-Throws { Assert-SafeTarget -Target (Join-Path $repo 'src\FastVRecorder.App') -RepoRoot $repo } 'source-tree target refused'

    $nonRuntime = Join-Path $sandbox 'not-a-runtime'
    New-Item -ItemType Directory -Path $nonRuntime -Force | Out-Null
    Write-TextFile (Join-Path $nonRuntime 'personal.txt') 'personal-data'
    Assert-Throws { Assert-SafeTarget -Target $nonRuntime -RepoRoot $repo } 'non-empty non-runtime target refused'

    $junctionTarget = Join-Path $sandbox 'junction-target'
    New-Item -ItemType Junction -Path $junctionTarget -Target $sentinel | Out-Null
    Assert-Throws { Assert-SafeTarget -Target $junctionTarget -RepoRoot $repo } 'reparse-point target refused'
    Assert-True ((Get-Content -LiteralPath (Join-Path $sentinel 'sentinel.txt') -Raw) -match 'do-not-touch') 'sentinel survives refused target'

    # ---- fresh, spaced and unicode targets -------------------------------------------
    $fresh = Join-Path $sandbox 'fresh target ünïcode'
    Install-PortablePackage -Staging $staging -Target $fresh -RepoRoot $repo | Out-Null
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh 'FastVRecorder.exe')) 'fresh target with spaces/unicode created'
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh 'Recordings')) 'fresh target gets Recordings directory'

    $absoluteOutside = Join-Path $external 'portable'
    Install-PortablePackage -Staging $staging -Target $absoluteOutside -RepoRoot $repo | Out-Null
    Assert-True (Test-Path -LiteralPath (Join-Path $absoluteOutside 'FastVRecorder.exe')) 'absolute external target installed'

    # ---- cleanup only removes owned staging ------------------------------------------
    $dist = Join-Path $repo 'dist'
    New-Item -ItemType Directory -Path (Join-Path $dist '.staging\publish-old') -Force | Out-Null
    Write-TextFile (Join-Path $dist '.staging\publish-old\junk.txt') 'junk'
    $runtimeKeep = Join-Path $dist 'FastVRecorder\Recordings\keep.wav'
    Write-TextFile $runtimeKeep 'runtime-user-recording'
    $removed = Remove-OwnedStaging -RepoRoot $repo
    Assert-True ($removed -ge 1) 'owned staging directory removed'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $dist '.staging\publish-old'))) 'staging child gone'
    Assert-True (Test-Path -LiteralPath $runtimeKeep) 'cleanup leaves runtime recordings untouched'
}
finally {
    try { Remove-Item -LiteralPath $sandbox -Recurse -Force } catch { }
}

Write-Host ""
Write-Host "Publish safety: $($script:passed) passed, $($script:failed) failed." -ForegroundColor Cyan
if ($script:failed -gt 0) { exit 1 }
exit 0
