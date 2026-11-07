# GrimList 0.2.4 distribution

The copy-and-share folder is `GrimList-Share-0.2.4/`:

- `GrimList-Windows-0.2.4.msi`: Windows x64 installer with a private runtime and offline library.
- `GrimList-Android-0.2.4.apk`: Android ARM64/x64 package, application ID `com.grimlist.mobile`, version code **6**.

This release includes the current effective-datasheet, weapon ability and upgrade
fixes, plus generic attachment-recipient routing. Bodyguard and support abilities
apply to their specified recipients, disappear when the qualifying attachment is
removed, and preserve independent effects. Recovery metadata updates matching
existing libraries without changing their base profiles. No faction or unit name
checks are used by the recipient implementation.

Close GrimList before running the MSI. Install the APK over the existing app;
do not uninstall or clear app storage to update. The previous 0.2.3 packages are
retained in their own share folder.

Sizes and SHA-256 hashes are recorded in [release-0.2.4-files.json](release-0.2.4-files.json).

## Verification

- Both Release builds completed successfully. The fixes passed 300 regression
  checks and the native Windows recipient checks before packaging.
- All 19 MSI checks passed, including cabinet extraction, all 490 payload files,
  table validation and upgrade authoring. The upgrade identity matches 0.2.3 and
  the product identity changes for 0.2.4.
- The MSI-extracted app passed 13 native checks with temporary data outside the
  repository, including offline bootstrap, save/reopen and its bundled runtime.
  Actual MSI installation/repair/uninstall was not rerun.
- APK signature verification passed, with the same certificate as 0.2.3.
  Its manifest confirms the production application ID, version 0.2.4, version
  code 6 and ARM64/x64 support. ZIP integrity and payload checks passed.
- The Core and Application assemblies match byte-for-byte between Windows and
  both Android ABI packaging inputs. The published Application assembly includes
  the updated effective-datasheet recovery metadata.
- Both share-folder copies match the verified build-output SHA-256 hashes.
- Android device startup and an actual in-place upgrade were not tested for this
  release because no device or emulator was connected.

Evidence: `.local/release-0.2.4-installer-acceptance/`,
`.local/release-0.2.4-package-verification/` and the corresponding build logs.
