# Windows installer

## Latest Windows package: 0.2.14

The Windows MSI is `GrimList-Share-0.2.14/GrimList-Windows-0.2.14.msi`.
This release fixes missing detachment Stratagems across supported armies,
including Space Marine chapters. Shared offline recovery runs on startup and
after library updates, and detachment previews use the same resolved cards as
the Rules view. Saved armies and imported overrides are preserved.

Close GrimList and run the MSI over the existing installation. The upgrade
identity is unchanged. No uninstall or data reset is needed. Android remains
at 0.2.13. See [the 0.2.14 release report](release-0.2.14.txt) for verification
and the existing installation-lifecycle limitation.

## Previous package: 0.2.13

The Windows MSI and Android APK are in `GrimList-Share-0.2.13/`.
Both versions now display army units alphabetically within each category,
keeping attached leaders with their bodyguard units. See
[the 0.2.13 release report](release-0.2.13.txt) for package verification.
Windows keeps its upgrade identity; Android keeps its application/signing
identity and advances to version code 15. Close GrimList before running the
MSI, or install the APK over the existing app to retain saved armies.

## Previous package: 0.2.12

The Windows MSI and Android APK are in `GrimList-Share-0.2.12/`.
This update adds background import processing and progress stages on Windows,
and clearer import progress messages on Android. See
[the 0.2.12 release report](release-0.2.12.txt) for package verification.
Windows keeps its upgrade identity; Android keeps its application/signing
identity and advances to version code 14. Close GrimList before running the
MSI, or install the APK over the existing app to retain saved armies.

## Previous package: 0.2.7

The current Windows MSI and Android APK are in `GrimList-Share-0.2.7/`.
See [the 0.2.7 release report](release-0.2.7.txt) for changes and verification.
Windows retains its upgrade identity; Android retains its application and signing
identity with version code 9. Close GrimList before running the MSI, or install
the APK over the existing app without clearing storage.

## Previous package: 0.2.2

The copy-ready `GrimList-Share-0.2.2/` folder contains exactly two files:

- `GrimList-Windows-0.2.2.msi`
- `GrimList-Android-0.2.2.apk`

Both include generic Warlord eligibility, detachment cost and faction applicability
fixes, and Report a Bug, alongside the previous effective-datasheet and display
changes. Application version is 0.2.2; Android version code is 4. Windows upgrade
identity and Android application/signing identity are retained. Close GrimList
before running the MSI; install the APK over the existing app. Do not uninstall
or clear application storage to update.

The MSI is 82,755,584 bytes, containing 490 payload files totaling 205,871,113
bytes. SHA-256: `05cc5c0641da466b7efaf5da4a7ed1495f0c4d21d27b92a1b5c82fd916c6563e`.
The APK is 73,492,613 bytes. SHA-256:
`bf8bc7e742e4bc93800883678825f91328b108f092038d69e628889c2ea90bff`.
Exact Windows inventory:
`artifacts/installers/windows/0.2.2/GrimList-0.2.2-win-x64.inventory.json`.
Evidence: `artifacts/distribution-evidence/0.2.2/`, including `share-files.json`
with both copies verified identical to their build outputs.

The MSI passed table/identity validation, extraction and exact payload hash
checks. Its extracted app passed all 13 native offline first-run, validation and
army save/reopen checks outside the repository. The installation-lifecycle
limitation below remains: these checks do not install the MSI.

The APK passed signature verification against 0.2.1 and native startup-binding
checks for ARM64/x64. All nine offline upgrade checks passed on the disposable
Android emulator: installing over 0.2.1 preserved saved army identities, names
and totals, and the saved army opened and validated. Report a Bug was available
in the release APK. Physical-phone upgrade acceptance remains untested here.

## Previous package: 0.2.1

The updated installer is
`artifacts/installers/windows/0.2.1/GrimList-0.2.1-win-x64.msi`.
The copy-ready `GrimList-Share-0.2.1/` folder contains only this Windows MSI
and the corresponding Android APK. Both include effective datasheets,
allegiance/enhancement effects, current-only stat presentation and the weapon
spacing fix for double-digit Strength and dice-plus-bonus Damage.

