# GrimList data and storage

GrimList keeps a shared active library and separate saved armies. The library supplies unit definitions, prices and rules; armies store selections that refer to those definitions. Applying a library update can therefore change an existing army's calculated total without changing the units the user selected.

This guide explains the implemented formats, import path and persistence behavior. See [Using GrimList](user-guide.md) for the interface and [How GrimList works](architecture.md) for calculations and rules.

## MFM unit points

For 11th edition, **Update data** downloads the BSData catalogues and the separate [MFM snapshot repository](https://github.com/BSData/wh40k-11e-mfm). Each download is pinned to a commit. The MFM archive's faction manifest is checked before any prices are offered for review. The existing direct official HTML/PDF importer remains the 10th-edition provider.

MFM unit-size prices are matched by faction and normalized unit name, with matching model sizes. Copy tiers (such as the third unit costing more) are retained. Space Marine chapters without a separate MFM page use the Space Marines page. Unsupported composition prices, add-ons, ambiguous matches and missing units retain catalogue prices and produce review warnings. MFM-only updates do not clear units absent from the snapshot.

`PointsSource` records the MFM version, per-faction observation date, archive hash and pinned source URL independently of `ProfileSource`. Compatible MFM prices survive subsequent BSData imports and JSON/database round trips. Older MFM observation dates do not overwrite newer saved MFM prices. A changed catalogue composition invalidates incompatible saved prices with a warning. Manual non-BSData points imports remain available for deliberate corrections.

This overlay changes base unit prices only. Equipment costs, allied prices, composition formulae, enhancement/upgrade costs, detachment costs and reference rules retain their existing import paths. A mirror download failure is shown in the review; compatible saved MFM prices are preserved, while other units use BSData. Cancellation does not apply anything. The mirror may lag the official website, so the review displays source observation dates rather than claiming the prices are current.

Original MFM archives and parsed points are cached in `sources/mfm-11-<commit>`. Windows and Android use the same download, matching and precedence code. Applying still requires the normal in-app review.

## Data directory

| Path inside the data directory | Contents |
| --- | --- |
| `grimlist.db` | SQLite database containing armies, entries, source records and library snapshots. |
| `sources` | Downloaded originals and normalized data, when created by the download clients. |
| `sources/bsdata-<edition>-<commit>` | A BSData download's `repository.zip`, `source.json`, `dataset.json` and `warnings.json`. |
| `temp` | Working directory used during PDF text extraction. |
| `backups` | JSON backups and reports created by specific repair/maintenance operations. |

The source launcher uses the project's `.local` directory. The executable defaults to `%LOCALAPPDATA%\GrimList`; `--data-dir` overrides it. Each directory has an independent active library and saved armies. Use separate directories for different game editions or experiments.

There is no ordinary application settings file governing this layout. See [`App.xaml.cs`](../src/GrimList.Desktop/App.xaml.cs), [`Start-GrimList.ps1`](../Start-GrimList.ps1) and [`ArmyStore.cs`](../src/GrimList.Persistence/ArmyStore.cs). The mobile adapter supplies its app data directory through the same platform contract.

## Dataset format

The authoritative C# contracts are [`Models.cs`](../src/GrimList.Core/Models.cs) and the records in the feature modules. `DataJson` uses `System.Text.Json`, accepts case-insensitive property names, and writes indented JSON for exports. The dataset's `SchemaVersion` describes its structured rule data; the internal database storage version is separate.

This is a minimal synthetic dataset for understanding the format. Its units and prices are fictional:

```json
{
  "Game": "Warhammer 40,000",
  "Source": {
    "Name": "Synthetic documentation example",
    "Version": "1.0",
    "Date": "",
    "Url": "",
    "Edition": "11"
  },
  "Detachments": [
    { "Faction": "Example faction", "Name": "Example detachment" }
  ],
  "Units": [
    {
      "Faction": "Example faction",
      "Name": "Example squad",
      "MaxCopies": 3,
      "Options": [
        { "Models": 5, "Points": 80, "FromCopy": 1 },
        { "Models": 10, "Points": 150, "FromCopy": 1 },
        { "Models": 5, "Points": 95, "FromCopy": 3 }
      ]
    }
  ]
}
```

Every priced size needs a base option with `FromCopy: 1`. The copy threshold is the first copy receiving that price, not a maximum quantity. `MaxCopies` is an optional constraint independent of price tiers. This example has no character profile or warlord metadata, so importing it does not establish a fully valid army.

`Dataset.Validate` checks source names/versions, optional ISO dates, unique unit keys and price bands, valid model counts and supported structured metadata. Profiles-only imports use `Options: []` and a populated `Profile`. A unit's empty price list can also represent an unresolved import with a `PointsWarning`.

Reference-only packages set `ReferenceOnly: true`, supply `Units: []`, and include nonempty `ReferenceCards`. They require an existing unit library and a profiles/combined import mode. Each card identifies its faction, section, name, text, optional detachment/cost and optional source. Costs on reference cards become purchasable only where the relevant feature recognizes them.

For complete, round-trippable data, export an existing dataset and preserve its fields rather than recreating complex profile or loadout structures by hand.

## Identity and provenance

`UnitDefinition.Key` uses `Id` when populated and otherwise falls back to the normalized faction/name key. `DatasetMerge` assigns stable IDs to definitions without them and retains aliases. Matching uses explicit IDs and conservative normalized names/aliases; a semantic rename needs a stable ID or alias. BSData source-entry aliases help retain identity across inherited and overridden catalogue entries.

`ArmyEntry.UnitKey` resolves through `Dataset.FindUnit`, which recognizes current keys, name keys and aliases. Applying an import migrates recognized old roster keys and unit names transactionally, preserving entry IDs and selections.

A dataset-level `Source` describes its latest merged import. Individual units also have `PointsSource` and `ProfileSource`, because prices and datasheets may have come from different updates. Reference cards and detachment definitions can carry their own source. Use these per-item fields when explaining where a price or rule came from.

`Date` is a source/publication date or blank when unknown; `ImportedAt` is the local import timestamp. `Edition` is a string such as `"10"` or `"11"`. `ContentHash` identifies acquired content. Unknown editions produce warnings; known incompatible layers are rejected by merge checks. Keep editions in separate libraries even where metadata is incomplete.

## Import pipeline

The normal desktop workflow has five stages:

1. **Acquire.** Read a local file/pasted text or download a source. The source clients return a dataset, warnings and cache location.
2. **Parse.** Normalize source formats and extract the supported units, prices, profiles and reference metadata.
3. **Merge.** `DatasetMerge.Prepare` validates the incoming package, matches identities and updates the selected layers without writing the library.
4. **Preview.** `ImportWindow.Preview` compares prices, profiles and references and recalculates saved armies against the candidate library.
5. **Apply.** `ArmyStore.ApplyImportAtRevision` checks the preview's library revision and commits a new active library in one SQLite transaction. Repository implementations without revision support retain the full-snapshot comparison fallback.

Changing import fields invalidates the preview. If another operation changes the library between preview and apply, the transactional revision check rejects the stale update and requires a new preview. Saving armies or editing context does not invalidate the library revision.

### BSData

[`BsDataClient`](../src/GrimList.Application/BsDataClient.cs) resolves the configured edition repository's `main` commit through GitHub, then downloads a ZIP pinned to that SHA. All shared files therefore come from one revision. It records the commit/date and SHA-256 hash and caches both the archive and parsed result. HTTP calls have timeouts, response limits and cancellation support.

[`BsDataDocument`](../src/GrimList.Core/BsDataDocument.cs) reads archives and normalizes supported XML/JSON catalogue structures. The UI expects CAT sources for edition 10 and JSON catalogues for edition 11. `BsDataImporter.ImportAll` in [`BsDataArmies.cs`](../src/GrimList.Core/BsDataArmies.cs) discovers army catalogues and imports each with shared definitions available.

The importer resolves entry links, shared profiles/rules and native catalogue inheritance. Chapters can override inherited same-name entries. Linked allied definitions do not automatically become unrestricted native choices. Source variants are preserved with distinct labels where needed. Legends entries are excluded during import.

| Importer files | Extracted information |
| --- | --- |
| `BsDataProfiles`, `BsDataReferences` | Characteristics, weapons, abilities, keywords and army/detachment reference cards. |
| `BsDataPricing`, `BsDataModelPrices`, `BsDataContextPrices` | Size/copy prices, model pricing and native/allied/detachment contexts. |
| `BsDataLoadouts`, `BsDataModelConstraints` | Equipment groups, defaults, model roles and supported selection limits. |
| `BsDataAssociations`, `LeaderRules.Import` | Structural association constraints and compiled attachment semantics. |
| `BsDataMuster` | Supported detachment-dependent keyword conditions. |

Missing required dependencies can abort an import. Unsupported pricing can leave a retained profile with no price and a warning. These outcomes are distinct: a visible unit is not evidence that all of its catalogue logic was understood.

The older `BsDataImporter.Import` entry point specifically selects the Death Guard catalogue. The all-army desktop path uses `ImportAll`; new all-army integrations should follow that path.

### Official MFM and local text

[`OfficialMfmClient`](../src/GrimList.Application/OfficialMfmClient.cs) handles the optional official-source download route and accepts an injected PDF extractor. [`MfmImporter`](../src/GrimList.Core/MfmImporter.cs) extracts ordinary model-count points rows from text and supported extracted PDF layouts. The normal points parser excludes enhancement, wargear and detachment prices; it reports unsupported rows rather than inventing prices.

PDF extraction invokes an external `pdftotext` executable. Discovery checks `GRIMLIST_PDFTOTEXT`, known Git for Windows and dnGrep locations, then PATH. PDF generation for exports is a separate implementation and does not require that extractor. Scans need OCR before the text parser can use them.

[`MfmDetachmentImporter`](../src/GrimList.Core/MfmDetachmentImporter.cs) supports a separate detachment-table/reference-package maintenance workflow through the data tool. This is not the ordinary desktop points-text parser.

## Merge semantics

The selected import layer determines what changes on a matched unit:

| Layer | Main behavior |
| --- | --- |
| Points | Replaces incoming pricing information, retains existing profiles, and preserves existing category/availability metadata where the merge specifies it. |
| Profiles | Replaces supplied profiles and associated metadata, retains existing price information. |
| Combined | Updates supplied pricing and profiles together, while preserving identity and accumulated aliases. |

Units absent from a partial update remain. **Complete faction points** clears absent units' price options within included factions when the layer updates points. It does not delete those definitions or roster entries.

Detachment definitions and reference cards merge by normalized identity. Missing cards are not generally treated as deletions. For a BSData enhancement without readable bearer restrictions, merge can preserve an existing complete card and warn that it needs source review. Earlier source dates also produce warnings. These preservation rules are why the preview matters even when a download succeeds.

## SQLite persistence

[`ArmyStore`](../src/GrimList.Persistence/ArmyStore.cs) owns the shared schema and SQL behind an injected `ISqliteConnection`. The [Windows adapter](../src/GrimList.Platform.Windows/WindowsSqliteConnection.cs) calls `winsqlite3.dll`, enables foreign keys, sets a five-second busy timeout, binds SQL values and uses `BEGIN IMMEDIATE` transactions. The [Android adapter](../src/GrimList.Mobile/Platforms/Android/AndroidSqliteConnection.cs) implements the same contract using Android SQLite and still requires device verification.

| Table | Purpose |
| --- | --- |
| `sources` | Metadata for each applied import, keyed by a generated source ID. |
| `units` | Basic unit identity/name/faction/copy-limit rows for each source snapshot. |
| `points` | Model count, copy threshold and points rows for each source/unit. |
| `detachments` | Detachment names and factions for each source. |
| `source_details` | JSON profile/reference metadata supporting the earlier normalized storage format. |
| `dataset_snapshots` | Snapshot format/count/checksum headers; historical rows may contain complete JSON. |
| `dataset_chunks` | Compressed complete datasets in bounded, ordered records. |
| `settings` | The `active_source` pointer and `editor_context` JSON used for session recreation. |
| `armies` | Saved army identity, name, faction, limit, game, detachments, disposition, warlord selection and `muster_selections` JSON. |
| `entries` | Ordered army selections, notes, JSON loadouts, enhancement selections and attachment targets. |

`LoadDataset` streams the active snapshot and verifies its integrity before returning it. Storage schema 2 writes compact JSON through gzip into 48 KiB compressed chunks, each stored as at most 64 KiB of base64 text. The header records the chunk count, compressed byte count and SHA-256. No complete JSON string crosses Android's Java database bridge. New imports no longer write a duplicate profile/reference copy into `source_details`.

Historical JSON snapshots and legacy `source_details` remain readable in bounded byte slices. Existing library history and armies are retained; applying an update writes the new format atomically. Corrupt or incomplete chunked snapshots fail rather than falling back to incomplete point tables. The full snapshot is important: the basic `points` table alone does not describe every modern contextual price or rule. Databases containing the new format require this version or newer; portable JSON exports retain their existing format.

On startup, `ArmyStore` creates missing tables and uses `PRAGMA table_info` plus additive `ALTER TABLE` statements to migrate older databases. There is no separate migration framework.

`Save` normalizes supported warlord/attachment state, upserts the army and replaces its entry rows within a transaction, preserving their list order. When supplied, editing context is saved in that same transaction. Deleting an army cascades to its entries. Entries deliberately retain their unit keys even when current data cannot resolve them, allowing validation to report a missing definition.

`ApplyImport` appends source rows and a complete dataset snapshot, updates recognized roster keys/names and the active-source pointer, and normalizes warlord selections. Previous library snapshots remain stored. Armies are not pinned to the source that was active when they were created.

## Pricing repair

[`PricingRepair`](../src/GrimList.Core/PricingRepair.cs) scans units with missing or unresolved pricing and compares them with a newly parsed source. The desktop can re-read a cached repository, download again, or accept a local ZIP. Cached reads verify the archive against the adjacent `source.json` hash.

Repair focuses on recoverable pricing instead of treating every incoming item as an ordinary full update. It reports recovered/unresolved entries and skips Crucible and battle-reference entries in its scan. Applying a repair checks that the library still matches the preview and saves the prior dataset, armies and report under `backups/before-pricing-repair-<timestamp>`.

Recovering a price does not prove complete army legality or complete catalogue support. Review unresolved rows and resulting army validation separately.

## Backups and recovery

A closed-app copy of `grimlist.db` is the complete routine backup of saved armies and library history. Preserve downloaded sources separately if you need offline re-import or diagnostics. Ordinary imports retain database snapshots but do not promise a separate backup file before every change.

**Export dataset** creates a portable active-library JSON file. It omits saved armies and previous snapshots. Repair-tool `dataset.json` and `armies.json` files are useful recovery evidence, but the desktop does not offer a general army-JSON restore command. Keep a database backup when you need a direct full restore.

To restore a database, close all instances using that directory, preserve the current file and put the backup at the same `grimlist.db` path. Restart using the intended `--data-dir`. There is no user-facing snapshot rollback screen; recovery by a historical snapshot requires deliberate developer tooling or a database backup.

## Network boundaries

The normal network operations are user-triggered data downloads and an explicit Yellowscribe upload. BSData uses GitHub metadata and archive hosts; the optional official provider uses its source-discovery client. **Send to Yellowscribe** sends generated roster XML to that external service. Local exports and ordinary roster editing do not require a server.

The code does not schedule unattended data refreshes. A network result becomes active only through the apply path in the desktop workflow. Console maintenance commands can apply immediately; check their behavior in [Development and testing](development.md).

Muster selections store the granting Detachment/rule, roster entry ID and optional model index. They never persist derived keywords into a unit profile. `ArmySession` normalizes orphaned selections, saves them with the roster transaction and includes them in undo/redo snapshots. Dataset Detachment definitions carry `Tags`, `CannotCombineWithTags` and optional structured `Nominations`; older libraries can compile supported explicit rule text on demand. BSData Enhancement entries retain an Upgrade flag derived from category metadata or their roster cap and shared Enhancement-cost modifiers.
