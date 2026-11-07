[CmdletBinding()]
param(
    [string]$LogPath = 'artifacts/distribution-evidence/regression.log',
    [string[]]$TestArguments = @('--lf-json'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$log = [IO.Path]::GetFullPath((Join-Path $repo $LogPath))
if (-not $log.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Test log must stay in the workspace.' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null
if (-not $SkipBuild) {
    & dotnet build (Join-Path $repo 'tests/GrimList.Tests/GrimList.Tests.csproj') '-m:1' '-nr:false' '-p:UseSharedCompilation=false' '-v:minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Regression harness build failed.' }
}
# Windows PowerShell treats native stderr as ErrorRecords; capture it without
# mistaking an informational native message for a failed process.
try {
    $ErrorActionPreference = 'Continue'
    & dotnet (Join-Path $repo 'tests/GrimList.Tests/bin/Debug/net10.0/GrimList.Tests.dll') @TestArguments 2>&1 |
        Tee-Object -FilePath $log
    $result = $LASTEXITCODE
} finally { $ErrorActionPreference = 'Stop' }
if ($result -ne 0) { throw "Regression failed ($result); see $log. Failure artifacts retained for diagnosis." }
$line = Get-Content -LiteralPath $log | Where-Object { $_ -like 'Artifacts: *' } | Select-Object -Last 1
if ($line) {
    $run = (Resolve-Path -LiteralPath $line.Substring(11)).Path
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repo '.local/tests'))
    if ((Split-Path -Parent $run) -ne $expectedParent -or (Split-Path -Leaf $run) -notmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') {
        throw "Refusing unexpected test output: $run"
    }
    for ($part = $run; $part -ne $repo; $part = Split-Path -Parent $part) {
        if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing linked test directory.' }
    }
    if (Get-ChildItem -LiteralPath $run -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Refusing test output containing links.' }
    Remove-Item -LiteralPath $run -Recurse -Force
    Write-Output "Removed this run's synthetic test data; results retained in $log"
}
