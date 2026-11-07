# Android proof-of-concept verification

## BSData Apply update memory repair - 2026-10-14

Android 0.2.8 (version code 10) repairs the large Java-string allocation when
applying a full BSData library. Schema 2 stores compact JSON through gzip in
48 KiB compressed chunks (at most 64 KiB base64 per SQLite value), with a small
count/length/SHA-256 header. Legacy JSON snapshots and source details are read
in bounded byte slices. New imports stop duplicating the complete profiles in
source_details. Revision tokens replace full JSON copies retained by import
previews, and the token is checked inside the apply transaction.

Read transactions keep all chunks and the revision consistent. Corrupt or
incomplete chunk sets fail closed; a failed write rolls back the whole import.
A committed update followed by a reload failure is reported as saved, with a
restart instruction. JSON export/backup contracts remain unchanged. Bundled
schema-1 libraries remain supported.

Host verification: all nine focused storage checks passed, covering legacy
Unicode snapshots, saved armies/context, injected partial-write rollback,
stale previews, post-commit reload failure, and malformed chunk sets. The full
suite recorded 355 passing checks and one failure because the historical
fixture samples/death-guard-historical-points.json is absent from this workspace.
Windows build passed with zero warnings/errors. Evidence is under
.local/import-memory-fix: storage-final-tests.log, shared-final-tests.log,
and windows-build.log.

The production APK was published from a clean source copy. ZIP/signature checks
and ARM64/x64 native onCreate binding checks passed. It uses the same signing
certificate as 0.2.7 and was installed over that version without clearing app
storage. Two physical Redmi cold starts succeeded; both displayed the existing
Larisa (1875/2000) and Teams (2000/2000) armies. Evidence:
production-installation.json and production-start-{0,1}.{xml,png,log} in the
same evidence directory.

Copy-ready APK: GrimList-Share-0.2.8/GrimList-Android-0.2.8.apk,
102,250,629 bytes, SHA-256
b24eb62a098560c959088930fbf7d7cf8d9640e5c92771916fd2b8fb8ebc9a33.

The repeatable native check is scripts/Test-ImportStorageAndroid.py. Build a
clean Standalone APK with IncludePerformanceChecks=true and
ImportStorageFixtureDirectory pointing to repository.zip, source.json and
mfm.json, then pass --apk, --serial and --output. The script only accepts
com.grimlist.perftest; fixtures and test actions are excluded from production.
It parses pinned BSData, prepares the full preview, invokes the review Apply
button twice, compares the complete canonical dataset and saved army, rejects
a stale preview, then force-stops and reopens the process to verify persistence.
The expected dataset includes the existing import normalization of attachment
rules/reference-card identities and the saved import timestamp.

Native verification on the physical Redmi passed: both complete-library
review/apply passes and force-stop/cold-reopen retained all 2,219 units and the
saved army. All 23 apply assertions and three restart assertions passed, including
full canonical dataset hashes, session reload and stale-preview rejection.
BSData fixture revision: 8b50fc3240e29c6ab0e254f75bcefa8cce8ce0ef, with the
cached MFM overlay. Sampled peak Java heap usage was 15,195,888 bytes (14.49 MiB)
under a 268,435,456-byte (256 MiB) limit; this excludes managed/native memory
and is sampled, not an exact allocation high-water mark. The first parse took
366.3 seconds. Preview took 21.3/22.8 seconds; apply plus full-data verification
took 59.8/62.1 seconds. These are test-stage timings, not isolated UI latency.
Evidence: .local/import-memory-fix/phone-verified/import-storage.json,
import-storage-reopen.json and memory reports; runner log:
.local/import-memory-fix/phone-verified-check.log.

## Android-only Stratagem delivery repair — 2026-10-08

Android 0.2.5 (version code 7) embeds the existing frozen reference pack from
`artifacts/references-real/GrimList-References-11e.json`: 2,214 Stratagem applications
across 34 factions, with 444 coverage declarations. The uncompressed pack SHA-256
is `0b28075b80bf2564a862f42ed663f1733a65713416bbb81cda1fbe741da75662`.
Windows code, its bundled library, and its distribution packages are unchanged.

`MobileWorkspace` runs `AndroidReferenceRecovery` before opening the editing
session, for both newly bootstrapped and existing edition-11 libraries. It adds
missing references through shared canonical identities and applicability matching,
fills absent detachment matching IDs, and supplies missing pack definitions for
installed factions. Existing detachment costs, dispositions and restrictions remain
intact. Existing cards, removed identities and authoritative complete-source
omissions take precedence over bundled content. Other games/editions are skipped.
The existing repository transaction persists the supplement and retains the
previous dataset snapshot; a repeated startup does not write another import.

Android's rules screen now respects Complete/VerifiedComplete zero-card coverage,
instead of displaying a contradictory missing-content hint for those detachments.

Six focused checks pass: all 444 faction/detachment scope counts and filtering,
core cards, existing unit/detachment semantics, newer and removed cards, complete
source omissions, edition/game isolation, 15 complete zero-card scopes, SQLite
reopening, saved-army/editor-context preservation and idempotence. These tests
compile the actual Android recovery source/resource into the host test harness.
Evidence: `.local/android-references-tests.log`.

Clean Android Standalone Release publish succeeded. The signed APK has application
ID `com.grimlist.mobile`, version 0.2.5/code 7, the same signing certificate as
0.2.4, and valid ARM64/x64 native startup bindings. Both packaging inputs include
the exact embedded pack. APK ZIP integrity passed. Build evidence:
`.local/android-references-publish.log`; output:
`artifacts/builds/android/0.2.5/com.grimlist.mobile-Signed.apk`.

The full host regression suite passes **309 checks**, including the six Android
recovery checks (`.local/android-references-full-tests.log`). The copy-ready
Android-only release is `GrimList-Share-0.2.5/GrimList-Android-0.2.5.apk`,
102,152,325 bytes, SHA-256
`333d44d061fc4e13e483f5365002f403e108604dd64a578a991670ef39c650b4`.
It was installed with `adb install -r` on the connected Redmi phone without
clearing storage. The repair completed and the existing Emperor’s Children rules
screen reopened. Phone UI/log evidence is in `.local/android-references-phone/`.
Automated scrolling is blocked by the phone's input-injection permission; native
detachment-card inspection awaits user confirmation.

Native emulator upgrade from 0.2.3 also completed without clearing storage. The
saved `ConditionalNative` Necrons army retained its 120-point total, Technomancer,
Lokhust Destroyers and attachment. Army rules displayed all six Cursed Legion
cards with Complete coverage, CP/turn labels and the expanded Methodical Murder
WHEN/TARGET/EFFECT text. Evidence: `.local/android-references-emulator/`, including
before/after UI hierarchies and `stratagems.png`.

Physical-phone force-stop/cold-relaunch passed: the saved Emperor’s Children /
Carnival of Excess rules screen restored with its selected 3-DP detachment.
No fatal exception appeared in the captured restart log. See `restarted.xml`,
`restarted.png` and `restart-logcat.txt` in the phone evidence directory.
The emulator also passed force-stop/cold-relaunch and displayed the same six
Cursed Legion cards afterward (`restarted-stratagems.xml`). The headless emulator
started for this verification was then shut down.

## Enhancement selection repair — 2026-10-06

The affected catalogue supplied enhancement effects without leading bearer-restriction sentences. Its restrictions lived in structured selection visibility conditions, which the importer discarded. The shared eligibility check consequently hid those cards on unit datasheets even though Army rules displayed them.

The generic importer now retains supported enhancement eligibility conditions. Shared selection and validation use them without weakening Character/Epic Hero, faction, detachment, named-bearer or keyword restrictions. Existing libraries receive an eligibility-only repair from matching cached source data or a bundled, source/text-fingerprinted compatibility map; the latter also handles libraries transferred as JSON. No faction-specific rule branch was added.

Validation: **226 checks passed**, including imported Daemon Prince of Nurgle/Revolting Regeneration selection (+30 points), source Boolean groups/exclusions, undo/redo, removal and SQLite reopening. A separate run against the phone's exported library exercised the persisted offline repair and verified unchanged units, detachments, other rules and saved armies. The newer all-faction fixture demonstrated equippable recovered cards across 33 factions; this is a regression sample, not certification of every source condition. Unsupported source formats remain unavailable unless supported textual eligibility exists.

Clean AndroidTest Release publish passed; NU1900 reported that NuGet vulnerability metadata was unreachable. ARM64/x64 native startup bindings and APK signature passed. The APK is **65,788,037 bytes**, SHA-256 `918A1E88B7E60FC63437421CC8250960188F517891B1242DC373A14F610E718D`, signed with the existing development certificate. It was installed with `adb install -r` on the connected Redmi Note 12 Pro+ without clearing storage. Initial launch restored the existing Death Guard Army rules screen after the one-time repair; force-stop/cold-relaunch also succeeded. Manual phone enhancement interaction is left to the user, as requested; automated taps were blocked by device input-injection permissions.

Evidence: `.local/enhancement-fix/` (test logs, original APK, final APK, build logs and phone UI/log captures). Final automated phone-library test artifacts: `.local/tests/d128ac2c-6a51-438d-a66a-531bb2159ee8/`.

Subsequent manual interaction was observed through the phone UI hierarchy: **Selected: Revolting Regeneration**, with **215 pts / Base 185 pts / Enhancement +30 pts** and the removal button present. Evidence: `manual-check.xml` and `equipped-on-phone.png` in the directory above. On-device equipping is verified; removal and persistence are covered by the shared regression checks, with manual confirmation pending.

## Phone startup repair — 2026-10-05, after BugReport0

This supersedes the APK and disconnected-phone status below. The transferred
APK with SHA-256 `347B289E55FF4962844456D2E90FBAC58E9E0EDFF1D939369EE0FF1E1C5BFF8E`
crashed before application initialization with `MauiApplication.n_onCreate()` /
`UnsatisfiedLinkError`. The installed APK hash matched the transferred file.
Its ARM64 and x64 `libxamarin-app.so` files lacked the named native startup
binding. A clean Release rebuild using the unchanged AndroidTest profile restored
that binding on both architectures. The exact incremental-build invalidation
failure has not been isolated; no application logic or data migration was changed.

The replacement is `artifacts/android/GrimList-Android-Test.apk`, **64,673,727 bytes**,
SHA-256 `8765DB6E6B79223094076479753007963B989D054088A1231134BE4F2C9AA45F`.
APK v3 signature verified. `adb install -r` succeeded on the Redmi Note 12 Pro+
without uninstalling or clearing storage. Two cold launches succeeded, including
force-stop/relaunch; the existing World Eaters army-rules screen was restored.
The replacement was copied over `Downloads/GrimList/GrimList-Android-Test.apk`
and the phone checksum manifest was updated and verified. Stratagem import and
full content acceptance remain separate manual checks.

Evidence: `.local/startup-crash-2026-10-05/` contains the broken APK, original
process log, clean/publish logs, replacement process logs and a phone screenshot.
Publish succeeded with NU1900 warnings because the NuGet vulnerability service
was unreachable. Native startup checks do not establish dependency audit results.

Before distributing subsequent AndroidTest builds:

1. Run `dotnet clean` with the same Release configuration, AndroidTest profile,
   SDK and JDK arguments as publish, then publish to a candidate directory.
2. Run `python scripts/check_android_startup.py <candidate-signed.apk>`.
   This regression check rejects the reported broken APK and accepts the repaired
   APK; it checks the native binding for the currently pinned MAUI version.
3. Verify the signature, install that exact candidate as an update, and verify
   a cold launch and force-stop/relaunch on a physical device, with process logs
   and a rendered application screen. Build success alone is insufficient.
4. Only then replace the friendly APK filename and regenerate its checksums.

## Reference content/universe audit — 2026-10-05

This is the latest content-pack/build status. The accepted reference architecture was retained. Two production-code changes support this milestone: the generic BSData reference reader accepts both `Detachment` and `Detachments` container names, and its existing native-catalogue traversal is exposed to offline diagnostic tooling. No faction parser, new schema, new roster engine, network acquisition or UI permission was introduced.

### Universe sanity check

Inputs are the unchanged user export `AndroidDetachFixExport/grimlist-points.json` and its exact cached BSData archive, revision `da7248777da54218034f09b6271ad89ff6b68a77`. The tool verifies the archive SHA-256 against the export. The explicit source force context is `Army Roster`, ID `bb9d-299a-ed60-2d8a`. All identities use edition + primary faction catalogue ID + resolved detachment source ID. Matching display names never merges distinct IDs.

**823 was a raw imported projection count, not a verified playable universe.** Of those records, 401 are source-selectable and 422 are hidden in the audited context. Space Marine chapters inherited all 53 raw definitions each, despite source chapter-visibility conditions. Conversely, plural detachment containers caused four factions' definitions/rules to be omitted. The generic fix raises raw imported definitions to 892; that larger raw number is also not the playable count.

| Audit measure | Count |
|---|---:|
| Raw faction names / canonical faction catalogue identities | 36 / 36 |
| Original imported detachment records | 823 |
| Fresh imported detachment records after container fix | 892 |
| Reachable source occurrences, including inherited/allied projections | 1,023 |
| Canonical faction/detachment identities before context exclusions | 1,014 |
| Duplicate linked occurrences collapsed within the same identity | 9 |
| Excluded hidden projections | 448 |
| Excluded allied-root projections | 122 |
| Canonical source-selectable faction/detachment combinations | 444 |
| Distinct source IDs among those combinations | 270 |
| Distinct raw detachment definition IDs | 284 |
| Unresolved IDs/visibility in this run | 0 |
| Factions with native selectable detachments | 34 |
| Factions with no native detachment in this source/context | 2 |

Titan Legions and Chaos Titan Legions have no native detachment in this source/context. Twelve of the 444 combinations refer to Armoured Speartip from a catalogue whose name includes Legends. They are explicitly flagged. A historical catalogue name alone does not establish expiration; these were not silently removed to improve coverage. The audit establishes source visibility/native reachability, **not independent certification of published current-edition legality or every legal combination at every points limit**. Shared source IDs appearing legitimately across factions remain separate applicability combinations; they are not false duplicates.

`tests/fixtures/references/11e/detachment-universe.json` contains every original record, source ID, edition, faction ID, catalogue/revision, matching evidence, selectability, duplicates/aliases, other-faction projections, historical signals, reference count and coverage. No source/reference database was modified by the audit.

### Exhaustive BSData/source inspection

`artifacts/BSDataReferenceAudit.json` records **263,347 scanned objects** and **40,096 reference candidates**, with container inventory, source IDs/paths, catalogue revisions, scope relationships, apparent type, text availability/hash and actual shared-importer results. It covers rules/shared rules, profiles/abilities, publications, selection/shared-selection structures, links/categories and descriptive modifier/constraint fields. Profile characteristics are represented on their owning profile instead of counted twice.

- Explicit usable BSData stratagem definitions detected: **0**.
- Structured timing/target/effect/cost or labelled-text stratagem candidates needing review: **0**.
- Mentions of stratagems in other abilities/rules: **303**; these are not converted into stratagem cards.
- Canonical detachment scopes with usable BSData stratagem content imported: **0**.
- Stratagem adapter misses detected: **0**; partial BSData stratagem content detected: **0**.
- Canonical detachment scopes with no usable BSData stratagem text detected: **444**.

The demonstrated extraction gap concerned **ordinary detachment references**, not hidden stratagem cards: singular-only container recognition discarded existing plural-group content. One structural fix recovered:

| Faction | New raw detachment definitions | New army/detachment display cards |
|---|---:|---:|
| Adeptus Mechanicus | 10 | 11 |
| Aeldari | 24 | 38 |
| Drukhari | 24 | 39 |
| Grey Knights | 11 | 16 |
| Total | 69 | 104 |

Real Mechanicus and Grey Knights regressions exercise the same structure; a synthetic format test covers singular/plural equivalence. Source data, not faction names, selects the behavior. The per-faction before/after extraction report is `artifacts/reference-extraction-delta.json`.

`artifacts/reference-source-inventory.json` identifies the explicit local structured sources inspected. Existing supplemental stratagem content is confined to the same 45 Death Guard cards duplicated across prior exports/source packs; it is not four independent content collections. No additional authorized all-faction stratagem source was present in these inputs. This is a local-source finding, not a claim that content cannot exist elsewhere. No website was scraped, missing rule inferred, other-edition text reused or completeness claim fabricated.

### Portable pack and content gap

`artifacts/android/GrimList-References.json` now contains **1,119 display cards**: 1,043 detachment rules, 31 faction/army rules and 45 existing supplemental stratagems. It includes the 892 source detachment definitions needed for matching. These are raw definitions, not a certification that each is playable for every faction. It contains **no units, points, enhancements or disposition rules**. The existing generic JSON adapter processes this multi-faction pack; source ownership/revisions remain per-card. `artifacts/reference-source-manifest.json` records pack identity/revision/hash, owners, coverage scopes/modes and provenance. The source format remains unchanged.

