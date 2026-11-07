# GrimList 0.2.20 — community testing

This build brings Windows and Android to the same version, including compact battle QR sharing and the Android battle-scrolling fix. It is a community test release: please report problems, including incorrect data and confusing behavior.

## Install or update

- **Windows 10/11, x64:** close GrimList and run `GrimList-0.2.20-win-x64.msi`. It includes its own .NET runtime. The installer is unsigned, so Windows may show an unverified-publisher warning.
- **Android 11 or newer, ARM64/x64:** open `GrimList-Android-0.2.20.apk`. Android may ask you to allow installation from the browser/file app you used. This is a sideload test build signed with the existing development certificate.
- Existing users: install over the previous app. Do not uninstall or clear app storage to update. Armies stay in the app's user-data directory.
- Check that the app/bug-report diagnostics show **0.2.20**. No account, Python, SDK or separate .NET installation is required.

## Things to try

1. Launch offline, create an army, choose its faction/disposition/detachments, add units, change model counts and equipment, and choose enhancements, attachments and a warlord. Check points and validation against the source you use.
2. Save, close and reopen. Duplicate/edit armies and try undo/redo. Existing users should check an army saved in their previous version.
3. Open army/detachment rules, stratagems and unit datasheets. Report missing or incorrect content with the faction, detachment, source edition/version and expected rule.
4. Start/resume a battle, scroll through both players' controls, record CP and VP, change rounds, finish and reopen the scoreboard. On Android, vertical scrolling should work smoothly; swipe the round heading or use Previous/Next to change rounds.
5. Test PC + Android sharing: on PC open **Battle tracker → Play with another GrimList**, enter your player name and show the invitation QR. Scan using the phone's camera/QR app and open the GrimList link. Review and join on the phone, then return its response text to the PC and accept it. Each player selects secondary choices locally before starting.
6. Try text/PDF/roster exports and the preview/apply flow for data imports or updates. Check that saved armies remain available after an update.

QR exchanges include player name, faction and disposition plus matching identifiers. They do not transfer army lists or synchronize scores. If the phone scanner cannot open a custom app link, copy the link and use **Paste from clipboard** in GrimList. A one-way scan does not send the phone's response back to the PC automatically.

## Report a problem

Use **Report a Bug** on the Windows Home/roster menu or Android menu. Include a short title, repeatable steps, expected result and actual result. Review the report, then save/share its ZIP with the person coordinating your test. Nothing is submitted automatically. Up to five selected screenshots can be included.

For crashes or cases where the report screen cannot open, send:

- Version **0.2.20**, Windows/Android version and device model.
- The steps immediately before the issue and whether it repeats after restarting.
- Faction, disposition, detachment, affected units and data-source version when relevant.
- A screenshot or short screen recording, if possible.

## Test limits

The bundled library and recovery resources are versioned snapshots; this app release does not claim to update all rules or points to today's publications. Review source dates and compare suspected data errors with the applicable edition. Camera recognition depends on the external scanner. Windows install/repair/upgrade/uninstall lifecycle testing on an unrestricted machine is part of this community test; package extraction and app tests are recorded separately in the release notes.
