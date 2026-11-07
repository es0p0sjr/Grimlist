# Using GrimList

GrimList is a Windows desktop application for building Warhammer 40,000 army lists and consulting their datasheets during play. Your armies and imported library live in a local SQLite database. You can work offline after importing data; downloads and the optional Yellowscribe upload need a connection.

This guide describes the checked-in application. Its descriptions of allowances explain what GrimList implements, rather than establishing the current game rules. For implementation details, continue with [How GrimList works](architecture.md).

## Start the application

Install the .NET 10 SDK to run the source launcher, then double-click `Start-GrimList.cmd`, or run this from the project directory:

```powershell
.\Start-GrimList.ps1
```

The launcher builds and runs the desktop project in Release mode and uses `.local/grimlist.db` in the project directory. A directly launched executable uses `%LOCALAPPDATA%\GrimList\grimlist.db` unless you pass `--data-dir`. These are separate libraries: if your armies appear to disappear after changing how you launch the app, check the data directory first.

A new library has no unit prices. Import a source before building an army. The historical files in `samples` are demonstrations and are not loaded automatically.

## Import your first library

1. Open **Manage data**, select **BSData + MFM**, and choose the edition matching your source. The interface defaults to edition 11 unless the active library says 10.
2. Choose **Download data** to fetch all army catalogues, or **Choose local source** to read a complete BSData repository ZIP. A complete ZIP includes shared catalogues that individual files may depend on.
3. Use **Points + datasheets** for a full library. Check the source name, version, date and edition.
4. Preview the import. Read the warnings, changed prices, extracted profiles and effects on saved armies.
5. Select **Apply update** to make the preview active.

The **Update data** toolbar action starts this download and preview workflow. Downloading alone does not apply an update. You can cancel a download or close its window without changing the active library.

The app also accepts GrimList dataset JSON, individual supported BSData catalogues, and MFM PDF/text. MFM means Munitorum Field Manual; ordinary PDF/text imports supply unit points, not complete datasheets. PDF extraction requires a separate `pdftotext` executable. See [Data and storage](data-and-storage.md) for formats and import behavior.

| Import mode | When to use it |
| --- | --- |
| Points only | Update pricing while keeping existing profiles and equipment definitions. |
| Datasheets only | Refresh characteristics, rules and equipment choices while retaining existing pricing. |
| Points + datasheets | Import both layers together. |

Use **Complete faction points** only for a source containing the entire points list for each included faction. When checked for a points update, prices for absent units in those factions are cleared. Their profiles and saved roster entries remain, but their totals become incomplete.

## Create an army

The home screen shows saved army tiles, a search tile, and a **+** tile. Each army tile opens its roster; its menu provides duplication and deletion.

Choose **+**, then follow **Select army → Detachments → Name → Roster**. Selecting a faction advances to detachments. Inspect a detachment's rules before adding it, name the army, then create it. Back preserves setup choices; cancelling does not save a partial army.

When imported detachments have DP costs, the app uses the following budgets:

| Army points limit | Detachment points |
| --- | --- |
| 1,000 | 2 DP |
| 2,000 | 3 DP |
| 3,000 | 4 DP |

Other sizes have no defined DP budget in the implementation. Libraries without DP metadata use the single-detachment setup, with manual entry when needed. If selected detachments offer multiple dispositions, select the appropriate disposition; a single available disposition is selected automatically.

## Build and edit the roster

The roster has three resizable areas: the unit library, your army, and details for the selected unit. Search and the **Fits remaining**, **Hide Legends**, and **Hide Crucible** filters narrow the library. Filters do not remove existing roster entries. The BSData importer excludes Legends entries itself, so disabling that filter cannot restore entries that were never imported.

Use **+** to add a library unit. Select a roster entry to edit its size and equipment. Sizes follow the imported choices. A unit without a usable price cannot be added through the normal library control.

Equipment controls reflect imported model roles, standard equipment, replacements and quantity limits. Champions can have separate choices. Adding replacement equipment reduces the standard equipment; removing it restores the standard choice. Increasing squad size adds defaults for new models. Reducing size can leave selections needing review, which the app reports.

For old libraries with only flat weapon profiles, manual quantities are available. Import **Datasheets only** from the original catalogue ZIP to obtain automatic defaults and replacement limits. **Reset to defaults** adopts the current imported choices and supports undo.

