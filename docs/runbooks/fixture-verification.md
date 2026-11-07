# Verification after fixture promotion

Run from the repository root. These commands build code and create disposable
libraries; they do not open or apply changes to `.local/grimlist.db`.

```powershell
dotnet build GrimList.slnx -m:1 -nr:false
dotnet tests/GrimList.Tests/bin/Debug/net10.0/GrimList.Tests.dll
python -B -m unittest tools.reference_acquisition.test_acquisition tools.reference_acquisition.test_collection tools.reference_acquisition.test_real_html tools.reference_acquisition.test_shared_sources tools.reference_acquisition.test_semantic_identity tools.reference_acquisition.test_coverage_cleanup
powershell -NoProfile -File scripts/Test-ReferenceFixtures.ps1
powershell -NoProfile -File scripts/Test-ReferenceFixtures.ps1 -IntegrationOnly
```

The optional real tests include the historical library/45-card transfer,
catalogue import, equipment and attachment checks. The integration-only command
uses the frozen all-faction pack and canonical BSData seed through the production
import path in a new disposable library. Its report location is printed by the
runner. Preserve that report before removing the corresponding test directory.

The independent Python audit suite runs with `python -B -m unittest test_audit`
from `tools/reference_audit`.

`tests/fixtures/manifest.json` records the promoted files' exact hashes and old
locations. Old absolute paths within immutable provenance JSON are historical
evidence; the tests use the new fixture locations. Do not rewrite goldens when
upstream pages change.

The snapshot store was promoted as a whole so the eight parser regression pages
and remaining release-source evidence have one authoritative copy. Use a separate
working cache/output for new acquisitions. The accepted pack remains under
`artifacts/references-real`; no hosted publishing workflow is implemented.

The original repository audit describes the pre-cleanup tree. Cleanup receipts,
the full safety archive location, and verification results are in
`artifacts/repository-cleanup/`. Historical milestone reports may intentionally
refer to their original locations; consult the move receipts rather than
recreating duplicate fixture files there.
