# GrimList development and testing

Use this guide to build the checked-in Windows application, exercise its regression checks and inspect imported data. The shared projects target .NET 10; the desktop frontend uses WPF and its Windows SQLite adapter. The current regression harness also uses that adapter. The MAUI Android toolchain is installed and physical-device builds have passed; see [AGENTS.md](../AGENTS.md) and the current `docs/distribution` release notes. Older [Android verification](android-verification.md) notes describe historical limitations.

See [How GrimList works](architecture.md) for the code map and [Data and storage](data-and-storage.md) before using maintenance commands against a library.

## Build and launch

Run commands from the repository root in PowerShell. The .NET 10 SDK is required for building or `dotnet run`. No external NuGet packages are referenced by the desktop solution; the separate mobile solution requires MAUI packages and the Android workload.

```powershell
dotnet --version
dotnet build GrimList.slnx
.\Start-GrimList.ps1
```

`GrimList.slnx` contains the shared layers, Windows adapters, Desktop, Tests and the data utility. To build only the utility:

```powershell
dotnet build tools/GrimList.Data/GrimList.Data.csproj
```

The launcher runs Desktop in Release configuration with `.local` as its data directory. To work with an isolated development library:

```powershell
dotnet run --project src/GrimList.Desktop/GrimList.Desktop.csproj -- --data-dir .local/dev-library
```

Arguments after `--` go to GrimList. Relative data paths resolve from the working directory. Use a dedicated directory when loading synthetic fixtures or experimenting with imports.

`Directory.Build.props` enables nullable references, implicit usings and the latest compiler language version. It also ignores failed restore sources. There is no `global.json` pin in the current repository.

## Publish a local build

To create a framework-dependent output directory:

```powershell
dotnet publish src/GrimList.Desktop/GrimList.Desktop.csproj -c Release -o artifacts/documented-build
```

This is a local publish, not an installer or remote deployment. A framework-dependent executable requires the matching .NET Windows Desktop runtime on the destination machine. The source launcher requires the SDK. Launching the published executable directly defaults to `%LOCALAPPDATA%\GrimList`, so pass `--data-dir` if you want a specific library.

## Core regression checks

The tests are an executable harness, not an xUnit/NUnit/MSTest project. Run the program to execute its checks:

```powershell
dotnet run --project tests/GrimList.Tests/GrimList.Tests.csproj
```

The harness prints `PASS`/`FAIL`, writes fixtures to a fresh GUID directory under `.local/tests`, prints that artifact path, and returns a nonzero exit code when checks fail. Its normal run includes a real PDF extraction check, so configure `pdftotext` if it is not discoverable:

```powershell
$env:GRIMLIST_PDFTOTEXT = 'C:\tools\pdftotext.exe'
dotnet run --project tests/GrimList.Tests/GrimList.Tests.csproj
```

The executable must exist at the configured path. The importer also checks known Git for Windows/dnGrep locations and PATH. Text/JSON imports do not require this tool, but the full current harness treats a failed PDF extraction check as a failure.

| Area | Existing checks to consult |
| --- | --- |
| Persistence, source updates and base calculations | `Program.cs` |
| BSData parsing and multi-army inheritance | `BsDataChecks`, `BsDataProfileChecks`, `AllArmyChecks` |
| Size, contextual, model and equipment pricing | `ContextualPricingChecks`, `PricingPatternChecks`, `PricingRepairChecks`, `UpgradePricingChecks` |
| Equipment editing and constraints | `LoadoutChecks`, `LoadoutOptionChecks`, `SelectionLimitChecks` |
| Attachments and structural permissions | `LeaderChecks`, `MultipleLeaderChecks`, `AssociationChecks`, `AttachmentCombinationChecks`, `CrossFactionLeaderChecks` |
| Army legality and available units | `ArmyValidationChecks`, `MusterChecks`, `WarlordChecks`, `UnitAvailabilityChecks`, `AlliedChecks` |
| Detachments, enhancements and references | `DetachmentChecks`, `MultiDispositionChecks`, `EnhancementChecks`, `EnhancementDefenceChecks`, reference checks |
| Derived effects and exports | `AttachedEffectChecks`, `DatasheetReferenceChecks`, `ExportChecks` |

Some extra checks run only when given real catalogue/dataset fixture arguments. A successful default run does not imply those optional paths ran. The relevant check files parse their own flags; for example, `--loadout-catalogue <dataset.json>` supplies normalized imported data to several equipment/effect checks, while `--reference-catalogue` consumers use raw catalogue/reference fixtures. Inspect all consumers of a shared flag before assembling a command.

Tests commonly use synthetic datasets or fixture HTTP handlers. A passing local run does not establish that an external download/upload service is currently reachable.

## Desktop smoke checks

Desktop has its own [`SmokeChecks`](../src/GrimList.Desktop/SmokeChecks.cs) partial class suite. It exercises actual WPF controls, rendering and feature workflows. Use a dedicated data directory because the checks create and change test armies and library data.

```powershell
$smokeRoot = Join-Path $PWD ('.local\docs-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $smokeRoot | Out-Null
$smokeImage = Join-Path $smokeRoot 'desktop.png'
$testLog = dotnet run --project tests/GrimList.Tests/GrimList.Tests.csproj
if ($LASTEXITCODE -ne 0) { throw 'Regression fixture generation failed.' }
$fixtureRoot = ($testLog | Where-Object { $_ -like 'Artifacts: *' } | Select-Object -Last 1).Substring(11)
dotnet run --project src/GrimList.Desktop/GrimList.Desktop.csproj -- --data-dir (Join-Path $fixtureRoot 'ui') --smoke-test $smokeImage
Get-Content ($smokeImage + '.result.txt')
```

