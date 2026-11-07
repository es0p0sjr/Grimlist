# Versioned codex rules packs

Rules packs use the ordinary **Import data / JSON** workflow on Windows and Android.
Choose **Datasheets only**, review the changes and saved-army impact, then apply.
Do not use the display-reference import: a codex pack can change army-building rules.
Packs persist in the library, its exports and database snapshots. Existing snapshot
rollback can restore the library before installation. This is a file import feature;
Update data still downloads BSData and MFM and retains installed rules overrides.

## Current source coverage

`data/rules-packs/dark-angels-2026-partial.json` corrects Lion's statline, including
the model profile used by the UI. It is deliberately labelled Partial, and is not
a complete Space Marine or Dark Angels codex. The official September 15 article
supplies M8, T10, Sv2+, W16, LD5+, OC4. The existing 3+ invulnerable save is retained
from the catalogue; that value is not a newly verified change from the article.
Other profiles, abilities and weapons remain the catalogue versions.

The current BSData MFM mirror is useful for detachment names, DP, dispositions,
uniqueness tags, enhancement/upgrade prices and leader target lists as well as
unit prices. It does **not** contain full datasheet stats, ability text, detachment
rule text, enhancement effects/eligibility or stratagems. Its presence does not
establish that a complete codex has been imported. The existing MFM overlay
continues to update unit points only; importing its remaining metadata requires
matching it to current, complete rules rather than manufacturing rule text.

Sources checked:

- https://www.warhammer-community.com/en-gb/articles/voakmnny/space-marines-detachments-blood-angels-and-dark-angels/
- https://www.warhammer-community.com/en-gb/articles/ifgezlgu/space-marines-rules-first-look/
- https://github.com/BSData/wh40k-11e-mfm/blob/main/data/dark-angels.yaml

A complete current codex source remains required for the full Space Marines update.

## Built-in detachment catalogue repair (0.2.9)

The shared Core resource `Resources/detachment-catalogue-11.json.gz` contains an
explicit list of 34 retired Space Marine detachment names across the 12 Marine
catalogue contexts. Its readable manifest and official source links are in
`data/catalogue-corrections/space-marines-11.json`. Rebuild with
`scripts/Build-DetachmentCatalogueRepair.py <pinned-dataset.json>`; the generator
rejects a different source revision or incomplete set of current definitions.

The supplement uses BSData revision
`8b50fc3240e29c6ab0e254f75bcefa8cce8ce0ef` (2026-10-13). It is not a full Codex
replacement. Existing Deathwatch Support placeholders are left unchanged; the
supplement does not fabricate its missing rules or costs. Other current missing
definitions and available source cards are supplied only for recognised catalogue
contexts. The two exact older bundled/cache source hashes can recover missing
contexts. Older dated BSData detachment metadata can be updated, while newer
metadata and explicit rules-pack ownership remain intact.

`ReferenceData.Normalize` applies the correction on both platforms when existing
snapshots are read and new imports are saved. No reimport is required. Raw old
snapshots remain available as history; the next library write stores the corrected
view. Imports and Android reference recovery cannot resurrect retired names.
Only explicit retirements are removed: new future detachments are not discarded
merely because they are absent from this supplement. Retired detachment cards
and coverage are removed too; shared cards retain their surviving targets.

Saved armies retain their old detachment selections and receive a specific
retirement diagnostic. Units, points and roster entries are not rewritten by this
repair. Current detachment text/stratagem completeness remains dependent on the
available source; this change must not be described as a complete Codex update.

Regression checks cover old stored snapshots, all import layers, SQLite restart,
export, Chapter restrictions, newer metadata, shared-card scopes, Deathwatch,
other games/editions and Android's supplement. The Windows smoke mode is
`--smoke-test <image.png> --detachments-only`; native Android checks use
`scripts/Test-DetachmentCatalogueAndroid.py` with an isolated
`IncludePerformanceChecks=true` APK and run twice across cold starts.

Verification on 2026-10-14: **360 host checks passed**. The sole failed check
requires the absent `samples/death-guard-historical-points.json` fixture. Windows
native detachment setup passed and the MSI-extracted standalone app passed offline
first launch, army creation/save and reopen checks. On the physical Redmi, both
isolated Android cold starts passed catalogue, Chapter restriction, retirement
warning and saved-army stability checks. Production Android 0.2.9/code 11 was
installed over 0.2.8 without clearing storage, and two cold starts retained the
Larisa (1875/2000) and Teams (2000/2000) armies. Evidence and release hashes are
under `.local/detachment-fix/`; installers are in `GrimList-Share-0.2.9/`.

## Format

The envelope is a Dataset with SchemaVersion 3, Source, an empty Units list, and
RulesPacks. A full exported library can also carry the installed packs. Schema 3
prevents older applications from silently accepting a library whose precedence
rules they do not understand.

