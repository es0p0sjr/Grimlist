$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\GrimList.Desktop\GrimList.Desktop.csproj'
$localDataPath = Join-Path $PSScriptRoot '.local'
dotnet run --configuration Release --project $projectPath -- --data-dir $localDataPath
