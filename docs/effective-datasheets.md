# Effective datasheets

Windows and Android use `GrimList.Core.EffectiveDatasheets.Project`. Imported
profiles remain the base library. Choices are roster data; evaluated values are
new objects and are never written into imported profiles or source snapshots.

## Shared evaluation

The projection resolves equipment, existing detachment/muster keyword grants
(including CHARACTER nominations), roster choices, imported profile modifiers,
eligible enhancement/upgrade modifiers, and qualifying self Auras. It exposes
`EffectiveKeywords`, `EffectiveCharacteristics`, `EffectiveWeaponProfiles`,
`EffectiveAbilities`, and `AppliedModifierSources`, alongside `BaseUnit`.
The existing `MusterEffects.EffectiveUnit` entry point delegates to this projection,
so roster validation, attachments, warlord selection, allied rules and exports
use current state. Library browsing still has an immutable base preview.

`DatasheetSemantics` version 1 stores choice groups/options and modifiers. A
modifier identifies its source, target, characteristic, operation, optional
profile/weapon type, and condition. Model fields include M, T, Sv, W, LD, OC
and InSv; weapon fields include Range, A, BS, WS, S, AP and D. Keywords and
abilities support grants/removals. Numeric operations include set, increment,
decrement and multiply; dice expressions are preserved rather than rolled.

Conditions are three-valued: true applies, false excludes, unknown creates a
labelled conditional alternative. Supported roster predicates include selection
counts, keywords, unit identity, detachment membership, and Boolean groups.
Battlefield positioning is not inferred from the roster. Conditional values
remain in shared provenance for diagnostics/exports and do not replace the
always-active value shown on the datasheet.

## Source import and existing libraries

`BsDataEffectiveDatasheets` reads resolved source choice groups, category grants,
profile characteristic modifiers and supported enhancement selectors. Choice
groups are detected structurally: mutually exclusive upgrades granting keywords,
without weapon profiles. This covers both Allegiance and Mark of Chaos without
faction, unit or source-ID branches. Profile modifiers retain source conditions.

The conservative prose parser supplies recognised effects absent from structural
data. It accepts complete supported clauses, not arbitrary keyword matches.
Unrecognised selectors, conditions and prose remain reference rules; they do
not become invented permanent bonuses. This is not a general natural-language
rules interpreter. External nearby-unit Auras remain contextual.

Older libraries do not contain the new semantic fields. The shared embedded
`Resources/effective-datasheets.json.gz` supplies generated metadata for the
validated cc183 and da724 BSData archives. An overlay requires the exact source
archive hash, source alias and base-profile hash; enhancement cards also require
matching text. Version 1 profile hashes use canonical CRLF JSON on both platforms,
independent of the serializer's operating-system newline default. The overlay
neither overwrites base values nor modifies saved armies.
Unmatched/custom libraries need reimporting through the updated importer.
Both platforms use the same Application resource; no runtime repository files
are required.