The builder uses the shared merge, then reimports the output through the public JSON adapter. Unit data from the user's export is unchanged under shared serialization. Previous supplements are retained; partial input does not delete absent records. Synthetic multi-faction supplemental tests cover Partial, CompleteForDetachment, CompleteForFaction and CompleteForEdition declarations with the same adapter used by the real Death Guard collection. No synthetic text enters the delivered pack.

Pack SHA-256: `8E8071B0847AA64AFCC8F534FF73953DC2A86562A00C6C1F6ACF35DDB61BAC74`.

| Canonical stratagem coverage | Count |
|---|---:|
| VerifiedComplete | 0 |
| Complete | 0 |
| Partial | 9 |
| Unknown / no suitable local source | 435 |
| BSData-sourced scopes | 0 |
| Supplemental-sourced scopes | 9 |
| Mixed-source scopes | 0 |

Supplemental coverage represents **one faction, nine detachments, zero complete and nine partial scopes**. All 444 scopes fall into class D (no usable BSData stratagem text detected); nine also fall into E (supplement exists), and 435 into F (no usable source). A/B/C stratagem counts are zero. The plural-container fix is separately classified as a non-stratagem extraction improvement.

Every missing detachment is explicitly named in **`artifacts/reference-coverage.txt`** and its machine-readable counterpart **`artifacts/reference-coverage.json`**, along with counts, sources, coverage, missing/unresolved fields and provenance. The earlier 823-scope report below is historical. The coverage model itself was not changed to produce smaller numbers.

### Regression and Android acceptance

- Full shared automated suite: **243 PASS** (`.local/coverage-tests.log`), retaining all prior 240 checks and adding multi-faction supplemental/declaration tests, singular/plural extraction and real-source regressions.
- Offline source-audit tests: **11 PASS** (`.local/coverage-audit-tests.log`), covering source-ID deduplication versus name collisions, raw/canonical counts, parent/primary/force/group visibility, allied roots, broken references, candidate classification, completeness semantics and edition filtering. **254 automated checks total**, with the two suites reported separately.
- Desktop build: **PASS**, zero warnings/errors (`.local/coverage-desktop-build.log`).
- Extended WPF smoke: **PASS** (`.local/tests/efdfddd3-a09b-4d9d-90e4-6ed5edeeea75/coverage.png.result.txt`).
- Android Release publish using the existing AndroidTest profile: **PASS**, no warning/error diagnostics (`.local/coverage-android-publish.log`). Existing SDK/JDK/API configuration unchanged.
- Shared portable pack replay and existing bidirectional serialization/SQLite tests: **PASS**. Equipment-edit tests continue to assert **zero reference source reads, parsing and merging**, stable reference indexes, zero dataset reads and one calculation.

Latest APK: `artifacts/android/GrimList-Android-Test.apk`, 64,473,023 bytes, SHA-256 `347B289E55FF4962844456D2E90FBAC58E9E0EDFF1D939369EE0FF1E1C5BFF8E`. Signature verified against the same development certificate; package/version/minimum API 30/target API 36/ARM64+x64 are unchanged. Previous APK is `.local/GrimList-before-coverage-audit.apk`.

The phone is **disconnected** (`adb devices -l` has no device). For this latest build: native deployment, launch, reference import, filtering, persistence and export round trip are **NOT RUN**, both automatically and manually. The prior build's successful launch is not evidence for these new acceptance steps. No app permission, security bypass or testing-only production behavior was added to overcome the earlier `INJECT_EVENTS` tooling restriction.

Manual checklist: **`docs/runbooks/android/TEST-REFERENCE-COVERAGE-ON-PHONE.txt`**. It supplies exact expected Death Guard/Virulent Vectorium, Sisters/Hallowed Martyrs and Orks/War Horde cards, selected-detachment filtering, cancel/error safety, save/force-stop/relaunch/edit/reopen and Android→Windows→Android dataset checks. Sisters/Orks should display their BSData rules; their missing stratagem text must still be reported as missing. Manual results remain unreported, not assumed PASS.

Reproduction and audit limitations: `tools/reference_audit/README.txt`. The portable pack is complete for the collected input records, **not a complete all-faction stratagem library**. Filling the remaining gap requires suitable source content.

## Generic shared reference library — 2026-10-05

This section is the latest reference-content verification. Earlier APK hashes and runtime notes below are historical.

### Inspection and minimum changes

The original shared `ReferenceCard` already held faction, section, name, text, detachment, cost and `SourceInfo`. Cards entered through BSData reference parsing, structured dataset/rules JSON and the old data-tool Death Guard text converter. Both frontends already serialized/deserialized `Dataset.ReferenceCards` through shared source-snapshot JSON and SQLite. Android could transfer existing content; it could not supply absent text. Association previously depended principally on faction/detachment names and section names. `ArmyReferences` was already the common filtering entry point.

Read-only comparison found 45 supplemental Death Guard stratagems in the Windows active SQLite snapshot. Their recorded source is edition 11, Faction Pack 1.2, 2026-08-26. The same cards exist in the previously exported full library and reference-only pack. They are imported data, not a Windows-only runtime store. The old `DeathGuardReferenceText` converter was faction-specific; it has been removed along with its data-tool command. Existing card text is preserved as structured input to the generic adapter. No replacement text was generated or downloaded.

The supplied phone export, `AndroidDetachFixExport/grimlist-points.json`, has 2,194 units and 3,817 reference records, but **zero stratagems**. Its BSData revision is `da7248777da54218034f09b6271ad89ff6b68a77`, dated 2026-10-04. Its SHA-256 remains `1790DD635C237706C78EB05C26CEBA7CF3822846C2DCC289DABD3E9856B7B8F9`. This confirms a source-content gap rather than an Android serialization loss.

The implementation extends existing projects and storage boundaries:

- Core: canonical `ReferenceCard.Details`, source ownership, stable/source IDs, structured text, multi-detachment applicability, semantic hashes and `ReferenceCoverage`. `ReferenceMerge` provides deterministic preview decisions, explicit conflicts, owner-scoped complete-source removals, tombstones and replacement identities. Unknown legacy editions remain unidentified rather than inheriting a later catalogue edition.
- Application: `IReferenceSourceAdapter`, BSData catalogue and structured JSON adapters, bounded stream parsing and `LibraryWorkflow.PrepareReferenceImportAsync` / `ApplyReferenceImportAsync`. Explicit Apply uses the existing atomic transaction and stale-preview guard. Cancel, malformed input and unresolved conflicts cannot overwrite active data.
- Persistence/export: schema-2 dataset JSON in the same Windows/Android SQLite schema. Schema-1 input remains readable. Both export actions use the same canonical projection, retaining cards, applicability, provenance and coverage. Reference-only imports preserve unit data, prices, catalogue provenance and saved armies.
- Filtering/performance: `DatasetSnapshot.References` owns reusable indexes and precomputed coverage. Both frontends call the same `ArmyReferences` API. Normal equipment edits retain the index and assert **zero reference source reads, zero parsing and zero merges**, alongside existing zero-dataset-read/one-calculation assertions.
- UI: import previews show reference changes, matching, conflicts and coverage; conflicts disable Apply. Army displays show only applicable references, with structured fields and coverage. Enhancements/dispositions that affect roster legality stay in their established army-building path; display imports cannot replace them.

Source precedence is never inferred. Users can cancel and correct conflicting input; the shared API also accepts an explicit preferred-owner map. Stable IDs take priority over names, unknown IDs never silently fall back, and ambiguous/unresolved scopes remain stored for audit but hidden from army displays. Partial inputs never delete absent cards. Only an authoritative complete declaration can retire that owner's absent cards in its stated edition/category/scope.

### Content audit and multi-faction evidence

Real-catalogue shared-service tests use the pinned local BSData archive `cc1830fbfead059f8059ed6c7f8e73cd4c390908` and the existing Windows supplement:

| Faction | Source adapter | Imported display cards | Source detachments |
|---|---|---:|---:|
| Adepta Sororitas | BSData catalogue | 10 army/detachment references | 8 |
| Orks | BSData catalogue | 38 army/detachment references | 17 |
| Death Guard | Structured JSON | 45 stratagems | 9 |

The pinned BSData archive contains no typed Stratagem profile definitions. Typed stratagem parsing, linked profiles and multi-detachment applicability are therefore also covered by explicit synthetic format fixtures. These fixtures are tests only and are not included in the delivered pack or user libraries. The real Sisters/Orks evidence proves the shared reference pipeline, not missing stratagem coverage.

`artifacts/android/GrimList-References.json` is a generic reference-only pack: 970 existing BSData army/detachment cards plus the 45 existing supplemental stratagems, with individual provenance intact. It contains no units, prices, enhancements or disposition rules. It does not claim all-faction stratagem content. The existing full-library artifact is older than the phone export and should not replace the phone's newer unit library for this test.

The all-faction report is `artifacts/android/Reference-Coverage.json`, generated from a separate preview merge of the supplied phone export and existing supplement:

| Scope | Count |
|---|---:|
| Factions | 36 |
| Detachments | 823 |
| VerifiedComplete | 0 |
| Complete | 0 |
| Partial | 9 |
| Unknown | 814 |
| Unresolved reference matches | 0 |

Before the supplement, all 823 detachment stratagem scopes were Unknown. Six cards for Virulent Vectorium do not establish completeness: there is no authoritative completeness declaration. The audit describes available source content, not a completed import on the physical phone.

### Regression and portability results

- Phase build/test gates were run throughout implementation; final desktop build **PASS**, zero warnings/errors: `.local/generic-references-desktop-build.log`.
- Full automated suite **240 PASS**: `.local/generic-references-tests.log`. Includes real-catalogue association constraints in both attachment orders, dynamic constraints, nested selections, repeated pricing and all existing persistence/history behavior.
- Extended WPF smoke **PASS**: `.local/tests/175aad45-a7a0-4ea5-8bbd-5594bcc02abd/generic-references.png.result.txt`. It covers imports, roster editing, references/stratagem colours, attachments, enhancements and persistence. An initial smoke run exposed unknown-edition legacy reference compatibility; that is fixed and covered by a new regression. A repeated run against an already-mutated smoke database hit duplicate fixture entry IDs; the final run used the fresh full-suite fixture.
- Shared Windows-compatible export → Android-compatible import → reverse export **PASS** through the canonical model, merge and shared SQLite workflow. This is host verification, not a claim of a completed native phone document-picker round trip.
- BSData update retaining supplemental cards, partial import retaining absent cards, complete-owner retirement, explicit conflict handling, ambiguous matching, cancellation/failure/stale preview, source preservation and SQLite reopen **PASS**.
- Android clean/build/publish **PASS** with the existing AndroidTest Release profile, no warning/error diagnostics: `.local/generic-references-android-clean.log` and `.local/generic-references-android-publish.log`. SDK 10.0.302, JDK 21, API 36/build-tools 36.0.0; target `net10.0-android`. No project reinstall or workload change.

Delivered APK: `artifacts/android/GrimList-Android-Test.apk`, 64,334,885 bytes. SHA-256 `4EC401C998A19A658B9F74CCFD8E6F2553491FFF61E9F9F5AC230B6D032F478F`. Package `com.grimlist.mobile`, version 0.1/code 1, minimum API 30, target API 36, ARM64/x64. Signature verification passes with the existing development certificate SHA-256 `9be9c191c53dc33ee955dcaedad94868fe4a4e6817a1fb30187ac0baf7f28f25`. Previous APK retained at `.local/GrimList-before-generic-references.apk`.

### Physical phone status

Connected device: Xiaomi model `22101316UG` (`rubypro`), Android 14/API 34. This is the actual phone, not an emulator.

| Step | Result |
|---|---|
| Install new APK over existing app, preserving storage | PASS (`adb install -r`: Success) |
| Launch updated app | PASS; existing Death Guard/Virulent Vectorium rules screen restored |
| Capture startup diagnostics | PASS; no unhandled exception found in captured logcat window |
| Stage generic pack in Downloads | PASS; `GrimList-References.json` copied |
| Automated taps/document picker | BLOCKED by phone `INJECT_EVENTS` permission denial |
| Native reference preview and explicit Apply | NOT RUN |
| Cancelled/failed native reference import | NOT RUN |
| Native selected-detachment stratagem/content display | NOT RUN |
| Save → force-stop → relaunch → compare → edit/save/reopen after reference update | NOT RUN |
| Native dataset export back to Windows | NOT RUN |
| New physical-device performance measurements | NOT RUN |

The phone's Release app also correctly rejects `run-as` because it is not debuggable. No root access, database replacement, storage clearing or security bypass was used. No existing army was edited. Screens/UI and captured logs are under `.local/phone-reference-check`. The user has been asked to enable Xiaomi's USB debugging security setting, or choose manual testing. Launch alone does not complete reference or persistence acceptance.

The generic importer capability is implemented and shared. All-faction source coverage is still incomplete, and native phone reference acceptance remains pending as listed above.

Runtime verification on 2026-10-03 uses **Medium_Phone_API_35**, Android 15 / API 35, x86_64, Debug configuration. The earlier toolchain and emulator blockers are superseded. The core save/process-death/restore/edit/reopen workflow passes. Architecture and rules ownership remain unchanged. Statuses below describe Android observations and explicitly retain untested items; desktop tests are reported separately.

The latest **2026-10-04 desktop-feature parity and keyword-colour pass** is recorded at the end of this document; it supersedes earlier statements that mobile PDF/data-management tools are unavailable. The earlier acceptance tables remain historical evidence for the original prototype; the new section identifies which paths were exercised again through the redesigned controls.

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

## Acceptance status

The required **save → force-stop → relaunch → restore → compare → edit → save → reopen** sequence passed on Android. The following rows distinguish that completed milestone from remaining or unavailable checks.

| Runtime check | Status | Evidence / scope |
| --- | --- | --- |
| `ArmyWorkspace` initializes | PASS | Loaded editor and imported/saved state on initial and subsequent launches |
| Android SQLite initializes in MAUI app data | PASS | Actual database, Android provider, runtime maps and integrity checks |
| Shared schema initializes and reopens | PASS | Nine shared table definitions match Windows |
| Startup without unhandled exception | PASS | Recorded process logcat; historical input ANRs are described above |
| Native Android document picker opens | PASS | `ACTION_OPEN_DOCUMENT`, DocumentsUI screenshot/log |
| Document stream reaches `LibraryWorkflow` | PASS | Full JSON parsed through `OpenReadAsync()`; no document path consumption |
| Import preview generated | PASS | 2,194 units, 4,162 price changes, zero affected armies at initial import |
| Preview does not apply automatically | PASS | Database still had zero sources before pressing Apply |
| Explicit Apply updates library | PASS | One source, 2,194 units, 3,748 price rows and 823 detachments |
| Factions and detachments appear | PASS | Death Guard and Virulent Vectorium selected |
| Create named 2,000-point army | PASS | Android DG Acceptance persisted |
| Saved-army picker / open | PASS | Same army reopened from picker |
| Add imported unit and size | PASS | 10-model Plague Marines, then the two characters |
| Select roster entry and model | PASS | Marine Model 2 context persisted across process death |
| Change equipment | PASS | Champion power fist replaced plague knives |
| Mandatory/default equipment | PASS | Imported default equipment present; replacement retained valid required choices |
| Equipment limits | PASS | Additional power fist increment disabled at limit |
| Shared points recalculation | PASS | 175 → 235 → 295 → 305 with enhancement |
| Shared validation refresh | PASS | Warlord/character warnings updated; final Army validated |
| Plaguecaster then Putrifier attachment | PASS | Both attached to the same Marine entry |
| Putrifier then Plaguecaster attachment | PASS | Both first detached, then attached in the reverse order; saved state matched |
| Warlord | PASS | Plaguecaster persisted as warlord |
| Eligible enhancement and cost | PASS | Putrifier's Daemon Weapon of Nurgle adds 10 points |
| Attached-group enhancement restriction | PASS | Second enhancement attempt rejected with shared restriction message |
| Undo | PASS | Removed enhancement and its 10 points |
| Redo | PASS | Restored enhancement and its 10 points |
| Explicit Save | PASS | Android SQLite snapshot contains committed selections |
| Save/reopen selections and calculated values | PASS | Full entry/loadout comparison; 305 points, Army validated |
| Background/resume | PASS | Same process and identical persisted state |
| Destroy/recreate activity | PASS | Android destroy/create events in same process; usable restored editor |
| Force-stop without data clearing | PASS | Old process ended; app data retained |
| Relaunch after process death | PASS | New process successfully loaded library and army |
| Restore selected army / editor context | PASS | Same army, Marine entry, model index 1 and equipment section |
| Compare all committed state after death | PASS | Entries, loadouts, attachments, warlord, enhancement, sizes and settings matched |
| Recalculated points/validation after death | PASS | 305 points; Army validated |
| Undo/redo empty after process death | PASS | Both controls disabled |
| Edit/save/reopen after restoration | PASS | Detached/rebuilt attachments, explicitly saved, reopened from picker; all entries matched |
| Invalid two-ordinary-Leader attachment | PASS | With Putrifier detached and one Plaguecaster attached, a second ordinary Plaguecaster had no eligible Bodyguard target; native picker was empty |
| Cancel native document selection | PASS | Returned to editor; source and army remained unchanged |
| Invalid/failed JSON source load | PASS | Handled parse error; database comparison matched army, entries and settings |
| Missing-price warning | PASS | Plague Sorcerer [Crucible] shows imported Points unavailable warning; no entry added and 305-point army preserved |
| Failed operations preserve saved data | PASS | Failed source and enhancement operations retained the recorded army |
| Windows-only runtime dependency isolation | PASS | Android SQLite and shared assemblies loaded; no WPF/winsqlite3/Windows dialog/PDF executable required |
| Resize existing unit | NOT RUN | No resize control in current prototype |
| Duplicate roster entry | NOT RUN | No duplicate control in current prototype |
| Remove roster entry | PASS | Temporary second Plaguecaster removed through shared session |
| BSData ZIP import on Android | NOT RUN | Optional second import path not exercised in this run |
| Cancel an in-progress parse or reject an existing-source preview | NOT RUN | Native document cancellation was tested; these separate cancellation paths were not |
| Physical-device / Release performance | NOT RUN | Debug emulator observations only |

