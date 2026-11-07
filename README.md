# GrimList

A minimal, local Windows army builder with a dark interface. C# / .NET 10, WPF, and SQLite. No account, cloud storage, telemetry, artwork, or private GW APIs.

## Documentation

Start with the guides below for a walkthrough of the current application and its implementation. The rest of this README retains the detailed feature notes and development history.

| Guide | What it explains |
| --- | --- |
| [Using GrimList](docs/user-guide.md) | First launch, importing data, building and checking an army, Play mode, exports, backups and troubleshooting. |
| [How GrimList works](docs/architecture.md) | Project structure, application state, calculations, rule evaluation, attachments, loadouts and the source files behind each feature. |
| [Data and storage](docs/data-and-storage.md) | Dataset structure, import stages, update merging, provenance, SQLite tables and recovery. |
| [Development and testing](docs/development.md) | Build and launch commands, isolated libraries, regression checks, desktop smoke checks and data maintenance tools. |
| [Shared architecture and Android](docs/android-architecture.md) | Shared editing/import services, injected persistence, lifecycle state and extraction status. |
| [Android verification](docs/android-verification.md) | Android setup, recorded runtime acceptance and the reference-based mobile interface checks. |

## Run

Double-click **Start-GrimList.cmd**, or run:

```powershell
.\Start-GrimList.ps1
```

Requires the .NET 10 SDK. The launcher stores your database in `.local/grimlist.db` beside this repository. Launching the built executable directly uses `%LOCALAPPDATA%\GrimList` instead. Override with `--data-dir <directory>`.

The app starts with an empty points library. It does not bundle invented or potentially stale game prices.

## Build an army

The home screen contains compact army tiles, a **+** tile to create a list, and a search tile. Each army tile shows its faction, detachment and points total; its **•••** menu duplicates or deletes it. Factions are presented in coloured groups matching the supplied War Organ layout references.

