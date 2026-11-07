# Pinned regression inputs

These are immutable inputs promoted from developer caches and historical exports.
The cleanup preserved their bytes; `manifest.json` records their SHA-256 hashes.
Do not replace them with current upstream content to make a regression pass.

- `bsdata/11e/cc1830.../`: historical catalogue archive and metadata, plus the Death Guard raw catalogue and original source trace. `CatalogueAttachmentChecks` resolves the trace's filenames relative to this revision folder; historical absolute paths inside the trace remain provenance only.
- `bsdata/11e/da724.../`: canonical imported dataset and catalogue inventory used by reference matching and disposable integration.
- `references/11e/`: canonical detachment universe and full faction-source manifest.
- `references/`: historical 45-card Death Guard pack and rendered-text/rules controls.
- `libraries/`: full and filtered historical datasets for import, equipment, attachment and reference-transfer checks. `installed-attachment-source.json` contains only three Death Guard units; use `all-factions-attachment-source.json` for the two-faction enhancement-defence check.
- `wahapedia/snapshots/`: the complete pinned snapshot store, retained together to keep its index and all-faction offline rebuilding valid without duplicate HTML copies. Eight pages are used directly by parser regressions; the other final pages retain release provenance. The unchanged small fixture descriptors remain in `tools/reference_acquisition/fixtures/`.
- `upstream/yellowscribe/`: the two upstream files consumed by the offline export compatibility script.

Full reference tests:

```powershell
python -B -m unittest tools.reference_acquisition.test_acquisition tools.reference_acquisition.test_collection tools.reference_acquisition.test_real_html tools.reference_acquisition.test_shared_sources tools.reference_acquisition.test_semantic_identity tools.reference_acquisition.test_coverage_cleanup
```

The application does not load this directory. Test databases and generated reports remain under `.local/tests` and `artifacts`. The frozen release pack remains `artifacts/references-real/GrimList-References-11e.json`; it is deliberately not copied into this fixture store.

For a new acquisition, use a separate working cache and output directory. Treat this store as the pinned source for historical/offline verification; do not publish its raw HTML or user-library fixtures as client bootstrap data.

`eleventh-gaps/` contains focused BSData JSON projections from revision `da7248777da54218034f09b6271ad89ff6b68a77` (archive SHA-256 `9ba45e3e9fffa9eb061f762d7703e386c566b391c7a463eeb60f3800bfa7f8cd`). The Houndpack Lance and Subterranean Assault Detachment nodes, Loping Predator entry and inline Plague Marines Enhancement group retain source rule text and conditions. Minimal containers/category identities supply their reference context. Test units and points surrounding these rules are synthetic. These fixtures cover required/optional nominations, membership conditions with an unused zero value, and structural Upgrade classification without name suffixes.