PDF and final visual design are outside this milestone.

## SQLite and lifecycle evidence

Android uses `AndroidSqliteConnectionFactory` and Android's `/system/lib64/libsqlite.so`. The database is `/data/user/0/com.grimlist.mobile/files/grimlist.db`, MAUI app data. All nine shared table definitions match the desktop database; Android additionally owns `android_metadata`. Shared initialization, transactions and UPSERT-based save/context operations completed, and snapshot integrity checks returned `ok`. Fresh initialization and current-schema reuse pass; migration from a historical Android installation was not separately exercised.

The force-stop comparison used saved snapshots before and after process replacement (PID 17883 to 19708). Army metadata, every entry's full loadout, model counts, attachments, enhancement and warlord matched, excluding only the expected updated timestamp. Active-source and `editor_context` settings matched exactly, including the Plague Marines entry, model index 1 (**Model 2**) and `equipment` section. The UI recalculated **305 / 2000 pts**, **Army validated.** Undo and Redo were disabled after process recreation, as designed.

After restoration, both characters were detached and reattached in the opposite order, explicitly saved and reopened from the saved-army picker. After the negative tests, the temporary extra character was removed and the valid group restored, saved and reopened again. `final-reopened-comparison.json` records matching army metadata, all entry/loadout fields and settings against the earlier reopened baseline. The final device state contains the clean three-entry army at 305 points with clear validation. Opening from the saved-army picker intentionally selects the first roster entry; this is separate from process restoration retaining the saved model context.

Background/resume retained the same process and identical persisted state. An initial `always_finish_activities` setting attempt only produced stop/resume events. A subsequent `am start -W -R 2` finished and recreated MainActivity: Android logged destruction of activity 25000836 and creation of 251384511 while PID 19708 remained alive. The recreated editor was usable and still showed 305 points and clear validation. The original attempted recreation is not counted as a separate pass.

Runtime maps show the shared Core/Application/Persistence assemblies and Android SQLite. No `winsqlite3.dll`, WPF/PresentationFramework, Windows platform assembly, Windows dialog API or `pdftotext.exe` was required. PDF remains out of scope.

## Defects demonstrated and narrow fixes

1. The full exported JSON exceeded the shared 50 MB stream guard. The guard now allows 128 MB, with a new service regression checking a 51 MB stream and no automatic application of its source.
2. Searching initially disabled the form on the first keystroke. Retesting also exposed Android input-dispatch ANRs while search/detail rule queries ran on the UI thread (19:34:59 and 19:40:00). Search now debounces and discards obsolete queries; existing shared detail queries run on the session worker before controls render. No rules implementation or architecture was replaced.
3. Logcat reported unclosed Android cursors. The Android adapter now explicitly closes each query cursor in `finally`. Subsequent process logs show no new cursor-close warnings or unhandled exceptions.
4. Adding the unpriced Plague Sorcerer [Crucible] silently did nothing and displayed a success status. The mobile Add handler now displays the imported pricing warning when no imported size/points option can be selected. It does not invent a price or bypass the shared service.

The earlier ANRs are retained as failure evidence; successful retesting does not erase them. Long Debug emulator waits remain and are recorded below. No production desktop UI was changed for this runtime work.

## Performance observations

These are approximate observations on the API 35 x86_64 emulator, Debug, with the complete 2,194-unit source above. They are not physical-device performance claims or isolated Core benchmarks.

| Operation | Observation |
| --- | --- |
| Cold application/workspace restoration | About 83-102 seconds until the loaded editor is usable; Android activity launch alone was about 6.6-10 seconds |
| Full JSON read/parse/preview | Several minutes, approximately 4 minutes; phases were not separately instrumented |
| Source apply | Approximately 30 seconds, including refresh |
| Library refresh during editing | Debug timing logs: approximately 15-29 seconds |
| Detail preparation | Approximately 0.6-1.1 seconds for the selected Marine model; 16-19 seconds for a character |
| Search | Approximately 16-35 seconds to update results |
| Edit through usable refreshed UI | Approximately 35-80 seconds in observed runs |
| Opening an army from saved picker | Approximately 35 seconds until refreshed editor in the recorded reopen run |
| Core recalculation alone | NOT RUN as an isolated measurement |
| Full BSData ZIP parse | NOT RUN on Android |

Moving work off the UI thread addressed demonstrated input ANRs; it did not make these latency figures acceptable for a finished UI. Further profiling is separate from correctness acceptance.

## Desktop regression

`dotnet build GrimList.slnx -m:1`: **PASS**, zero warnings/errors. Automated executable suite with the optional real-catalogue diagnostic: **185 PASS** (181 regular checks plus four real-catalogue checks). Extended WPF smoke: **PASS**, covering persistence, roster/loadout editing, imports, attachments, detachments, enhancements and muster behavior.

The earlier quick-add smoke failure was a **test timing problem**. The control and its unit-key Tag still existed and the helper searched the correct visual tree, but replacement item templates had not been realized after an ItemsSource rebuild. The generic lookup now completes WPF layout before searching. The smoke also clicks the actual quick-add button and verifies saved counts. No production UI change was needed for that failure.

## Local evidence

Evidence lives in `.local/android-acceptance/`: source metadata/hash, import-preview screenshot and unapplied database, applied-library database, schema comparison, Android association data, runtime maps, ANR traces, restored/final process logcat, activity recreation events, document intents, screenshots and before/after database comparisons. The final process is PID 24219; the last recorded ANR remains the earlier 19:40 failure, with no new unhandled exception or cursor-close warning in the final run. The snapshot files are local test artifacts and include the imported dataset and test army.

Build/deployment logs: `.local/android-final-install.log`, `.local/android-cursor-fix-build.log`, `.local/android-missing-price-install.log`. Desktop logs: `.local/android-acceptance/desktop-build.log`, `desktop-tests.log`. Latest regression artifacts: `.local/tests/4adc3774-41a5-4eb3-b924-efcf1189d15d/`.


## Use an existing library

Export the active dataset from Windows using **Export dataset**, transfer that JSON file to the device, and choose **Load library JSON / BSData ZIP** in the prototype. A complete BSData ZIP is also supported; select its matching edition. Review and apply the preview.

The JSON route preserves the structured imported data, including prices, profiles, loadouts, attachment rules and associations. It does not transfer Windows saved armies: a dataset export contains the library only. Android armies are saved locally by the prototype. Database transfer and account/cloud synchronization are outside this first UI milestone.

## Acceptance workflow

1. Start the app and load an existing library, or reopen a previously imported Android library.
2. From Home, choose **New army**, a faction and detachment, enter a name and points limit, and create the army. Use 2,000 points for the acceptance case.
3. Select a library unit and imported size and add it.
4. Tap its roster card, open **Equipment**, select a model and change equipment. Confirm default replacements, limits, points and validation follow the imported rules. **Equipment** includes unit size. Leader/Support attachment controls are at the bottom of **Datasheet**, above **Abilities**. Hold a roster card for **Delete/Duplicate**, or use its remove button directly.
5. Add eligible Leader/Support entries. Select a candidate in the attachment controls and attach it. Test both attachment orders for a supported multi-character group.
6. Select a warlord. Optionally equip an eligible enhancement and confirm its points and attached-unit slot restrictions.
7. Exercise undo/redo. Each committed command saves through the shared session automatically, matching desktop behaviour; there is no separate Save army menu item in the consolidated menu. Note the roster total, validation messages, attached entries, equipment and selected model.
8. Background and resume the app. Recreate the activity using the development tools and check that the workspace remains usable.
9. Force-stop the app without clearing its storage, launch again and confirm that the selected army/context is restored and committed equipment/attachments/totals agree. Undo history should be empty after process recreation.
10. Open the same saved army from its Home tile and repeat an edit/save/reopen cycle. The original acceptance run used the earlier prototype's saved-army picker.

Also test a cancelled import, an invalid attachment, a missing-price entry, and a failed source load. None should silently replace the active library or lose saved army changes.

## Record evidence

Record the device/emulator, Android version, .NET/MAUI versions, source hash, build result, screenshots and observed validation results. Separate service-test success from Android UI/provider success. Keep PDF integration, final visual sign-off and performance claims pending until those paths are actually tested.

Use [the architecture guide](android-architecture.md) for service ownership and [development checks](development.md) for the Windows regression workflow.

## Reference-based mobile UI — 2026-10-04

The first mobile interface pass follows the eleven supplied images in `docs/design/android-reference-images`, using GrimList branding and the desktop faction palette. Production changes are confined to mobile presentation. Core, Application, platform adapters, persistence/schema and WPF source are unchanged. Mobile source before this pass is backed up in `.local/android-ui-before.zip`.

| Reference | Implemented presentation |
| --- | --- |
| `HomePage.jpg` | Faction-coloured saved-army tiles, New army, search, sorting and per-army menu |
| `NewListFaction.jpg` | Faction tiles grouped by Chaos, Imperium, Space Marines and Xenos |
| `ListCreate.jpg` | Army name, points limit, imported detachment costs/budget and Legends filter |
| `EmptyList.jpg`, `ListEmptyPreviweUnits.jpg` | Persistent points/validation header, category accordions, searchable library and adjacent roster |
| `SampleListWithAttachedUnits.jpg` | Bodyguard/character groups, combined points, warlord and enhancement labels |
| `BurgerMenuExpand.jpg` | Initially reference-based; superseded by the Windows menu consolidation recorded below |
| `MortarionInspection.jpg`, `BiologusEnhancements.jpg` | Imported stats, defences, weapon profiles, abilities, enhancement costs/info, plus Equipment and Manage tabs |
| `ArmyRuleStratagems.jpg`, `Stratagems.jpg` | Expandable army/detachment rules, dispositions, enhancements and stratagems with shared turn-colour classification |

Equipment, model sizes, attachments, enhancements, warlord selection and undo/redo call the existing `ArmySession`. The UI displays results from the shared Core engine. Play mode hides editing controls and disables mutation actions. Search filters the prepared library view after a 250 ms debounce; it does not rerun the army engine for each keystroke. This is presentation work, not a second mobile rules implementation.

### Emulator checks through the new UI

Environment: **Medium_Phone_API_35**, Android 15/API 35, x86_64, Debug, same toolchain and 2,194-unit edition-11 catalogue recorded above. Separate test army: **Android UI Reference Test**, ID `66933201-db86-449e-bcc9-9b35396182c5`, Death Guard / Virulent Vectorium / 2,000 points. Its name was temporarily changed to **Android UI Reference Saved** for the process-death test, then changed back after restoration.

| Check | Status | Observed result |
| --- | --- | --- |
| Android build and deployment | PASS | Final MSBuild Install: zero warnings/errors; actual emulator launch |
| Existing library and saved army open | PASS | Existing Android DG Acceptance displayed at 305 points with its attached characters, warlord and enhancement |
| Home, faction and creation screens | PASS | Created a separate named 2,000-point army with imported detachment budget |
| Unit search, imported size and add | PASS | Added 10-model Plague Marines, Malignant Plaguecaster and Biologus Putrifier |
| Library preview and roster datasheet | PASS | Stats, weapon profiles and roster details displayed |
| Equipment/default replacement | PASS | Champion power fist replaces plague knives; imported mandatory/default controls are enforced |
| Model selection | PASS | Model 2 selected and later restored on the Equipment tab |
| Warlord | PASS | Malignant Plaguecaster selected |
| Enhancement and points | PASS | Biologus: Daemon Weapon of Nurgle, +10 points |
| Plaguecaster then Biologus | PASS | Both attached to Plague Marines through Manage controls |
| Biologus then Plaguecaster | PASS | Detached both, attached in reverse order; same valid group |
| Shared points and validation | PASS | 175 + 60 + 70 = **305**; **Army validated.** |
| Undo/redo | PASS | Undo removed the second attachment; Redo restored it |
| Rules and stratagems | PASS | Expanded imported rule text and displayed faction/core stratagem sections and turn colours |
| Read-only Play mode | PASS | Full-width roster; undo disabled; unit equipment/manage tabs hidden and warlord/enhancement mutation disabled |
| Rotation and menu access | PASS | Editor adjusts columns; all menu actions reachable by scrolling in landscape; original rotation settings restored |
| Back while editing search | PASS | Dismisses input focus without leaving the editor |
| Android document picker/cancellation | PASS | Native Downloads picker opened; cancellation returned to the same library |
| Invalid JSON source | PASS | Parse error shown; library/source tables remain identical to the earlier source snapshot; saved army remains usable |
| Save before process death | PASS | Committed a rename with non-empty undo history, then used Save army |
| Force-stop, launch and restore | PASS | No storage clearing; named army, Equipment tab and Model 2 restored |
| SQLite state comparison | PASS | `armies`, `entries` and `settings` match before/after process death, excluding modification timestamps; integrity check `ok` |
| Restored selections and calculations | PASS | Ten models, champion power fist, both attachments, warlord, enhancement, 305 points and valid status retained |
| Undo/redo after process recreation | PASS | Both disabled, as designed |
| Edit/save/reopen after restoration | PASS | Renamed back to Android UI Reference Test, saved, went Home and reopened the tile; 305 points and valid status retained |
| Unhandled runtime exceptions | PASS | No fatal/unhandled exception or application-ANR marker in captured final-process logcat |
| Fresh full JSON apply / BSData ZIP import in this UI pass | NOT RUN | Existing imported library used; successful JSON acceptance remains recorded in the earlier run; Android ZIP acceptance remains pending |
| Resize, duplicate/remove and every negative rule path in this UI pass | NOT RUN | Controls delegate to existing shared services; these paths were not all repeated through the redesigned UI |
| Explicit background/resume and forced activity recreation in this UI pass | NOT RUN | Rotation and full process-death restoration were tested; earlier lifecycle evidence remains above |
| Physical device, large font settings and final visual sign-off | NOT RUN | This is the first reference-based interface pass |

Two presentation issues found during this pass were corrected and retested: Back navigating away from a focused input, and the menu's lower actions being clipped in landscape. No shared-engine or architecture change was required. The invalid-source parser alert is an expected handled failure, not a startup/runtime crash.

Observed Debug emulator waits were approximately 35–45 seconds from activity launch to usable restored workspace, 22–23 seconds to reopen an army, 3–12 seconds for detail/tab preparation, and 12–29 seconds for a command plus refresh. These include UI-automation polling and are not isolated engine benchmarks. Dedicated search timings, Release/physical-device profiling, full import phase timings and broader performance optimization remain pending.

### Regression and evidence

- `dotnet build GrimList.slnx -m:1`: **PASS**, zero warnings/errors.
- Automated suite including real-catalogue diagnostics: **185 PASS**.
- Extended WPF smoke: **PASS**, including persistence, roster/loadouts, attachments, detachments, dispositions, enhancements and Play mode.
- Final desktop artifacts: `.local/tests/7bfb42e3-d96a-4e35-97ae-be07b26dfd5e/`.
- Build/test logs: `.local/android-ui-final-install.log`, `.local/android-ui-desktop-build.log`, `.local/android-ui-final-tests.log`.
- Android evidence: `.local/android-acceptance/ui-before-death.db`, `ui-after-death.db`, `ui-after-death-comparison.json`, `ui-persistence-assertions.json`, `ui-runtime-before.log`, `ui-runtime-final.log` and `ui-*.png` screenshots. Final captured process: PID 7888.
- Useful screenshots: `ui-final-home.png`, `ui-final-roster.png`, `ui-biologus-enhancement.png`, `ui-rules.png`, `ui-stratagems.png`, `ui-landscape-menu-fixed.png` and `ui-process-restored.png`.

