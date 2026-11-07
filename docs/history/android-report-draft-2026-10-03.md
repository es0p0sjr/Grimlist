# Android proof-of-concept verification

Runtime verification on 2026-10-03 uses **Medium_Phone_API_35**, Android 15 / API 35, x86_64, Debug configuration. The earlier toolchain and emulator blockers are superseded. Architecture and rules ownership remain unchanged. Statuses below describe Android observations; desktop tests are reported separately.

## Toolchain, build and deployment

| Check | Status | Observed value |
| --- | --- | --- |
| .NET SDK | PASS | 10.0.302; MSBuild 18.6.11 |
| Workload set | PASS | 10.0.303.1 |
| Installed MAUI workload | PASS | `maui`, manifest 10.0.20/10.0.100, installation source SDK 10.0.300 |
| MAUI Controls | PASS | 10.0.110 |
| Android SDK pack | PASS | Microsoft.Android.Sdk.Windows 36.1.69 |
| JDK 21 | PASS | Microsoft OpenJDK 21.0.12.1, `C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot` |
| Android SDK | PASS | `C:\Users\es0p0\AppData\Local\Android\Sdk` |
| Android platform 36 | PASS | API 36, package revision 2, extension level 17 |
| Build-tools | PASS | 36.0.0 |
| Restore | PASS | Existing successful restore; subsequent builds used restored packages |
| `net10.0-android` build | PASS | Zero warnings and errors |
| Emulator boot | PASS | `Medium_Phone_API_35`, serial `emulator-5554`, `sdk_gphone64_x86_64` |
| Deployment | PASS | MSBuild `Install`, including Debug fast-deployment assemblies |
| Application launch | PASS | `com.grimlist.mobile`, visibly interactive; cold launch also verified after force-stop |

The verified build explicitly supplies `JavaSdkDirectory` and `AndroidSdkDirectory`, so these are the SDKs used by MSBuild, independent of shell PATH. The emulator's earlier named-pipe error is not reproduced as a blocker in this running session; no GrimList architecture change was made for it.

```powershell
dotnet build src/GrimList.Mobile/GrimList.Mobile.csproj -t:Install -f net10.0-android --no-restore -m:1 `
  '-p:JavaSdkDirectory=C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot' `
  '-p:AndroidSdkDirectory=C:\Users\es0p0\AppData\Local\Android\Sdk' `
  '-p:AdbTarget=-s emulator-5554'
