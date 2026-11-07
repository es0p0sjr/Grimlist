# Run the optional real-source regressions from the promoted immutable fixtures.
[CmdletBinding()]
param([switch]$IntegrationOnly)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
$tool = 'tests/GrimList.Tests/bin/Debug/net10.0/GrimList.Tests.dll'
if (-not (Test-Path -LiteralPath $tool)) { throw 'Build GrimList.slnx in Debug first.' }
$fixtureRoot = 'tests/fixtures'
$catalogueRoot = "$fixtureRoot/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908"
if ($IntegrationOnly) {
    $testArguments = @(
        '--disposable-reference-only',
        '--disposable-reference-pack', 'artifacts/references-real/GrimList-References-11e.json',
        '--disposable-reference-dataset', "$fixtureRoot/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json"
    )
} else {
    $testArguments = @(
        '--attachment-diagnostic', "$catalogueRoot/death-guard",
        '--loadout-catalogue', "$fixtureRoot/libraries/android-equipment-catalogue.json",
        '--legacy-reference-library', "$fixtureRoot/libraries/reference-import-before-library.json",
        '--reference-import-archive', "$catalogueRoot/repository.zip",
        '--selection-import-archive', "$catalogueRoot/repository.zip",
        '--reference-catalogue', "$catalogueRoot/death-guard/raw-catalogue.json",
        '--attachment-source', "$fixtureRoot/libraries/sororitas-attachment-source.json",
        '--all-factions-attachment-source', "$fixtureRoot/libraries/all-factions-attachment-source.json",
        '--enhancement-defence-source', "$fixtureRoot/libraries/all-factions-attachment-source.json",
        '--reference-phone-library', "$fixtureRoot/libraries/GrimList-Library.json",
        '--stratagem-library', "$fixtureRoot/libraries/GrimList-Library.json",
        '--stratagem-pack', "$fixtureRoot/references/GrimList-DeathGuard-Stratagems.json"
    )
}
& dotnet $tool @testArguments
exit $LASTEXITCODE