PDF integration, final visual refinement and the remaining acceptance checks are separate follow-up work. Desktop styles and functionality remain intact.

## Windows menu consolidation — 2026-10-04

The Windows roster menu in `MainWindow.OpenRosterMenu` is now the source of the Android menu's names, descriptions and grouping. The single top-right menu contains **Home**, **New army**, **Export army**, **Update from BSData**, **Play mode / Build mode**, **Army rules**, **Manage data**, **Export dataset**, **BSData ↗** and **Delete army**. Army-specific actions appear when viewing an open army. The five Windows burger actions are present exactly once; the requested toolbar actions are consolidated into the same menu.

Menu rows use desktop surface/input/text colours, icons, bold titles, smaller descriptions, separators and the destructive-action colour. The menu scrolls in landscape. Unit preview and roster remain separate compact screens. Build mode displays all weapon alternatives; Play mode displays equipped weapons/counts and hides editing controls. Shared attachment modifiers remain active; the contextual Target is Afflicted switch follows desktop behaviour. Mobile-only weapon-display toggles, Save army and Share army menu entries were removed. Commands continue saving through `ArmySession`.

Mobile-only presentation and document-save integration changed. Core, Application, Persistence, shared contracts, schema and WPF production source were not changed. Backup before this pass: `.local/android-before-windows-menu.zip`.

### Android checks for this change

Device: **Medium_Phone_API_35**, Android 15/API 35, x86_64, Debug. Existing army **Android DG Acceptance** remained at **305 / 2000 points**, **Army validated.** Library: edition 11, 2,194 units, BSData revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908`, source hash `747db24bf29456f0606a937f794a7e7f33154e76116eff1506e9b0750ab5c045`.

| Check | Status | Evidence/result |
| --- | --- | --- |
| Android build, deployment and relaunch | PASS | Final Install build: zero warnings/errors; existing army restored on the running emulator |
| Windows menu actions and labels | PASS | Compared desktop menu declarations with Android native accessibility tree; `.local/android-acceptance/windows-menu-parity.json` |
| Portrait and landscape access | PASS | All rows visible in portrait; scrolled to Delete army in landscape; restored rotation settings |
| Build/Play toggle | PASS | Menu label changes; Play hides undo/redo and editing controls; Build restores them |
| Library preview → Play mode | PASS | Switched to selected roster Plague Marines; preview Add button no longer present |
| Army rules | PASS | Imported Death Guard rules opened from the menu |
| New army and Delete army | PASS | Created and deleted **Menu verification disposable**; reopened original army at 305 points |
| Delete cancellation | PASS | Cancel retained the original army |
| Text export | PASS | Native document picker saved `grimlist-menu-test.txt`; content includes all three units and 305 points |
| Yellowscribe roster export | PASS | Native picker saved `grimlist-menu-fixed.ros`; valid BattleScribe roster XML with original army ID/name |
| Export dataset | PASS | Native picker saved `grimlist-menu-dataset.json`, 52,836,770 bytes; valid JSON with 2,194 units |
| Native save cancellation | PASS | Returned to export screen without a success message or mutation |
| Manage data / local-source cancellation | PASS | Edition/layer choices and native document picker opened; cancel retained library and army |
| Update from BSData cancellation | PASS | Download/parser started; cancellation alert returned to existing army, without applying a source |
| Full exported JSON → import preview within three minutes | FAIL | Stream read completed, but shared preview preparation exceeded the 180-second automation timeout; no source was applied |
| Full JSON preview/apply/cancel completion in this pass | NOT RUN | Preparation was still running when cancellation was requested; process restarted without clearing storage |
| Restore after stopping the pending preview | PASS | Original three-unit army, warlord, enhancement, 305 points and valid status restored; all ten menu actions present |
| Final-process unhandled exceptions | PASS | No fatal/unhandled exception or app-ANR marker in captured PID 16007 logcat |
| Fresh BSData download → preview → apply | NOT RUN | Download cancelled intentionally; no library update applied in this menu pass |
| Yellowscribe network upload / external BSData browser launch | NOT RUN | Actions wired; no roster uploaded during testing |
| PDF export | NOT RUN | Format is visible with an explicit Android-unavailable message; platform integration remains pending |

The native document-save test exposed the picker appending `.xml` when the roster used a generic XML MIME type. The mobile exporter now supplies a roster MIME type; retesting produced the correct `.ros` extension and readable XML. Saving uses `ContentResolver.OpenOutputStream`, not a filesystem path.

Full-library preview performance is unresolved. During `Preparing import preview…`, the process remained active at approximately 165% CPU, 681 MiB RSS and 883 MiB total PSS including swap on the 2 GiB emulator. These are one-off Debug observations, not physical-device benchmarks. The current shared `LibraryWorkflow.PrepareAsync` checks cancellation before and after synchronous preparation, so cancellation cannot interrupt its middle stages immediately. No architectural/performance change was made for this menu task. This is not evidence of successful full-library apply through the new screen; retain that acceptance item as pending. Evidence: `menu-preview-timeout.png`, `menu-runtime-before-restart.log`.

### Regression and remaining scope

- Desktop solution build: **PASS**, zero warnings/errors (`.local/android-menu-desktop-build.log`).
- Automated checks: **185 PASS**, including `--attachment-diagnostic tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/death-guard` (`.local/android-menu-tests.log`).
- Extended WPF smoke: **PASS**, including the real-catalogue fixture; `.local/tests/528137d9-ddd5-4593-841f-d077584fd2b1/android-menu-regression.png.result.txt`.
- Android install log: `.local/android-menu-final-install.log`.
- Screenshots and exports: `.local/android-acceptance/menu-*.png`, `grimlist-menu-test.txt`, `grimlist-menu-fixed.ros`, `grimlist-menu-dataset.json`.
- Final running-app evidence: `.local/android-acceptance/menu-final.png`, `menu-final-assertions.json`, `menu-runtime-final.log`. App left on the original army with its menu open.

This completes the menu consolidation, not every remaining desktop feature or visual detail. Android PDF integration and the other Windows data-management providers/repair tools remain pending. Existing Android persistence acceptance above is unchanged; this pass did not repeat every editing or negative-rule scenario. Emulator responsiveness remains a separate performance task.

## Standalone phone APK and wargear references — 2026-10-04

This section records the first phone APK; the updated package/hash is in the roster-actions section below.

The phone test package is [`artifacts/android/GrimList-Android-Test.apk`](../artifacts/android/GrimList-Android-Test.apk), **64,464,831 bytes** (about 64 MB). It is a standalone, development-signed **Release** APK containing ARM64 phone and x64 emulator code. Minimum Android version: **11 / API 30**; target/compile API: **36**. Version: **0.1**, package `com.grimlist.mobile`. Both architectures contain an embedded assembly store; fast deployment and a PC connection are not required. APK v3 signature verification passes, and the manifest is not debuggable.

SHA-256: `1AA5460369F852B092BEAB6EEC27F40B54C0B0BE966B62BF76835A2BD14D7EF9`.

### Install on a phone

1. Copy `GrimList-Android-Test.apk` and the accompanying `GrimList-Library.json` from `artifacts/android` to the phone's Downloads folder using USB or Quick Share.
2. Open the APK in Files. If Android requests it, allow installation from that Files app, then install and open GrimList.
3. Open **menu → Manage data → Choose local source**, select the JSON, review it and choose **Apply update**. The accompanying JSON contains the tested edition-11 library with 2,194 units. Alternatively, use a fresh Windows **Export dataset** JSON.
4. Choose **menu → New army**, select faction/detachment, enter a name and create the army.
5. Add a unit, open its roster card and select **Equipment**. Committed changes save automatically. Close/reopen the app to test restoration.

The JSON transfers library/rules only; Windows and emulator armies are not bundled into the phone package. Install subsequent APKs over the existing app to retain local armies. Full-library import preview speed is still pending physical-phone measurement; the earlier Debug emulator timeout remains recorded above. PDF integration is unchanged and remains pending. `docs/runbooks/android/INSTALL-ON-PHONE.txt` contains the same installation steps.

### Build configuration

The initial trimmed Release publish failed in the local ILLink **MSBuild task host**, with `MSB4216` and the resulting `MSB4027` during `ComputeManagedAssemblies`. This was a build-tool process failure, not a GrimList compile error. The checked-in `AndroidTest` publish profile disables trimming/AOT for this test package, preserves Release compilation and embeds all application assemblies. The resulting larger APK publishes successfully. Store signing and a trimmed/AOT release remain separate work.

```powershell
dotnet publish src/GrimList.Mobile/GrimList.Mobile.csproj -c Release -f net10.0-android `
  -p:PublishProfile=AndroidTest -m:1 `
  '-p:JavaSdkDirectory=C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot' `
  '-p:AndroidSdkDirectory=C:\Users\es0p0\AppData\Local\Android\Sdk' `
  -o artifacts/android
```

Use the generated **`com.grimlist.mobile-Signed.apk`**, copied to the friendly test filename above. The unsigned APK is not the phone installation artifact.

### Wargear presentation

`UnitWithWargearOptions.jpg` and `WargearAndEnhancementsPReview.jpg` now guide the Equipment view and roster summaries. The Equipment screen has an expanded composition/wargear card, selected-equipment summary and large single-choice buttons with a highlighted selected state. Imported groups allowing multiple selections retain quantity controls. Model selection remains available for squads. Collapse state and the equipment scroll position survive refreshes within the editing session.

Option labels/prices use `WargearPricing.Label`; bounds use the imported `LoadoutBound`; enablement and mutations use `ArmySession.CanChooseEquipment` / `ChooseEquipment`. Roster badges show selected paid equipment, non-default free choices and enhancement costs using shared model/pricing queries. The displayed total still comes from `ArmyResult`. No Defiler-specific rule, price, Core change, architecture change or WPF production change was introduced. Source backup: `.local/android-before-wargear-apk.zip`.

### Package and regression evidence

- Signed APK installed directly using `adb install -r` and visibly launched on **Medium_Phone_API_35**, Android 15/API 35. No MSBuild deployment or separate debug assemblies were used for this run. Existing app storage was retained; pre-install database backup: `.local/android-acceptance/before-phone-apk.db`.
- Desktop solution build: **PASS**, zero warnings/errors (`.local/android-wargear-desktop-build.log`).
- Automated suite with real-catalogue diagnostic: **185 PASS** (`.local/android-wargear-tests.log`).
- Extended WPF smoke: **PASS**, `.local/tests/64904ecb-84e5-424e-9048-b92827fa4896/android-wargear-regression.png.result.txt`.
- Publish log: `.local/android-phone-publish.log`; initial trimming-tool failure: `.local/android-phone-trimming-blocker.log`.
- Physical phone installation and performance: **NOT RUN** by the agent; the package is ready for the user's phone test.

### Standalone APK runtime results

These checks used the exact signed APK supplied above, on the existing API-35 emulator, with a separate **Android Wargear Phone Test** army (Death Guard / Virulent Vectorium / 2,000-point limit). The catalogue's Defiler base price is **300 points**; prices in the reference pictures were not hardcoded.

| Check | Status | Result |
| --- | --- | --- |
| Direct APK install and launch | PASS | `adb install -r` succeeded; existing library and local armies loaded |
| Create army/add Defiler | PASS | One-model imported size; default equipment; 300 points |
| First paid replacement | PASS | Heavy reaper autocannon selected; total 315 points |
| Second paid replacement | PASS | Two autocannons; total 330 points |
| Selected choices/scroll | PASS | Selected buttons highlighted; equipment refresh retained the working scroll position |
| Imported cross-group equipment limit | PASS | Selecting Electroscourge in one group disabled it in the second group; maximum one enforced by shared service |
| Undo/redo | PASS | Replaced/returned paid equipment; expected 315 ↔ 330 totals |
| Free replacement | PASS | Ectoplasma destructor selected; total remained 330 |
| Roster equipment preview | PASS | `2 × Heavy reaper autocannon / 30 pts` and selected free cannon displayed |
| Enhancement preview | PASS | Existing Biologus roster card displayed `Daemon Weapon of Nurgle / 10 pts` |
| Validation | PASS | Correctly reports missing eligible warlord and missing Character for the deliberately single-Defiler test army |
| Force-stop/relaunch | PASS | Equipment, 330 points and exact validation messages restored; undo/redo disabled |
| Edit after restoration, save and reopen | PASS | Changed free cannon back to default, returned Home and reopened test army; 330 points, two paid weapons, free replacement badge removed |
| Final-process exceptions | PASS | No fatal/unhandled exception or app-ANR marker in captured PID 19175 logcat |

Evidence: `.local/android-acceptance/wargear-release-assertions.json`, `wargear-before-restart.json`, `wargear-release-runtime.log`, `wargear-options-initial.png`, `wargear-options-selected.png`, `wargear-roster-selected.png`, `wargear-restored.png`, `wargear-final-roster.png` and `wargear-enhancement-preview.png`. The test army remains available in the emulator. No performance conclusion for physical phones is implied.

## Roster actions and datasheet sections - 2026-10-04

The refreshed standalone APK is `artifacts/android/GrimList-Android-Test.apk`, **64,473,023 bytes**, SHA-256 `27EE135493141740833D808F2AAC9BE3A002CE982639409954F04C55513025DB`. It retains the AndroidTest Release configuration, ARM64/x64 support and development signing key described above. APK v3 signature verification passes. Install it over the existing app to preserve local data.

### Changes

- Removed the unit **Manage** tab. **Equipment** now includes unit-size choices. Older stored `manage` editor context falls back to Datasheet.
- Hold a roster card for **Delete / Duplicate / Cancel**. Delete asks for confirmation. The inline remove button also works without opening a preview. Normal tap still opens the datasheet; scrolling cancels the tap/hold gesture. Play mode exposes no delete/duplicate actions.
- Moved **Leaders & Support** below the weapon profiles and immediately above **Abilities**. Both attach and detach actions use the existing shared `ArmySession`.
- Datasheet abilities and universal weapon-rule definitions are separated. **Keywords - Universal Weapon Rules** uses shared `WeaponRuleReference` and imported rule text, including keywords granted by attachments. **Unit Keywords** separately lists faction/type tags. No mobile rule definitions or pricing were introduced.
- Roster mutations, persistence and undo/redo continue through the shared session. Core, schema, application architecture and WPF production code are unchanged.

### Runtime checks

Run on **Medium_Phone_API_35**, Android 15/API 35, using the standalone signed APK installed with `adb install -r`. Editing checks used **Android UI Reference Test (copy)**, army ID `1e2c2568-fd04-4c7d-af14-9202fe287c3c`, with the existing edition-11 library.

| Check | Status | Evidence/result |
| --- | --- | --- |
| Clean build, APK signature, install and visible launch | PASS | Clean Release publish; installed APK starts and loads existing library/armies |
| Hold opens action sheet without preview | PASS | Native touch hold on Plague Marines shows Duplicate/Delete/Cancel |
| Duplicate | PASS | Shared command duplicates Marines; total 305 -> 480; army remains valid |
| Delete cancellation | PASS | Cancelled confirmation retains roster and unit |
| Inline remove without preview | PASS | Removed duplicate; total returns to 305 |
| Hold -> Delete -> confirm | PASS | Removed original bodyguard from copied army; total 130 |
| Undo/redo | PASS | Duplicate removal undone/redone, 305/480; bodyguard deletion undone and attachments restored |
| Tap/scroll behaviour | PASS | Tap opens datasheet; drag on roster leaves roster open |
| Play mode | PASS | Hold exposes no mutation menu; remove buttons absent |
| Manage removal / size controls | PASS | Only Datasheet and Equipment tabs; Equipment contains size choices with current size disabled |
| Attachment placement | PASS | Leaders & Support appears immediately before Abilities; screenshot below |
| Plague Marines + Biologus then Plaguecaster | PASS | Detach both and reattach in this order via relocated controls; 305, Army validated |
| Plague Marines + Plaguecaster then Biologus | PASS | Repeat in reverse order; 305, Army validated |
| Ability/keyword separation | PASS | Datasheet-specific ability cards remain; Lethal Hits opens imported text in separate Keywords section |
| Updated-package process-death/edit/reopen | PASS | Current four-entry army restored at 680 points; equipment, 10-model Marines, both attachments, warlord, enhancement and validation retained; undo/redo empty. Duplicate after restart produced 855; Home/open restored 855. |
| Physical phone | NOT RUN | Updated APK available for sideload testing |

An initial incremental Release package compiled but failed before application initialization with `java.lang.UnsatisfiedLinkError: MauiApplication.n_onCreate()`. A **clean Release rebuild** with the same source and publish profile resolved the native binding failure. No architecture or rules-engine changes were made. The broken package was never copied to the friendly phone-download filename. Root cause within the incremental native build cache was not conclusively isolated. Logs: `.local/android-actions-publish.log`, `.local/android-acceptance/actions-native-startup.log`, `.local/android-actions-clean.log`, `.local/android-actions-publish-clean.log`. The clean package runtime PID 25691 contains no fatal/unhandled exception or ANR marker in `actions-runtime.log`.

The user added Mortarion and selected it as warlord during testing, so the final lifecycle baseline was **680 points**, not the earlier 305. The first fixed-baseline automation was stopped when that changed; with the emulator idle, the same current state was recorded and restored. A second assertion initially counted only one attachment because the other label was below the viewport; scrolling confirmed both. These were test coordination/viewport issues, not persistence defects. User edits were preserved. The temporary duplicate was removed after the reopen check, leaving the four-entry 680-point army. Final PID 28540 logcat contains no fatal/unhandled exception, native-binding error or ANR marker.

Screenshots/text: `.local/android-acceptance/actions-attachments.png`, `actions-keywords.png`, `actions-menu.png`, `actions-datasheet-text.json`, `actions-before-restart.json`, `actions-restored.json` and `actions-final-runtime.log`. Source backup: `.local/android-before-roster-actions.zip`.

### Desktop regression

- Desktop solution: **PASS**, zero warnings/errors (`.local/android-actions-desktop-build.log`).
- Automated suite with real-catalogue diagnostic: **185 PASS** (`.local/android-actions-tests.log`).
- Extended WPF smoke: **PASS**, `.local/tests/44380934-aeb5-48c2-8dce-554040e09e77/android-actions-regression.png.result.txt`.

## Windows equipment-editor parity - 2026-10-04

Latest phone APK: `artifacts/android/GrimList-Android-Test.apk`, **64,481,215 bytes**, SHA-256 `29D04EDC1775B4E681E8BC7BF4FFD2715BFC0FE2199ED852CD98B3A3E4891B56`. Standalone AndroidTest Release, ARM64/x64, development signed; APK v3 signature verified. This replaces the earlier APK at the same path.

The preceding Android editor exposed a model picker and a flat list of role upgrades. It did not reproduce Desktop `SectionChoices`: squad sections, per-role maximum labels and the nested shared special-weapons pool were missing. The existing shared `ArmySession.CanAdjust`/`AdjustEquipment` already enforced the imported role/pool constraints; the missing presentation has now been ported to Android.

`MainPage.LoadoutOptions.cs` mirrors the Windows presentation queries over `Loadouts.Sections`, `ModelGroups`, role bounds and `OptionCount`. The champion and squad have separate expandable cards and equipment summaries. Each upgrade shows its maximum; special weapons share a highlighted named group and a selected/maximum count. Standard equipment updates through the existing aggregate commands. Champion/single-model exclusive choices retain buttons; squad-wide replacements use counters. Disabled equipment controls are visibly dimmed. Core, Application, database schema and Windows production code are unchanged.

### Checks

Device: Medium_Phone_API_35, Android 15/API 35 x64, standalone AndroidTest Release APK. Catalogue: existing edition-11 snapshot, revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908`. Army: **Android Equipment Parity Test**, copied from the reference army; original armies were not edited.

