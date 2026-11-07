# Reference acquisition and pack preparation

`GrimList.Data references` is an explicit PC data-preparation command. Source
acquisition, parsing and matching live in `tools/reference_acquisition`, using
Python 3.10+ standard-library HTTP and HTML parsing. The C# entry point validates
the candidate with `StructuredJsonReferenceAdapter`, runs `DatasetMerge.Prepare`
and exports a portable pack plus an import preview. It never opens the active
database or applies an import. Windows and Android consume these packs offline
without a Python dependency. Since the October 27 source change, explicit 11e
BSData updates also use the shared .NET stratagem downloader for mapped source
pages; opening the app remains offline. See
`docs/stratagem-refresh-and-importer-gaps.txt` for its supported layouts,
compatibility checks and live-acquisition limitations. The historical
acquisition results below describe their pinned inputs, not current coverage.

## Current acceptance and limits

The generic text parser reproduces all 45 historical Death Guard stratagems,
including names, CP, detachment and WHEN/TARGET/EFFECT/RESTRICTIONS fields. It also
parses real, short browser excerpts for Hallowed Martyrs and Berzerker Warband.
Those excerpts contain one card each. Their provenance is documented under
`tools/reference_acquisition/fixtures/README.txt`.

The initial portable output therefore contains **47 cards across three factions**:
45 historical Death Guard cards, Suffering and Sacrifice for Hallowed Martyrs,
and Hack and Slash for Berzerker Warband. All 11 represented scopes are Partial.
The pinned BSData universe contains 36 factions and 444 selectable combinations;
433 combinations remain Unknown. These counts describe these inputs, not an
assumption in the collector or an independent certification of game legality.

An all-faction `update --all` was attempted. The local HTTP environment returned
`WinError 10061` before receiving a source response. The tool retained successful
cached sources and reported 33 factions without acquired sources. It did not
guess faction URLs or rules. Full live HTML acquisition and full-faction HTML
parser acceptance remain **unverified**. The HTML parser has synthetic structural
tests; real text fixtures do not substitute for full HTML acceptance. Supply
normally saved source pages, or run the same command where ordinary HTTP access
is available, to finish those acceptance steps.

## Commands

### All-faction manifest review

The three cached HTML pages now produce 120 cards, with 24 complete stratagem
collections and one complete zero-card collection (Sanctified Orators). The
coverage layer confirms the latter from its bounded detachment section in the
complete HTML document; parser version 1.3 is unchanged. Text excerpts, truncated
HTML, missing section boundaries and parser failures cannot declare complete
zero-card coverage. The shared JSON adapter/merge preview reports Sanctified
Orators as Complete with StratagemCount 0 and no missing-content warning.

`artifacts/references-real/reference-fallback-review.json` and `.txt` enumerate
all 12 existing fallback matches. Six cards belong to Mortarion's Hammer and six
to Death Lord's Chosen. Source names use curly apostrophes, BSData uses straight
apostrophes; all use the existing generic NormalizedName fallback. No semantic
alias is required.

Generate the faction-page manifest without making HTTP requests:

```powershell
dotnet tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll references plan-factions --dataset tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json --universe tests/fixtures/references/11e/detachment-universe.json --catalogues tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/catalogues.json --url-mappings tools/reference_acquisition/faction-source-mappings.json --cache tests/fixtures/wahapedia/snapshots --output artifacts/references-real
```

Review `reference-faction-source-manifest.json` or `.txt` in that output folder.
It contains all 36 canonical faction IDs and names, source slugs/URLs, discovery
methods, evidence, enabled state and reasons. Three URLs come from cached HTML,
three from indexed Wahapedia faction pages, and 30 are explicitly unverified
generic slug candidates. No URL field is blank. URL existence and applicability
are not established merely by assigning a candidate. No required semantic URL
alias has been identified yet. Unusual source locations can be recorded by
canonical faction ID in `faction-source-mappings.json`, outside the parser.

The two Titan catalogues are listed but disabled because the pinned canonical
universe assigns them zero selectable detachments. The other 34 faction entries
are enabled. This is one faction landing-page candidate per faction, never one
URL per detachment.

Bulk acquisition is held for the user's requested manifest review. After review,
the prepared command is:

