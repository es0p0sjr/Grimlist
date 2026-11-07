[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Acquire', 'ImportSnapshots', 'Parse', 'Build', 'Audit')]
    [string]$Step = 'Acquire',
    [double]$Delay = 3,
    [double]$Timeout = 30,
    [int]$Retries = 2
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$dataTool = 'tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll'
$targetManifest = 'tools/reference_acquisition/initial-real-sources.json'
$sourceCache = 'tests/fixtures/wahapedia/snapshots'
$outputDirectory = 'artifacts/references-real'
$datasetPath = 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json'
$universePath = 'tests/fixtures/references/11e/detachment-universe.json'
foreach ($requiredPath in @($datasetPath, $universePath, $targetManifest)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Required input missing: $requiredPath" }
}
& python --version
if ($LASTEXITCODE -ne 0) { throw 'Python 3.10+ must be available as python.' }
& dotnet build tools/GrimList.Data/GrimList.Data.csproj --no-restore -m:1 -nr:false -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Data tool build failed; no acquisition was attempted.' }

$script:HadPartial = $false
function Invoke-References([string[]]$Arguments) {
    & dotnet $dataTool references @Arguments
    if ($LASTEXITCODE -eq 2) { $script:HadPartial = $true }
    elseif ($LASTEXITCODE -ne 0) { throw "Reference command failed with exit code $LASTEXITCODE." }
}

$targets = Get-Content -LiteralPath $targetManifest -Raw | ConvertFrom-Json
$referenceArgs = @('--dataset', $datasetPath, '--universe', $universePath,
    '--cache', $sourceCache, '--output', $outputDirectory, '--source-manifest', $targetManifest)
foreach ($target in $targets.Factions) { $referenceArgs += @('--faction', $target.Name) }
$baselinePack = 'artifacts/references-real/GrimList-References-11e.json'
if (Test-Path -LiteralPath $baselinePack) { $referenceArgs += @('--coverage-pack', $baselinePack) }
$planArgs = @('plan') + $referenceArgs
New-Item -ItemType Directory -Path '.local/reference-input' -Force | Out-Null

switch ($Step) {
    'Plan' { Invoke-References -Arguments $planArgs }
    'Acquire' {
        Invoke-References -Arguments $planArgs
        # update is the existing acquire -> parse -> match -> candidate -> C# preview command.
        # Explicit source manifest prevents unrelated index retrieval.
        Invoke-References -Arguments (@('update') + $referenceArgs + @('--missing', '--delay', "$Delay", '--timeout', "$Timeout", '--retries', "$Retries"))
    }
    'ImportSnapshots' {
        foreach ($target in $targets.Factions) {
            $snapshotFile = $target.ManualHtml
            $mediaType = 'text/html'
            if (-not (Test-Path -LiteralPath $snapshotFile)) {
                $snapshotFile = $target.ManualText
                $mediaType = 'text/plain'
            }
            if (-not (Test-Path -LiteralPath $snapshotFile)) {
                Write-Warning "Missing snapshot for $($target.Name): $($target.ManualHtml) or $($target.ManualText)"
                $script:HadPartial = $true
                continue
            }
            Invoke-References -Arguments @('import-snapshot', '--cache', $sourceCache,
                '--file', $snapshotFile, '--uri', $target.SourceUri, '--source-faction', $target.Name,
                '--media-type', $mediaType, '--edition', '11')
        }
        Invoke-References -Arguments $planArgs
    }
    'Parse' { Invoke-References -Arguments (@('parse') + $referenceArgs + @('--offline')) }
    'Build' { Invoke-References -Arguments (@('build-pack') + $referenceArgs + @('--offline')) }
    'Audit' { Invoke-References -Arguments (@('audit') + $referenceArgs + @('--offline')) }
}
Write-Host "Cache: $sourceCache"
Write-Host "Reports/candidate: $outputDirectory"
Write-Host 'Active GrimList library untouched. Full real-page acceptance is a separate verification step.'
if ($script:HadPartial) { exit 2 }
exit 0