| Check | Status | Result |
| --- | --- | --- |
| Ten-model launcher limit | PASS | Selected two Blight launchers; third addition disabled; maximum shown as two |
| Shared special-weapons group | PASS | One meltagun plus one plasma gun fills 2/2; meltagun, plasma gun and plague belcher additions all disabled |
| Undo/redo | PASS | Undo frees a shared slot; redo returns to full capacity |
| Squad resizing | PASS | Five models permit one launcher and one shared special weapon; no second addition enabled |
| Champion and defaults | PASS | Champion remains separate; power fist retained; standard squad equipment recalculated |
| Points/validation | PASS | Ten-model reference army 305; five-model version 220; army validated |
| Final APK restart/reopen | PASS | Force-stop/install/relaunch restored the five-model army, Equipment tab, 220 points, valid state and empty undo/redo. Resized to ten, added the second launcher and second special weapon, then Home/open restored 305 points, selected quantities, shared 2/2 capacity and disabled add controls. |
| Physical phone | NOT RUN | Emulator checks do not represent physical-device testing |

The real-catalogue automated check `Imported Plague Marine session counters enforce size and shared special-weapon caps` exercises the same application commands, rejection without state mutation, mixed-pool limits, champion preservation, undo/redo and persistence. The full run passes **191 checks**, using `--attachment-diagnostic tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/death-guard --loadout-catalogue tests/fixtures/libraries/android-equipment-catalogue.json --reference-catalogue tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/death-guard/raw-catalogue.json`. The catalogue fixture filters the exported library to Death Guard for existing faction-specific tests; it preserves the imported unit definitions unchanged. An initial run passed the new check but an existing faction-specific test rejected the unfiltered multi-faction fixture due to duplicate unit names; this was corrected in the test input, without changing application code.

Desktop build: PASS, zero warnings/errors. Extended WPF smoke: PASS, including selection-limit fixtures. Logs: `.local/android-equipment-desktop-build.log`, `android-equipment-tests-final.log`; smoke result: `.local/tests/b3f04694-96bf-48af-9c5c-cd5f57e90c7f/android-equipment-regression.png.result.txt`.

Runtime evidence: `.local/android-equipment-runtime-ten.log`, `android-equipment-runtime-five.log`, `.local/android-acceptance/equipment-launcher-cap-ten.png`, `equipment-special-pool-full.png`, `equipment-launcher-cap-five.png`, `equipment-special-pool-five.png`. These first screenshots precede the final disabled-button opacity adjustment.

Final-package evidence: `.local/android-acceptance/equipment-final-restored.json`, `equipment-final-five-dimmed.png`, `equipment-final-ten-dimmed.png`, `equipment-final-reopened.png`, `equipment-final-runtime.log` (PID 32105; no fatal/unhandled exception, native-binding error or ANR marker). The separate **Android Equipment Parity Test** army remains open with ten Marines, two Blight launchers, one meltagun and one plasma gun, both attached characters and the existing enhancement; total 305 and Army validated. Original armies were preserved.

Final publish used a clean rebuild to avoid the previously observed incremental native-binding packaging problem. Logs: `.local/android-equipment-final-clean.log` and `.local/android-equipment-final-clean-publish.log`. No toolchain, architecture or engine change was needed.


## Remaining desktop feature parity ? 2026-10-04

Implemented Android controls for editing an existing army points limit, persistent/removable orphaned enhancements, enhancement allowance/eligibility/occupied-slot guidance, Feel No Pain reference popups, direct attached-unit/bodyguard links, detachment cost/disposition/availability presentation, official MFM downloads, manual source editing, missing-price scan/preview/repair/report export, and PDF roster/datasheet export. These controls call the existing shared services and Core rules. Windows production UI and database schema are unchanged.

Weapon keywords use the exact Desktop `WeaponKeywords` palette: gold for Lethal/Sustained Hits and Devastating Wounds; mint for Anti/Precision/Twin-linked; coral for Hazardous/One Shot; lilac for Psychic/Extra Attacks; blue for other rules. Each has a bordered tap target. Both individual keywords and the whole weapon profile open imported rule definitions; reference headings retain the same colours.

PDF export uses Android Canvas/PdfDocument with shared roster, equipment, attachments, enhancements and reference content. PDF text import uses Android 15/API 35 text-search coordinates to reconstruct layout for the shared `MfmImporter.ReadColumns`/parser. Earlier Android versions receive an explicit unsupported message and can use MFM text or Windows-exported JSON. This limitation remains: a cross-version extraction dependency could not be restored because this environment blocks NuGet access (`NU1301`, proxy connection refused). No unavailable package dependency remains. Android API documentation: https://developer.android.com/reference/android/graphics/pdf/PdfRenderer.Page . Image-only PDFs do not support text import.

A narrow shared merge optimization indexes the original normalized identity/alias predicate, preserving ambiguous matches, permanent-ID protections and same-batch collisions. The 2,194-unit, 52.84 MB edition-11 snapshot merge measured 12.034 seconds before and 1.070 seconds after on this Windows PC (Debug). These are merge-only PC observations, not phone timings or a claim about full Android import latency.

Initial validation: desktop build zero warnings/errors; **194 automated checks PASS**, including new limit/undo/persistence, orphaned enhancement removal and indexed-merge collision checks; extended WPF smoke PASS. Logs: `.local/parity-desktop-build.log`, `.local/parity-tests.log`, `.local/tests/d249d86e-b5ef-4572-b6a1-b07692d68b6a/parity.png.result.txt`.

Runtime testing uses a new copy of `Android UI Reference Test`, ID `6929fcb8-b96f-4b36-8f8f-520fec82ee19`. Original armies are preserved. Initial Android checks: launch/restore PASS; keyword colour rendering and Hazardous rule popup PASS; points-limit 2000 -> 1500, undo -> 2000, redo -> 1500 PASS. 1500 intentionally produces the shared detachment-budget warning. PDF export through Android document creation PASS: `.local/parity-android-export.pdf`, 128,832 bytes, text extracted independently and confirmed roster, sizes, equipment, both attachments, Warlord, enhancement, points, validation and rule content. First PDF-import attempt safely rejected the fixture because API 35 returns empty text-content bounds; this was diagnosed using an independent native probe and corrected to use text-search bounds. Retest and final package results follow below.


### Final package and runtime evidence

Standalone AndroidTest Release (ARM64/x64), Medium_Phone_API_35 / Android 15. Final APK `artifacts/android/GrimList-Android-Test.apk`: **64,518,079 bytes**, SHA-256 **A3DF262513D911DBB8F5D6692BD2BCF245A500507A2296F4EC5316B95DD5E6E5**. APK v3 signature verified. Clean publish completed without reported warnings/errors (`.local/parity-verified-clean.log`, `parity-verified-publish.log`, `parity-apk-signature.log`).

| Check | Android result | Evidence / limits |
| --- | --- | --- |
| Final APK install, launch, context restoration | PASS | Restored copied army, selected Marines datasheet, 305/2000 points, valid state, empty undo/redo; `parity-final-restored.json` |
| Coloured individual keyword targets and rule popup | PASS | Windows palette visually checked; Hazardous opens exact imported text; `parity-keywords.png` |
| Existing points limit, undo/redo, validation | PASS | 2000 to 1500 and back; shared unsupported detachment-budget warning at 1500 |
| Enhancement allowance, selected card, occupied-slot guidance | PASS | Selected Daemon Weapon remains visible/removable; other choices disabled with shared rejection reason |
| Orphaned enhancement removal | NOT RUN on Android with an orphan fixture | Shared application regression PASS; UI removal no longer depends on available choices |
| Enhancement remove/undo | PASS | Removed selected enhancement and restored it through shared undo |
| Attached-unit navigation | PASS | Both leader-to-bodyguard and bodyguard-to-leader links open the corresponding saved entry |
| Enhancement change and Feel No Pain | PASS | Revolting Regeneration costs 30; total becomes 325; badge opens imported Feel No Pain 5+ text |
| Detachment cost display | PASS | Virulent Vectorium shows 3 DP and shared budget; detailed disabled-choice coverage remains NOT RUN |
| Missing-price scan | PASS | Real current-library warnings shown without altering prices |
| Price-repair apply/backup/report | NOT RUN on Android | Implemented through existing shared repair service; no invented prices applied to user's library |
| Official MFM network download | NOT RUN | Provider routes to existing OfficialMfmClient and Android PDF adapter |
| Manual source editor | NOT RUN end-to-end | Metadata/text controls route through shared Parse/Prepare/Apply preview workflow |
| PDF export via Android document picker | PASS | Independent PDF text extraction confirms roster and datasheet content |
| Two-column PDF import via document stream | PASS | Native API 35 correction: Test Alpha 5 models/80, 10/150; Test Beta 1/150; three price changes in review, no apply. `parity-pdf-preview.json`, `parity-pdf-prices.json`, `parity-pdf-final.png` |
| Full library JSON preview | PASS | 2,194 units, zero price changes/affected armies, 1,399 existing source warnings; cancelled without applying |
| Full-preview timing | OBSERVATION | 76.55 seconds from document selection through preview, including file access/parsing/merge/comparison/army impact/render; not separately instrumented stage timings. Previous check exceeded 180 seconds. Concurrent host build and emulator memory pressure prevent treating this as a controlled phone benchmark. |
| Failed PDF load retains active data | PASS | First adapter attempt rejected safely; subsequent full library preview retained original revision, units and armies |
| Android below API 35 PDF text import | NOT RUN | Explicit version guard; JSON/text and PDF export remain available |
| Physical phone | NOT RUN | APK supplied for user testing |

Final desktop regression: zero-warning/error build, **194 PASS**, extended WPF smoke PASS. Logs: `.local/parity-final-desktop-build.log`, `.local/parity-final-tests.log`, `.local/tests/6af8cd4e-7ff0-4792-a860-3c2689e08e85/parity-final.png.result.txt`. UI hierarchy tooling occasionally returned a null root while Android was updating; these were automation observations, not application crash reports. Screenshot/JSON evidence lives under `.local/android-acceptance/`.


**Final process-death and writable-restoration check: PASS on the exact APK above.** Selected Revolting Regeneration on the copied Biologus Putrifier: 30 enhancement points, 325 total, Army validated, Feel No Pain 5+ reference confirmed. Captured the complete shared roster export, force-stopped without clearing data, relaunched, verified the selected datasheet/FNP and empty undo/redo, then captured the export again. `.local/android-acceptance/parity-state-before.txt` and `parity-state-after.txt` are byte-identical, covering units, sizes, equipment, both Leader attachments, Warlord, enhancement, total, detachment and validation. After restoration, renamed the copy to **Android Parity Completion Test**, allowed the shared command to commit, navigated Home and opened it from the saved-army picker: name and 325/2000 points survived. Evidence: `parity-reopened.json`. Captured final-process log (`parity-restored-runtime.log`) contains no fatal/unhandled exception, native-binding error or ANR marker. No original army was edited and neither test library preview was applied.

The final emulator remains on this disposable copied army. Live official-source downloading, actual price-repair apply/report, manual-source end-to-end import, older-Android PDF guard, and physical-phone behaviour remain explicitly unverified on Android; their availability in code is not reported as a runtime PASS.


## BSData self-update and price-repair verification - 2026-10-05

This pass uses an isolated installation, **GrimList Update Test** (`com.grimlist.verify`), on Medium_Phone_API_35 / Android 15. It compiles an unchanged copy of the current mobile production sources against the same shared projects. Only the application ID/title, local project-reference paths and Android debuggable flag differ, allowing direct persistence/backup inspection without touching `com.grimlist.mobile`. Source hashes are recorded in `.local/android-update-source-manifest.json`. No production code change is implied by this test installation.

The seeded test database uses the shared schema and repository. It contains the real 2,194-unit edition-11 library and a shared-service-created 2,000-point Death Guard army: ten Plague Marines, attached Malignant Plaguecaster and Biologus Putrifier, the Plaguecaster as Warlord, and Daemon Weapon of Nurgle on the Putrifier. Its valid baseline is 305 points. Only the Marines' prices are deliberately removed to produce a recoverable test fault: Android correctly starts at 130 known points with incomplete pricing. Existing unsupported entries remain in the source and must not receive guessed prices. This is deliberate test-fixture damage, not a discovered user-library corruption.

Evidence and per-path results are recorded under `.local/android-update-evidence/`; build/import logs use `.local/android-update-*.log`. Runtime results follow after each path is exercised.

Android live update attempt:

| Check | Result | Evidence / limitation |
|---|---|---|
| Isolated Release installation and startup | PASS | `com.grimlist.verify`; seeded shared SQLite library and incomplete-price army displayed correctly. `initial-missing-price.json/png` |
| Online repository lookup and ZIP download | PASS | Actual Android UI **Update from BSData** advanced from repository lookup/download into catalogue parsing. This was a live request, not a mocked response. |
| Full parse / import preview | FAIL to complete | Progress reached **World Eaters (36/36)** at 247.5 seconds. No completed parse, preview, or apply was observed. The progress label reports entry into a catalogue, not its successful completion. `.local/android-update-online-import.log` |
| Explicit update apply | NOT RUN | No Apply button was reached or pressed. |
| Update followed by process-death restore | NOT RUN | Blocked by emulator failure before preview. Earlier editor lifecycle passes do not establish update persistence. |
| Android local ZIP import | NOT RUN in this pass | Shared-service ZIP test below is a Windows-hosted test, not Android document-picker evidence. |
| Android repair from local / cached / online source | NOT RUN | Emulator unavailable before repair controls could be exercised. |
| Android repair apply / backup / report export | NOT RUN | Must still verify these in Android app data and through the document picker. |
| Android cancelled / invalid / wrong-edition source | NOT RUN in this pass | Shared negative-path checks below do not substitute for Android UI tests. |

