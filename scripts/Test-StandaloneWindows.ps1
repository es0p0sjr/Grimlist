[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishedDirectory, [Parameter(Mandatory)][string]$EvidenceDirectory, [switch]$UsePublishedDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$acceptRoot = Join-Path $env:TEMP ('GrimList-Standalone-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $acceptRoot | Out-Null
if ($UsePublishedDirectory) {
    $appRoot = $published
} else {
    $appRoot = Join-Path $acceptRoot 'app'
    New-Item -ItemType Directory -Path $appRoot | Out-Null
    Copy-Item -Path "$published/*" -Destination $appRoot -Recurse
}
$taskPath = $env:Path
[Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
[Environment]::SetEnvironmentVariable('Path', $taskPath, 'Process')
$env:DOTNET_HOST_TRACE = '1'
$env:DOTNET_HOST_TRACEFILE = "$acceptRoot/host-trace.txt"
$checks = [System.Collections.Generic.List[string]]::new()
$script:appProcess = $null
$script:window = $null
function Check([string]$name, [bool]$success = $true) {
    if (-not $success) { throw "FAIL $name" }
    $checks.Add($name)
    Write-Output "PASS $name"
    @{ Root=$acceptRoot; Checks=$checks.ToArray(); ProcessId=$script:appProcess.Id } | ConvertTo-Json -Depth 5 | Set-Content "$evidence/results.json"
}
function Launch {
    $script:appProcess = Start-Process -FilePath "$appRoot/GrimList.exe" -ArgumentList @('--data-dir', ('"' + $acceptRoot + '\data"')) -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
}
function Controls {
    $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$script:appProcess.Id)) |
        Where-Object { $_.Current.Name -eq 'GrimList' } | Select-Object -Last 1
    if ($script:window) { $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
}
function WaitControl([string]$id) {
    $until = [DateTime]::UtcNow.AddMinutes(3)
    do {
        $found = Controls | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1
        if ($found) { return $found }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $until)
    throw "Control did not appear: $id"
}
function InvokeId([string]$id) {
    $control = WaitControl $id
    ([System.Windows.Automation.InvokePattern]$control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 500
}
function SelectItem([string]$pattern) {
    $item = Controls | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $_.Current.Name -like $pattern } | Select-Object -First 1
    ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
}
function Dump([string]$name) {
    Controls | ForEach-Object { '{0} | {1} | {2}' -f $_.Current.ControlType.ProgrammaticName, $_.Current.AutomationId, $_.Current.Name } | Set-Content -Encoding UTF8 "$evidence/$name.txt"
}
function CloseApp {
    if ($script:window) { ([System.Windows.Automation.WindowPattern]$script:window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close() }
    if (-not $script:appProcess.WaitForExit(10000)) { throw 'Test application did not close.' }
}

Launch
$null = WaitControl 'NewArmyTile'
Check 'Standalone app launches with fresh user data and unrelated working directory'
Check 'Database initialized locally' (Test-Path -LiteralPath "$acceptRoot/data/grimlist.db")
Dump 'first-launch'
InvokeId 'NewArmyTile'
$null = WaitControl 'FactionList'
SelectItem '*Name = Death Guard,*'
Check 'Factions available'
InvokeId 'NextButton'
$null = WaitControl 'OptionsList'
SelectItem '*Name = Flyblown Host,*'
Dump 'detachment-references'
Check 'Detachments and reference previews available' ((Get-Content "$evidence/detachment-references.txt" -Raw).Contains('Flyblown Host'))
InvokeId 'ToggleButton'
InvokeId 'NextButton'
$nameBox = WaitControl 'NameInput'
([System.Windows.Automation.ValuePattern]$nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('Offline Windows acceptance')
InvokeId 'NextButton'
$null = WaitControl 'UnitPicker'
$unit = Controls | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $_.Current.Name -like 'LibraryUnit {*Name = Mortarion,*' } | Select-Object -First 1
Check 'Units and points available' ($unit.Current.Name.Contains('375 pts'))
$add = $unit.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))
([System.Windows.Automation.InvokePattern]$add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Start-Sleep -Seconds 1
Check 'Created army has 375 points' ((WaitControl 'TotalText').Current.Name -eq '375/2000')
Check 'Normal army validation succeeds' ((WaitControl 'ValidationText').Current.Name -eq 'Army validated.')
Check 'Army saved locally' ((WaitControl 'StatusText').Current.Name -eq 'Saved locally')
Dump 'saved-roster'
InvokeId 'RulesButton'
Dump 'references'
Check 'Stratagem and army reference cards available' ((Get-Content "$evidence/references.txt" -Raw).Contains('Command Re-roll'))
CloseApp
Launch
$null = WaitControl 'NewArmyTile'
Dump 'reopened-home'
Check 'Saved army survives close and relaunch' ((Get-Content "$evidence/reopened-home.txt" -Raw).Contains('Offline Windows acceptance'))
$open = Controls | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.Name -like '*Offline Windows acceptance*' } | Select-Object -First 1
if (-not $open) {
    $text = Controls | Where-Object { $_.Current.Name -eq 'Offline Windows acceptance' } | Select-Object -First 1
    $open = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($text)
    while ($open -and $open.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) { $open = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($open) }
}
([System.Windows.Automation.InvokePattern]$open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
$null = WaitControl 'TotalText'
Check 'Reopened army retains units and points' ((WaitControl 'TotalText').Current.Name -eq '375/2000')
Check 'Reopened army still validates' ((WaitControl 'ValidationText').Current.Name -eq 'Army validated.')
Dump 'reopened-roster'
CloseApp
Copy-Item -LiteralPath "$acceptRoot/host-trace.txt" -Destination "$evidence/host-trace.txt"
Check 'Host loaded the bundled runtime' ((Get-Content "$evidence/host-trace.txt" -Raw).Contains("$appRoot\runtime\shared\Microsoft.NETCore.App"))
$resolvedRoot = (Resolve-Path -LiteralPath $acceptRoot).Path
$tempParent = (Resolve-Path -LiteralPath $env:TEMP).Path
if ((Split-Path -Parent $resolvedRoot) -ne $tempParent -or (Split-Path -Leaf $resolvedRoot) -notlike 'GrimList-Standalone-*') { throw 'Unexpected test cleanup path.' }
if ((Get-Item -LiteralPath $resolvedRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing linked test root.' }
if (Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1) { throw 'Refusing test output containing links.' }
Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
Write-Output "Windows standalone acceptance complete. Temporary test data removed; evidence: $evidence"
