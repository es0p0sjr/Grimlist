"""Extract operative core stratagem cards from a locally saved plain-text reference.

Usage: python scripts/build_core_stratagems.py <10|11> <reference.txt> <output.json>
The input must contain the complete core stratagem section, including lists.
Excludes introductory flavour text and unrelated chapters. Review source changes
before rebuilding; this is a development utility, not an automatic rules updater.
"""
import argparse
import json
import re
from datetime import date
from pathlib import Path


def build(edition, text):
    start = re.search(r"(?m)^COMMAND RE-ROLL(?: 15\.02)?\s*$", text)
    end = re.search(r"(?m)^#{2,3} (?:Actions|Strategic Reserves)\s*$", text[start.end():]) if start else None
    if not start or not end:
        raise ValueError("Missing complete core stratagem section")
    text = text[start.start():start.end() + end.start()]
    headings = list(re.finditer(r"(?m)^([A-Z][A-Z -]+?)(?: 15\.\d+)?\s*\n\s*(\d+)CP\s*\n\s*Core[^\n]*Stratagem\s*\n", text))
    if len(headings) != (10 if edition == "11" else 11):
        raise ValueError("Unexpected core stratagem count")
    source = {
        "Name": "Core stratagem rules (Wahapedia reference)",
        "Version": "Checked " + date.today().isoformat(), "Date": "",
        "Url": f"https://wahapedia.ru/wh40k{edition}ed/the-rules/core-rules/" + ("#Core-Stratagems" if edition == "11" else "#Stratagems"),
        "Edition": edition,
    }
    cards = []
    for i, heading in enumerate(headings):
        end = headings[i + 1].start() if i + 1 < len(headings) else len(text)
        body = text[heading.end():end]
        when = body.find("WHEN:")
        if when < 0 or "TARGET:" not in body or "EFFECT:" not in body:
            raise ValueError("Incomplete stratagem: " + heading[1])
        body = body[when:].split("### Errata")[0].strip()
        body = re.sub(r"[ \t]+([.,;:])", r"\1", body)
        body = re.sub(r"\n{3,}", "\n\n", body)
        body = re.sub(r"(?m)^\s*\* ", "\u2022 ", body)
        if re.search(r"\bL\d+:", body) or any(marker in body for marker in ["\ue200", "Core –", "Core Stratagem"]):
            raise ValueError("Unprocessed source markup")
        name = heading[1].title().replace("Re-Roll", "Re-roll").replace("Counter-Offensive", "Counter-offensive").replace("Go To Ground", "Go to Ground")
        cards.append({"Faction": "Core", "Section": "Stratagems", "Name": name,
                      "Text": body, "Detachment": "", "Cost": heading[2] + " CP", "Source": source})
    return {"Source": source, "Units": [], "ReferenceCards": cards, "ReferenceOnly": True}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("edition", choices=["10", "11"])
    parser.add_argument("reference", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = build(args.edition, args.reference.read_text(encoding="utf-8-sig"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(result['ReferenceCards'])} core stratagems for edition {args.edition}")
