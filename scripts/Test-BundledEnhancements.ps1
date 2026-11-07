[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& dotnet build (Join-Path $repo 'tools/GrimList.Bootstrap/GrimList.Bootstrap.csproj') '-m:1' '-nr:false' '-p:UseSharedCompilation=false' '-v:quiet'
if ($LASTEXITCODE -ne 0) { throw 'Could not build the bundled rules validator.' }
& dotnet (Join-Path $repo 'tools/GrimList.Bootstrap/bin/Debug/net10.0/GrimList.Bootstrap.dll') --validate-bundled
if ($LASTEXITCODE -ne 0) { throw 'Bundled enhancement/army-rule/equipment-price candidate validation failed; previous release retained.' }