Each pack has a stable Id, positive integer Revision, edition-specific dated Source
with a URL, explicit Factions, Units, Detachments and Cards. Status is Verified or
Provisional; Coverage is Partial or Complete. These are author declarations, not
an automatic certification by the importer. Provisional/leaked material must be
labelled Provisional and its limitations described in Notes. These labels appear
in both platforms' import review. No provisional Custodes rules are supplied.

RulesUnitUpdate identifies a unit by exact normalized faction/name, or an exact
UnitId/alias within that faction. Missing or ambiguous targets stop the whole
preview. Fields lists the UnitProfile properties to replace; '*' replaces the
entire profile. Omitted fields are retained; included lists/dictionaries replace
their entire previous value. Characteristics changes must include Models when
the existing profile has model statlines. Abilities, Weapons and Loadout changes
must also replace Semantics to prevent obsolete compiled effects. Null Semantics
explicitly clears those effects and produces a manual-review warning; the importer
does not invent executable behavior from arbitrary text. Author complete profiles
with current loadout, keywords, associations, attachment and warlord restrictions.

New units require AddIfMissing and a complete '*' profile. InitialPoints may supply
a baseline; absent points remain unavailable with a diagnostic. Existing unit
prices are never overwritten by this field. ReplaceSelectionRules explicitly
replaces Category, MaxCopies and RequiredDetachments. Stable unit IDs, aliases and
saved roster selections remain intact. Leader prose is recompiled through the
existing shared importer after profile changes.

Cards use the existing full ReferenceCard schema, including IsUpgrade, Eligibility,
Modifiers and WarlordRestrictions. A supplied card replaces the same
faction/section/detachment/name across sources. ReplaceCardScopes declares a
complete category for one exact faction and detachment (empty means faction-level
cards only). RemoveCards explicitly retires named cards. Partial packs never
remove unrelated rules merely because they are absent.

Detachments use the full DetachmentDefinition schema, including costs, dispositions,
tags, applicability and nomination restrictions. Supply the complete definition:
old restrictions are not silently retained. ReplaceDetachmentsFor replaces the
declared factions' entire detachment lists; RemoveDetachments retires named ones.
Retired detachments' cards disappear too. Saved armies keep their old selection
and receive the existing validation diagnostics, rather than silently switching.

## Precedence and maintenance

### Space Marine army-rule reference (0.2.10)

For known 11th-edition Marine catalogues, `SpaceMarineArmyRule` promotes Combat
Doctrines from the current BSData unit abilities into a faction-wide Army Rules
card. The linked shared-library rule is otherwise absent from the native
catalogue reference reader. Existing bundled installations use the dated
2026-10-13 BSData fallback in `space-marine-army-rule-11.json`, verified against
the official Space Marines rules preview and current published rule timing.
`scripts/Build-SpaceMarineArmyRule.py` reproduces it from that pinned dataset.

Normalization applies on library reads, imports, exports and both platform rule
views. It removes old BSData Oath of Moment army-reference cards and the old
Gladius Combat Doctrines detachment card. Other Chapter rules, unit profiles,
points, roster selections and saved armies are retained. This is an army-rule
reference update, not a unit-profile migration or a doctrine turn tracker.
Newer BSData ability text can replace the fallback; explicit manual references,
current tombstones and rules-pack ownership retain precedence. Edition 10 and
unknown catalogue identities do not receive the fallback.

Verification: `--army-rules-only` in the shared test runner and Windows smoke
runner; `scripts/Test-SpaceMarineArmyRuleAndroid.py` exercises the native rules
screen and saved-army stability across two cold starts in isolated app storage.

The generic DatasetMerge applies BSData/base imports, preserves MFM pricing through
the existing price layer, then reapplies installed rules packs. This also prevents
covered BSData references from generating source conflicts against the deliberate
override. Uncovered references keep their normal source conflict checks.

Packs remain authoritative until explicitly updated or the installation snapshot
is rolled back; a newer BSData timestamp alone does not retire them. Revisions
must be cumulative: retain earlier covered fields/scopes, replace their contents,
or explicitly remove obsolete cards/detachments. Same-revision content changes,
downgrades and overlapping faction ownership by different pack IDs are rejected.
Consolidate all updates for an owned faction into a new revision of its pack.

Android's bundled reference recovery respects the same ownership and removals.
Import previews report additions, updates, removals and saved-army impact. Applying
still uses the existing snapshot check and atomic database transaction.

## Validation

RulesPackChecks exercises profile/model consistency, actual enhancement effects,
stratagem visibility, upgrade retention, removals, MFM coexistence, catalogue
refresh, persistence, export, both document readers, and Android recovery. Run:

```powershell
dotnet run --project tests/GrimList.Tests --no-build -- --rules-packs-only
```

No faction names or unit-specific exceptions occur in the importer. The included
Lion correction is data and uses exactly the same rules as the synthetic tests.