Application version is 0.2.1; Android version code is 3. The Windows upgrade
identity and Android application/signing identity are retained. Close GrimList
and run the new MSI, or install the new APK over the existing app. Do not
uninstall or clear application storage to update.

The new MSI is 77,320,192 bytes, with 488 payload files totaling 184,886,341
bytes. SHA-256: `49BAC3177465AE697A816AE3BBDBA9C0D368F58DE0BCFB2ED98C710D14537621`.
Exact inventory: `artifacts/installers/windows/0.2.1/GrimList-0.2.1-win-x64.inventory.json`.
Verification evidence: `artifacts/distribution-evidence/0.2.1/`.

The 0.2.1 MSI passed table/identity validation, extraction and exact payload
hash checks. Its extracted application passed the 13 native offline
first-run/validation/save/reopen checks. The existing installation-lifecycle
limitation below still applies; this validation does not install the MSI.

Android 0.2.1 passed signature verification against the original 0.2.0
certificate and native startup-binding checks for ARM64/x64. An in-place update
on the disposable Android emulator retained the saved army after relaunch.
The APK is 73,312,389 bytes, SHA-256
`5E2A8C5241A109A613B132F1856A6BEBC855A0A9C421346C36D5EAAA4DF8C5A2`.
`artifacts/distribution-evidence/0.2.1/share-files.json` records hashes of the
two share-folder files, each verified identical to its packaged build output.

## Initial 0.2.0 package and installation behavior

The single end-user file is:

`artifacts/installers/windows/0.2.0/GrimList-0.2.0-win-x64.msi`

Double-click it, select **Install**, then open **GrimList** from the Start menu or desktop. The MSI includes the application, private .NET runtime, and canonical offline bootstrap library. Users do not need Python, PowerShell tooling, .NET installation, repository access, or an internet connection. The adjacent inventory JSON is build evidence and is not needed by users.

This installer is unsigned. Windows identifies the publisher as unverified. Code signing remains future distribution work.

## Installation behavior

| Item | Location or behavior |
| --- | --- |
| Program files | `%LOCALAPPDATA%\Programs\GrimList\` |
| Database, settings, backups and other application data | `%LOCALAPPDATA%\GrimList\` (existing application behavior) |
| Shortcuts | Current user's Start menu and desktop |
| Installation scope | Current user; package does not request elevation |
| Uninstall | Windows Settings → Apps → Installed apps → GrimList → Uninstall |
| Repair | Run the same MSI again and select Repair; retain the original MSI for repair |
| Later versions | Stable upgrade identity; a higher three-part version replaces program files transactionally |
| Older versions | Detected and rejected while a newer installer version is installed |
| User libraries | Never installed, modified or removed by MSI actions |

The MSI writes only its own installation markers under `HKCU\Software\GrimList\Installer\production`. It owns no user-data directory or database. Uninstall removes packaged files, shortcuts, and empty owned directories; it does not recursively delete folders. The app performs its existing bootstrap on first launch if no database exists. Installing does not launch the app or import data.

Portable copies are not registered installer products and remain where they were copied. The new installed app uses the normal production user-data location. Existing command-line or environment data-directory overrides continue to apply when launching with those overrides; the installed shortcuts do not set one.

The platform gate requires x64 Windows 10 or later; actual app acceptance here covers Windows 11 build 26200. Microsoft's MSI `VersionNT` compatibility value is not the real Windows 10 version, so the installer reads `CurrentMajorVersionNumber` from the Windows registry as well as checking `VersionNT64`. See [Microsoft's VersionNT explanation](https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/versionnt-value-for-windows-10-server).

## Build

Build machine requirements: Windows, the existing published Windows payload, and **64-bit CPython 3.12** with its built-in `msilib` module. Python 3.13 removed that module. These are build dependencies only.

```powershell
# If the standalone payload needs rebuilding:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-StandaloneWindows.ps1 -UseInstalledRuntime

