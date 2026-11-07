[CmdletBinding()]
param(
    [string]$JavaSdkDirectory = 'C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot',
    [string]$AndroidSdkDirectory = 'C:\Users\es0p0\AppData\Local\Android\Sdk',
    [switch]$TestIdentity,
    [switch]$PerformanceChecks
)
$ErrorActionPreference = 'Stop'
if ($TestIdentity -and $PerformanceChecks) { throw 'Choose one isolated test identity.' }
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Test-BundledEnhancements.ps1')
$project = Join-Path $repo 'src/GrimList.Mobile/GrimList.Mobile.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup[0].Version
$code = [string]$projectXml.Project.PropertyGroup[0].ApplicationVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid Android release version.' }
$kind = if ($PerformanceChecks) { 'android-performance' } elseif ($TestIdentity) { 'android-standalone-test' } else { 'android' }
$package = if ($PerformanceChecks) { 'com.grimlist.perftest' } elseif ($TestIdentity) { 'com.grimlist.standalonetest' } else { 'com.grimlist.mobile' }
$output = Join-Path $repo "artifacts/builds/$kind/$version"
$friendly = Join-Path $output $(if ($PerformanceChecks) { "GrimList-Performance-$version.apk" } elseif ($TestIdentity) { 'GrimList-Standalone-Test.apk' } else { "GrimList-Android-$version.apk" })
$buildTools = Get-ChildItem -LiteralPath (Join-Path $AndroidSdkDirectory 'build-tools') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'apksigner.bat') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $buildTools) { throw 'Android build tools are required for package verification.' }
$env:JAVA_HOME = $JavaSdkDirectory
$properties = @('-p:PublishProfile=Standalone', "-p:PublishDir=$output/",
    "-p:JavaSdkDirectory=$JavaSdkDirectory", "-p:AndroidSdkDirectory=$AndroidSdkDirectory",
    '-p:NuGetAudit=false', '-p:UseSharedCompilation=false', '-m:1', '-nr:false', '-v:minimal')
if ($TestIdentity) {
    $properties += @('-p:ApplicationId=com.grimlist.standalonetest', '-p:ApplicationTitle=GrimList Standalone Test',
        '-p:IntermediateOutputPath=obj/StandalonePhone/', '-p:OutputPath=bin/StandalonePhone/')
}
if ($PerformanceChecks) { $properties += '-p:IncludePerformanceChecks=true' }
# Reuse the real project tree; never copy source/bin/obj into per-task folders.
# Cleaning native intermediates prevents stale MAUI startup bindings.
& dotnet clean $project '-c:Release' @properties
if ($LASTEXITCODE -ne 0) { throw 'Android clean failed; previous release retained.' }
& dotnet publish $project @properties
if ($LASTEXITCODE -ne 0) { throw 'Android publish failed; previous release retained.' }
$signed = Join-Path $output "$package-Signed.apk"
& (Join-Path $buildTools.FullName 'apksigner.bat') verify $signed
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
$badging = & (Join-Path $buildTools.FullName 'aapt.exe') dump badging $signed
if ($LASTEXITCODE -ne 0 -or -not ($badging -match "^package: name='$([regex]::Escape($package))' versionCode='$code' versionName='$version'")) {
    throw 'APK identity/version verification failed.'
}
if ($TestIdentity -or $PerformanceChecks) {
    $label = if ($PerformanceChecks) { 'GrimList Performance Test' } else { 'GrimList Standalone Test' }
    if (-not ($badging -contains "application-label:'$label'")) { throw 'Unexpected test application label.' }
    $manifest = & (Join-Path $buildTools.FullName 'aapt.exe') dump xmltree $signed AndroidManifest.xml
    if ($LASTEXITCODE -ne 0 -or ($manifest -match 'com\.grimlist\.mobile')) { throw 'Test APK references the production identity.' }
}
& python (Join-Path $PSScriptRoot 'check_android_startup.py') $signed
if ($LASTEXITCODE -ne 0) { throw 'APK native startup verification failed.' }
Copy-Item -LiteralPath $signed -Destination $friendly -Force
$hash = (Get-FileHash -LiteralPath $friendly -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$friendly.sha256" -Value "$hash  $([IO.Path]::GetFileName($friendly))" -Encoding Ascii
& (Join-Path $PSScriptRoot 'Complete-Build.ps1') -Kind $kind -Version $version -Artifact $friendly
# Keep one distributable APK, not signed/unsigned copies beside it.
foreach ($name in @("$package.apk", "$package-Signed.apk", '_Microsoft.Android.Resource.Designer.dll')) {
    $duplicate = Join-Path $output $name
    if (Test-Path -LiteralPath $duplicate) { Remove-Item -LiteralPath $duplicate -Force }
}
Write-Output "Verified APK: $friendly"
