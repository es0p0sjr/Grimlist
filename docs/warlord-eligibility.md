# Warlord eligibility

Windows and Android use `WarlordRules.Evaluate` and `Candidates` in shared Core
code. Evaluation projects the current roster entry through `EffectiveDatasheets`:
equipment, muster effects, keyword/ability choices, enhancements and deterministic
self-effects are included. Imported base profiles remain unchanged.

## Sources and precedence

`WarlordEligibility` reports eligibility, `MustBeWarlord`, `CannotBeWarlord` and
source-labelled reasons. Applicable prohibitions always win over CHARACTER,
Supreme Commander and imported mandatory-Warlord constraints. An ineligible
mandatory unit neither gets auto-selected nor suppresses legal candidates.

Prohibitions come from active datasheet abilities, optional structured
`WarlordRestrictions` on unit profiles/reference cards/detachments, selected
army/detachment reference rules and allied permissions. An allied unit also
consults its origin faction's reference card for the permission actually used
to include it. Unselected detachments, other factions, obsolete/unresolved
reference cards and stratagems do not supply permanent Warlord restrictions.

Text parsing recognizes explicit self-prohibitions such as “cannot be your
Warlord”, “cannot be selected as your Warlord” and “cannot be selected to be your
Warlord”, plus targeted army statements such as “INFANTRY models cannot be your
Warlord” and “You cannot select a SCOUT model as your Warlord”. It handles case,
HTML/markup and whitespace variations. Recognized keyword/army-faction
conditions are evaluated rather than flattened into unconditional rules.
Unresolved structured conditions require eligibility review; unsupported prose
or ambiguous recipient pronouns are not guessed into army-wide effects.

BSData Warlord selection constraints with a parent-scoped maximum of zero import
as structured prohibitions. Conditional SET overrides retain their source
conditions. Hiding a source selection alone is not assumed to forbid Warlord.

Existing libraries need no schema migration or bootstrap replacement: their
stored ability text is evaluated directly. New optional metadata round-trips
through the existing JSON/SQLite profile and reference storage. No faction,
unit, source ID or ability name is used to identify prohibited units.

## Editor and persistence

Both interfaces exclude prohibited units from Warlord selection and show the
source rule. Validation names the prohibition for an invalid selected Warlord,
or explains excluded units when no eligible candidate remains. A prohibited
unit is legal in an army that has a different eligible Warlord.

Existing normalization behavior clears an ineligible Warlord selection. Choice
and detachment edits, including that cleared selection, remain one undoable
session edit. Save/reopen retains legal choices and recalculates eligibility;
there is no persisted eligibility cache to become stale.

## Verification

Focused regressions: `dotnet tests/GrimList.Tests/bin/Debug/net10.0/GrimList.Tests.dll --warlord-only`.
Full fixture suite: `powershell -File scripts/Test-ReferenceFixtures.ps1`.

Coverage includes all four C'tan datasheets, Sydonian Skatros, Nork Deddog,
Ogryn Bodyguard, Lieutenant with Combi-weapon, Exalted Flamer, Deathleaper,
Callidus Assassin and Murderfang, with a legal Overlord control. Generic tests
cover mandatory-rule precedence, effective keyword/ability changes, army,
detachment and allied scope, structured metadata, BSData constraints,
save/reopen and undo/redo. The WPF smoke suite additionally checks the real
picker, visible explanation, legal selection and undo/redo.

On 2026-10-07 the full shared reference-fixture suite passed **292 checks**, with
zero failures (`.local/warlord-full-regression-final.log`; fixture output
`.local/tests/59e4dc20-9d6b-44c8-86b4-28eea2221449/`). The full Windows UI smoke suite passed, including the new Warlord
picker check (`.local/warlord-wpf/warlord.png.result.txt` and `.warlord.txt`).
The Android Release verification build passed ARM64/x64 startup-binding checks.
Native Android emulator checks confirmed exclusion/source explanations for
Transcendent C'tan and Deathleaper, including after process termination/reopen
(`.local/warlord-android-ui.log`, screenshots/XML in `.local/warlord-android/`).

The previously distributed `GrimList-Share-0.2.1` packages are unchanged by this
source fix. The Android APK under `artifacts/builds/warlord-fix/android/` is a
verification build, not a new versioned distribution release.
