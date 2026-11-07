"""Build compact, deterministic compatibility metadata from an enhancement-only pack.

The pack must come from BsDataImporter.ImportEnhancementReferences (the optional
SourceEnhancementChecks --enhancement-catalogue run writes one). No game rule text,
unit profiles, faction-specific logic or guessed eligibility is bundled.
"""
import gzip
import json
from pathlib import Path
import sys

pack = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
cards = [dict(Faction=c["Faction"], Detachment=c["Detachment"], Name=c["Name"],
              TextHash="", Cost="", Eligibility=c.get("Eligibility"), IsUpgrade=c.get("IsUpgrade", False),
              SourceEntryId=(c.get("Details") or {}).get("SourceCardId", ""))
         for c in pack["ReferenceCards"] if c.get("Section") == "Enhancements"]
output = Path(sys.argv[2])
output.parent.mkdir(parents=True, exist_ok=True)
data = json.dumps(dict(Source=pack["Source"], Cards=cards), ensure_ascii=True,
                  separators=(",", ":")).encode("utf-8")
output.write_bytes(gzip.compress(data, mtime=0))
print(f"Wrote {len(cards)} enhancement eligibility records ({output.stat().st_size} bytes).")