**Failure isolation:** the AVD has 2 GB configured RAM. During import, a captured `top` observation showed 1,508,896 KB of 1,514,204 KB swap used, with the test app around 494 MB resident at that observation. Subsequently even `adb shell echo alive`, `pidof`, and `logcat` stopped responding. A bounded shell check timed out; after reconnect, ADB listed the guest as **offline**. The emulator console remained responsive. A console restart created a new emulator process but restored the stuck Quick Boot state; restarting the ADB server did not restore guest access. A cold boot without wiping data was requested.

This is an incomplete Android import under severe resource pressure. Memory pressure is a suspected contributor, **not a proven importer exception or confirmed out-of-memory crash**: logcat could not be retrieved after the failure. The exact application-versus-guest cause remains unresolved. Do not mark Android self-update or repair accepted. Do not infer the downloaded commit/hash from the older seed: the live attempt did not reach a readable cache/source manifest.

No production code or architecture was changed in response. The original `com.grimlist.mobile` installation was not targeted by database replacement, uninstall, or data clearing. Restarting the emulator affects all running apps, but no original app storage was intentionally modified. Production mobile source hashes still match the pre-test manifest, and the distributed APK retains SHA-256 `A3DF262513D911DBB8F5D6692BD2BCF245A500507A2296F4EC5316B95DD5E6E5`.

Separate shared-service real-catalogue verification uses the existing ZIP for edition 11, revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908` (2026-10-02), 4,869,775 bytes, SHA-256 `747db24bf29456f0606a937f794a7e7f33154e76116eff1506e9b0750ab5c045`. It runs the unchanged `LibraryWorkflow`, `PricingRepairService`, Core importer/calculator and shared `ArmyStore` through the Windows SQLite adapter on another disposable database. Local ZIP metadata correctly says **Local repository snapshot**; revision provenance comes from the existing cache manifest.

| Shared-service check (Windows hosted) | Result |
|---|---|
| Full ZIP read as a stream, all 36 catalogues / 2,194 units | PASS |
| Full update preview leaves library and saved armies unchanged | PASS |
| Preview projects missing Marine prices restored to 305 complete points | PASS |
| Repair finds one recoverable fault, retains nine unsupported entries | PASS |
| Repair leaves healthy unit records unchanged | PASS |
| Repair preview is read-only until apply | PASS |
| Apply writes exact pre-repair dataset and army backups, plus complete report | PASS |
| Equipment, sizes, both Leader attachments, Warlord, enhancement and editor context preserved | PASS |
| Recalculation after repair: 305 points, complete, zero validation issues | PASS |
| Stale preview, empty repair and wrong-edition repair rejected | PASS |
| Invalid JSON and cancelled stream import rejected | PASS |
| Rejected operations preserve active library and saved armies | PASS |
| Repeated repair is idempotent | PASS |
| Explicit full-source update retains saved army/context | PASS |
| Database dispose/reopen and shared session restore: validated 305-point army | PASS |

The disposable verification harness completed **20 assertions**, separately from the regular suite's **194 passing tests**. Evidence: `.local/android-update-harness/Exercise.cs`, `.local/android-update-shared-real-source-final.log`, and `.local/android-update-evidence/shared-real-source-20261004-234431/` (database, `result.json`, backups). The first harness attempt omitted the explicit `ArmySession.Restore()` call at reopen; correcting that test setup produced the complete passing run without production changes. Full ZIP parse took approximately **51 seconds on the Windows host**, and the complete harness 82 seconds; these are not Android or physical-phone performance measurements.

Regular regression log: `.local/android-update-regression-tests.log` (**194 PASS**). Extended WPF smoke: **PASS**, `.local/tests/b92d9806-20ba-428e-b5f3-1cf72b4dec5d/update-check.png.result.txt`. Final ADB diagnostic: `.local/android-update-evidence/adb-after-restart.json` (guest still offline). Remaining acceptance resumes after an AVD cold boot without wiping app storage, beginning with saved-state inspection, then live import preview/apply and each Android repair path. No production APK rebuild is needed for the verification-only files.

These are library-data updates, not APK updates. New datasheets and changed points/rules can flow through the existing generic importer when their constructs are supported. Unsupported pricing remains explicit and repair cannot invent absent or uninterpretable values. A new rule construct may still require a shared importer/validator update; this test does not establish how rarely that will happen.

## Native categories before allies - 2026-10-05

Android's faction-specific headings (`Allies: Chaos Knights`, for example) previously fell through the shared category sorter to the same rank as Others. Alphabetical library ordering could consequently put allies first; roster ordering depended on insertion order. Both Android grouping sites now normalize these display headings to `Allies` before using the existing shared category rank. The unit browser and Build/Play roster therefore place Others before every allied-faction section. Windows already used the plain Allies category and required no change. No rules, persistence, architecture or desktop production code changed.

Clean standalone Release publish: PASS, no reported warnings/errors (`.local/android-category-order-clean.log`, `.local/android-category-order-publish.log`). Existing automated suite: **194 PASS** (`.local/android-category-order-tests.log`). APK signature verification: PASS; same signing certificate as the previous phone package, with ARM64 and x64 assembly stores embedded. Phone runtime verification of this ordering is **NOT RUN**; emulator testing was intentionally left aside at the user's request.

Updated phone package: `artifacts/android/GrimList-Android-Test.apk`, 64,518,079 bytes, SHA-256 `278070858D4BA8AD4D7F5BAED73FB6900CDDCED64E4D4B305368155B86BC140C`. Install over the existing app to retain local armies. The previous APK is retained at `.local/GrimList-before-category-order.apk`.

## Sisters reference scope and duplicate enhancements - 2026-10-05

Phone-reported defects reproduced directly from the distributed `GrimList-Library.json` and its pinned BSData ZIP (`cc1830fbfead059f8059ed6c7f8e73cd4c390908`). This pass deliberately does not use the emulator.

**Proven causes:**

- The Sisters catalogue stores detachment rules in `sharedRules` and links them from the detachment selection. The old importer emitted each linked rule both as a scoped Detachments card and as an unscoped Army Rules card. The mobile page also included global disposition-reference cards in its Army Rules section.
- The enhancement importer emitted a priced card for every linked description. Furnace of Plagues links the universal Devastating Wounds rule; Revolting Regeneration links Feel No Pain. Their explanatory texts became separate enhancement cards. Two Flyblown Host upgrades also existed under names with and without the UPGRADE suffix.
- The actual Sisters catalogue contains **no stratagem definitions**. The existing Death Guard stratagems come from a separately attributed faction reference, not from BSData. The official [Sisters faction pack](https://assets.warhammer-community.com/eng_11-06_warhammer40000_faction_pack_adepta_sororitas-ktlklgb0t5-knvswx9kyw.pdf), page 13, supplies amendments to existing Hallowed Martyrs stratagems rather than their complete cards. Missing Hallowed Martyrs text remains a source-content dependency; no guessed cards or old-edition replacements were added.

**Test-data audit:** the distributed library had 3,940 cards attributed to the real BSData revision and 76 to the pre-existing Death Guard faction reference. No fixture-named units were found. The distributed APK contains no bundled GrimList database or library JSON. The reported keyword cards carry BSData provenance and reproduce from its actual linked nodes. The previous intentionally damaged update-test army/database belonged exclusively to `com.grimlist.verify`; it was not packaged into `com.grimlist.mobile` or the distributed JSON.

**Corrections:**

- Shared `BsDataReferences` retains detachment scope and emits one enhancement card per selectable entry, with supporting keyword definitions inside that card.
- Shared `ReferenceData.Normalize` conservatively recognizes old BSData duplicates using matching scoped rule text, exact imported ability definitions plus their same-cost owning enhancement, or a complete supplemental UPGRADE alias. It does not remove cards merely because their names resemble keywords, nor replace independent supplemental rules.
- Shared repository loading cleans existing snapshots in memory. This fixes an installed library on opening the updated app without requiring another full import. Loading does not overwrite the old stored snapshot or any army; subsequent explicit library imports persist the cleaned references. Merge/apply uses the same cleanup, preventing old JSON from restoring the duplicates.
- Enhancement identity accepts the optional UPGRADE suffix, so saved upgrade spellings still resolve. No saved enhancement selection is silently reassigned.
- Army reference selection includes only selected detachment rules and their available dispositions. Android's Army Rules section now uses the same explicit section classification as Windows.
- Both frontends identify selected detachments whose stratagem text is missing. BSData import previews warn when a catalogue supplies no detachment stratagem cards.

The corrected library has **3,880 reference cards** instead of 4,016: 19 duplicated global detachment rules, 115 linked keyword pseudo-enhancements and two upgrade aliases were removed. All **2,194 unit definitions, profiles, prices and loadouts are unchanged**. The removed-card audit is `.local/reference-import-removed-cards.json`; original JSON is retained at `tests/fixtures/libraries/reference-import-before-library.json`.

| Check | Result |
|---|---|
| Sisters Hallowed Martyrs: Acts of Faith global, The Blood of Martyrs scoped; unrelated detachment rules absent | PASS, shared real-library regression |
| Virulent Vectorium: four named enhancements, no Feel No Pain / Devastating Wounds pseudo-enhancements | PASS |
| Death Guard: 30 enhancement/upgrade cards and all 45 existing supplemental stratagems retained | PASS |
| Future imports: full real ZIP preserves enhancement ownership and detachment scope | PASS |
| Existing SQLite snapshot load, saved army/context preservation, old upgrade spelling, reimport without resurrecting duplicates | PASS |
| Cleanup idempotence, legitimate keyword-named choices, unrelated sources and orphan references | PASS |
| Hallowed Martyrs complete stratagem content | NOT AVAILABLE in supplied BSData; complete matching-edition reference requested |
| Phone rendering and install of this new build | NOT RUN; requires physical-phone testing |

Desktop build: **PASS, zero warnings/errors**. Automated tests: **200 PASS**, including six new reference-import checks and the real-catalogue options. Logs: `.local/reference-import-fix-desktop-build.log`, `.local/reference-import-fix-tests.log`. Artifacts: `.local/tests/febad980-8d89-47bc-87b6-5d0026bff126/`.

An additional historical Death Guard text-generation check was attempted with `tests/fixtures/references/death-guard-reference-text.txt`; its older single-disposition Virulent Vectorium heading disagrees with the current catalogue's two dispositions, and the existing consistency guard rejected it. That mismatched optional source pair is not included in the 200-pass run. The guard was not weakened, and this pass does not regenerate the retained supplemental Death Guard rules from that outdated text.

Extended desktop smoke: **PASS**, `.local/tests/febad980-8d89-47bc-87b6-5d0026bff126/reference-import-fix.png.result.txt`. Final clean Android Release publish: **PASS**, with no reported warnings/errors (`.local/reference-import-fix-android-clean.log`, `.local/reference-import-fix-android-publish.log`). APK signature verified against the previous phone package; application ID remains `com.grimlist.mobile`, Android minimum API 30 / target 36, ARM64 and x64 assembly stores embedded. `.local/reference-import-fix-package-check.json` records the checks. Android runtime acceptance for this update is **NOT RUN**, as the emulator is intentionally out of use and no physical phone is connected.

Distributed files were replaced only after validation:

- `artifacts/android/GrimList-Android-Test.apk`: 64,522,175 bytes; SHA-256 `728B1A42CA2E068ACE87F74368A9F0B69DF5609C33D91EB8E2F0E87B0994DDD6`.
- `tests/fixtures/libraries/GrimList-Library.json`: corrected reference cards, unchanged units/source revision; SHA-256 `9B2BBB35432A5C5C7E38E602AF28D2C9CD1BBEAC210AF4FDD6740ECEAE3DD8A3`.

Install the new APK over the existing phone app. Existing-library correction happens on load; importing the corrected JSON is optional. Old APK retained at `.local/GrimList-before-reference-import.apk`. No user desktop database or phone database was directly edited during development.


## Missing-points parity audit ? 2026-10-05

The phone report listed nine ordinary roster entries with unavailable prices. A read-only comparison of the active desktop snapshot in `.local/grimlist.db`, the distributed `tests/fixtures/libraries/GrimList-Library.json`, and a fresh import of the pinned full BSData ZIP reproduced the same nine failures. These entries remain unresolved in the shared importer; this is not evidence that Android lost a desktop fix. The earlier shared repair test explicitly retained nine unresolved entries. The reference-card correction above intentionally did not change unit prices.

Source: edition 11, revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908`, archive SHA-256 `747db24bf29456f0606a937f794a7e7f33154e76116eff1506e9b0750ab5c045`. All 2,194 units were parsed using current `BsDataImporter.ImportAll`; this audit does not fetch or establish the latest upstream revision.

| Entry | Current shared-import failure |
|---|---|
| Cadian Recon Squad ? Astra Militarum | Conditional model limits |
| Cadian Recon Squad ? Genestealer Cults | Conditional model limits |
| Scourges with Heavy Weapons | Paid model/equipment composition cannot be mapped to supported loadout pricing |
| Inquisitorial Agents | Conditional price multiplication is rejected by the basic pricing reader |
| Kapricus Defenders | Price scope points to the root entry-link ID, which the importer fails to resolve |
| Wolf Guard Headtakers ? variant `0f93-6850-8f14-a351` | Unsupported unit composition |
| Wolf Scouts | Conditional model limits |
| Ghostkeel Battlesuit | Unsupported copy-count condition |
| Jakhals ? World Eaters | Conditional model limits |

Kapricus Defenders requires particular care: scope `e49d-ee2d-8886-071d` exists in the source as a root `entryLink` targeting this unit (`f788-ecf2-9b2e-bc23`). The current diagnostic claiming the source reference must be corrected is misleading. This audit does not establish an upstream source defect.

Mobile import/update/repair calls the existing `LibraryWorkflow`, which uses the shared Core importer and `PricingRepair`. Exported dataset JSON preserves the normalized prices and warnings; it does not reparse the underlying catalogue. Reimporting this same source with the current importer reproduces the failures, and repair cannot recover prices the importer still rejects. These cases need shared parsing/pricing fixes with real-catalogue regression coverage; they must not be addressed with Android-only hard-coded points.

Evidence: `.local/pricing-audit-fresh.json` and `.local/pricing-audit-fresh.log`; the local diagnostic harness has a `pricing-audit` mode. No production code, distributed APK/JSON, or user army data changed in this audit. Phone runtime checks were not performed. The previous 200-pass regression result remains historical; no new full regression run was needed for this diagnostic-only task.

## Generic selection and pricing importer corrections - 2026-10-05

This implementation supersedes the missing-points audit above. It changes the shared Core importer, evaluator and repair/merge handling, without changing project architecture or either frontend's production UI. No normal importer branch was added for a unit name, faction name, catalogue name or particular unit ID. Unit names/IDs in the regression fixtures identify test subjects only.

### Audit and reuse

Before implementation, the missing reasons originated in these paths:

| Old rejection | Location and missing capability |
|---|---|
| Conditional model limits need manual review | `BsDataImporter.ModelBounds`: static bounds rejected conditional model constraints; required retained constraints evaluated against proposed selections |
| Unsupported unit composition | `BsDataImporter.Prices` / `BsDataPricing`: flat sizing could not represent nested unit/model bundles |
| Paid models or equipment need a loadout-aware importer | `BsDataImporter.Prices`, `BsDataProfiles` and `BsDataPricing`: partial normalized equipment mapping discarded a usable selectable tree and its known costs |
| Unsupported conditional price rule | Basic `BsDataImporter.Prices` modifier parsing did not support multiplication in the ordered price path |
| Unsupported copy-count condition | `BsDataImporter` copy-condition parsing did not represent the catalogue's local copy filter |
| Missing price scope | `BsDataModelPrices.CheckScopes` and the importer registry omitted root entry links from scope identity resolution |

Existing `RuleEvaluation`, dynamic `AssociationRules`, `FormulaPricing`, `Loadouts`, `WargearPricing`, `ArmyCalculator` and `PricingRepair` were reused. Windows and Android still reach these through the existing shared application services. There is no mobile pricing, composition or association implementation.

### Implemented constructs

