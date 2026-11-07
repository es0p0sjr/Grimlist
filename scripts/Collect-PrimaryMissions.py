"""Collect primary cards for the offline battle UI from a saved Event Companion page.
Usage: python scripts/Collect-PrimaryMissions.py source.html output.json
Reuses the secondary collector's strict scoring-row reader; preserves each timing.
"""
import hashlib
import importlib.util
import json
import re
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("secondary", Path(__file__).with_name("Collect-SecondaryMissions.py"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
raw = Path(sys.argv[1]).read_bytes()
html = raw.decode("utf-8-sig")
tree = m.Tree()
tree.feed(html[html.index('<a name="Primary-Mission-deck"'):html.index('<a name="Deployment-deck"')])
cards = []
for card in tree.root.nodes("cgCardCA7"):
    name = card.one("ca7Name").text()
    card_id = "ca2026-primary-" + re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    sections = []
    group = 0
    for block in card.one("ca7Body").children:
        if isinstance(block, str):
            if block.strip():
                raise ValueError("Unclassified primary text")
            continue
        if block.has("caPmBlock"):
            # Several primary blocks contain two WHEN headings. Split at each
            # heading so command-phase and end-of-turn controls cannot collide.
            header = block.one("caPmHdr")
            when = None
            rows = []
            def flush():
                global group
                if not rows:
                    return
                group += 1
                synthetic = m.Node("div")
                timing = when or m.Node("div", [("class", "caPmWhen")])
                if not when:
                    timing.children = ["End of the battle."]
                synthetic.children = [header, timing, *rows]
                sections.append(m.scoring(synthetic, ["Primary"], card_id, group))
            for child in block.children:
                if not isinstance(child, m.Node):
                    if child.strip():
                        raise ValueError("Unclassified scoring text")
                    continue
                if child.has("caPmHdr"):
                    continue
                if child.has("caPmWhen"):
                    flush()
                    rows = []
                    when = child
                elif child.has("caPmScore"):
                    rows.append(child)
                else:
                    raise ValueError(f"Unknown scoring block: {child.attrs}")
            flush()
        elif block.has("caAct"):
            fields = []
            for row in block.nodes("caActRow"):
                label = row.one("caActLbl").text()
                text = m.requirement(row)["text"].removeprefix(label).strip()
                fields.append({"label": label.rstrip(":"), "text": text})
            sections.append({"kind": "action", "name": block.one("caActHdr").text(),
                             "actionType": block.one("caActType").text(), "fields": fields})
        elif block.has("caPmIntro") or block.has("caPmNote"):
            sections.append({"kind": "instruction", **m.requirement(block)})
        else:
            raise ValueError(f"Unknown primary section: {block.attrs}")
    cards.append({"id": card_id, "name": name, "modes": ["Primary"], "sections": sections})
if len(cards) != 25 or len({c["name"] for c in cards}) != 25:
    raise ValueError("Expected 25 unique primaries")
result = {"schemaVersion": 1, "edition": "11", "season": "2026-27",
          "source": {"url": m.SOURCE.replace("Secondary-Mission", "Primary-Mission"),
                     "sha256": hashlib.sha256(raw).hexdigest()}, "cards": cards}
Path(sys.argv[2]).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Collected {len(cards)} primaries, {sum(len(s.get('conditions', [])) for c in cards for s in c['sections'])} conditions")