```

APK: `src/GrimList.Mobile/bin/Debug/net10.0-android/com.grimlist.mobile-Signed.apk`. This is a Debug deployment, not a production distribution package.

## Source and reproducible army

The test JSON contains the active Windows dataset snapshot, equivalent to **Export dataset**; no Windows saved armies were transferred. Android's native document picker selected it from Downloads and `MobileFilePicker` passed `OpenReadAsync()` to shared `LibraryWorkflow`. No selected-document filesystem path is consumed.

| Source attribute | Value |
| --- | --- |
| Edition | 11 |
| Catalogue revision | `cc1830fbfead059f8059ed6c7f8e73cd4c390908` |
| Death Guard catalogue revision | 17 |
| Unit definitions | 2,194 |
| JSON size | 55,531,722 bytes |
| JSON SHA-256 | `2fcf9d2059504b63f7f4800526c27dfacaef750b9580cd5166c1ef9d1612e16a` |
| Original source/archive hash | `747db24bf29456f0606a937f794a7e7f33154e76116eff1506e9b0750ab5c045` |

Army: **Android DG Acceptance**, Death Guard, Virulent Vectorium, 2,000-point limit.

| Entry | Models | Recorded selection | Points |
| --- | --- | --- | --- |
| Plague Marines | 10 | Champion power fist replaces plague knives; remaining imported equipment retained | 175 |
| Malignant Plaguecaster | 1 | Attached to Marines; warlord | 60 |
| Biologus Putrifier | 1 | Attached to Marines; Daemon Weapon of Nurgle | 60 + 10 |
| Total | | **Army validated.** | **305** |

The first attachment test passed at 295 points before the enhancement. Adding the enhancement produced 305; Undo removed it and Redo restored it. The stored Plague Marines association constraint and its conditional max-two modifiers match the Windows JSON. Android calls shared `ArmySession`, `LeaderRules`, `ArmyCalculator` and validation; there is no Android-specific Death Guard exception or undo stack.

## SQLite and lifecycle evidence

Android uses `AndroidSqliteConnectionFactory` and Android's `/system/lib64/libsqlite.so`. The database is `/data/user/0/com.grimlist.mobile/files/grimlist.db`, MAUI app data. All nine shared table definitions match the desktop database; Android additionally owns `android_metadata`. Shared initialization, transactions and UPSERT-based save/context operations completed, and snapshot integrity checks returned `ok`. Fresh initialization and current-schema reuse pass; migration from a historical Android installation was not separately exercised.

The force-stop comparison used saved snapshots before and after process replacement (PID 17883 to 19708). Army metadata, every entry's full loadout, model counts, attachments, enhancement and warlord matched, excluding only the expected updated timestamp. Active-source and `editor_context` settings matched exactly, including the Plague Marines entry, model index 1 (**Model 2**) and `equipment` section. The UI recalculated **305 / 2000 pts**, **Army validated.** Undo and Redo were disabled after process recreation, as designed.

Background/resume retained the same process and identical persisted state. An initial `always_finish_activities` setting attempt only produced stop/resume events; that attempt is not evidence of activity recreation.

Runtime maps show the shared Core/Application/Persistence assemblies and Android SQLite. No `winsqlite3.dll`, WPF/PresentationFramework, Windows platform assembly, Windows dialog API or `pdftotext.exe` was required. PDF remains out of scope.

## Defects demonstrated and narrow fixes

1. The full exported JSON exceeded the shared 50 MB stream guard. The guard now allows 128 MB, with a new service regression checking a 51 MB stream and no automatic application of its source.
2. Searching initially disabled the form on the first keystroke. Retesting also exposed Android input-dispatch ANRs while search/detail rule queries ran on the UI thread (19:34:59 and 19:40:00). Search now debounces and discards obsolete queries; existing shared detail queries run on the session worker before controls render. No rules implementation or architecture was replaced.
3. Logcat reported unclosed Android cursors. The Android adapter now explicitly closes each query cursor in `finally`. Subsequent process logs show no new cursor-close warnings or unhandled exceptions.

The earlier ANRs are retained as failure evidence; successful retesting does not erase them. Long Debug emulator waits remain and are recorded below. No production desktop UI was changed for this runtime work.

## Performance observations

These are approximate observations on the API 35 x86_64 emulator, Debug, with the complete 2,194-unit source above. They are not physical-device performance claims or isolated Core benchmarks.

| Operation | Observation |
| --- | --- |
| Cold application/workspace restoration | About 83-102 seconds until the loaded editor is usable; Android activity launch alone was about 9-10 seconds |
| Full JSON read/parse/preview | Several minutes, approximately 4 minutes; phases were not separately instrumented |
| Source apply | Approximately 30 seconds, including refresh |
| Library refresh during editing | Debug timing logs: approximately 16-25 seconds |
| Detail preparation | Approximately 0.6-1.1 seconds for the selected Marine model; 16-19 seconds for a character |
| Search | Approximately 16-23 seconds to update results |
| Edit through usable refreshed UI | Approximately 35-80 seconds in observed runs |
| Core recalculation alone | NOT RUN as an isolated measurement |
| Full BSData ZIP parse | NOT RUN on Android |

Moving work off the UI thread addressed demonstrated input ANRs; it did not make these latency figures acceptable for a finished UI. Further profiling is separate from correctness acceptance.

## Desktop regression

`dotnet build GrimList.slnx -m:1`: **PASS**, zero warnings/errors. Automated executable suite with the optional real-catalogue diagnostic: **185 PASS** (181 regular checks plus four real-catalogue checks). Extended WPF smoke: **PASS**, covering persistence, roster/loadout editing, imports, attachments, detachments, enhancements and muster behavior.

The earlier quick-add smoke failure was a **test timing problem**. The control and its unit-key Tag still existed and the helper searched the correct visual tree, but replacement item templates had not been realized after an ItemsSource rebuild. The generic lookup now completes WPF layout before searching. The smoke also clicks the actual quick-add button and verifies saved counts. No production UI change was needed for that failure.

## Local evidence

Evidence lives in `.local/android-acceptance/`: source metadata/hash, import-preview screenshot and unapplied database, applied-library database, schema comparison, Android association data, runtime maps, ANR traces, restored-process logcat, screenshots and before/after database comparisons. The snapshot files are local test artifacts and include the imported dataset and test army.

Build/deployment logs: `.local/android-final-install.log`, `.local/android-cursor-fix-build.log`. Desktop logs: `.local/android-acceptance/desktop-build.log`, `desktop-tests.log`. Extended smoke artifacts: `.local/tests/330da54f-fa4a-4956-a7a7-db210a709645/android-runtime-final.png.result.txt`.

