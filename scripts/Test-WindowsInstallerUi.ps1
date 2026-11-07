[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer, [Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GrimListMsiUi {
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
}
'@
$msi = (Resolve-Path -LiteralPath $Installer).Path
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$taskPath = $env:Path
[Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
[Environment]::SetEnvironmentVariable('Path', $taskPath, 'Process')
$process = Start-Process msiexec.exe -ArgumentList @('/i', ('"' + $msi + '"'), '/L*V', ('"' + $evidence + '\ui.log"')) -WindowStyle Hidden -PassThru
function Window {
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$process.Id)) |
        Where-Object { $_.Current.Name -like 'GrimList* Setup' } | Select-Object -Last 1
}
function Controls($window) {
    $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}
function Click($window, [string]$name) {
    $button = Controls $window | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if (-not $button) { throw "Installer control missing: $name" }
    # MSI's native controls expose HWNDs, but do not expose UIA InvokePattern.
    [GrimListMsiUi]::SendMessage([intptr]$window.Current.NativeWindowHandle, 0x111,
        [intptr][int]$button.Current.AutomationId, [intptr]$button.Current.NativeWindowHandle) | Out-Null
}
$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 300
    $window = Window
    $names = if ($window) { @(Controls $window | ForEach-Object { $_.Current.Name }) } else { @() }
} until (($names -contains 'Install') -or [DateTime]::UtcNow -gt $deadline)
if ($names -notcontains 'Install') { throw 'Fresh-install welcome screen did not appear; use an uninstalled product identity.' }
$names | Set-Content -Encoding UTF8 "$evidence/welcome.txt"
if (-not (($names -join "`n").Contains('Programs\GrimList'))) { throw 'Welcome screen has unexpected program path.' }
Write-Output 'PASS Native installer welcome screen and per-user destination'
Click $window 'Cancel'
Start-Sleep -Milliseconds 500
$window = Window
$names = @(Controls $window | ForEach-Object { $_.Current.Name })
if ($names -notcontains 'Setup cancelled') { throw 'Installer did not show cancellation screen.' }
$names | Set-Content -Encoding UTF8 "$evidence/cancelled.txt"
Click $window 'Finish'
if (-not $process.WaitForExit(10000)) { throw 'Installer did not exit after cancellation.' }
if ($process.ExitCode -ne 1602) { throw "Expected user-cancelled exit 1602, received $($process.ExitCode)." }
Write-Output 'PASS Cancel and Finish exit cleanly without installing'
@{ Installer=$msi; Welcome=$true; Cancel=$true; ExitCode=$process.ExitCode } | ConvertTo-Json | Set-Content "$evidence/ui-results.json"
