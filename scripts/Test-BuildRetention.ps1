$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$fixture = Join-Path $repo ('.local/build-retention-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Complete-Build.ps1') -Destination (Join-Path $fixture 'scripts/Complete-Build.ps1')
$cleanup = Join-Path $fixture 'scripts/Complete-Build.ps1'
function Put([string]$Relative) {
    $path = Join-Path $fixture $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    Set-Content -LiteralPath $path -Value 'retention fixture'
    return $path
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Output "PASS: $Message"
}
try {
    $old = Put 'artifacts/builds/android/0.2.9/old.apk'
    $current = Put 'artifacts/builds/android/0.2.19/current.apk'
    $newer = Put 'artifacts/builds/android/0.2.20/newer.apk'
    $unversioned = Put 'artifacts/builds/android/notes/keep.txt'
    $other = Put 'artifacts/builds/windows/0.2.1/GrimList.exe'
    $userData = Put '.local/grimlist.db'
    & $cleanup -Kind android -Version 0.2.19 -Artifact $current -WhatIf
    Check (Test-Path -LiteralPath $old) 'Preview leaves older builds untouched'
    foreach ($invalid in @((Join-Path $fixture 'missing.apk'), $other, $userData)) {
        $rejected = $false
        try { & $cleanup -Kind android -Version 0.2.19 -Artifact $invalid } catch { $rejected = $true }
        Check $rejected 'Invalid replacement rejected before deleting old builds'
        Check (Test-Path -LiteralPath $old) 'Previous release survives failed validation'
    }
    & $cleanup -Kind android -Version 0.2.19 -Artifact $current
    Check (-not (Test-Path -LiteralPath $old)) 'Older numeric release removed'
    foreach ($keep in @($current, $newer, $unversioned, $other, $userData)) {
        Check (Test-Path -LiteralPath $keep) 'Current/newer releases, other channels and user data preserved'
    }
    & $cleanup -Kind android -Version 0.2.19 -Artifact $current
    Check (Test-Path -LiteralPath $current) 'Repeated cleanup preserves current release'
    $oldPerformance = Put 'artifacts/builds/android-performance/0.2.18/old.apk'
    $performance = Put 'artifacts/builds/android-performance/0.2.19/performance.apk'
    & $cleanup -Kind android-performance -Version 0.2.19 -Artifact $performance
    Check (-not (Test-Path -LiteralPath $oldPerformance)) 'Older performance package removed'
    Check ((Test-Path -LiteralPath $performance) -and (Test-Path -LiteralPath $current)) 'Performance cleanup preserves production Android package'
} finally {
    $resolved = (Resolve-Path -LiteralPath $fixture).Path
    if (-not $resolved.StartsWith($repo + '\.local\build-retention-check-', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the generated fixture'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