Right-click a roster entry and choose **Duplicate unit** to copy its equipment into a separate entry. A copied character starts unattached. Duplicating an entire army from home creates fresh entry IDs and remaps its attachments and warlord.

Open **Army rules** to view the selected army and detachment references and change the points limit. Selecting or removing detachments can change available units, keywords, enhancements and allowances. Existing selections that become invalid are retained for you to resolve.

## Warlords and attachments

Choose an eligible roster character in the **Warlord** selector. Imported mandatory-warlord information can select a sole required candidate automatically. If several candidates are mandatory, choose among them.

Characters offer a **Bodyguard unit** picker, and bodyguards offer a picker for eligible Leaders or Support units. Picking an item attaches it immediately. Choices depend on imported bodyguard targets and on the whole proposed group, including characters already attached.

The supported attachment rules can allow a Leader plus Support, a second Leader, or a named partner exception. They also check supported size requirements, duplicate restrictions and bodyguard constraints. Without structural catalogue rules covering the proposed group, the fallback permits at most two attached characters and still requires permission for the pair. If an expected choice is absent, refresh datasheets and check the imported permission; a shared keyword alone does not authorize attachment.

Attached characters appear beneath their bodyguard. Removing the bodyguard detaches its characters. Attachments save with the roster and support undo/redo.

## Enhancements and allies

Eligible units have enhancement buttons on their datasheet. Click to select a choice; remove the current choice before choosing another. Click the selected button again to remove it. Choices depend on selected detachments and recognized bearer restrictions. Rules with unknown restrictions can remain reference-only. Epic Heroes do not receive ordinary character enhancement choices.

BSData enhancements also use their imported catalogue eligibility conditions, even when their displayed text contains only the effect. Existing libraries recover missing eligibility from a matching cached catalogue or the bundled compatibility metadata for the affected source revision. The repair restores eligibility and Upgrade classification; it does not require reimporting unit profiles or selecting enhancements again. On Android, open a roster unit's **Datasheet** tab to equip an enhancement.

Purchased enhancements and upgrades contribute their imported points. The implementation permits one selection across an attached bodyguard and its characters. Upgrades are Enhancements that can be assigned to eligible non-Character units. Up to three copies of the same Upgrade share one enhancement choice; each different Upgrade uses its own choice, and every copy pays its points. The implemented choice budgets are 2 through 1,000 points, 4 through 2,000, and 6 through 3,000; the six-choice value is explicitly provisional in the code. Larger sizes have an unspecified allowance. Check the source rules when relying on these limits.

Detachment choices show DP cost, Force Dispositions, tags and explicit combination restrictions before selection. Sharing a tag alone does not prohibit a combination.

When a selected Detachment offers **Muster nominations**, use that step in the army editor (Android opens the choices in Army rules). It shows required, current and maximum counts and offers only eligible units or models. Nominated units display their granted keywords and can use them for Enhancement and Warlord eligibility. Removing the Detachment removes its grants; undo restores them. Duplicating an army remaps its nominations, while duplicating a unit creates an unnominated copy and removes an Enhancement that depended on its nomination.

On Android, hold a roster unit to open the anchored **Duplicate / Delete** menu. Ordinary taps still open the datasheet. The text export preview scrolls independently, supports text selection, and keeps **Copy text** and **Save .txt** visible.

Supported allies become available from imported factions according to the army and selected detachments. Unit details identify the permission and allowance, and the roster shows an allied subtotal. The app prevents changes that increase an exceeded supported allowance, while retaining existing selections after a rules or game-size change so you can fix them.

