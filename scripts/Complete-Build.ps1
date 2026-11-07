[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('android', 'android-standalone-test', 'android-performance', 'windows', 'windows-installer')][string]$Kind,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Artifact
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part release version.' }
$relative = if ($Kind -eq 'windows-installer') { 'artifacts/installers/windows' } else { "artifacts/builds/$Kind" }
$root = [IO.Path]::GetFullPath((Join-Path $repo $relative))
$current = Join-Path $root $Version

function Assert-BuildPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Outside workspace: $full" }
    # Check every ancestor: a junction could redirect an otherwise safe-looking path.
    for ($part = $full; $part -ne $repo; $part = Split-Path -Parent $part) {
        if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing linked build path: $part"
        }
    }
    return $full
}

$artifactPath = Assert-BuildPath ((Resolve-Path -LiteralPath $Artifact).Path)
if (-not $artifactPath.StartsWith($current + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Verified artifact must be inside the selected release directory.'
}
$file = Get-Item -LiteralPath $artifactPath
$expectedExtension = if ($Kind -like 'android*') { '.apk' } elseif ($Kind -eq 'windows-installer') { '.msi' } else { '.exe' }
if ($file.PSIsContainer -or $file.Length -eq 0 -or $file.Extension -ne $expectedExtension) { throw 'Missing or invalid replacement artifact.' }
if ($Kind -eq 'windows' -and $file.Name -ne 'GrimList.exe') { throw 'Expected GrimList.exe.' }

# Only superseded numeric releases in this one build channel. Never .local/user data.
$targets = @(Get-ChildItem -LiteralPath $root -Directory -Force | Where-Object {
    $_.Name -match '^\d+\.\d+\.\d+$' -and [version]$_.Name -lt [version]$Version
})
foreach ($target in $targets) {
    $null = Assert-BuildPath $target.FullName
    if (Get-ChildItem -LiteralPath $target.FullName -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) {
        throw "Refusing release containing links: $($target.FullName)"
    }
}
foreach ($target in $targets) {
    if ($PSCmdlet.ShouldProcess($target.FullName, 'Delete superseded generated release')) {
        Remove-Item -LiteralPath $target.FullName -Recurse -Force
        Write-Output "Removed old build: $($target.Name) ($Kind)"
    }
}
Write-Output "Kept verified release: $current"
