# Test a fresh Android installation without touching your existing GrimList

Install `artifacts/builds/android-standalone-test/0.2.0/GrimList-Standalone-Test.apk`.
Its launcher name is **GrimList Standalone Test** and its application ID is
`com.grimlist.standalonetest`. Your existing **GrimList** uses `com.grimlist.mobile`.
Android gives these two apps separate libraries, armies and settings. The test app
uses the same code and bundled bootstrap release as the normal build.

There is no new editor feature to look for: this test proves that an empty installation
can become usable without importing anything or connecting to the internet.

1. Keep your existing GrimList installed. Install the **Standalone Test** APK above.
2. Before opening **GrimList Standalone Test**, enable airplane mode and turn Wi-Fi off.
3. Open **GrimList Standalone Test**. The first launch should show library preparation
   and installation progress, then reach the home screen. Allow a few minutes; the
   emulator's first initialization took about 108 seconds. Your old saved armies
   should **not** appear in this separate app.
4. Tap **New army**. Factions should already be available without any data import.
5. Choose **Death Guard**, name the army **Offline phone test**, leave **2000** points,
   select **Flyblown Host**, and create it.
6. Open **Epic Heroes** and add **Mortarion**. The roster should show **375/2000** points
   and the validation check mark; Mortarion is selected as the mandatory Warlord.
7. Open the menu's **Army rules**. Expand an army-rule card and check that stratagem
   cards such as **Command Re-roll** are present. Stay offline.
8. Open Android **Settings → Apps → GrimList Standalone Test → Force stop**. Reopen
   **GrimList Standalone Test**. The army, Mortarion and 375-point total should survive.
9. Open your original **GrimList** and confirm that its original armies are still there.

Success means all those steps work offline with no manual library import. The test
armies stay in the test app; they do not transfer automatically to your original app.

To repeat first-run testing, clear storage or uninstall **GrimList Standalone Test only**.
Never clear storage or uninstall your original **GrimList** for this test.

Rebuild the isolated APK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-StandaloneAndroidTest.ps1
```

The normal production application's package ID and title remain unchanged. This
separate sideload test package uses the existing development signing certificate.