- `RuleEvaluation` now applies ordered SET, INCREMENT, DECREMENT and MULTIPLY operations, conditional AND/OR trees and count-driven repeats. The same numeric evaluator serves composition constraints, selection prices and dynamic association limits. Association evaluation retains its existing proposed-attachment context.
- `BsDataSelectionRules` retains scoped queries, condition groups, repeat metadata, source ordering and link identities. Query comparisons include atLeast, atMost, equalTo, greaterThan and lessThan. Native/allied and copy-count contexts are data-driven.
- `SelectionComposition` retains recursive unit/group/model containers, source/target IDs, base limits, modifiers, defaults and conditional error rules. `SelectionCompositions` counts physical models independently from bundle/group selections and validates proposed composition changes before committing them. Legal states are cached after a bounded search; they do not replace the retained constraints.
- Nested models, replacement bundles and linked model requirements now pass through the existing shared loadout controls. Imported conditional errors also reject mixed Headtaker wargear. No model-count subtraction formula is keyed to a particular unit.
- `BsDataSelectionPrices` and `FormulaPricing` calculate against selected roles in the recursive tree. `BsDataPricing` retains source cost identities and nested cost provenance independently of normalized weapon profiles. Known paid options remain selectable and chargeable when a normalized weapon profile is missing; unsupported variable paid bundles remain explicit failures.
- `ImportDiagnostics` / `UnitDefinition.ImportStatus` distinguish Available, AvailableWithWarnings, Unavailable and SourceDataError. Genuine unresolved price-scope references carry catalogue, unit, reference, referencing rule/ordinal, purpose, revision and archive hash. Root entry-link chains are resolved before classifying a reference as broken.
- `DatasetMerge` and `PricingRepair` carry required composition/loadout definitions alongside recovered prices, preserving the existing shared storage format and supplemental reference cards.

The principal new files are `SelectionComposition.cs`, `BsDataSelectionRules.cs`, `BsDataSelectionPrices.cs` and `ImportDiagnostics.cs` under `src/GrimList.Core`. Existing changes are concentrated in the importer/profile/loadout/pricing partials, `RuleEvaluation`, association rules, `FormulaPricing`, `Models`, `Loadouts`, `LoadoutOptions`, `DatasetMerge` and `PricingRepair`. No platform adapter or database schema change was needed.

### Real-catalogue results

Source: edition 11, revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908`, archive SHA-256 `747db24bf29456f0606a937f794a7e7f33154e76116eff1506e9b0750ab5c045`. This is the pinned source behind the reported failures, not a claim about the latest upstream revision.

Every row below has a passing regression using that actual full catalogue archive. All nine are **AvailableWithWarnings**, because existing profile/visibility or other explicitly reported importer limitations remain. Recovered pricing does not certify complete support for every rule on a datasheet.

| Previously unavailable entry | Old reason | Generic support and verified result | New status | Real data |
|---|---|---|---|---|
| Cadian Recon Squad - Astra Militarum | Conditional model limits | Two-model weapons-team replacement preserves ten physical models; conditional sniper limit enforced; 80-point base | AvailableWithWarnings | Yes |
| Cadian Recon Squad - Genestealer Cults | Conditional model limits | Same graph/query path and replacement checks as the AM entry | AvailableWithWarnings | Yes |
| Scourges with Heavy Weapons | Paid models/equipment need loadout-aware importer | Selectable paid equipment retained; four Dark Lances add exactly 20 points to the source base | AvailableWithWarnings | Yes |
| Inquisitorial Agents | Unsupported conditional price rule | Ordered multiplication and native/allied context; native 50/100-point options, later allied SET yields 60 in the imported applicable option | AvailableWithWarnings | Yes |
| Kapricus Defenders | Missing scope `e49d-ee2d-8886-071d` | Resolve the actual root entry link to `f788-ecf2-9b2e-bc23`; one/two-model base prices 75/150 | AvailableWithWarnings | Yes |
| Wolf Guard Headtakers, variant `0f93-6850-8f14-a351` | Unsupported composition | Nested unit/model tree; three Headtakers plus three wolves are six physical models at 115 points; mixed wargear rejected | AvailableWithWarnings | Yes |
| Wolf Scouts | Conditional model limits | Recursive six/twelve-model bundles and specialist replacement limits; 90/180-point bases | AvailableWithWarnings | Yes |
| Ghostkeel Battlesuit | Unsupported copy-count condition | Generic matching-copy query; first/second/third copies calculate 150/150/165 | AvailableWithWarnings | Yes |
| Jakhals | Conditional model limits | Linked Dishonoured counts and ten/twenty-model bundles; 65/120-point bases; missing linked models rejected | AvailableWithWarnings | Yes |

Kapricus is **not a source-data error in this archive**: the ID exists, and the importer previously failed to resolve it. No guessed value or compatibility patch was added. A separate deliberately broken-scope fixture verifies SourceDataError and provenance instead of invented prices.

Full reimport and repair report: `.local/generic-importer-report.json`. The ordinary missing-price scan is now **zero**, with **nine recovered / zero unresolved**. The scan intentionally excludes 113 Crucible and five reference entries with missing prices; this is not a claim that all catalogue constructs or nonstandard game modes are supported.

### Regression evidence

| Check | Result |
|---|---|
| Desktop solution build, `dotnet build GrimList.slnx --no-restore -m:1 -nr:false` | PASS, zero warnings/errors |
| Automated test executable, including real-catalogue optional suites | **214 PASS** |
| Real Death Guard association constraints, Plague Marines + Malignant + Biologus, both orders | PASS |
| Ordered SET/MULTIPLY, repeated numeric rules, proposed association repeats | PASS |
| Paid option without normalized weapon profile retains cost/source identity | PASS |
| Nested model inside model/group; physical model count | PASS |
| Recovered recursive definitions serialize and reload through shared SQLite storage | PASS on Windows provider |
| Extended WPF smoke | PASS |
| Clean `net10.0-android` Release publish with shared Core | PASS, no reported warnings/errors |
| APK signature, matching existing certificate, ARM64/x64 assembly stores | PASS |
| Android installation, import, editing and process-death workflow for this update | **NOT RUN**; emulator intentionally left aside, no connected phone |

Logs: `.local/generic-desktop-build.log`, `.local/generic-full-tests.log`, `.local/generic-final-import.log`, `.local/generic-android-clean.log`, `.local/generic-android-publish.log`. Desktop smoke result: `.local/tests/e9aca780-f876-40f1-b63f-497ce83da7d2/generic-importer.png.result.txt`. New regression coverage is in `tests/GrimList.Tests/SelectionImporterChecks.cs` and `AssociationChecks.cs`; older assertions expecting newly supported constructs to be unavailable were updated, while unsupported constructs continue to be rejected.

The full run used these existing optional inputs: `--attachment-diagnostic tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/death-guard`, `--loadout-catalogue tests/fixtures/libraries/android-equipment-catalogue.json`, `--legacy-reference-library tests/fixtures/libraries/reference-import-before-library.json`, both `--reference-import-archive` and `--selection-import-archive` pointing at the pinned repository ZIP, and `--reference-catalogue tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/death-guard/raw-catalogue.json` last. It did not pair the historical reference text with an incompatible newer catalogue.

Toolchain remains .NET SDK 10.0.302, workload set 10.0.303.1, MAUI 10.0.110, Android SDK pack 36.1.69. Publish explicitly selected `C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot` and `C:\Users\es0p0\AppData\Local\Android\Sdk`. Package remains `com.grimlist.mobile`, version 0.1/code 1, minimum API 30, target API 36. Archive inspection found no winsqlite3, WPF PresentationFramework or pdftotext binary; this is a package check, not an Android runtime assertion.

### Phone delivery and remaining limits

Updated standalone package: `artifacts/android/GrimList-Android-Test.apk`, 64,571,327 bytes, SHA-256 `9EF911D6F68D810F08D8C25E0ACE2B4D22A70845A975C5BF0A9713960FDC91F6`. Signature matches the previous development package.

Updated library: `tests/fixtures/libraries/GrimList-Library.json`, 54,863,318 bytes, SHA-256 `FAFC749F56F19DBF09E3117772C39384B3674DF1EC0CDE3A932A1DA414AD6439`. It contains 2,194 units and the same 3,880 reference cards, including the existing supplemental Death Guard material. Exactly nine unit definitions change semantically; new optional serialized fields account for harmless default-field differences in other definitions. The repair result was validated rather than replacing the library with a raw import lacking those supplemental cards. Backups are `.local/GrimList-before-generic-importer.apk` and `.local/GrimList-Library-before-generic-importer.json`.

Install the APK **over** the existing app, then preview/apply the accompanying JSON through Manage data. Alternatively, use the updated shared importer to update/repair from the full BSData ZIP. Installing an APK does not reparse already stored library definitions, so a library update/repair is required for these nine recoveries. Do not uninstall or clear storage. The repaired JSON is also usable by the updated desktop app. No user desktop or phone database was edited during development.

Remaining unsupported contexts and unrepresented paid selections still fail explicitly. Recursive search is bounded (50,000 intermediate candidates and up to 100 physical models); arbitrary repeated non-model containers and complex variable paid bundles are not claimed as universally supported. Source query flags are retained, but this unit/army model does not introduce nested force ownership. Existing conditional profile/visibility, some conditional keyword and equipment-scope warnings remain visible. No mobile performance claim or physical-phone acceptance claim is made by this change. Missing Sisters stratagem source text remains the separate issue documented above.

## Editing latency diagnosis - 2026-10-05

The user confirmed the importer works and clarified that the slow "Updating..." happens after unit, equipment and attachment edits. This investigation changes **no production code, APK, library or user database**. It diagnoses the current editing path; it does not implement an optimization.

### Measurement scope

No Android device was connected (`adb devices -l` returned an empty list). The emulator was not started. Consequently there are **no new physical-phone timings, Android GC traces or native MAUI render timings**. The measured values below are Windows-host evidence for code that Android actually calls, not a claim that the phone has these exact durations.

Host: Intel Core i7-9750H, Windows x64, Release/net10.0 diagnostic executable built with SDK 10.0.302. Dataset: the repaired 54,863,318-byte JSON, 2,194 units, 3,880 reference cards, edition 11 revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908`, hash `FAFC749F56F19DBF09E3117772C39384B3674DF1EC0CDE3A932A1DA414AD6439`.

The disposable harness uses actual Core/Application/Persistence assemblies and the Windows SQLite provider. An isolated database contains a real full-library snapshot in the same schema/read path. It exercises a three-entry Death Guard army (ten Plague Marines, Malignant Plaguecaster, Biologus Putrifier), and a larger twenty-entry workload. The latter is a load fixture, not a suggested legal roster. Mobile `PrepareUnit`, equipment preparation and wargear summary methods were extracted without their rendering methods into `.local/edit-performance/MobileProbe.cs`; only timing wrappers were added around button eligibility checks. Editor preparation mirrors the post-edit cache-miss branch of `MainPage.PrepareAsync`. Native Android view creation is excluded.

Medians of three or five iterations are reported; individual values, allocations and GC counts are retained. Initial JIT/cache warming can affect early samples. `AllocatedMiB` measures cumulative managed allocation during an operation, **not retained memory, peak RAM or a memory leak**. Rows overlap and must not be added as if they were independent pipeline stages.

| Operation | Median host time | Median managed allocation |
|---|---:|---:|
| Load full dataset from SQLite and normalize references | 616 ms | 201 MiB |
| Save three-entry army, including implicit dataset reload | 614 ms | 203 MiB |
| Complete three-entry equipment command, before mobile preparation/render | 633 ms | 232 MiB |
| Three-entry attachment/detachment command | 602 ms | 231 MiB |
| One three-entry army calculation | 5.1 ms | 6.0 MiB |
| Three-entry equipment-screen preparation | 14.6 ms | 16.2 MiB |
| Three-entry datasheet preparation | 15.0 ms | 18.2 MiB |
| Three-entry editor/roster preparation after army change | 239 ms | 367 MiB |
| Full-library eligibility scan alone | 181 ms | 286 MiB |
| BrowseLibrary alone, including eligibility and add limits | 210 ms | 332 MiB |
| One twenty-entry army calculation | 63 ms | 105 MiB |
| Twenty-entry equipment command | 897 ms | 697 MiB |
| Twenty-entry editor/roster preparation | 397 ms | 665 MiB |
| Twenty-entry equipment-screen preparation | 15.7 ms | 18.2 MiB |

### Confirmed causes

1. **Every committed edit reloads the full library before saving.** `ArmySession.Commit` calls `repository.Save`. `ArmyStore.Save` calls `LoadDataset` at line 110, despite the session already owning a loaded dataset. `LoadDataset` fetches the entire `dataset_snapshots.content`, deserializes every unit/profile/rule and runs `ReferenceData.Normalize`. SQL tracing confirms a 54,863,318-character snapshot read for each Save. In the paired three-entry equipment samples, Save consumed 95.8-96.7% of command elapsed time. This is the largest measured small-edit cost. It is shared persistence behavior, not an Android-only importer or rule-engine branch. Android reads that same snapshot via `SQLiteStatement.SimpleQueryForString`, crossing the platform string boundary as well; its additional cost is unmeasured.

2. **Roster preparation rescans all catalogues after an army edit.** `MainPage.PrepareAsync` invalidates `libraryRows` whenever `ReferenceEquals(libraryArmy, s.Army)` is false. `ArmySession.Commit` creates a new army object on every committed change, so even equipment-only edits invalidate this cache. `BrowseLibrary` calls `AlliedRules.Library`, which checks `UnitAvailability.Problem` across all 2,194 entries, derives origin factions/permissions, normalizes strings repeatedly, sorts/groups eligible choices, and computes add limits. Only 49 rows remained for the measured Death Guard view, but the full dataset was scanned. This expensive preparation occurs on the editor screen; an edit while staying on the equipment screen does not immediately run the editor branch, but returning to the roster does.

3. **Repeated lookups and normalization create substantial allocation and amplify rules work.** `Dataset.FindUnit` line 73 scans the unit list and evaluates the computed `NameKey` for nonmatching entries. `NameKey` repeatedly creates uppercase faction/name strings. A thousand identical Plague Marine lookups allocated 146 MiB and took 87 ms on the host. `AlliedRules.OriginFaction`, `Has`, `IsAlly` and `UnitIdentity.Normalize` also repeat string/list/array work: a thousand OriginFaction calls allocated 34 MiB. The full-library eligibility path allocated 286 MiB by itself. These allocations caused repeated garbage collections in the host measurements; the resulting Android GC pauses and frame impact require device profiling. There is no evidence here of a memory leak.

4. **Each commit calculates the full army three times.** `AlliedRules.ValidateIncrease` lines 193-195 calculates the previous army and proposed army before its early-return check. `ArmySession.Commit` then calculates the normalized proposed army again to publish `Result`. These calls occur even when an equipment change does not introduce allies. Additional muster/enhancement checks and two JSON army copies for commit/history add work. The same full calculation grows from approximately 5 ms for three entries to 63 ms for twenty in this workload. This contributes increasingly as the roster grows; it does not explain the fixed half-second full-library reload by itself.

5. **Android rebuilds the entire visible page and disables input for the whole operation.** `MainPage.Execute` lines 92-104 sets `page.IsEnabled = false`, shows Updating, awaits the command, always runs `PrepareAsync`, and calls `Render` before re-enabling input. `Render` line 165 clears `page` and its row definitions and recreates the screen. The roster uses `ScrollView` plus `VerticalStackLayout`, constructing all roster cards and every row in expanded library categories; search can open all matching categories. Equipment rendering constructs every card's child controls before putting them in a fold, even when that card is collapsed. Thus a one-counter edit recreates unrelated controls, event handlers and layout work. This mechanism is verified from code; its native render/layout duration has **not** been measured on Android. Moving the command to Task.Run does not remove the user-visible delay while Execute keeps input disabled.

6. **Saving rewrites the whole army's entries.** Inside the transaction, `ArmyStore.Save` deletes all entries for that army and inserts every entry/loadout again, along with army metadata and editor context. The three-entry trace had two SELECTs plus nine write statements per Save; a twenty-entry save has twenty entry inserts. The actual SQLite write/commit cost on a phone remains unmeasured. On the host, the full-library reload is much larger than the observed individual write-statement times, so blaming SQLite writes generally would misidentify the main measured bottleneck.

### Smaller or unproven contributors

- Preparing the Plague Marine equipment screen tests 37 hypothetical plus/minus/selection actions on each refresh. Those checks took roughly 3 ms once warmed on this host. They are repeated work, but were not the dominant measured delay.
- Equipment preparation for the new recursive units also remained small in these samples: Cadian Recon Squad 10.6 ms, Wolf Scouts 12.0 ms, Jakhals 9.8 ms (medians, including other unit-view preparation). Cold graph construction and every possible composition are not covered. These measurements do not support blaming the newly added generic composition engine for the reported general editing delay.
- The distributed APK is Release but uses `RunAOTCompilation=false` and `PublishTrimmed=false`. First-use compilation/code-loading can contribute to Android startup or first-use costs; no AOT comparison was performed, so this is not established as the primary cause or an explanation for every repeated edit.
- Session PropertyChanged notifications also schedule preparation when not busy. Whether dispatch timing causes extra refreshes on a particular Android run is unmeasured and is not counted as a confirmed duplicate-refresh defect.
- Ordinary edits do not download BSData, run the importer or write the entire library back. The problematic full-library operation here is a **read/deserialization during army saving**.

