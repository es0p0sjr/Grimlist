# Space Marine stratagem snapshot

Both platforms embed `src/GrimList.Core/Resources/space-marine-stratagems-11.json`.
It contains the 142 standard detachment stratagem entries on
[Wahapedia's 11th-edition Space Marine page](https://wahapedia.ru/wh40k11ed/factions/space-marines/),
captured 14 October 2026. The page lists the October 2026 app revision 2.7.1.
The source date is the snapshot date, not a claim about an official publication date.

Coverage is 15 generic detachments (48 entries) and 20 Chapter detachments (94).
Boarding Actions, core stratagems, enhancements, unit profiles and army-building
rules are outside this snapshot. Each card retains its CP cost, timing, target,
effect and any restrictions. Repeated names in different detachments remain
separate cards. Completeness describes the captured page; it is not official
rules certification.

The source URL, revision, date and payload hash are part of the embedded pack.
`scripts/Build-SpaceMarineStratagems.py` regenerates it from a captured JSON map of
numbered page lines. It verifies that the source range has no gaps, that all 142
entries have their required fields, and that 35 detachment sets are present.
It removes browser link markup and excludes flavour text, section headings and
an unrelated legacy errata tooltip under Gladius Armour of Contempt. A later
snapshot requires a new source capture and review; there is no live scraping on
app startup. The original capture for this release is under
`.local/stratagem-fix/wahapedia-lines.json`.

The shared normalization supplies cards only for known BSData Marine catalogue
contexts and applicable detachment definitions. Thus Chapter cards follow the
existing Chapter eligibility rules. The Deathwatch Support name alias supplies
its four reference cards when that BSData placeholder exists; the placeholder's
name, cost, restrictions and army-building status remain unchanged.

The exact old Wahapedia source hash is retired from Marine stratagem scopes,
including reference-only imports and Android recovery. This replaces old cards
in still-current detachments as well as preventing restoration from the old
Android supplement. Other factions, manual sources, explicit rules packs and
tombstones retain their normal ownership. Newer complete reference imports can
intentionally omit cards without having the older fallback fill them again on
a BSData refresh. Saved armies and unit/points data are not rewritten.

Validation entry points:

- Shared regression runner: `--marine-stratagems-only`.
- Native Windows smoke runner: `--marine-stratagems-only`.
- Android isolated app: `scripts/Test-SpaceMarineStratagemsAndroid.py`.

The native checks open the Rules view with Wrath of the Rock, check its six
current cards, and reject its obsolete Armour of Contempt entry. Android also
checks the three-card Ironstorm set and repeats across two cold starts.