Startup runs the checks when the window reaches application idle, writes screenshots/result files and closes the window. The data directory must be the regression run's generated `ui` directory: several smoke checks require sibling fixture JSON files. Inspect the result file and captured images as well as process output. Some catalogue-specific desktop checks depend on locally available source fixtures, so inspect their conditions before claiming complete real-catalogue coverage.

`SmokeChecks.*.cs` groups checks for menus, exports, references, detachments, pricing, attachments, enhancements, allies and other UI features. Core checks cover rule behavior; desktop checks verify the controls invoke that behavior correctly.

## Additional scripts

| Script | Purpose and prerequisites |
| --- | --- |
| [`check_pdf_exports.py`](../scripts/check_pdf_exports.py) | Extracts and checks PDFs from the desktop export smoke fixtures. Requires Python and `pdftotext`; pass the directory containing those fixture PDFs. |
| [`check_yellowscribe_export.py`](../scripts/check_yellowscribe_export.py) | Checks a `.ros` export using supplied upstream parser files. Requires Python, Node and the upstream files described in its opening comment. It does not upload the roster. |
| [`trace_attachment_source.py`](../scripts/trace_attachment_source.py) | Diagnoses source-level attachment data; inspect its argument parsing before use. |
| [`build_core_stratagems.py`](../scripts/build_core_stratagems.py) | Builds bundled reference data; review its source inputs and output paths before regenerating resources. |

These scripts are separate tools and are not all invoked by the normal C# regression harness.

## Data maintenance commands

The command implementation is [`tools/GrimList.Data/Program.cs`](../tools/GrimList.Data/Program.cs). Invoke commands using:

```powershell
dotnet run --project tools/GrimList.Data/GrimList.Data.csproj -- <command> <arguments>
```

Angle-bracket arguments below are placeholders. Quote paths with spaces. The program's fallback usage line is shorter than its full implemented command set; consult the dispatch branches when extending commands.

| Command and arguments | Effect |
| --- | --- |
| `inspect-allies <dataset.json>` | Reads a dataset file and prints native/allied availability summaries. |
| `inspect-attachments <data-dir> <army-id> <bodyguard-entry-id> <comma-separated-character-entry-ids> <report-dir>` | Evaluates a proposed attachment group and writes diagnostic text/JSON. |
| `audit-pricing <data-dir> <cached-repository.zip> <report-dir> [--apply]` | Writes pricing recovery and default-loadout reports; applies repairs only with `--apply`. |
| `refresh-associations <data-dir> <cached-repository.zip> <report-dir> [--apply]` | Rebuilds matching-provenance association metadata; optional application. |
| `refresh-muster <data-dir> <cached-repository.zip> <report-dir> [--apply]` | Rebuilds matching-provenance detachment keyword metadata; optional application. |
| `import-bsdata-all <data-dir> <edition> [repository.zip]` | Imports all armies from a ZIP or download and immediately applies the merged library after writing backups. |
| `build-mfm-detachments <faction> <table.txt> <source.json> <output.json>` | Writes a reference package from a detachment table and source metadata. |
| `references <discover/acquire/import-snapshot/parse/build-pack/audit/update> [options]` | Generic source acquisition, immutable snapshots, matching and portable pack preparation. See [Reference acquisition](reference-acquisition.md). |
| `import-rules <data-dir> <rules.json>` | Backs up and immediately imports reference rules. |
| `migrate-detachment-dispositions <data-dir>` | Immediately migrates stored disposition metadata. |
| `repair-loadout-limits <data-dir> <catalogue.json>` | Immediately applies the targeted loadout-limit repair from catalogue data. |
| `refresh-catalogue-pricing <data-dir> <catalogue.json>` | Immediately refreshes supported catalogue pricing/equipment metadata. |
| `repair-missing-points <data-dir> <points.json>` | Immediately fills eligible missing prices from supplied data; rejects already-priced replacements. |

Commands using `cached-repository.zip` call `BsDataClient.ReadCached`: they also require the cache's adjacent `source.json` and a matching archive hash. An arbitrary ZIP alone is insufficient for those commands. The desktop local-source workflow can read a standalone repository ZIP.

Commands opening `ArmyStore` can create/migrate database schema even when their main action is inspection or preview. Work on a copied library for diagnostics where you need the original database untouched. Immediate-apply commands do not show the desktop preview dialog.

Metadata refreshes apply only to units whose stored profile provenance matches the cached source. This avoids treating a different revision as a metadata-only repair. Review the generated report before adding `--apply`.

## Maintaining behavior and documentation

Keep game calculations and catalogue interpretation in Core, with Desktop responsible for interaction and display. When changing a rule, inspect both its selection guard and whole-army validation path; an existing saved army may need to report a problem even when new additions are already blocked.

For imported data changes, preserve source metadata and stable identities, test a representative unsupported/invalid case, and verify persistence or merge behavior when the stored shape changes. Use matching existing checks instead of relying only on a screenshot.

Update the guide closest to the behavior: [Using GrimList](user-guide.md) for workflows, [How GrimList works](architecture.md) for implementation, and [Data and storage](data-and-storage.md) for import/persistence contracts. Keep sample prices explicitly historical or synthetic. Avoid presenting a cached catalogue audit as proof about a later source revision.