```powershell
dotnet tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll references update --dataset tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json --universe tests/fixtures/references/11e/detachment-universe.json --source-manifest tests/fixtures/references/11e/reference-faction-source-manifest.json --cache tests/fixtures/wahapedia/snapshots --output artifacts/references-real --all --missing --delay 3 --timeout 30 --retries 2
```

A dry run verified this selects 31 new faction pages and skips the three complete
cached factions, including Sisters after the zero-card declaration. Existing
cache/conditional-request/failure handling remains in use. The candidate retains
previously collected unselected sources. After collection, the full offline build
includes all cached sources and produces one combined candidate and preview:

```powershell
dotnet tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll references build-pack --dataset tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json --universe tests/fixtures/references/11e/detachment-universe.json --source-manifest tests/fixtures/references/11e/reference-faction-source-manifest.json --cache tests/fixtures/wahapedia/snapshots --output artifacts/references-real --all --offline
```

The current preparation passes 86 acquisition/parser/collection tests. No bulk
acquisition, active-library application, or disposable-library application is
claimed by this manifest review step.

### First real sources: local Windows runbook

This is the next acceptance step, not full-source acceptance. The initial targets
are the previously recorded edition-11 faction pages below. Their URLs are known;
ordinary automated retrieval from the user's Windows session is still unverified.
An agent-side bounded probe returned `NetworkUnavailable` / `WinError 10061` at
`https://wahapedia.ru/robots.txt`, before any HTTP response. That does not establish
a parser failure, a global outage, or a need to change GrimList networking.

| Faction | Source URI | Manually saved HTML |
|---|---|---|
| Death Guard | `https://wahapedia.ru/wh40k11ed/factions/death-guard/` | `.local/reference-input/death-guard.html` |
| Adepta Sororitas | `https://wahapedia.ru/wh40k11ed/factions/adepta-sororitas/` | `.local/reference-input/adepta-sororitas.html` |
| World Eaters | `https://wahapedia.ru/wh40k11ed/factions/world-eaters/` | `.local/reference-input/world-eaters.html` |

The machine-readable page/file list is
`tools/reference_acquisition/initial-real-sources.json`. Save full pages with all
detachment stratagem sections, not search excerpts or individual cards. If normal
browser access is also denied, report that rather than bypassing the restriction.
Plain rendered text is supported at the same paths with `.txt` instead of `.html`;
preserve detachment headings, names, CP, WHEN, TARGET, EFFECT and RESTRICTIONS.
HTML is preferable for validating page structure. Text alone does not prove complete
section boundaries. Never relabel another edition's page as edition 11.

Run these setup commands in ordinary Windows PowerShell. They require .NET 10 and
Python 3.10+ available as `python`. They do not require running a PowerShell script
or changing its execution policy. The pinned dataset and audit are already present.

```powershell
Set-Location D:\Konstantinos\GrimList
dotnet build tools/GrimList.Data/GrimList.Data.csproj --no-restore -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'Data tool build failed' }
$dataTool = 'tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll'
$refArgs = @(
  '--dataset', 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json',
  '--universe', 'tests/fixtures/references/11e/detachment-universe.json',
  '--source-manifest', 'tools/reference_acquisition/initial-real-sources.json',
  '--coverage-pack', 'artifacts/references/GrimList-References-11e.json',
  '--cache', 'tests/fixtures/wahapedia/snapshots',
  '--output', 'artifacts/references-real',
  '--faction', 'Death Guard',
  '--faction', 'Adepta Sororitas',
  '--faction', 'World Eaters'
)
```

First generate/review the manifest without making HTTP requests:

```powershell
dotnet $dataTool references plan @refArgs
```

`artifacts/references-real/reference-source-manifest.json` contains **all 444**
canonical entries, including edition/IDs/names, known faction-page locations,
snapshot presence, baseline coverage, acquisition required and selection for this
run. A known faction page is not proof that it contains every detachment section.
The historical 47-card pack is used only to report baseline coverage (11 Partial,
433 Unknown); those fixture cards are not copied into the fresh candidate. Initial
known source locations cover 25 entries: 11 previously Partial and 14 previously
Unknown. The other 419 entries have no discovered source location. No guessed
URLs are assigned to them.

The one acquisition command for these three factions is:

```powershell
dotnet $dataTool references update @refArgs --missing --delay 3 --timeout 30 --retries 2
```

`update` is the existing end-to-end command: acquire, cache, parse, match, generate
candidate, validate using the shared C# adapter, and preview. `acquire` alone only
downloads. With an explicit source manifest, neither command retrieves the index.
Requests are serial, with robots checks, bounded retries, conditional validators,
and host-wide stopping after denial/challenges/rate limits. Missing coverage is
selected at faction-page granularity; this is not one request per canonical entry.
The existing `--missing` behavior skips fully covered factions. Checking those
complete factions for later revisions requires a deliberate update without
`--missing`; no claim of remote unchanged content is made without a check.

Reports distinguish `NetworkUnavailable`, `AccessDenied`, `NotFound`, `RateLimited`,
`ParseFailure`, `MatchFailure` and `Success`. Other HTTP failures retain `HttpFailure`
and their HTTP status; not attempted/offline-missing inputs remain `NotAttempted`.
Each source row records acquisition outcome and the URI/status responsible, including
robots-policy failures. One failed page does not discard another page's success.
The latest online result is also preserved as `reference-acquisition-last-online.json`
so later offline parsing does not erase the network diagnostic evidence.

Raw evidence lives under:

```text
tests/fixtures/wahapedia/snapshots/index.json
tests/fixtures/wahapedia/snapshots/wahapedia/11e/<source-key>/<sha256>-<evidence-key>/raw
tests/fixtures/wahapedia/snapshots/wahapedia/11e/<source-key>/<sha256>-<evidence-key>/metadata.json
tests/fixtures/wahapedia/snapshots/parsed/<parse-key>.json
```

The immutable metadata retains URI, acquisition time, ETag/Last-Modified when
available, hash, edition, adapter version and origin. Parsed caches record parser
version. The new cache uses the existing SnapshotStore format and keeps old test
excerpts separate from fresh acceptance inputs. Raw HTML/text never enters the
portable pack.

If automatic access fails, create the manual-input directory, save the three
pages using the table above, and import the saved files into the same cache:

```powershell
New-Item -ItemType Directory -Path .local/reference-input -Force | Out-Null
$targets = Get-Content tools/reference_acquisition/initial-real-sources.json -Raw | ConvertFrom-Json
foreach ($target in $targets.Factions) {
  $snapshotFile = $target.ManualHtml
  $mediaType = 'text/html'
  if (-not (Test-Path -LiteralPath $snapshotFile)) {
    $snapshotFile = $target.ManualText
    $mediaType = 'text/plain'
  }
  if (-not (Test-Path -LiteralPath $snapshotFile)) {
    Write-Warning "Missing saved page: $($target.Name)"
    continue
  }
  dotnet $dataTool references import-snapshot --edition 11 --cache tests/fixtures/wahapedia/snapshots --file $snapshotFile --uri $target.SourceUri --source-faction $target.Name --media-type $mediaType
  if ($LASTEXITCODE -ne 0) { throw "Snapshot import failed: $snapshotFile" }
}
```

Both acquired and manually saved snapshots go through the same adapter:

```powershell
# Parse/match offline and report; does not publish a validated pack.
dotnet $dataTool references parse @refArgs --offline

# Parse/match, build the candidate, validate, and generate an import preview.
dotnet $dataTool references build-pack @refArgs --offline

# Recompute the offline coverage/acquisition report.
dotnet $dataTool references audit @refArgs --offline
```

Outputs under `artifacts/references-real`:

- `GrimList-References-11e.json`: validated portable pack, only when cards were
  successfully generated and C# validation passed.
- `reference-import-preview.json`: generated with that validated pack; `Applied=false`.
- `reference-acquisition-report.json` and `.txt`: all canonical entries, source
  outcomes, parsing/matching, card counts, coverage, baseline coverage and gap reasons.
- `reference-coverage.json`, `reference-matches.json`, `reference-source-diff.json`.

Fresh-source coverage is separate from historical baseline coverage: an empty
real-source run has zero generated cards, while the historical pack remains intact.
Exit 2 means partial results; inspect the report. Exit 1 means a fatal input/tool
failure. Exit 0 does not by itself prove real-page or rule completeness.