# Package artifacts/builds/windows/<Version>/; version comes from Directory.Build.props:
python scripts/Build-WindowsInstaller.py
```

The authoring script uses Windows Installer tables and Windows' cabinet compression API. The cabinet is embedded in the MSI; there are no download actions, executable custom actions, hosted updates, or installation scripts running on the user's machine. Close GrimList before repair, upgrade or uninstall.

Product identity is deterministic for each three-part version; the upgrade identity and path-based component identities remain stable. **Increment the version whenever shipping changed application/runtime contents.** Do not distribute different payloads with the same version. Each build gets a new package identity. `--test-identity` generates separate product, upgrade, component, shortcut and default program-folder identities for isolated installer lifecycle testing; do not distribute that test build.

## Exact contents

The release MSI is **77,004,800 bytes** and contains **488 files**, totaling **184,473,669 bytes** before compression. It contains all files from the accepted `artifacts/builds/windows/0.2.0/` release except five developer PDB symbol files. This includes the native apphost, GrimList assemblies/configuration, embedded canonical bootstrap release, .NET Core and Windows Desktop 10.0.10 runtimes, hostfxr, resources, and runtime licenses. No developer databases, source files, scripts or build tools are packaged.

The exact relative paths, file sizes and SHA-256 hashes are recorded in:

`artifacts/installers/windows/0.2.0/GrimList-0.2.0-win-x64.inventory.json`

MSI SHA-256:

```text
41D82B7D4BDA1DDB353D617423F6B7BD0EEE676B16E86535E6631161E728E420
```

## Verification performed on 2026-10-06

| Check | Result |
| --- | --- |
| Production MSI identity, platform, embedded cabinet, file inventory, per-user scope | PASS |
| Windows Installer validation of all 2,140 authored table rows | PASS; row validation, not full ICE validation |
| Native welcome screen shows correct user installation path | PASS |
| Cancel → Finish exits without installing | PASS; exit code 1602 |
| Extract the production MSI cabinet outside the repository | PASS |
| Every extracted file equals its published source SHA-256 | PASS; 488 files, no extras |
| MSI-extracted app uses its private runtime | PASS |
| Fresh offline bootstrap, factions/units/points/detachments/references | PASS |
| Create/save/close/reopen army; points and validation retained | PASS; 13 native app checks in total |
| MSI table rules keep installation separate from user data | PASS |
| Actual installation, shortcut creation, repair, upgrade and uninstall | **NOT VERIFIED: restricted execution environment blocked Windows Installer system writes** |

An isolated test-identity install reached `InstallFinalize` but Windows Installer could not write `C:\Windows\Installer\inprogressinstallinfo.ipi` (access denied, `0x80030005`). It returned 1603 with errors 2503/2502. No test app files or uninstall registration were created. No privilege escalation or system-permission changes were attempted. This is a concrete environment limitation, not a successful lifecycle test. The production MSI therefore still needs a normal-user installation and uninstall check outside this restricted session before public distribution. No full SDK ICE validator was available here.

Evidence is under `artifacts/distribution-evidence/windows-installer/` (payload/app results and `installation-blocked.log`) and `artifacts/distribution-evidence/windows-installer-ui/` (welcome/cancel screens and log).

Repeat the non-installing verification:

```powershell
python scripts/Test-WindowsInstaller.py --msi artifacts/installers/windows/0.2.0/GrimList-0.2.0-win-x64.msi --evidence artifacts/distribution-evidence/windows-installer-repeat --run-app-acceptance
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-WindowsInstallerUi.ps1 -Installer artifacts/installers/windows/0.2.0/GrimList-0.2.0-win-x64.msi -EvidenceDirectory artifacts/distribution-evidence/windows-installer-ui-repeat
```

For the remaining lifecycle acceptance, use a disposable normal Windows user/VM: install the MSI, open its shortcut offline, create/save an army, close/reopen it, run Repair, then uninstall through Windows Settings. Confirm program files and shortcuts are removed while `%LOCALAPPDATA%\GrimList\grimlist.db` remains; reinstall and confirm the same army opens. Also test a higher-version MSI replacing this one and rejecting a downgrade. Keep real user libraries backed up when doing destructive lifecycle tests.

Hosted updating has not been implemented.
