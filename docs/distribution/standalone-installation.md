# Standalone installation — Milestone A

Application version: **0.2.0**. Bundled data release: **grimlist-11e-2026.10.06.1**.
This milestone adds offline installation and first-run initialization. It does not add a hosted update feed, automatic updating, accounts, cloud storage, telemetry, signing service or CI publishing. Existing user-requested data imports remain available.

## Production storage

| Purpose | Windows | Android |
| --- | --- | --- |
| Library and saved armies | `%LOCALAPPDATA%\GrimList\grimlist.db` | `FileSystem.Current.AppDataDirectory/grimlist.db` |
| Settings and editor context | `settings` table in the same database | Same |
| Repair backups | `GrimList\backups\` | App-private `backups/` |
| Downloaded source cache | `GrimList\sources\` | App-private `sources/` |
| Future downloaded releases | `GrimList\releases\` (reserved; no downloader) | App-private `releases/` (reserved) |
| Temporary PDF import | `GrimList\temp\` | MAUI `FileSystem.CacheDirectory` |
| Shared exports | User-selected destination | MAUI cache `exports/`, then Android document/share APIs |

`WindowsPaths` and `MobilePaths` implement `IPlatformPaths`. Shared code receives platform paths or the repository's resolved `DirectoryPath`; it contains no Windows/Android user-directory literals. Settings are transactional SQLite values, not a developer JSON file. Directly launching the published EXE uses the Windows default above. The explicit `--data-dir <directory>` development/test override remains supported, including intentionally relative paths. Existing `Start-GrimList` development launchers are not shipped.

## Runtime path audit

The review covered every production C# file and project file under `src`, including startup, SQLite, embedded resources, imports/repairs, exports and platform adapters. Generated `bin/obj` files were excluded from source inspection and assessed through the packaged file inventories. Searches included `.local`, `artifacts`, `tools`, `scripts`, snapshots, JSON, databases, `Environment`, `AppContext`, `Path`, `File`, `Directory` and `Process` calls.

| Occurrence / family | Classification | Disposition |
| --- | --- | --- |
| `Start-GrimList.cmd/.ps1`, build scripts, data/performance tools and their `.local`, `artifacts`, repository paths | Development-only | Retained; excluded from both builds. |
| `tests/**` fixture/database paths and test runner `AppContext.BaseDirectory` traversal | Test-only | Retained; not referenced or shipped by either application. |
| Desktop `SmokeChecks*.cs`, smoke fixture paths and `--smoke-test` | Test-only | Removed from Release compilation by default. Debug retains them; explicit `-p:IncludeSmokeChecks=true` permits a development Release. |
| Desktop `App` default folder and `--data-dir` | Development-only override; production default was already valid | Default now comes solely from `WindowsPaths`. No implicit repository or CWD search. |
| `WindowsPdfTextExtractor` empty/relative PATH entries resolving into the CWD | Production defect | Fixed: automatic PATH discovery accepts only fully qualified directories. Explicit `GRIMLIST_PDFTOTEXT` remains an optional development/user override. |
| `WindowsPdfTextExtractor` Git/dnGrep locations and `GRIMLIST_PDFTOTEXT` | Development-only integration conveniences | Optional extractor discovery only. No dependency for startup, bootstrap, editing, validation, references or PDF **export**. PDF **import** still explains its optional pdftotext requirement and offers text/JSON import when unavailable. No executable is copied from those locations. |
| `ArmyStore` database, settings and internal `dataset_snapshots` table | Production use; no repository-path defect | Only the supplied platform data directory. Internal SQLite snapshots are application library history, not raw repository/source snapshots shipped in the build. |
| `BsDataClient`, `OfficialMfmClient`, `LibraryWorkflow`, `EnhancementLibraryRepair`, `PricingRepairService` cache/source JSON, ZIP and backup paths | Production use; no repository-path defect | Resolved under the active user library. Optional original-source caches are not required on first launch. Bundled enhancement compatibility metadata remains embedded. |
| Desktop/mobile file pickers, reference adapters, BSData archive-entry names, user-selected exports | Production use; no repository-path defect | Explicit user documents or logical archive names. No implicit developer files. |
| `RosterPdf` platform Fonts folder | Production use; no repository-path defect | Windows-provided fonts; no repository asset at runtime. Android uses native PDF APIs. |
| Core stratagem JSON, enhancement recovery gzip, application icon/splash | Production use; no repository-path defect | Assembly/package resources. Source paths are build-time only. |
| Application startup with no database | Production defect | Previously created an empty library. Both frontends now call the same `LibraryBootstrap.Open`. |
| Bootstrap input in `tools/GrimList.Bootstrap` | Development-only generation input | An explicit validated JSON export is read at generation time. Runtime reads only embedded canonical release bytes. |

There are no remaining required repository-relative, `.local`, developer database, raw-source archive or CWD dependencies in the clean-install workflows. The classifications above distinguish supported production use of user files from defects; ordinary `.json` and `.db` use is not itself a development dependency.

## Canonical bootstrap release

The canonical files are:

* `src/GrimList.Application/Resources/bootstrap/manifest.json`
* `src/GrimList.Application/Resources/bootstrap/library.json.gz`

Both are embedded into **the same `GrimList.Application` assembly** consumed by Windows and Android. Neither frontend has its own data copy to maintain.

The manifest records release ID, release format version **1**, dataset schema version **1**, database schema version **1**, uncompressed size, SHA-256, content counts and the input export's SHA-256. The payload is a normalized, compressed production `Dataset`, not SQLite or a raw source snapshot. It has **2,194 units, 36 factions, 823 detachment definitions and 3,880 reference cards**. The uncompressed content is 31,928,367 bytes; gzip is 3,440,437 bytes. Content SHA-256:

`AF63CFBD46529E2BCF871128E766B311F818D97283D653A082561E6A7CB14778`

Generation input: the validated `tests/fixtures/libraries/GrimList-Library.json` export, SHA-256:

`FAFC749F56F19DBF09E3117772C39384B3674DF1EC0CDE3A932A1DA414AD6439`

This preserves its pinned BSData revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908` and supplemental reference provenance. It is not a claim to newly verified current published game rules. `.local/grimlist.db` is neither opened by generation nor copied into any package. Typed dataset projection excludes armies, roster entries, settings and editor context; personal `ImportedAt` metadata is cleared recursively. Existing bundled enhancement recovery metadata is applied during generation so equipment/enhancement semantics need no developer source cache.

Reproduce from the repository root:

```powershell
dotnet build tools/GrimList.Bootstrap -m:1 -nr:false -p:UseSharedCompilation=false
dotnet tools/GrimList.Bootstrap/bin/Debug/net10.0/GrimList.Bootstrap.dll tests/fixtures/libraries/GrimList-Library.json src/GrimList.Application/Resources/bootstrap grimlist-11e-2026.10.06.1
```

Generation validates both input and output. Runtime checks supported schemas, bounded decompression, byte count, SHA-256, top-level library-only fields, dataset validity and manifest counts. Unknown envelope fields such as `Armies` are rejected, rather than silently ignored.

## First-run sequence and existing-user safety

1. Resolve the platform data directory. If `grimlist.db` already exists, open it through the existing `ArmyStore` path; never consult or reapply the bootstrap. This includes a deliberately empty legacy database. Existing migrations/compatibility behavior remains in place.
2. With no database, acquire `.bootstrap.lock` exclusively and check again. The lock file can persist harmlessly; the operating system releases the lock when a process dies.
3. Remove any abandoned `.bootstrap-initializing` staging directory. Validate the embedded release and create the staging database with the existing schema creation/migration code.
4. Import through `LibraryWorkflow.Prepare(Combined)` and `LibraryWorkflow.Apply`, which use the production repository import transaction. No armies are created by bootstrap.
5. Check stored unit/snapshot counts, absence of armies and SQLite `quick_check`; record `installed_data_release` and `installed_data_sha256` in settings and database `PRAGMA user_version=1`.
6. Checkpoint SQLite, switch the staged database to DELETE journaling, close the connection, flush the completed file, then rename it to `grimlist.db` on the same volume without overwriting.
7. Open the normal application. Both platforms display startup progress; Android offers retry after failure.

Before the rename, failures or process termination leave no usable-looking library at the final path. The next attempt discards staging and retries. After the rename, the final file is a complete, committed library with its installed release record. Normal existing-file startup cannot create a second source release or modify saved armies through bootstrap. No network client participates in this sequence.

## Windows build and installation

Preferred SDK self-contained publish (requires the .NET Windows runtime NuGet packs on the build machine):

```powershell
dotnet publish src/GrimList.Desktop/GrimList.Desktop.csproj -p:PublishProfile=Standalone -m:1 -nr:false -p:UseSharedCompilation=false
```

Output: `artifacts/builds/windows/0.2.0/`. Copy the **entire directory** to the destination PC and run `GrimList.exe`. No repository or installed .NET SDK is needed.

This environment could not reach NuGet and did not have the Windows runtime packs cached. The delivered build therefore uses the supported app-relative .NET host with a private copy of the installed Microsoft .NET **10.0.10** Windows Desktop/Core runtimes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-StandaloneWindows.ps1 -UseInstalledRuntime
```

This fallback is technically a framework-dependent application **with its complete runtime redistributed in the directory**, rather than an SDK `SelfContained=true` publish. Its apphost searches only `runtime/`; the recipient still needs no .NET installation. Host tracing confirmed loading both frameworks from the copied directory outside the repository. Runtime licenses and third-party notices are included. The script accepts `-RuntimeVersion` and `-DotNetRoot` explicitly. Keep the app and private runtime together.

## Android build and installation

```powershell
dotnet publish src/GrimList.Mobile/GrimList.Mobile.csproj -p:PublishProfile=Standalone -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot" -p:AndroidSdkDirectory="C:\Users\es0p0\AppData\Local\Android\Sdk" -m:1 -nr:false -p:UseSharedCompilation=false
```

Adapt the two SDK paths on the build machine. Output: `artifacts/builds/android/0.2.0/`. Install **`com.grimlist.mobile-Signed.apk`**. The other APK is unsigned intermediate publish output; `_Microsoft.Android.Resource.Designer.dll` is an SDK-generated publish sidecar, not a separate installation requirement. The package contains ARM64 and x64 runtimes/assemblies, minimum Android API 30, target API 36, application ID `com.grimlist.mobile`, display version `0.2.0`, version code `2`. Trimming and AOT are disabled for this initial distribution milestone.

The APK uses the existing local development signing certificate for sideload acceptance. Production keystore custody/signing and store submission remain later distribution work; no signing server was added. A public release should use an explicitly managed release key. Ordinary existing installations should be upgraded in place; **do not clear a real user's storage**. Acceptance uses only a disposable emulator.

For a fresh-install test on a phone that already contains GrimList, use the separately named, isolated APK in the [phone test instructions](android-phone-test.md). Updating the normal app retains its database and therefore intentionally skips bootstrap.

## Exact build contents

The companion inventories describe the historical 0.2.0 milestone; see [windows-files.csv](windows-files.csv), [android-files.csv](android-files.csv) and [android-apk-entries.csv](android-apk-entries.csv). They include sizes and SHA-256 hashes for delivered files. Bootstrap resources live inside `GrimList.Application.dll` (inside the Android assembly stores in the APK), not loose developer JSON. The hard-coded 0.2.0 inventory generator has been retired. Use the versioned release notes and package manifests in this directory for current delivery evidence; do not overwrite these historical inventories with another release.

Windows consists of **493 files, 184,681,125 bytes**: the apphost, app/shared assemblies, runtime metadata and the private Microsoft runtime tree with license notices. Android publish output contains two APKs and the resource-designer sidecar (**3 files, 145,341,677 bytes**). The installable signed APK is **72,350,955 bytes**, SHA-256:

`C540220AE0A367103F31A67DEDCBEEEE957FFEB457B56C4B8ADC9F1C2C2C94FC`

Neither build includes `.local`, artifact history, tests, reports, raw BSData/Wahapedia source snapshots, developer databases, Python, PowerShell scripts or development tools. File inventories are report artifacts and are not copied into either application directory. The exact shared assembly supplied to Windows and both Android ABI packagers has SHA-256 `4BEA97751EED09A659E3E1A034EE522FD0A22527FCD6FD44F31160850B85D10D`; [shared-bootstrap-assembly.json](shared-bootstrap-assembly.json) records all three comparisons.

## Verification

**Milestone A standalone/offline acceptance: PASS on Windows x64 and an Android API 35 x64 emulator, 2026-10-06.** Detailed UI dumps, screenshots, process logs, host traces and test logs are retained under `artifacts/distribution-evidence/`.

| Acceptance | Windows copied release | Android clean install |
| --- | --- | --- |
| First launch, fresh user storage | PASS | PASS |
| No repository/development data at runtime | PASS: copied EXE directory and unrelated temporary CWD | PASS: APK only; emulator has no repository access |
| No network required | PASS: network-restricted session; no source import/download | PASS: airplane mode on, Wi-Fi off, mobile data off before first launch and throughout test |
| Factions, units, points | PASS | PASS |
| Select detachment | PASS: Flyblown Host | PASS: Flyblown Host |
| References and stratagem cards | PASS: native rules UI | PASS: expanded army-rule text and Command Re-roll card |
| Create/save army | PASS: Mortarion, 375/2000 points | PASS: Mortarion, 375/2000 points |
| Normal validation | PASS: Army validated | PASS: validation check mark |
| Close/reopen and retain army | PASS: exit, relaunch, open saved army; units/points/validation retained | PASS: force-stop, relaunch; name/units/points/validation retained |
| Platform runtime bundled | PASS: host trace loads private Core/Desktop 10.0.10 | PASS: APK embeds ARM64/x64 runtime and shared assemblies |

Windows acceptance used the unmodified final publish directory copied to `C:\Users\es0p0\AppData\Local\Temp\GrimList-Standalone-4a0dfd0c-2e3e-4e19-91fc-e558f8d30c1c\app`, with a new sibling `data` directory and `%TEMP%` as CWD. Thirteen native UI/host checks passed. The machine is Windows 11 build 26200; this is an isolated copied-output/new-user-data test, not a reimaged physical PC. Installed global .NET runtimes were not selected by the apphost.

Android acceptance installed the final signed APK on disposable `emulator-5580`, cleared only that test app's storage, disabled connectivity, and passed fifteen native installation/UI/lifecycle checks. The final first initialization took **108.2 seconds** on this cold emulator; it then continued to the normal home screen. Existing libraries skip this import. This is evidence for the Android x64 runtime, not a claim of a physical ARM64 phone run. APK signature verification passed with certificate SHA-256 `9BE9C191C53DC33EE955DCAEDAD94868FE4A4E6817A1FB30187AC0BAF7F28F25`; the manifest confirms version code 2, API 30/36 and both ABIs.

Repeat the native acceptance tests against the published outputs:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-StandaloneWindows.ps1 -PublishedDirectory artifacts/builds/windows/0.2.0 -EvidenceDirectory artifacts/distribution-evidence/windows-final
python scripts/Test-StandaloneAndroid.py --adb "C:\Users\es0p0\AppData\Local\Android\Sdk\platform-tools\adb.exe" --serial emulator-5580 --apk artifacts/builds/android/0.2.0/com.grimlist.mobile-Signed.apk --output artifacts/distribution-evidence/android-final --clear-data
```

The Android script requires an explicitly selected disposable emulator and `--clear-data`; it does not replace or seed any database. Windows automation only operates on the process it starts and retains its temporary copy for inspection. Tooling is used for verification, never included in end-user output.

Automated command:

```powershell
dotnet build tests/GrimList.Tests -m:1 -nr:false -p:UseSharedCompilation=false
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ReferenceFixtures.ps1
# Bootstrap-only subset:
dotnet tests/GrimList.Tests/bin/Debug/net10.0/GrimList.Tests.dll --bootstrap-only
```

The real-fixture suite passed **273 checks**, including 12 bootstrap checks: fresh install, existing populated/empty libraries, idempotence, concurrent first starts, failure during schema creation/import/release recording with successful retry, abandoned staging, incompatible schema/corrupt data/private envelope rejection, no user armies, real army validation, enhancements/equipment, references and save/reopen. The tests use the production services and Windows SQLite adapter, not a fake import pipeline. Android runtime acceptance exercises the Android adapter independently.

## Remaining distribution work

* A single-file Windows MSI, shortcuts and data-preserving uninstall rules are now authored; see [Windows installer](windows-installer.md). Payload and native setup UI checks pass, but actual install/repair/upgrade/uninstall acceptance remains blocked here by Windows Installer system-folder permissions. Windows code signing and public Android release signing/store packaging remain outstanding.
* Broader physical Windows/Android device coverage and first-install performance measurements. An x64 Android emulator proves native Android behavior; it does not measure ARM64 phone performance.
* Existing source coverage limitations remain visible: not every faction/detachment has supplemental stratagem text; unavailable/conditional source entries remain marked unavailable. This milestone preserves validated source content rather than inventing missing rules or prices.
* Optional Windows PDF import still requires pdftotext; basic use and PDF export do not. Initial bootstrap can take time on a cold emulator; progress is shown.
* Hosted updating is explicitly deferred until this milestone is reviewed.