An optional equivalent wrapper is `scripts/Acquire-ReferenceSources.ps1` with
`-Step Plan`, `Acquire`, `ImportSnapshots`, `Parse`, `Build` or `Audit`. Use the
direct commands above where PowerShell script execution is disabled.

Do not start bulk discovery/collection until full real snapshots for the three
targets have been parsed and reviewed, including semantic comparison with the
45-card Death Guard control and several complete Sisters/World Eaters detachment
sections. Disposable-library application/BSData-retention tests and Android
portability acceptance remain subsequent steps. This preparation neither applies
to the active library nor claims those acceptance tests have been completed.

Run from the repository root. Build once:

```powershell
dotnet build tools/GrimList.Data/GrimList.Data.csproj -m:1
$dataTool = 'tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll'
$dataset = 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json'
$universe = 'tests/fixtures/references/11e/detachment-universe.json'
dotnet $dataTool references --help
```

Use your own exported dataset and matching audit for another source revision.
`--universe` is produced by the existing offline source audit documented in
`tools/reference_audit/README.txt`. Edition, revision and archive hash must match
the dataset. Only source-selectable, resolved canonical identities participate.
Source pages cannot add factions or detachments to that universe.

```powershell
dotnet $dataTool references discover --dataset $dataset --universe $universe
dotnet $dataTool references acquire --dataset $dataset --universe $universe --all
dotnet $dataTool references acquire --dataset $dataset --universe $universe --missing
dotnet $dataTool references update --dataset $dataset --universe $universe --all
dotnet $dataTool references update --dataset $dataset --universe $universe --faction 'World Eaters'
dotnet $dataTool references update --dataset $dataset --universe $universe --all --dry-run
dotnet $dataTool references build-pack --dataset $dataset --universe $universe --all --offline
dotnet $dataTool references parse --dataset $dataset --universe $universe --offline
dotnet $dataTool references audit --dataset $dataset --universe $universe --offline
```

`--faction` is repeatable and filters canonical names; it never selects a parser.
With no faction filter, all canonical factions are selected. `--missing` selects
factions having scopes without an authoritative completeness declaration in the
dataset or previous output. Use ordinary `update --all` to check complete sources
for later revisions. `--offline` makes zero HTTP requests. `--dry-run` writes
nothing and makes zero HTTP requests; it reports planned pages and undiscovered
factions. `--source-manifest` supplies a previous discovery result;
`--index-uri` supplies a saved or online source index/sitemap location.

Import manually saved snapshots through the same cache and parser:

```powershell
dotnet $dataTool references import-snapshot --file saved-page.html --uri 'https://wahapedia.ru/wh40k11ed/factions/world-eaters/' --source-faction 'World Eaters' --media-type text/html
dotnet $dataTool references build-pack --dataset $dataset --universe $universe --offline
```

For saved rendered text use `--media-type text/plain`. `--source-revision` and
`--source-date YYYY-MM-DD` preserve known metadata; unknown values remain blank.
An index HTML or XML sitemap can also be imported using its original URI. Its
links will be discovered by an offline `discover`/`update`. A partial excerpt is
identified with `--origin browser-excerpt`. The parser does not change based on
whether bytes came from HTTP, a saved file or an existing cache.

Defaults: `--edition 11`, `--cache .local/reference-sources`,
`--output artifacts/references`, `--delay 3`, `--timeout 30`, `--retries 2`.
Exit codes: **0** selected work succeeded; **2** partial results with explicit
source/parser/matching failures; **1** invalid inputs or fatal validation failure.
An exit code of zero does not declare source completeness.

## Boundaries and provenance

`snapshots.py` writes content-addressed raw documents and immutable metadata;
`index.json` tracks latest snapshots and revision history. Each snapshot includes
the source URI, acquisition time, content hash, edition, adapter version,
origin, ETag, Last-Modified and known source revision/date. Parser results use a
separate cache keyed by the snapshot and parser version. Change the parser version
when changing parsing semantics so old parsed results are not silently reused.

`http.py` performs serial requests, identifies the collector, checks robots rules,
honors crawl delay/request rate, sends conditional validators, limits response
size, applies timeouts and bounded transient retries. It stops on authentication
denial, CAPTCHA/challenge responses, redirects and HTTP 429. It does not use
credentials, browser impersonation, alternate proxies or bypasses. HTTP access
can remain unavailable while manual snapshots continue to work.