Allied permissions are a defined set of rules in the application, principally for edition 11. Importing another faction does not automatically authorize every unit in it as an ally. See the [existing detailed allowance notes](../README.md#allies-and-transports) and [rule implementation map](architecture.md#rule-responsibilities).

## Understand points and validation

The points bar displays the calculated total and remaining points. The total can include unit size, copy-dependent pricing, allied or detachment pricing, equipment and enhancements. Two copies of the same unit can cost different amounts when the source defines copy tiers.

If any entry cannot be priced, the total represents only the known costs and the army is marked incomplete. **Points unavailable** does not mean free.

**Army validated.** means all currently implemented checks passed. These include detachment selection, Character and warlord requirements, complete points, the points limit, supported copy limits, equipment constraints, enhancements, attachment rules and supported allied restrictions. Hover over the validation area for the full issue list; the main display shows only the first few issues.

Unsupported catalogue conditions, transport passenger assignments and mission-specific requirements still need source review. Validation is not a complete game-rules engine.

## Use Play mode and rule references

**Play mode** hides the library and equipment editor and displays selected weapons, quantities and relevant equipment abilities. Alternate profiles of an equipped weapon remain together. **Build mode** restores the editor. Opening a roster starts in Build mode.

Tap a weapon profile, or select it and press Enter/Space, to read its weapon keyword definitions. Abilities remain in the sidebar; invulnerable saves and recognized Feel No Pain information appear separately from the ordinary save where available.

Attached abilities are displayed with the unit granting them. Recognized weapon effects adjust the displayed profile, with explanations of the changes. **Target is Afflicted**, where available, previews a conditional effect shared between the bodyguard and its attached characters. It is a temporary display setting, not saved battle state. Imported profiles and equipment selections remain the basis for these derived views.

Army rules combine imported references with bundled edition-specific core stratagem cards. Stratagem timing and CP costs are reference information. Play mode does not track turns, spending CP, damage or battle results.

## Save and undo changes

Roster edits save automatically. Names and limits are committed when leaving their fields, saving, navigating away or closing. An invalid name or limit must be corrected before the app can commit it.

| Shortcut | Action |
| --- | --- |
| Ctrl+K | Focus the current screen's search field where available. |
| Ctrl+N | Start a new army. |
| Ctrl+S | Commit the open army's fields. |
| Ctrl+Z | Undo a roster change outside text fields. |
| Ctrl+Y | Redo a roster change outside text fields. |

Undo/redo applies to roster edits in the current editing session. Opening a roster or applying a data update clears those stacks. It does not restore deleted armies or reverse a library import. Editing and roster undo/redo are disabled in Play mode.

## Export an army

Open the roster menu and choose **Export army — text / PDF / Yellowscribe**.

| Output | Purpose |
| --- | --- |
| Text or clipboard | Share a readable list, including validation and selected equipment. |
| PDF | Save roster and datasheet pages, with options for rule references and all weapon profiles. |
| Yellowscribe `.ros` | Save a compatible XML roster for the supported edition and available model/loadout data. |
| Send to Yellowscribe | Upload the generated roster to the external service and receive an army code for its TTS mod. |
| Export dataset in the roster menu | Save the active unit and reference library as GrimList JSON. |

Text and PDF can describe an invalid army; review their validation status. Yellowscribe availability also depends on whether the exporter can represent the selected data, and the export dialog explains missing prerequisites. Saving a file is local. **Send to Yellowscribe** sends roster content over the network.

Dataset JSON contains the library, not your saved armies. Text/PDF/ROS exports are sharing formats; this application does not provide a corresponding army import workflow for them.

## Back up and restore

Close GrimList, then copy `grimlist.db` from the data directory to a backup location. It contains saved armies, the active library and historical imported snapshots. Copy the `sources` directory too if you want to retain downloaded originals for offline repairs. Keep manually imported originals separately.

To restore, close the app, preserve a copy of the current database, then replace the database in the intended data directory with the backup. Restart using that same directory. A restore returns the entire library and armies to the backup state. See [storage and recovery details](data-and-storage.md#backups-and-recovery).

## Troubleshoot common problems

| Symptom | What to check |
| --- | --- |
| Empty library or missing armies | Verify which data directory the launcher is using. A new directory starts empty. |
| Download failure | Retry later or choose a matching-edition repository ZIP saved locally. The active library is unchanged until Apply. |
| Missing linked entries | Use the complete BSData repository ZIP, including shared catalogues and the game system. |
| Edition mismatch | Choose the matching edition and use separate data directories for different editions. |
| No datasheets or replacement controls | Refresh with Datasheets only from the original BSData source. A points-only cache cannot supply equipment definitions. |
| Points unavailable | Open the pricing repair action in Manage data. Check a cached repository, an online source or a local ZIP; review recovered entries before applying. Some entries still need importer or source support. |
| PDF import cannot start | Install/configure `pdftotext`, or use text/JSON. Scanned PDFs need OCR before import. |
| Unexpected total after an update | Review the source changes. Saved armies calculate against the newly active library. |
| Expected attachment or enhancement is missing | Check eligibility, selected detachments, restrictions and the freshness of imported metadata. |
| Save or database error | Check write access to the data directory and whether another process is holding the database. Keep the database before attempting recovery. |

Continue with [How GrimList works](architecture.md), [Data and storage](data-and-storage.md), or [Development and testing](development.md).