1. Click **Update data** on the home screen or roster toolbar to download all army catalogues for the library's edition (11 when unspecified). Alternatively, open **Manage data**, leave **BSData + MFM** selected, choose **10th edition** or **11th edition**, then **Download data**. BSData defaults to **Points + datasheets**, including categories, army rules and detachment metadata where available. Review the faction count, changes and warnings, then **Apply update**. Downloads use GitHub; no GW account is required.
2. Alternatively, use **Choose local source**. For BSData, save the matching repository with GitHub's **Code → Download ZIP** and choose that ZIP here. This includes shared catalogues and the game system. Individual `.cat`/`.json` catalogues are supported when their required entries are available; missing dependencies are reported. GrimList JSON and MFM PDF/text imports remain available. **MFM points only** is an optional provider in the selector.
For 11th edition, online updates also fetch the [versioned MFM mirror](https://github.com/BSData/wh40k-11e-mfm). Matching unit prices take priority over catalogue prices and survive later BSData updates. Review shows coverage, observation dates and unmatched/unsupported units. Equipment, enhancement and detachment costs retain their existing catalogue sources. See [MFM unit points](docs/data-and-storage.md#mfm-unit-points).

Versioned [rules packs](docs/rules-packs.md) can override outdated datasheets and replace army rules, detachments, stratagems and enhancements through **Choose local source → Datasheets only** on both platforms. Installed overrides survive BSData refreshes. The included Dark Angels pack is explicitly **partial: Lion's statline only**; it does not contain the complete new codex.

3. Review the edition, source version/date, extracted units, changes and affected roster totals. Unknown dates/editions remain blank. Click **Apply update** to save. Downloading does not silently apply points or run on a schedule.
4. From **+**, follow the separate screens: **Select army → Detachments → Name → Roster**. Clicking a faction tile advances immediately. With imported DP costs, choose a game size and add detachments within its budget: **1,000 points = 2 DP**, **2,000 = 3 DP**, **3,000 = 4 DP**. Select any detachment to preview its rule, stratagems and enhancements, then add it. Name the army on the next screen. Back preserves your selections; a new army is saved only when you create it. Saved armies open directly from home tiles.
5. Edits save to SQLite automatically. Use **Copy list** or **☰ → Export army — text / PDF / Yellowscribe** to share a roster. **Export dataset** backs up the active unit library.

The roster uses three resizable columns: unit library, selected army units, and unit details. **+** adds a library unit; selecting a roster unit opens its composition and equipment editor on the right. Datasheets and loadout cards scroll independently. The champion has its own exclusive choices. The rest of the squad uses one counter per equipment option, with imported maximums and shared limits. Adding an upgrade replaces standard equipment; removing it restores the standard equipment. Counters disable at the cap or when no eligible models remain. Fixed equipment and remaining standard weapons update automatically. Size controls step through actual imported sizes. Unit and roster groups can collapse. The toolbar contains undo/redo, a live points bar, **Army rules**, and a **☰** menu for exports, imports and army actions. The army-rules screen also contains the editable points limit.

Unit abilities and rules appear in the right-hand sidebar, with composition and loadout below them in Build mode. The sidebar remains available in Play mode. Weapon keyword definitions are omitted from those ability cards: click/tap a weapon profile, or select it and press Enter/Space, to open the imported definitions for that weapon. Parameterized keywords such as Rapid Fire 2 and Anti-Infantry 4+ resolve to their shared rule. Added weapon keywords can use definitions from other imported datasheets of the same edition; absent definitions are labelled unavailable.

Model and weapon characteristics show only their final effective values. Values that differ from the imported source are gold and underlined on Windows and Android; unchanged values keep their usual styling. The source values remain intact internally. Active enhancement, detachment and unit rules can grant or remove weapon abilities using shared type, profile, source-category and keyword predicates. Grants are deduplicated, explicit removals take precedence, and changing the roster recalculates from the source. Conditions or selectors that cannot be resolved remain conditional rather than granting a permanent ability. Complete supported rule wording can supply missing weapon grants in older libraries; structured source restrictions take precedence.

Related ability options now appear inside their parent card in Build and Play mode and together in PDF exports. For example, **Lord of the Death Guard** contains its usage rule followed by **Diseased Influence**, **Boon of Death** and **Inflamed Reprisal**. Grouping uses the imported section names for any faction; it preserves each option's complete text. Existing libraries work without reimporting. Missing or ambiguous parent relationships leave the original abilities visible separately.

**Army rules → Core Stratagems · All armies** shows complete operative rules independently of detachment selection: 10 cards for 11th edition and 11 for 10th. Cards display CP cost, turn colour and the full WHEN, TARGET, EFFECT and applicable RESTRICTIONS text, including optional modes. The 11th-edition Overwatch card also includes the Snap Shooting procedure. Rules are bundled for offline use and appear in PDF exports; there are no reminder summaries or source-link buttons. Source metadata is retained in the embedded reference packs, using the [11th-edition](https://wahapedia.ru/wh40k11ed/the-rules/core-rules/#Core-Stratagems) and [10th-edition](https://wahapedia.ru/wh40k10ed/the-rules/core-rules/#Stratagems) references checked on 2026-10-03. Reference JSON cards with `Faction: "Core"`, `Section: "Stratagems"` and an empty `Detachment` apply to every army; matching imported cards override bundled rules. Unknown editions receive no guessed core set. Army-wide and selected-detachment stratagems appear above the core stratagems. Developers can rebuild each embedded pack from a complete plain-text reference using `python scripts/build_core_stratagems.py <10|11> <reference.txt> <output.json>`.

Concrete imported Feel No Pain values appear beside the invulnerable save as **5+++ FNP**, for example. Click the badge to read its source rule. Restriction markers are retained as `FNP*`; temporary grants in other abilities are not displayed as permanent defensive stats. Switching units refreshes the badge, and equipped-item rules follow the selected loadout.

Selected enhancements also contribute FNP through generic bearer-grant wording, including combined armour/Wounds grants. This covers Revolting Regeneration and other factions' enhancements without unit-name checks. Only the selected, eligible enhancement contributes; removal, undo/redo, detachment changes and reopening recompute the result. The badge shows the best unconditional roll alongside stronger conditional rolls marked `*`, while its reference pop-up retains every source. Bearer-only grants do not spread to attached bodyguards, and aura/selected-other-unit grants do not automatically grant the bearer FNP.

## Exporting an army

Open **☰ → Export army — text / PDF / Yellowscribe** after opening a saved army.

Exports show **Army validated.** only when the army has at least one detachment, at least one Character, exactly one eligible selected warlord, known points at or below its limit, and no unit copy-limit violations or other detected roster issues. A 1,995-point army can validate for a 2,000-point game; four ordinary Defilers cannot. Invalid drafts remain exportable with **Army not validated.** and their roster issues. The export window and text/PDF output share the roster's validation result; Yellowscribe files retain it in roster notes.

- **Text list** previews a category-grouped roster. Attached Leaders and Support appear directly beneath their bodyguard, with links in both directions. Each saved unit is counted once. Copy to the clipboard or save `.txt`; equipment, enhancements, Warlord, disposition, detachment DP, ally origin and source information are retained. Unknown points remain explicit. Text exports and copied lists omit source URLs to avoid social-media link previews. This follows War Organ's attachment-aware export approach; it is not a byte-for-byte copy of a proprietary format.
- **PDF + datasheets** saves an A4 PDF directly, with embedded fonts and selectable text. It begins with the list and follows with each roster entry's available datasheet, keeping different copies and their equipment separate. Each datasheet starts on a fresh page and long rules continue onto additional pages. Choose equipped weapons or all available profiles, and whether to include imported army/selected detachment rules and stratagems. Missing sheets are marked unavailable. Profiles use effective characteristics and weapon keywords; unresolved battlefield conditions are printed separately. No PDF printer or online service is required.
- **Yellowscribe / TTS** saves a `.ros` compatible with Yellowscribe's 10e/11e parser. **Send to Yellowscribe — get code** explicitly uploads that roster to `https://yellowscribe.link/`; copy the returned code into its TTS mod before it expires. Saving a file is offline. Missing datasheets, incomplete model profiles, or flat weapon quantities without per-model ownership require correction before export. Model counts, equipped weapons, alternate firing modes, invulnerable saves, enhancements and attachment notes are retained. Leaders remain separate units, with notes identifying their bodyguard; this does not script TTS attachment effects. The `.ros` is a Yellowscribe interchange file, not a catalogue-linked round-trip backup for editing in another builder.

The design was checked against [War Organ's release notes](https://warorgan.com/release-notes), [Yellowscribe's roster parser](https://github.com/ThePants999/Yellowscribe/blob/main/bin/roszParser.js) and [its upload service](https://github.com/ThePants999/Yellowscribe/blob/main/service.js). Tests run generated files through the actual upstream parser offline and extract the PDFs with `pdftotext`. A real uploaded code/TTS spawn requires the external service and TTS; automated tests use a local HTTP fixture rather than uploading saved armies.

For export regression artifacts, run the core suite, followed by the native smoke test described below. `python scripts/check_pdf_exports.py <test-artifact-directory>` checks the resulting PDF text, pagination, Unicode, attachment notes and option handling. `python scripts/check_yellowscribe_export.py <file.ros> <upstream-bin-directory>` runs the unmodified upstream parser/model implementation with an XML adapter; the directory must contain `roszParser.js` and `10e11eDataModel.js` from the linked repository.

BSData or optional JSON supplies the unit panel. `UnitDefinition.Category` supplies grouping; `Profile.Characteristics` is a stat dictionary; `Profile.Models` retains multiple named stat lines; `Profile.Weapons` contains weapon profiles; `Profile.Keywords`, `Profile.Abilities` and `Profile.Warnings` supply the reference sections. Dataset `ReferenceCards` can separately supply army rules, detachments, enhancements and stratagems, filtered by faction and detachment. Missing data remains visibly unavailable. No artwork or lore is imported.

**Army rules → Add detachments** opens compact choices directly on the page. Click **Add** once to select a detachment or **×** on a selected detachment to remove it. Each change saves immediately; visible **Undo/Redo** buttons restore changes. Additions that exceed the DP budget are disabled. Reducing the game size retains existing selections and flags any excess DP. DP is separate from unit points. Nonstandard points limits have no inferred DP budget. Unknown detachment costs are shown as unavailable. Army creation also has direct Add/Remove buttons alongside its rule previews.

**Army rules** shows faction-wide army rules plus the combined rules, enhancements and stratagems from your selected detachments only. The compact **Army disposition** selector lists the distinct dispositions granted by those detachments. A sole option is selected automatically; adding detachments preserves a valid choice. Removing its last granting detachment selects the sole remaining option, or prompts for a choice when several remain. Disposition survives saving, duplication, undo/redo and text export. Mission scoring is not automated. Stratagem headers are green for either turn, blue for your turn and red for your opponent's turn, with text labels as well.

BSData imports extract army-level rules and detachment metadata; stratagems absent from BSData can be supplied in a reference-only GrimList JSON (`ReferenceOnly: true`, empty `Units`). Import these with **Datasheets only**. Reference imports preserve existing units and saved armies, reject edition mismatches, and keep source URLs on rule cards. Stratagem CP remains a reference value.

Eligible roster characters have an **Enhancements** section above **Ranged Weapons**, with a button for each eligible choice showing its name, detachment and points. Click to equip or replace an enhancement; click its highlighted button again to remove it. Epic Heroes and non-characters have no enhancement options. Options come only from selected detachments and match the character's own faction, keywords and named-model restrictions, including exclusions. Attached bodyguards do not grant eligibility keywords. Unknown restriction formats stay hidden. The selected enhancement saves with the unit, adds its imported points to the roster total, and appears on the datasheet in Build and Play modes and in text exports. Play mode shows only the selected rule, with no selection buttons. Selection supports removal, undo/redo and duplication. If later rules or detachment changes invalidate a saved selection, it is retained with a warning and can be removed; missing enhancement costs make the total incomplete. Enhancement effects are displayed as rule text, not automatically applied to weapon stats.

The Death Guard 11th-edition reference pack at `tests/fixtures/references/death-guard-rules-11.json` contains nine detachments, five disposition mappings, 30 enhancements, 45 stratagems and two army rules. It combines BSData catalogue rules with the Wahapedia faction reference, including the version 1.2 faction-pack additions. That historical faction-specific converter has been replaced by the generic `GrimList.Data references` acquisition pipeline. It reproduces the 45 saved stratagems with the same parser used for other factions; see [Reference acquisition](docs/reference-acquisition.md) for commands, source access limits and current partial coverage. `import-rules <data-directory> <rules.json>` backs up the current dataset and rosters before importing and verifies that units and saved armies are unchanged.

Loadouts are saved per roster entry. Existing selections are preserved and displayed as grouped option quantities. New units use imported defaults. Choices survive restart, unit/army duplication and undo/redo, and appear in text exports. Resizing retains existing model choices, adds defaults for new models and reports selections that exceed the new limits. Source Notes and Unit Notes sections are removed from the builder and Play mode. Existing saved notes remain in storage and exports. Additional equipment costs are not applied from reference text.


**Play mode** in the roster toolbar hides the unit library and equipment editor, expands the datasheet, and displays only selected weapons with their quantities and relevant equipment rules. Alternate profiles of an equipped weapon (for example, standard and supercharge) stay together. **Build mode** restores all available profiles and the editor without changing equipment. A unit with no saved loadout shows a configuration prompt rather than every possible weapon.

Existing libraries need a **Datasheets only** refresh from the original BSData catalogue/ZIP to obtain automatic loadout options; the old flat weapon cache cannot reconstruct replacement rules. Until then, set weapon quantities manually in the editor. If a later datasheet changes the available selections, saved choices are retained and flagged for review; **Reset to defaults** adopts the new options and can be undone. The importer and UI have synthetic coverage for shared groups, defaults, per-five limits, alternate firing profiles, persistence and Play mode; the cached Blightlord Terminator loadout is also covered by an optional catalogue check, including its two-launcher cap.

Leaders can select a **Bodyguard unit** from eligible roster entries; squads also offer an attachment picker for available Leaders and Support units. Eligibility comes from the explicit bodyguard list in imported Leader/Support abilities. Duplicate squads are numbered. Attached characters sit below their squad in the roster, with role-labelled links between their datasheets available in Play mode. Attachments save automatically, support undo/redo and text export, and remap when duplicating an army. Duplicating a single leader leaves its copy unattached; removing a bodyguard detaches its characters. Imported rules can permit a Leader plus Support, two Leaders on a bodyguard, or a named partner exception, in either attachment order. Supported restrictions include squad size, distinct Leaders, at most one Command Squad, and named bodyguards/partners. Attachments are limited to two characters; unrecognized conditional exceptions do not grant extra slots. The original attachment rules remain available for review, and changed eligibility is flagged without silently discarding a saved attachment.

Attached units display their leaders’ “while leading” abilities with the granting unit’s name. Supported weapon clauses update the displayed profiles in Build and Play modes, including granted weapon keywords and Blistering Fusillade’s ranged Strength/AP bonus. **Target is Afflicted** previews target-dependent effects for the attached unit; it starts off and is shared when navigating between bodyguard and leader. Blistering Fusillade also activates for a bodyguard starting size of five or more without a leader. Changed stats are highlighted, with each weapon showing the source and original/new values. These are derived views: imported profiles and saved equipment are not overwritten. Unsupported conditions, re-rolls, defensive effects and other characteristics remain readable ability text rather than guessed profile changes.

The top-right roster menu groups **New army**, **Export army**, **Manage data**, **Export dataset** and **Delete army**. Updates and Army rules use their toolbar buttons; list copying is in Export army, and army duplication stays on the home screen. Right-click an individual roster unit (or use its keyboard context menu) and choose **Duplicate unit**. This targets the clicked unit, retains its equipment, leaves the copy unattached, and supports undo/redo. Copy limits and Play mode restrictions also apply to this action.

Shortcuts: `Ctrl+K` search, `Ctrl+N` new army, `Ctrl+S` save, `Ctrl+Z` undo and `Ctrl+Y` redo (outside text fields). Army names and limits save on leaving their fields, saving, or closing the app.

The unit library has independent **Hide Legends** and **Hide Crucible** filters. They match labels in imported unit names, categories and keywords, combine with search and **Fits remaining**, and leave existing roster entries intact. Both start off and retain their selection while the app is open. The BSData importer currently excludes Legends at import time; turning off the filter shows only units already in the library.

Invulnerable saves appear as a separate badge underneath SV, matching `pictureReferences/InvulSave.png`. The value comes from the imported characteristic; absent values stay hidden. Multiple model profiles retain their own save badges.

Choose a warlord from the roster's **Warlord** selector. Selection is saved by roster entry, appears beside that entry and in text exports, and survives army duplication and undo/redo. An imported **Supreme Commander** ability or unconditional BSData Warlord minimum automatically selects that unit and takes precedence over a manual choice. Mortarion uses the latter representation in BSData. If several units require the role, choose among them; removing the chosen unit clears the selection unless another sole mandatory warlord remains. Existing libraries need a one-time **Datasheets only** refresh to capture BSData's newly supported Warlord constraints; saved prices are preserved.

Detachment choices and DP costs come from dataset metadata. Factions without DP metadata retain the single-name setup, including a manual entry when needed. Existing databases migrate automatically and retain their original detachment as a selection. Multiple selections are saved, duplicated and exported with the army. Closing or cancelling setup never creates a partial army.

## Points and updates

- Updates create immutable library snapshots. Each unit has separate `PointsSource` and `ProfileSource` metadata: document, version/date, edition, import timestamp and URL. Downloads record a SHA-256 content hash under the data directory's `sources` folder. BSData caches points and datasheets in `dataset.json`, alongside warnings; MFM caches its original documents too.
- Points imports preserve profiles, categories, copy limits, reference cards, detachments and other factions. Datasheet imports preserve points. Partial imports update only named units. **Complete faction points** also clears prices for absent units within the incoming factions; their profiles and saved roster entries remain available and show unknown prices. Automatic MFM downloads select this option; local file imports default to partial updates.
- Units receive permanent IDs. Existing name-based rosters migrate in the same database transaction as the update. Case and punctuation changes match automatically. Real renames use the exported `Id` or an explicit `Aliases` entry such as `DEATH GUARD|OLD UNIT NAME`. Conflicting IDs/aliases are rejected; no fuzzy name guessing occurs.
- Changes include added, removed, and altered sizes/prices. Saved lists always calculate against the active source; missing units/sizes are marked unknown, never treated as a complete zero-cost army.
- Copy-dependent model prices are supported (`FromCopy` in JSON). Copies are counted in roster order; removing a unit recalculates remaining copies.
- Downloads finish before a candidate library is shown. Cancellation and HTTP failures leave the library untouched. Review edits invalidate Apply, and a concurrent library change requires a fresh preview. A known edition mismatch is rejected; backwards dates and unknown editions are highlighted.
- A source date is provenance, not a guarantee of freshness. Application network access is blocked by the development environment's proxy, so a successful live download remains unverified. HTTP fixtures cover the download and failure paths; reduced real-schema fixtures cover BSData pricing. A full live Death Guard catalogue has not yet been validated end to end here.

## BSData community source

The adapter reads [BSData 10e](https://github.com/BSData/wh40k-10e) BattleScribe XML or [BSData 11e](https://github.com/BSData/wh40k-11e) JSON. It requests the repository's current `main` commit, downloads an archive pinned to that commit, and resolves shared entries by ID. The recorded date is the commit date, not an official points publication date. Local files record catalogue revision and content hash, leaving the unknown commit date blank.

The adapter discovers playable army catalogues in the repository and imports their native units, model-count price bands and supported copy surcharges. Shared libraries and the game system supply definitions without appearing as separate armies. Space Marine chapters inherit common marine units, with chapter overrides taking precedence. Unrelated allied catalogue roots do not become unconditional native choices. Existing Death Guard identities are preserved. It retains the largest model count in each equal-price band, such as 5/7/10 models. The library and roster display **Epic Heroes → Characters → Battleline → Dedicated Transports → Vehicles → Others → Allies**, without changing the underlying roster order used to calculate copy surcharges. Only the Dedicated Transport keyword grants that category; Transport alone does not. Allies remain grouped under Allies. Monsters and other units without a matching keyword appear under Others.

Army and detachment rules are assigned to the owning faction, including linked detachment definitions and multiple dispositions. BSData enhancement cards without readable bearer restrictions remain reference-only. If an existing supplemental card contains eligibility, the importer retains that complete card and its source with a review warning. Stratagems absent from BSData still require supplemental reference data. Complex faction-specific legality and conditional pricing are not inferred.

Same-name entries within one catalogue are retained as separate variants, with stable source-ID suffixes and a review warning. Each keeps its own points, datasheet and equipment. Unconditional source name/visibility modifiers are applied before resolving names; hidden entries do not conflict with visible peers. Chapter entries still take precedence over same-name inherited units. Conditional eligibility remains subject to source review.

Nested links to the same shared definition are allowed when their local children differ. This handles the finite nested Warlord links on Chaos Cerastus Knights Castigator and Atrapos without rejecting their prices. Identical resolved ancestors still trigger cycle protection, and paid nested selections remain subject to pricing validation. Base points and later-copy surcharges are imported independently.

For a backed-up command-line import, run `dotnet run --project tools/GrimList.Data -- import-bsdata-all .local 11` (latest GitHub revision), or append a repository ZIP path to import offline. The command saves the previous dataset and armies under `.local/backups`, applies a combined import, and verifies that saved armies are unchanged. Use a separate data directory for fixtures and historical sources.

Datasheets include characteristics, keywords, ranged/melee weapon profiles, weapon keywords, abilities and linked shared rules. Multiple unit/model stat lines are retained individually. BattleScribe XML characteristic text and JSON `$text` values use the same reader. Repeated profiles are deduplicated, unconditional display overrides are applied, and missing links or conditional changes generate visible warnings. Weapon profiles show available alternatives; they do not imply that every option is equipped. Loadout import retains shared equipment groups, default selections, fixed equipment, model composition, minimum/maximum selections and supported model-count conditions or per-N-model limits. Nested equipment alternatives, complex conditional groups, conditional visibility, and unsupported scopes still need source review; warnings identify these limitations. Full army legality remains separate.

Supported 11th-edition allied allowances are included in army calculation; see below. Crusade rules are not included. Selected enhancement points are included; detachment DP costs are tracked separately. **Points only** preserves existing datasheets/categories; **Datasheets only** updates profiles/categories while preserving points. **Points + datasheets** updates both. If no datasheet can be read for a unit, its existing profile is retained and the review explains the omission. A partial readable datasheet replaces that unit's old profile with warnings. BSData aliases retain identity through subsequent community catalogue renames. Reimport the original catalogue/ZIP to enrich a library previously imported with points only; old points-only caches cannot reconstruct missing datasheets.

Unsupported conditional costs, paid equipment, missing dependencies and unsupported compositions produce an explicit `PointsWarning` and no prices for the affected unit. Applying that result clears its previous prices rather than leaving a stale price looking current. If no units can be priced, the import fails. Community imports default to partial faction updates because their scope excludes some entries; prices for units absent from this scope are retained with their original source metadata. Read the warnings and source versions in the review before applying.

BSData is maintained by volunteers and may lag rules releases. Downloads are on demand and require review before applying. Local imports and cached datasets work offline; no automatic background scheduler is installed.

If a unit shows **Datasheet not imported**, its saved entry has no profile. Older points-only imports remain points-only after an app update. Click **Import datasheets** in the unit panel, then **Download data** or choose the original BSData ZIP, preview and apply. This route keeps **Datasheets only** selected when loading the catalogue, so current prices are preserved while profiles and categories are added. An old `points.json` cache cannot supply the missing profiles.

## Repair unavailable prices

Open **Manage data → Repair missing points** to audit the entire library. **Find fixes online** downloads the matching-edition BSData repository and retries the importer for every failed unit. **Check cached source** retries a saved repository offline; **Choose source ZIP** accepts an offline repository download. Older caches contain only normalized results, so they need one fresh download or the original ZIP before they can be retried.

The review lists recovered native/allied prices, copy surcharges, remaining failures and affected roster totals. Crucible units and battle-only references are skipped and counted separately. **Apply recovered prices** updates only failed units with unambiguous identities and supported pricing. It refreshes their loadout definitions when available and verifies default loadouts can be priced; healthy units, other datasheet content, detachments and saved army selections are preserved. Existing selections that no longer match refreshed options are flagged for review. A dataset/army backup and repair report are written under `.local/backups`. A concurrent library update invalidates the preview. Cancellation and failed checks leave the library unchanged. **Export report** saves every unresolved unit and its reason for a bulk source/importer investigation.

Imperial Agents' catalogue-dependent native/allied prices are stored separately and used in the library, size picker, totals and exports. The importer supports named model/group counts with fixed offsets, variable group totals, nested specialist groups, exclusive composition bundles, exact specialist substitutions, grouped per-model costs, unit-category copy surcharges and fixed paid weapon bundles. Boarding Actions and Crusade force conditions do not override Army Roster prices or model limits. Chapter-specific native taxes do not generate allied-price failures for Space Marines, which have no implemented ally permission.

Mandatory paid detachment upgrades, optional model costs and model-selection price formulas are included in roster totals. Mixed units sharing a source copy-count category share its surcharge. Optional enhancements remain separate purchases. Unconstrained weapon containers allow every mandatory weapon; explicit selection caps remain enforced. Battle-only reference entries are retained as source data but excluded from roster selection.

Supported model-limit queries retain the source scope, selection ID and descendant-counting flag. They evaluate named model/group counts against the actual loadout, including during resizing and validation. This covers additional Wolfquads and Neurogaunt Nodebeasts. Unsupported selection scopes and bundle dependencies remain explicit failures. Missing ID-shaped pricing scopes identify the affected unit, catalogue and missing ID in the repair report.

Paid equipment and copy surcharges still require supported source data. The repair does not infer prices from names or copy prices from another faction: missing source costs, unresolved references and unsupported loadout/roster-dependent conditions remain explicit failures. Additional source-rule support can then be retried for all affected units with the same action. An unresolved result means importer **or** source support is needed; it does not necessarily mean the source has no points.

For a reproducible offline audit, run `dotnet run --project tools/GrimList.Data -- audit-pricing <data-directory> <cached-repository.zip> <report-directory>`. It writes the newly parsed library, per-unit repair report and default-loadout totals without applying changes. Add `--apply` to apply recovered prices through the same backup and stale-preview safeguards as the UI.

## Allies and transports

The library and roster group units as **Epic Heroes, Characters, Battleline, Dedicated Transports, Vehicles, Others, Allies**. Native units with the Dedicated Transport keyword appear under Dedicated Transports, including previously imported vehicles. Other transports remain in their normal category. Allies always appear under Allies, including their transports. Their original identity, faction, prices and datasheets remain intact.

Add, quick-add and Duplicate enforce a per-datasheet limit of **3 at 2,000 points**, doubled to **6 for Battleline or Dedicated Transports**. In 11th-edition Incursion (1,000 points), these limits are **2 and 4**. Epic Heroes remain limited to one; a narrower explicit `MaxCopies` limit is also respected. Other/custom game sizes retain the 3/6 ceiling rather than inferring extra slots. Model count and copy-based points surcharges do not change the number of permitted units. See the [11e Muster Your Army rules](https://wahapedia.ru/wh40k11ed/the-rules/core-rules/).

Imported detachment-dependent category modifiers supply effective keywords without changing the shared datasheet. For example, selecting Shamblerot Vectorium grants Poxwalkers Battleline, moves them from Others to Battleline, and doubles their copy allowance. The library, roster, datasheet keywords and exports use these effective keywords. Removing that detachment or reducing the game size retains existing selections, flags excess copies and blocks further additions. Undo/redo and reopening recompute the same rules. Unsupported conditions produce source-review warnings. Older libraries can recognise explicit unconditional keyword grants in retained detachment rules; refreshing datasheets imports the structured rules.

With an 11th-edition dataset, eligible allies appear automatically from imported factions. Select the required detachment to unlock detachment-specific units. Unit details show the granting rule, allowance and source; the roster shows an allied subtotal. New additions, duplications, size increases and enhancement purchases cannot increase an exceeded supported allowance. Removing units or changing detachments/game size retains the list and displays any resulting problems. Allies save, export and support undo/redo like native units.

The implemented muster rules were checked against the following 11th-edition references on 2026-10-02. Three-number allowances below correspond to 1,000/2,000/3,000-point games. Intermediate limits use the next published size; above 3,000 points, size-dependent allowances are flagged as unspecified.

| Permission | Implemented allowance |
| --- | --- |
| [Freeblades](https://wahapedia.ru/wh40k11ed/factions/imperial-knights/) / [Dreadblades](https://wahapedia.ru/wh40k11ed/factions/chaos-knights/) | Imperium/Chaos hosts respectively: either one Titanic model or up to three Armiger/War Dog models. No allied warlord or enhancement. |
| [Assigned Agents](https://wahapedia.ru/wh40k11ed/factions/imperial-agents/) | Imperium hosts: separate Retinue and Character limits of 1/2/3 units, plus Requisitioned limits of 1/1/2. Dedicated Transports are separate; passenger requirements remain a warning. Assassins cannot be warlords. |
| [Daemonic Pact](https://wahapedia.ru/wh40k11ed/factions/chaos-daemons/) | Chaos Space Marines and Chaos Knights: 250/500/750 points. For each god, non-Battleline allied units cannot outnumber Battleline allies. No allied warlord or enhancement. |
| [Tallyband Summoners](https://wahapedia.ru/wh40k11ed/factions/death-guard/), [Khorne Daemonkin](https://wahapedia.ru/wh40k11ed/factions/world-eaters/), [Changehost of Deceit](https://wahapedia.ru/wh40k11ed/factions/thousand-sons/), [Carnival of Excess](https://wahapedia.ru/wh40k11ed/factions/emperor-s-children/) | Their corresponding daemon legions: 500/1,000/1,500 points. These use their own detachment allowance, not the generic Daemonic Pact balance rule. |
| [Corsairs and Travelling Players / Reaper's Wager](https://wahapedia.ru/wh40k11ed/factions/drukhari/) | Drukhari: Harlequins and Anhrathe at 250/500/750 points; Reaper's Wager replaces this with Harlequins only at 500/1,000/1,500. |
| [Brood Brother Auxilia / Final Day](https://wahapedia.ru/wh40k11ed/factions/genestealer-cults/) | Specified Astra Militarum / Vanguard Invader Tyranid allies at 500/1,000/1,500 points, with the published excluded keywords. |
| [Questor Forgepact](https://wahapedia.ru/wh40k11ed/factions/imperial-knights/) / [Iconoclast Fiefdom](https://wahapedia.ru/wh40k11ed/factions/chaos-knights/) | Specified Mechanicus units / Damned units, up to 500 points. |
| [Shadow Legion](https://wahapedia.ru/wh40k11ed/factions/chaos-daemons/) | Listed Chaos Space Marine units, up to 500/1,000/1,500 points; roster validation flags excluded Daemon Princes and Epic Heroes. |

Allied point allowances include purchased enhancements. Detachment-specific allies only receive enhancements explicitly supported by imported bearer restrictions from that detachment. Leader attachments still require an explicit imported bodyguard list. Generic Daemonic Pact does not unlock daemons for Death Guard; use Tallyband Summoners and the Plague Legions entries.

This is a defined set of muster checks, not complete game adjudication. Passenger assignments, changes to battle abilities, and special keyword/attachment exceptions such as Disciple of Khorne are not automated. Additional routes such as Titans and Ynnari require separate verified support. Missing factions require importing their data; missing prices remain **Points unavailable** and cannot be guessed from another unit. These rules are not applied to 10th-edition libraries. A read-only catalogue audit is available with `dotnet run --project tools/GrimList.Data -- inspect-allies <dataset.json>`.

## PDF support

PDF extraction uses the installed `pdftotext` executable from Xpdf or Poppler. This machine has compatible copies in Git for Windows and dnGrep. GrimList detects those locations and PATH, or accepts:

```powershell
$env:GRIMLIST_PDFTOTEXT = 'C:\tools\pdftotext.exe'
.\Start-GrimList.ps1
```

The extractor is not bundled. No installation is needed for TXT/JSON/pasted-text imports. The parser handles ordinary model-count points rows and reconstructs columns using their positions. It excludes enhancement, wargear and detachment prices; unusual compositions are reported as skipped. Scans need OCR beforehand. A synthetic two-column PDF is covered by an end-to-end extraction test; an official MFM PDF has not yet been validated in this environment. Always review import results.

## Validation scope

Attachment exceptions are detected automatically from each unit's Leader/Support ability across all factions, without a list of special unit names. Imports store structured `Profile.AttachmentRules`: role, bodyguard targets, Leader + Support permission, additional-attachment exceptions and bodyguard restrictions. Validation checks the complete proposed group independent of selection or roster order. Either character can authorize the second attachment; the other character need not carry an exception, and the bodyguard's global Leader limit remains unchanged. Named partners, bodyguard targets, duplicate bans, size requirements and Command Squad restrictions still apply. Older libraries compile the same rules on demand; source changes invalidate stale semantic data.

Detection supports named partners and “even if one other Leader unit has already been attached,” including the parenthetical ban on duplicate Leaders. It tolerates source formatting, line breaks and an omitted final full stop. Direct abilities and shared profiles, rules and info groups are imported with the exception text intact. Bodyguard constraints with `field: associations` are also retained, together with modifiers targeting their IDs. Their `set`, `increment` and `decrement` operations evaluate association counts by source ID, including numeric comparisons and nested AND/OR groups. Unsupported condition grammar requires source review.

In the cached 11th-edition Death Guard catalogue (revision 17, repository `cc1830fb`), Plague Marines have a base Leader maximum of one. A structural modifier raises the effective maximum to two when the proposed group includes one of the specified characters, including Biologus Putrifier. The permission lives on the bodyguard, even though the character's Leader text only lists Plague Marines. The base maximum remains one. Developer diagnostics remain available through the data tool.

Both desktop attachment pickers evaluate the candidate together with all characters already attached to the selected bodyguard. Selecting a picker item attaches immediately; there is no separate Attach button or one-Leader capacity filter. Only eligible choices are offered; the builder omits the attachment diagnostic panels. Desktop regression checks use the actual revision-17 import with 10-model Plague Marines, Plaguecaster and either Biologus or Tallyman, in both orders through both pickers. They cover ordinary Leader rejection, removal of the qualifying character, undo/redo, persistence, reopening and Play mode. Catalogue tests also cover Foul Blightspawn and every imported squad size (5, 7 and 10).

Existing libraries require refreshed structural metadata. Reimport the original ZIP with **Datasheets only**, or use `dotnet run --project tools/GrimList.Data -- refresh-associations <data-dir> <cached-repository.zip> <report-dir>` to preview a metadata-only refresh. Add `--apply` to back up the library and apply the refresh while retaining prices, other profile fields and saved armies. Only units whose profile provenance matches the archive are updated.

Army validation requires a detachment (including libraries without DP metadata), a Character, exactly one eligible selected warlord, complete points at or below the selected limit, and compliance with the applicable unit copy limits. It also checks missing units/sizes, faction and game mismatches, supported detachment keyword changes, detachment DP budgets, enhancement allowances and bearer eligibility, leader attachments and the allied rules listed above. Missing prices prevent validation. Warlord candidates come from imported character categories/keywords and mandatory-warlord metadata; automatic mandatory selection still happens when editing or saving, while validation and exports inspect the selected roster without silently changing it. The status covers these implemented checks. Unsupported conditional restrictions, further ally exceptions, complex equipment constraints and mission-specific requirements still require source review.

## Dataset format

This example uses deliberately fictional test data; it is not a current Warhammer points source:

```json
{
  "Game": "Warhammer 40,000",
  "Source": {
    "Name": "Synthetic example — not game data",
    "Version": "1.0",
    "Date": "",
    "Url": ""
  },
  "Detachments": [{ "Faction": "Example faction", "Name": "Example detachment" }],
  "Units": [{
    "Faction": "Example faction",
    "Name": "Example squad",
    "MaxCopies": 3,
    "Options": [
      { "Models": 5, "Points": 80, "FromCopy": 1 },
      { "Models": 10, "Points": 150, "FromCopy": 1 },
      { "Models": 5, "Points": 95, "FromCopy": 3 }
    ]
  }]
}
```

`MaxCopies` is optional and can tighten the standard muster limit described above. Every size needs a base price (`FromCopy: 1`). Source dates use `YYYY-MM-DD` or an empty string.

Profiles-only JSON uses `Options: []` and a populated `Profile`; import it as **Datasheets only**. This can be done before importing any points. For renamed units, preserve the `Id` from an exported dataset. `Source.Edition` should be the edition number, e.g. `10`; leave it blank when unknown. Missing profiles/prices remain visibly unavailable.

## First real sample

The `samples` folder contains three **historical, not current** Death Guard units: Typhus, Poxwalkers and Death Guard Rhino. It is not installed into the live library automatically.

- `death-guard-historical-profiles.json`: factual characteristics, weapon profiles and paraphrased equipment notes from the official December 2024 index, with a per-unit PDF page link. No artwork, lore or full ability text.
- `death-guard-historical-points.json`: model-count prices from official MFM version 2.4, March 2025, page 18.
- `death-guard-historical-mfm.txt`: the same three price entries, for testing text extraction. Select Death Guard if needed and enter its historical source metadata when loading text. The JSON already includes it.

Use a separate test library for this sample, for example `artifacts/GrimList/GrimList.exe --data-dir .local/sample-library`. Import the profiles JSON, then the points JSON in **Points only** mode, with **Complete faction points** unchecked. The smallest size of each unit totals 205 historical points. The regression suite verifies both import orders, source preservation and roster calculation. Publication days are unknown, so dates are deliberately blank; the source versions record the archival months.

Equipment notes describe available choices but do not enforce them. Current datasheets can now be extracted from BSData as described above. This historical sample is an import/roster demonstration, not a complete or current faction pack.

## Development and checks

Current community-test packages are version **0.2.20**. See
[community testing instructions](docs/distribution/community-testing-0.2.20.md)
for installation, updates, testing priorities and bug reports. Developer/agent
handoff, source provenance and release procedures are in [AGENTS.md](AGENTS.md).

Release builds use the existing project tree and reuse its build directories.
Run `scripts/Publish-StandaloneAndroid.ps1` for APKs, or
`scripts/Publish-StandaloneWindows.ps1` followed by `scripts/Build-WindowsInstaller.py`
for Windows installers. After successful package verification, older numeric release
folders in the same output channel are deleted automatically. Failed builds retain
the previous release. Do not create per-task source copies or retain temporary test
databases. See `AGENTS.md` for the build-storage policy.

Run PowerShell build scripts with `powershell -NoProfile -ExecutionPolicy Bypass -File <script>`.
Retention checks: `scripts/Test-BuildRetention.ps1`. These use a disposable fixture;
the cleanup routine never targets `.local` or saved armies.

```powershell
dotnet build src/GrimList.Desktop/GrimList.Desktop.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Regression.ps1
```

- `src/GrimList.Core`: shared data models, validation, calculation, BSData interpretation and import parsing.
- `src/GrimList.Application`: shared editing session, history, lifecycle state and import workflows.
- `src/GrimList.Platform` and `src/GrimList.Persistence`: platform contracts and the common SQLite schema/repository.
- `src/GrimList.Platform.Windows` and `src/GrimList.Desktop`: Windows adapters and the existing WPF frontend.
- `src/GrimList.Mobile`: minimal MAUI Android frontend and Android adapters; build/device verification is pending the missing workload.
- `tests/GrimList.Tests`: dependency-free regression harness; synthetic fixtures and test databases stay under `.local/tests`.

The Windows SQLite adapter calls `winsqlite3.dll`; the Android adapter uses Android SQLite through the same repository contract. The desktop solution requires no external NuGet packages. The separate mobile solution requires MAUI Controls and the `maui-android` workload. Both frontends reference the same rules and application services.

For a native render/add/undo smoke check against a separate test database:

```powershell
dotnet run --project src/GrimList.Desktop/GrimList.Desktop.csproj -- --data-dir <test-directory> --smoke-test <absolute-png-path>
```

Back up `grimlist.db` while the application is closed to preserve armies and all historical source snapshots. Automatic downloads are archived under `sources`; retain the originals of manually imported files yourself.

## Next refinement

The complete cached all-army BSData revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908` was audited offline: the first pass recovered 382 of 555 previously unresolved entries; a second pass recovered another 44, including chapter copies. All 129 default squad sizes across those 44 entries have calculable totals and no loadout validation errors. That pass left 11 unresolved entries, with 113 Crucible entries and 5 battle-only references skipped. Its report is `.local/pricing-noncrucible-final/report.json`; it covers unsupported conditional compositions, paid bundles, modifier forms and broken scope references. The second pass was checked with 126 automated tests and the native desktop smoke test. Automated multi-army fixtures cover discovery, library inheritance, chapter overrides, shared detachments, cancellation, persistence and the native import UI. Collection and battle tracking are later milestones from the original requirements.

The subsequent proposal review added selected-model constraint queries and recovered Atalan Jackals and Neurogaunts. Nine unresolved imported entries remain in `.local/pricing-proposed-final/report.json`, including one Crusade-only Headtakers variant; its regular Army Roster variant already has prices. All five newly recovered default squad sizes passed loadout and price checks, alongside 129 automated tests and the native desktop smoke test. See `docs/history/bsdata-importer-review.txt` for the comparison with the proposed architecture and outstanding work.