`adapters.py` defines the source-format adapter boundary. Adding a source means
implementing discovery, parsing and an index URI, returning `ParsedDocument` and
`ParsedReferenceRecord`; acquisition stays in the fetcher/cache. The Wahapedia
adapter recognizes semantic card components and labeled operative fields. The
text fallback recognizes CP markers, stratagem type/scope lines and field labels.
It supports typed and untyped scope headers, repeated fields and optional target
or restriction fields. Unrecognized or malformed cards produce errors. No faction
name changes parser behavior. Raw source documents remain outside portable packs.

`matching.py` resolves faction then detachment inside edition/faction context.
Priority: configured mapping, exact source/canonical ID, exact name, normalized
name. Ambiguous and unresolved results are never silently selected. Overrides are
data; for example, `--aliases aliases.json` accepts this shape:

```json
{
  "source": "wahapedia",
  "edition": "11",
  "factionAliases": { "External faction spelling": "canonical-faction-id" },
  "detachmentAliases": {
    "canonical-faction-id": { "External detachment spelling": "canonical-detachment-id" }
  }
}
```

Generated cards retain external source ownership, snapshot hash, retrieval time,
known revision/date and parser version. BSData supplies applicability IDs only.
`reference-matches.json` also records the raw source location, fields and match
classification. Pack detachment definitions are copied from the supplied BSData
dataset with resolved IDs; prices and unit definitions are not acquired from
the external rules source. The shared merge preview asserts unchanged units.

## Completeness and updates

Card count never establishes completeness. Rendered text and excerpts are always
Partial. Recognizable HTML cards without verified section boundaries are Partial.
The currently supported complete-section form is an explicit stratagem section
with a detachment attribute, all recognized card containers parsed successfully,
no errors, and a complete HTML document. This form is covered by synthetic tests;
no real source in the delivered pack receives a completeness declaration.

Complete declarations authorize absence-based removal only within the existing
shared merge's source/category/edition/faction/detachment scope. Partial source
absence appears in the diff but retains earlier cards. Failed pages preserve
previous successful records and downgrade stale completeness. Unselected factions
remain in a previously built combined pack. Duplicate/conflicting alternate
occurrences are reported; conflicting incoming cards are quarantined.

## Outputs and application import

`artifacts/references` contains:

- `GrimList-References-11e.json`: the validated portable pack.
- `GrimList-References-11e.candidate.json`: intermediate Python output; use the
  validated file for application imports.
- `reference-source-manifest.json`: discovered pages, source scopes and matching.
- `reference-acquisition-report.json`: source outcomes and every canonical scope.
- `reference-coverage.json`: canonical coverage totals and rows.
- `reference-matches.json`: identity mapping evidence and parsed fields.
- `reference-source-diff.json`: Added/Updated/Unchanged/RemovedFromSource/FailedParsing.
- `reference-import-preview.json`: shared C# merge decisions and coverage; not applied.
- `reference-verification.json`: recorded test results, pack hash, reproducibility
  evidence and remaining real-source acceptance limits for this delivery.

Import the validated JSON explicitly with **Manage data → Import rules references**
on either platform. Review changes and conflicts before applying. Existing dataset
exports then carry the same canonical cards, provenance and coverage to Android.
Raw HTML/text and acquisition caches are never part of the portable dataset.

Tests:

```powershell
python -B -m unittest tools.reference_acquisition.test_acquisition -v
dotnet build tests/GrimList.Tests/GrimList.Tests.csproj -m:1
dotnet tests/GrimList.Tests/bin/Debug/net10.0/GrimList.Tests.dll
```

The shared equipment-edit regression verifies zero reference source reads,
zero parsing/merging, retained reference-index identity and one calculation.
The acquisition modules have no application project reference and cannot run
as a result of normal roster editing.

## Offline shared-page subsection validation

A manifest row may carry `SourcePageFaction` (the HTML title/h1 identity) and
`SourceFactionSection` (the requested subsection). Multiple canonical factions
can share a `SourceUri`; raw acquisition is deduplicated by that URI.

