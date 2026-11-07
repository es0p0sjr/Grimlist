# GrimList 0.2.3 distribution

The copy-and-share folder is `GrimList-Share-0.2.3/`. It contains only:

- `GrimList-Windows-0.2.3.msi` — Windows installer with the private runtime and offline data.
- `GrimList-Android-0.2.3.apk` — Android package, application ID `com.grimlist.mobile`, version code **5**.

Includes the shared conditional-rule evaluation, detachment rule visibility,
enhancement-driven attachment permissions, conditional defensive effects and Android
attachment undo/redo refresh fixes described in [the implementation report](../conditional-rule-evaluation.md).

Both builds use application version **0.2.3**. The Android signing certificate matches
the previously shared 0.2.2 APK. The Windows MSI retains the same UpgradeCode and
uses a new version-specific ProductCode.

## Verification

- Windows Release publish and MSI creation succeeded.
- **19 MSI checks passed**, including cabinet extraction, all 490 payload files,
  table validation, upgrade authoring and exclusion of developer/user material.
- The MSI-extracted application passed **13 native checks** with fresh temporary
  data outside the repository, including bootstrap, references, save/reopen and
  loading its private runtime. Production MSI installation lifecycle was not rerun.
- Android clean Release publish and APK signature verification succeeded.
- APK metadata confirms 0.2.3 / version code 5 and ARM64/x64 support.
- **6 Android upgrade checks passed** on the disposable emulator: install over
  0.2.2, verify the version, launch with networking disabled, and preserve the saved
  test army, its units and its 120-point total. Application storage was not cleared.
  A temporary ADB interruption/System UI prompt was cleared before completing the
  saved-roster checks; no app-data reset was needed.
- Both shared files match their build-output SHA-256 hashes. Sizes and hashes are
  recorded in [release-0.2.3-files.json](release-0.2.3-files.json).

Evidence: `.local/release-0.2.3-installer-acceptance/`,
`.local/release-0.2.3-android-acceptance/` and the corresponding build/test logs.
