[CmdletBinding()]
param(
    # Offline fallback: redistribute an installed Windows Desktop runtime privately.
    [switch]$UseInstalledRuntime,
    [string]$RuntimeVersion = '10.0.10',
    [string]$DotNetRoot = 'C:\Program Files\dotnet'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Test-BundledEnhancements.ps1')
$project = Join-Path $repo 'src/GrimList.Desktop/GrimList.Desktop.csproj'
$publishArgs = @('publish', $project, '-p:PublishProfile=Standalone', '-m:1', '-nr:false', '-p:UseSharedCompilation=false')
if ($UseInstalledRuntime) {
    foreach ($part in @('shared/Microsoft.NETCore.App', 'shared/Microsoft.WindowsDesktop.App', 'host/fxr')) {
        if (-not (Test-Path -LiteralPath (Join-Path $DotNetRoot "$part/$RuntimeVersion"))) { throw "Missing runtime: $part/$RuntimeVersion" }
    }
    $publishArgs += @('-p:SelfContained=false', '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=runtime', '-p:TargetLatestRuntimePatch=false', '-p:NuGetAudit=false')
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
[xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
$output = Join-Path $repo "artifacts/builds/windows/$($props.Project.PropertyGroup.Version)"
if ($UseInstalledRuntime) {
    foreach ($part in @('shared/Microsoft.NETCore.App', 'shared/Microsoft.WindowsDesktop.App', 'host/fxr')) {
        $destination = Join-Path $output "runtime/$part/$RuntimeVersion"
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Copy-Item -Path (Join-Path $DotNetRoot "$part/$RuntimeVersion/*") -Destination $destination -Recurse -Force
    }
    # License and third-party notices are part of the runtime distribution.
    foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        Copy-Item -LiteralPath (Join-Path $DotNetRoot $notice) -Destination (Join-Path $output "runtime/$notice")
    }
}
foreach ($required in @('GrimList.exe', 'GrimList.Application.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $required))) { throw "Missing Windows payload: $required" }
}
& (Join-Path $PSScriptRoot 'Complete-Build.ps1') -Kind windows -Version ([string]$props.Project.PropertyGroup.Version) -Artifact (Join-Path $output 'GrimList.exe')
Write-Output "Standalone output: $output"