`sections.py` selector version 1.0 validates a complete edition-specific page,
finds one matching `Codex Supplement` heading, and ends at the next top-level
non-detachment heading. It requires each detachment and card's `clFl` chapter
filter to agree with the supplement. It excludes generic detachments outside
that range. Missing, duplicate, truncated or conflicting boundaries fail closed.
Without composition configuration, missing supplement boundaries are rejected;
they are never treated as permission to parse the whole page for a chapter.

The selected original DOM nodes feed the common Wahapedia card parser.
Raw HTML and snapshot metadata stay immutable. Parsed-cache keys include the
subsection, expected page identity and selector version. Zero-card completeness
uses the same selected nodes. Unscoped sources keep their existing parser path.

Black Templars-only verification, with no HTTP or candidate publication:

```powershell
dotnet tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll references parse --offline --dataset tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json --universe tests/fixtures/references/11e/detachment-universe.json --source-manifest tests/fixtures/references/11e/reference-faction-source-manifest.json --cache tests/fixtures/wahapedia/snapshots --output artifacts/references-real/pilot/black-templars --faction "Black Templars"
```

The pinned real shared-page fixture is recorded in
`tools/reference_acquisition/fixtures/space-marines.snapshot.json`; its HTML
remains under `tests/fixtures/wahapedia/snapshots`. Run the isolation regressions with
`python -B -m unittest tools.reference_acquisition.test_shared_sources`.

### Parser 1.4: semantic card identity

The shared-page selector remains version 1.0. Card parsing now uses parser 1.4:
HTML anchors are provenance locators, not unique card keys. The cached Custodes
page contains two real cards for each repeated anchor `FLAWLESS-CONSTRUCTION`
and `UNLEASH-THE-LIONS`, in different detachment sections with different rule text.

Source card IDs now hash the normalized faction/subsection, detachment, category
and name. CP and ordered rule blocks distinguish identical repeats from conflicting
cards within that identity. Both kinds are reported and rejected by existing pack
validation. A changed anchor or updated rule text keeps the stable semantic ID;
different detachments get separate IDs. Anchors remain in parsed record locations
and matching reports. This intentionally changes source card IDs from parser 1.3,
so the parser version also invalidates older parsed caches.

Pinned pre-change semantic fingerprints verify every parsed field, detachment
declaration, warning and error for Death Guard, Adepta Sororitas, World Eaters
and Black Templars, excluding only the intentionally replaced source card ID.
Run `python -B -m unittest tools.reference_acquisition.test_semantic_identity`.

### Offline coverage cleanup: parser 1.5 and composition selector 1.1

Shared-page mappings now have `SourceComposition` data naming the source filter
group, option and label. The selector reads the cached page's `_wahaFBits` JSON
table and actual `data-f` masks without executing JavaScript. It includes every
applicable detachment container before the game-mode boundary, including generic
detachments, and excludes all source-declared inapplicable containers. A valid
closed document, heading boundaries and matching filter identity remain required.
Supplement-only selection remains available when composition data is absent.

Parser 1.5 recognizes an actual stratagem embedded under Detachment Rules using
the same component, CP, scope and boundary checks. Explicit army-rule movement
panels (`str11AMoveBg`, no CP or stratagem type) are reported as excluded;
unrecognized card wrappers still fail. No faction name selects parser behavior.

`reference-aliases.json` is the data tool's default matching configuration. Its
source scope aliases permit only recorded card-marker/heading discrepancies;
canonical detachment aliases identify the exact BSData destination and report
Alias matches. The parser cache includes an alias-configuration hash. Removing
an alias restores strict conflict rejection.

The portable schema has one faction per applicability record. Generic source
cards therefore have one record per applicable canonical catalogue, with no
duplicate `(faction, detachment, card)` entries. The cleanup audit also reports
distinct source cards and their canonical applications; raw snapshots are shared
and remain outside the portable pack. No application/reference schema changed.

Full offline regeneration (candidate and unapplied preview only):

```powershell
dotnet tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll references build-pack --offline --dataset tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json --universe tests/fixtures/references/11e/detachment-universe.json --source-manifest tests/fixtures/references/11e/reference-faction-source-manifest.json --cache tests/fixtures/wahapedia/snapshots --output artifacts/references-real --all
```

Regression fixtures and tests: `fixtures/cleanup-source-snapshots.json` and
`python -B -m unittest tools.reference_acquisition.test_coverage_cleanup`.