Evidence: `.local/edit-performance/final-run.log`, `results.json`, `save-sql.json`, `Program.cs`, `MobileProbe.cs`, and `diagnose.csproj`. Reproduce with `dotnet build .local/edit-performance/diagnose.csproj -c Release -m:1 -nr:false`, then `dotnet .local/edit-performance/bin/Release/net10.0/diagnose.dll`. The harness seeds only a new `.local/edit-performance/db-<guid>` database. It caches repository reads for setup while passing each measured Save through the unchanged real ArmyStore; the internal Save reload remains present. An initial optional composition probe reused an entry ID across disposable armies and hit the expected SQLite uniqueness constraint; the harness was corrected to use unique IDs, and the final run completed. This was diagnostic-fixture setup, not an application defect.

No optimization has been implemented. Exact phone wall time, native rendering share, Android SQLite boundary cost and GC/frame pauses remain unmeasured. The code paths and host measurements above establish redundant full-library loading, full-library browsing, repeated calculations/allocations, and full-screen rebuilding as the concrete sources of editing work to address after the user chooses a solution.

## Editing performance implementation - 2026-10-05

This section supersedes the diagnosis-only status above. The requested shared performance refactor and Android incremental presentation are implemented. Host architectural checks and benchmarks pass. **Actual Android runtime/performance acceptance for this update remains blocked and is not claimed complete.**

### Call-path changes

The original audited path was equipment control -> `ArmySession.AdjustEquipment`/`ChooseEquipment` -> `Commit` -> muster/enhancement guards -> two `ArmyCalculator.Calculate` calls inside `AlliedRules.ValidateIncrease` -> normalization -> third calculation -> `ArmyStore.Save` -> `LoadDataset` -> SQLite snapshot read -> JSON deserialization -> reference normalization -> army/context transaction -> `MainPage.Execute` -> `PrepareAsync` -> army-reference-based library invalidation -> `Render` -> `page.Clear()`.

`Save` used the full dataset for warlord normalization and canonical entry names/keys. Warlord and canonical identity preparation now belong to `ArmySession.Normalize`. The store retains only a small identity/name map for direct legacy repository callers; it loads that metadata on repository opening and replaces it after a successful source import. It does not keep a second dataset copy. Saving performs only army/context writes, keeps the existing transaction, and retains structural cleanup for missing warlord IDs and dangling attachments. No fire-and-forget saving was introduced.

`ArmySession.Commit` calculates the normalized proposed army once. The allied guard receives the current `Result` and that proposed result. It still checks paid equipment increases against allied allowances: an equipment-only change can increase allied points, so blindly skipping that guard would have weakened correctness. Once persistence succeeds, the session publishes state, history and an `ArmyMutation` describing equipment/composition/attachment/structure/metadata/rules/enhancement changes. Failed saves leave the published army, result, history, mutation and caches unchanged.

The session constructs a `DatasetSnapshot` once when loading a library; `ArmyWorkspace.Snapshot` exposes that same object. Process restoration reuses the constructor's loaded snapshot instead of loading it a second time. Only explicit reload/import replaces it. Snapshot identity indexes preserve the prior first-match lookup behavior for IDs, name keys and aliases. Faction and keyword indexes use frozen dictionaries/read-only collections. Imported unit facts cache stable names, factions, keyword membership and origin faction. String normalization is memoized by weak string identity. Record `with` replacements receive new facts/indexes; no army-dependent category or rule result is cached as a static imported fact.

The legacy dataset DTO still uses serializable lists for importer/JSON compatibility. Its published collections are read-only **by contract** during editing, as documented on `DatasetSnapshot`; importer/merge code must publish replacements rather than mutate them in place. The editing paths do not clone or modify the dataset. Weak caches are tied to snapshot/unit/string object lifetimes rather than a permanently growing global content dictionary.

### Explicit invalidation

| State/cache | Dependencies / invalidation | Does not invalidate it |
|---|---|---|
| Dataset snapshot and indexes | New dataset object after source/import/reload | Any army edit, context navigation, undo/redo |
| Static unit facts | New imported UnitDefinition object | Roster points, selections, faction/detachment context |
| Eligible library candidates | Dataset, game, owning faction, limit, selected detachments, disposition | Equipment, attachments, warlord, entry IDs, selected model |
| Available rows / copy-price and maximum-count projection | Candidate scope plus unit identity/model-count multiset and display filters | Ordinary equipment changes, army name, selection/navigation |
| Fits-remaining rows | Above plus current calculated total | Changes that preserve those dependencies |
| Search result | Trimmed query applied to cached candidate rows | Does not run eligibility or renormalize the dataset |
| Calculated result | Proposed rule-relevant command, undo/redo, source replacement | View navigation |
| Equipment presentation | Selected unit/tab; changed counter values and row structure | Unrelated controls remain alive |
| Roster presentation | Stable entry/group identity, priced state, wargear summary, warlord flag | Unchanged rows retain presentation/control identity |

Adding/removing a unit refreshes count-dependent available rows but reuses the eligible candidate set. Changing faction/detachment refreshes both. A paid equipment change refreshes the FitsRemaining projection only when total points change; the ordinary available list remains the exact same object. There is no fine-grained rule-dependency graph: measured indexed/cached work is already small enough that one was not justified.

### Android presentation

`MainPage.RefreshPresentation` replaces the unconditional full-page render after commands. Ordinary equipment updates retain the page, header, scroll view, tabs and counter controls. `ObservableValue`/`PresentationValue` objects implement property-change bindings for points, undo/redo, validation status, unit price, equipment quantities, limits and enabled/selected states. Button callbacks read the latest presentation value, avoiding captured stale counts.

An equipment role/size change that changes which option rows exist replaces only the affected card. Cards with unchanged structure receive bound-value updates. The roster uses an observable, keyed collection with a MAUI CollectionView; unchanged rows retain their controls, while changed rows and category totals are updated. Datasheet weapon, ability, attachment, enhancement and keyword regions have separate dependency fingerprints. A target-affliction toggle updates dependent effects/weapons without clearing the page. Feel No Pain presentation also updates when attachments change.

The equipment projection no longer prepares hidden datasheet weapons, keyword explanations, abilities, enhancement options or attachment candidates. It prepares its equipment controls, size options and visible Feel No Pain badge. Navigation to a different screen/unit/tab, mode changes and source replacement can still construct the newly selected view; a counter edit on the same view does **not** call `page.Clear` or `Render`.

`FullPageBuildCount`, `EquipmentUpdateCount` and `EquipmentCardBuildCount` are available on MainPage for subsequent Android runtime verification. This behavior is established by implementation/build review; native control lifetime, frame timing and responsiveness still require the blocked device/emulator run. No Android timing is inferred from the host harness.

### Before / after host measurements

Same host and repaired real library as the diagnosis: i7-9750H, Windows x64, Release .NET 10, 2,194 units, 3,880 reference cards, revision `cc1830fbfead059f8059ed6c7f8e73cd4c390908`. After values are medians of five samples from `.local/performance-final-projections/results.json`. Before values are retained in `.local/edit-performance/before-results.json` and the earlier `final-run.log`. Medians of nested stages are not additive.

| Equipment edit / related projection | Before: three entries | After: three entries | Before: twenty entries | After: twenty entries |
|---|---:|---:|---:|---:|
| Total shared command, excluding presentation | 632.8 ms | 7.94 ms | 896.9 ms | 10.34 ms |
| Save | 613.6 ms (separate Save sample) | 7.09 ms | about 603.8 ms (nested Save sample) | 7.08 ms |
| One army calculation | 5.13 ms | 0.26 ms | 63.21 ms | 1.16 ms |
| Calculations per committed equipment command | 3 | 1 | 3 | 1 |
| Full dataset loads per command | 1 | 0 | 1 | 0 |
| Equipment-view preparation | 14.59 ms | 2.71 ms | 15.75 ms | 2.97 ms |
| Available-list cache lookup after edit | Recomputed if roster requested it | 0.029 ms, no builds | Recomputed if roster requested it | 0.034 ms, no builds |
| Roster/editor preparation after edit | 239.2 ms | 0.21 ms | 396.6 ms | 0.55 ms |
| Command cumulative allocations | 231.66 MiB | 0.208 MiB | 697.29 MiB | 0.772 MiB |
| Command + cache lookup + equipment preparation | Not captured together before | 12.73 ms | Not captured together before | 13.35 ms |
| Same combined-path cumulative allocations | Not captured together before | 1.324 MiB | Not captured together before | 1.888 MiB |

The before full-page/native UI time was never measured, so there is no invented before/after frame-rate or total phone-tap number. Available-list work is part of roster preparation, not an extra hidden stage on every equipment-tab click. The command improvement is approximately 80x for the small workload and 87x for the larger one in this run; this is a host comparison, not a physical-device speedup claim. New equipment preparation still uses the existing shared constraint checks.

| Cache scenario | Host time in this run | Full eligibility builds | Row projections |
|---|---:|---:|---:|
| Cold | 30.67 ms | 1 | 1 |
| First warm lookup | 1.86 ms | 0 | 0 |
| Warm lookup after equipment | 0.016 ms | 0 | 0 |
| Add unit | 1.89 ms | 0 | 1 |
| Remove unit | 1.13 ms | 0 | 1 |
| Change detachment | 6.74 ms | 1 | 1 |
| Change faction | 12.80 ms | 1 | 1 |

These cache-transition values are single observations, including possible JIT effects; architectural counters are the acceptance assertions. The SQL trace for Save contains only army/context writes and zero snapshot reads. No timing threshold is used as a correctness test.

Repeatable harness: `tools/GrimList.Performance/GrimList.Performance.csproj` and its README. It uses the real shared services and a disposable SQLite database. `MobileProbe.cs` contains host-only pure presentation preparation, explicitly excluding MAUI/Android rendering. Existing diagnostic fixtures and old APK/source backups remain under `.local`.

### Regression and runtime status

- Desktop solution: **PASS**, zero warnings/errors, `.local/performance-final-desktop-build.log`.
- Full automated suite with the real-catalogue optional inputs: **221 PASS**, confirmed again after final source-cache lifetime review in `.local/performance-delivery-tests.log`. This retains both Death Guard Leader orders and dynamic constraints, repeated prices, nested selections, equipment, allies, enhancements, undo/redo and persistence checks.
- Seven architectural regressions cover zero dataset access during Save, one calculation and stable caches during equipment edits, add/remove/faction invalidation, paid-equipment remaining-points invalidation, source/record-copy index replacement, failed-save state/history, restored context/session-local history and stable observable presentation values.
- Extended desktop smoke: **PASS**, final result `.local/tests/9b733f4e-74dd-4b18-9976-0859f82d8d21/performance.png.result.txt`.
- Android C# compilation and build using the existing `AndroidTest` profile: **PASS**, zero warnings/errors, `.local/performance-mobile-profile-build.log`. A plain Release invocation first reproduced the pre-existing MSB4216/MSB4027 trimming task-host failure; no project restructuring or toolchain reinstall was used to address it.
- Android runtime and actual phone performance: **BLOCKED / NOT RUN**. ADB lists no connected device. `emulator.exe -version` and `-list-avds` reproduce `CreateNamedPipe: Access is denied (5)` in the emulator crash handler even without GrimList. `Medium_Phone_API_35` is still listed; it was not recreated or wiped. This remains a tooling/environment failure, separate from application verification.

Physical-device acceptance must still check repeated plus/minus and exclusive-choice edits, a role replacement that changes option rows, resizing, header/validation updates, both Leader orders, enhancement and warlord changes, undo/redo, search, and save -> force-stop -> reopen -> edit/save again. Observe page/card counters and actual tap/frame timings while doing so. The performance implementation must not be described as fully verified on Android until that run succeeds.

### Updated test APK

Final clean Release publish with the existing `AndroidTest` profile: **PASS**, exit 0, no warnings/errors in `.local/performance-delivery-android-clean-publish.log`. Delivered as `artifacts/android/GrimList-Android-Test.apk` (64,612,287 bytes). Android package inspection confirms `com.grimlist.mobile`, version 0.1/code 1, minimum API 30, target API 36, ARM64 and x64. `apksigner verify --print-certs` succeeds and confirms the same development signing certificate as the previously distributed APK, SHA-256 `9be9c191c53dc33ee955dcaedad94868fe4a4e6817a1fb30187ac0baf7f28f25`.

APK SHA-256: `D4EB71D72ACB54FFA9A31B7C2CB76C40532AE0BE59F40CE5B7A0B630DAD81397`.

Install over the existing app; do not uninstall or clear storage. This performance update needs no library reimport. The repaired library JSON is unchanged (SHA-256 `FAFC749F56F19DBF09E3117772C39384B3674DF1EC0CDE3A932A1DA414AD6439`). The previous APK is retained at `.local/GrimList-before-performance.apk`. Packaging and signature checks do not substitute for the pending Android runtime/lifecycle test.

## Windows / phone stratagem transfer - 2026-10-05

The user's phone is missing Virulent Vectorium stratagems while Windows displays them. The Windows launch script uses `.local/grimlist.db`; read-only inspection of its active snapshot confirmed 45 Death Guard stratagem cards, including six for Virulent Vectorium. These cards are exactly equal to those in the supplied `tests/fixtures/libraries/GrimList-Library.json`. Their recorded supplemental source is the Death Guard faction reference, edition 11, Faction Pack 1.2, dated 2026-08-26. No other faction's detachment stratagems are present in this Windows database. This is a comparison of existing local content, not a new verification of current published rules.

Both frontends use `ArmyReferences.ForArmy` and `MissingStratagemDetachments`, and both load references through the same `ArmyStore` snapshot path. Windows automatically chooses Profiles/Combined for library JSON in its file-open handler. Mobile retains the user's current import layer; its Official MFM provider also forces Points only. The shared merge deliberately excludes incoming reference cards for Points only and retains existing supplemental cards on later BSData merges. These are demonstrated paths that can explain different library contents; they do not establish which action caused this particular phone's state.

ADB reports no device. No export of this phone's active dataset was supplied, so its actual missing/mismatched cards cannot yet be inspected. Save a phone dataset export before the transfer test. No current Android loading/filtering defect has been demonstrated, and neither shared reference filtering nor rules semantics were changed.

Changes for the transfer test:

- Shared `StratagemImportSummary` reports source card inclusion/skipping, wrong-edition cards and resulting faction/detachment coverage during import preview. Both frontends display the summary; mobile explains the reference-capable layers before choosing a document. This reporting runs during import, not during army editing.
- `tests/fixtures/references/GrimList-DeathGuard-Stratagems.json` contains the exact existing 45 Windows Death Guard cards, with source provenance intact. It contains no units, prices, enhancements or detachment definitions. Its SHA-256 is `A2178F2790D0315DFFCEB74B55E70B42AF160AB58DAE47F1DDF5D5D263825DC8` (43,873 bytes).
- `docs/runbooks/android/TEST-STRATAGEMS-ON-PHONE.txt` gives the phone test: preserve an export, install over the existing app, import the small pack using Datasheets only, review 45 included cards / six Virulent Vectorium cards / zero price changes, apply, check selected-detachment filtering and unchanged army data, then force-stop/reopen.
- New regressions exercise Points-only exclusion, subsequent BSData preservation, edition diagnostics, reference-only stream preview/apply, live shared workspace refresh and SQLite reopening. The real-library scenario reads the exact distributed reference pack and compares it against the full Windows-exported library.

Phone comparison and runtime transfer acceptance remain **NOT RUN**, pending the user's device test. The pack restores existing Death Guard coverage; adding other factions' missing source text is a separate task.

Shared/desktop verification: `dotnet build GrimList.slnx --no-restore -m:1 -nr:false -v:minimal` **PASS**, zero warnings/errors (`.local/stratagem-transfer-desktop-build.log`). Full tests **225 PASS** (`.local/stratagem-transfer-tests.log`), using the existing real-catalogue flags plus `--stratagem-library tests/fixtures/libraries/GrimList-Library.json --stratagem-pack tests/fixtures/references/GrimList-DeathGuard-Stratagems.json`. In particular, the actual reference pack restores all six cards through `ReadDocumentAsync` -> preview -> `ArmyWorkspace.ApplyLibraryAsync` -> session refresh -> SQLite reopen, with unchanged unit JSON, saved army and editor context. Those are shared-service host checks, not a native phone run.

Extended WPF smoke **PASS**: `.local/tests/9fa2bd5e-3d50-4152-8ce7-b0b4f39481fc/stratagem-transfer.png.result.txt`. Clean Android Release publish with the existing AndroidTest profile **PASS**, exit 0, no warnings/errors (`.local/stratagem-transfer-android-publish.log`). The supplied APK is 64,616,383 bytes, SHA-256 `04D4A64A3184C7ACB80D0673BF197384E091DA5A2384EFC56FC3AD4633F37F86`. Signature verification passes with the same certificate as the previous APK; package remains `com.grimlist.mobile`, minimum API 30, target API 36, ARM64/x64. The previous performance APK is backed up at `.local/GrimList-before-stratagem-transfer.apk`. Full library JSON is unchanged. No phone/emulator install or launch was performed during this pass.
