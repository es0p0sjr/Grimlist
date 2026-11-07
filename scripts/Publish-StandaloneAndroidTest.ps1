[CmdletBinding()]
param(
    [string]$JavaSdkDirectory = 'C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot',
    [string]$AndroidSdkDirectory = 'C:\Users\es0p0\AppData\Local\Android\Sdk'
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Publish-StandaloneAndroid.ps1') -TestIdentity `
    -JavaSdkDirectory $JavaSdkDirectory -AndroidSdkDirectory $AndroidSdkDirectory