Regenerate the metadata with the production importer (repeat for each archive;
the command replaces only that archive's pack):

```powershell
dotnet build tests/GrimList.Tests/GrimList.Tests.csproj
dotnet tests/GrimList.Tests/bin/Debug/net10.0/GrimList.Tests.dll --build-effective-metadata <repository.zip> src/GrimList.Application/Resources/effective-datasheets.json.gz
```

## Choices, eligibility and persistence

`ArmyEntry.Choices` persists group/option identities in SQLite `roster_choices`.
The additive column migration preserves old rows. Legacy choices previously
stored as wargear are moved into roster choices when loaded by the session.
Duplication preserves choices according to existing unit duplication semantics;
undo/redo snapshots include them. Projection starts from the base on every edit,
so removing a source cannot leave a cached modifier behind.

Choice changes are evaluated against a complete proposed army. If an enhancement
would become illegal, both interfaces name it and require confirmation to remove
it as part of the same undoable edit. Cancelling preserves the original state.
Direct session commits also reject new illegal enhancement selections. Existing
orphaned selections remain removable. Existing detachment-change behavior keeps
an invalid enhancement visibly flagged for review, while its effective modifiers
cease to apply; undo restores the prior state.

The four imported Daemon Princes expose Khorne, Tzeentch, Nurgle and Slaanesh.
Their source modifiers yield +2 hellforged Strength, +3 infernal cannon Attacks,
+1 Toughness and +2 inches Move respectively. Khorne grants KHORNE and unlocks
A'rgath; another choice removes that keyword and eligibility. A'rgath adds +1
melee Attacks/Strength. Its stronger Shadow of Chaos value is an additional
conditional alternative, not a permanent +2. Neither rule is hard-coded in
production by name or identity.

Self Aura evaluation includes the bearer but requires its keyword predicate to
match and excludes clauses targeting other models/units. A matching self effect
is labelled as active while on the battlefield. Duplicate Auras with the same
name do not stack. Unequipped equipment rule cards remain available as builder
references, while active abilities and self-effects respect equipment choices.

## Display and exports

Both interfaces provide source-generated choice selectors and show only current
effective characteristics and weapon values. Datasheets omit previous values,
change highlighting, modifier tooltips and the changes-and-sources section.
The builder continues to show available
weapon profiles; Play/export selection filters use equipped profiles. Text,
PDF and Yellowscribe exports use projected values, with effective-change context
in the roster text/abilities. Imported data exports retain base profiles.

Weapon spacing accommodates double-digit Strength and dice-plus-bonus Damage:
Windows sizes these columns to their contents and reduces spare Attacks spacing;
Android reserves more space for dice expressions and wraps long values as needed.

## Verification — 2026-10-06

- Weapon-spacing update: full Windows WPF smoke **passed**
  (`.local/weapon-spacing-wpf/spacing.png.result.txt`). Both review builds were
  republished and Android native startup bindings passed. Native emulator
  inspection confirms Bloodflail shows Strength `16` and Damage `D6+1` fully,
  without clipping. Screenshot: `.local/weapon-spacing-android/bloodthirster-weapons.png`;
  log: `.local/weapon-spacing-android-ui.log`.
- Shared full reference-fixture suite: **284 passed**, zero failures.
  Log: `.local/effective-full-regression.log`; fixtures/evidence:
  `.local/tests/0116d8f8-029c-4d9f-9f42-4a113857b1ac/`.
- Full Windows WPF smoke suite after the display simplification: **passed**,
  including current-only allegiance/A'rgath values, absence of the changes panel,
  enhancement removal, undo/redo and transition to Nurgle.
  Evidence: `.local/current-values-wpf/current.png.result.txt` and
  `current.png.effective-datasheet.txt`/`.png` alongside the suite screenshots.
- Shared tests also cover all supported characteristics, unknown conditions,
  equipment removal, complete proposed-state eligibility, detachment changes,
  qualifying/non-qualifying/self-excluded Auras, non-stacking, all four real
  Princes, persistence across connection recreation, duplication and exact-source
  metadata compatibility. A self-Aura keyword grant can satisfy enhancement
  eligibility; an enhancement cannot grant its own eligibility.
- The effective suite also passes with `--lf-json`, which forces Android's JSON
  newline convention on Windows and checks compatibility against the same
  generated metadata. Evidence: `.local/effective-lf-regression.log`.
- Published Windows release copied outside the repository: **13 checks passed**,
  including clean bootstrap, normal validation, references, save/reopen and loading
  the bundled private runtime. Evidence: `.local/effective-windows-reviewed/`.
- Initial effective-state Android Release APK: **29 offline native acceptance checks passed**
  on the disposable Android 15/API 35 x64 emulator. This includes the entire
  standalone baseline plus all four allegiances, A'rgath's combined weapon values,
  conditional Shadow of Chaos, confirmation/cancellation of invalidating changes,
  undo/redo, and allegiance/stat persistence after process termination. Airplane
  mode remained enabled throughout. Evidence: `.local/effective-android-final/`
  and `.local/effective-android-final.log`. Both ARM64 and x64 native startup
  bindings pass the package check. Physical-phone verification was not run.
- After the display simplification, both review builds were republished. The
  clean Android Release APK passed ARM64/x64 startup-binding checks and **five
  focused native checks** on the same disposable emulator: installation,
  current-only Nurgle/Khorne/A'rgath values, absence of comparisons/history, and
  confirmation removing an invalid enhancement followed by recalculation.
  Evidence: `.local/current-values-android/` and
  `.local/current-values-android-ui.log`. The shared, standalone Windows and
  full Android results above are the baseline before this presentation change.

Review builds are in `artifacts/builds/effective-datasheets/`: copy the complete
`windows/` directory to run its `GrimList.exe`; Android needs only
`android/com.grimlist.mobile-Signed.apk`. The Windows folder contains 493 files
(185,106,745 bytes). The APK is 73,312,389 bytes, SHA-256
`1953427EDBB116AC7B92E2647D6EB7AA08C5706FDDF0610E2CF1ACA9CF044607`.
These are review builds retaining application version 0.2.0; the earlier shared
MSI/APK distribution folder is a separate, older build.

Commands:

```powershell
powershell -File scripts/Test-ReferenceFixtures.ps1
dotnet tests/GrimList.Tests/bin/Debug/net10.0/GrimList.Tests.dll --effective-only --lf-json
src/GrimList.Desktop/bin/Debug/net10.0-windows10.0.18362.0/GrimList.exe --data-dir <fresh-fixture-root>/persistence --smoke-test <fresh-fixture-root>/effective.png
python scripts/check_android_startup.py <published-signed.apk>
python scripts/Test-EffectiveDatasheetsAndroid.py --adb <adb.exe> --serial <disposable-emulator> --apk <published-signed.apk> --output <evidence-directory> --clear-data
```

The Android script first runs the complete standalone offline acceptance script,
then exercises allegiance, enhancement invalidation confirmation/cancellation,
all four modifiers, undo/redo and process-death persistence using native controls.
It installs only the published APK and never seeds a library/database from the
development machine. The explicit clear-data option applies only to the selected
disposable emulator.
