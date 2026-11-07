Offline reference-content audit
===============================

This tool is diagnostic only. The application never invokes it while editing.
No downloads, database writes, synthetic source text, new runtime rule engine,
faction-specific extraction or changes to the coverage model are involved.

Inputs must share the pinned edition/revision/archive hash. Use the existing
shared importer to generate actual extraction results, then inspect all source
objects independently. Source identity is edition + faction catalogue ID +
resolved detachment target ID. Linked aliases collapse within that identity;
the same name with different IDs never collapses. Shared source IDs across
different factions remain separate applicability combinations and are reported.

Source graph audit:
- Starts from root selections/entry links and resolves linked definitions.
- Uses the existing shared NativeCatalogues boundary to distinguish allied roots.
- An explicitly supplied force ID defines the context; no default is guessed.
- Evaluates hidden modifiers with primary-catalogue/force instance conditions,
  AND/OR groups and modifier groups. Unknown visibility stays unresolved.
- Reports all excluded projections, cycles, missing/ambiguous IDs, aliases and
  historical catalogue-name signals. Historical names alone do not prove expiry.
- This establishes source visibility/reachability, not independent published
  legality or the legality of combining detachments at every points limit.

The source scan visits every JSON object, inventories containers, and records
reference-like candidates with IDs/paths/provenance and actual importer results.
Profile characteristics are represented once on their owning profile. Rules,
profiles, abilities, publications, selection structures, links/categories and
descriptive modifier/constraint fields are covered. Merely mentioning a stratagem
does not make text a stratagem. Structured text candidates require manual review.

Current-source repeatable commands (from repository root, PowerShell):

  dotnet build tools/GrimList.Data/GrimList.Data.csproj -m:1

  $cache = '.local/sources/bsdata-11-da7248777da54218034f09b6271ad89ff6b68a77'
  dotnet tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll reference-audit-input "$cache/repository.zip" "$cache/source.json" tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/catalogues.json

  dotnet tools/GrimList.Data/bin/Debug/net10.0/GrimList.Data.dll build-audited-reference-pack AndroidDetachFixExport/grimlist-points.json tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json .local/coverage-audit/references-before-audit.json artifacts/android/GrimList-References.json

  python tools/reference_audit/audit.py --archive "$cache/repository.zip" --dataset AndroidDetachFixExport/grimlist-points.json --imported tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json --before-imported .local/coverage-audit/before-plural-fix.json --catalogues tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/catalogues.json --pack artifacts/android/GrimList-References.json --force-id bb9d-299a-ed60-2d8a --output artifacts

  python -m unittest discover -s tools/reference_audit -p 'test_*.py' -v

The two before-*.json files are preserved diagnostic inputs from before this
extraction fix. Do not overwrite them when comparing the extraction delta.
The force ID above is read from this archive's Army Roster entry, not a universal
or faction-specific constant in the audit implementation.

Manifest/inventory:
  python tools/reference_audit/inventory.py --output artifacts/reference-source-inventory.json <explicit local source files...>

Pack building uses existing Core/Application merge and adapter semantics. It
retains source detachment definitions needed for ID matching, preserves the
user export's unit data byte-for-byte through shared serialization, and replays
the final pack through the public structured JSON adapter. It never applies to
a user database. The emitted preview lists reference changes; all completeness
claims remain those actually supplied by sources. This pack is PARTIAL.

The 36 faction catalogues include two Titan catalogues with no native detachment
in this source/context. The 444 selectable faction/detachment combinations share
270 source IDs; 12 combinations originate in a catalogue named Legends and are
explicitly flagged. These numbers do not certify the intended published universe.

Output:
  tests/fixtures/references/11e/detachment-universe.json
  artifacts/BSDataReferenceAudit.json
  artifacts/reference-extraction-delta.json
  artifacts/reference-source-manifest.json
  artifacts/reference-source-inventory.json
  artifacts/reference-coverage.json
  artifacts/reference-coverage.txt

All missing detachment source content is enumerated in the coverage files.
