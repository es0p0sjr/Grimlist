"""Collect the Chapter Approved 2026-27 secondary deck from a saved source page.

Usage: python scripts/Collect-SecondaryMissions.py --source-html snapshot.html
       --output data/missions/11e/secondary-missions-2026-27.json
Only collects reference data. Does not infer executable scoring predicates.
"""
import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path

SOURCE = "https://wahapedia.ru/wh40k11ed/the-rules/warhammer-event-companion/#Secondary-Mission-deck"
OFFICIAL = "https://assets.warhammer-community.com/eng_wh40k_event_companion-pl87i44rzn-a7ieny8i9x.pdf"
INVENTORY = "https://game-datamissions.com/11th/secondary-missions"
EXPECTED = {
    "A GRIEVOUS BLOW", "A TEMPTING TARGET", "ASSASSINATION", "BEACON",
    "BEHIND ENEMY LINES", "BRING IT DOWN", "BURDEN OF TRUST", "CENTRE GROUND",
    "CLEANSE", "DEFEND STRONGHOLD", "DISPLAY OF MIGHT", "ENGAGE ON ALL FRONTS",
    "FORWARD POSITION", "NO PRISONERS", "OUTFLANK", "OVERWHELMING FORCE",
    "PLUNDER", "SECURE NO MAN'S LAND",
}
FIXED = {"A GRIEVOUS BLOW", "ASSASSINATION", "BRING IT DOWN", "ENGAGE ON ALL FRONTS"}


class Node:
    def __init__(self, tag="root", attrs=()):
        self.tag, self.attrs, self.children = tag, dict(attrs), []

    def has(self, cls):
        return cls in self.attrs.get("class", "").split()

    def nodes(self, cls):
        result = [self] if self.has(cls) else []
        for child in self.children:
            if isinstance(child, Node):
                result.extend(child.nodes(cls))
        return result

    def text(self):
        return re.sub(r"\s+", " ", "".join(c.text() if isinstance(c, Node) else c
                                           for c in self.children)).strip()

    def one(self, cls):
        found = self.nodes(cls)
        if len(found) != 1:
            raise ValueError(f"Expected one {cls}, got {len(found)}")
        return found[0]


class Tree(HTMLParser):
    VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.root = Node()
        self.stack = [self.root]

    def handle_starttag(self, tag, attrs):
        n = Node(tag, attrs)
        self.stack[-1].children.append(n)
        if tag not in self.VOID:
            self.stack.append(n)

    def handle_endtag(self, tag):
        for i in range(len(self.stack) - 1, 0, -1):
            if self.stack[i].tag == tag:
                del self.stack[i:]
                return

    def handle_data(self, data):
        self.stack[-1].children.append(data)


def requirement(node):
    """Preserve emphasis separately; only underlined 'one' has exact-count meaning."""
    underlined = []

    def visit(n):
        if isinstance(n, str):
            return n
        value = "".join(visit(c) for c in n.children)
        if n.tag == "u":
            underlined.append(n.text())
            if value.strip().lower() == "one":
                return "exactly one"
        return value

    return {"text": re.sub(r"\s+", " ", visit(node)).strip(), "underlined": underlined}


def points(node, modes):
    vp = node.nodes("caPmVP")
    if len(vp) != 1:
        raise ValueError("Scoring value is ambiguous")
    match = re.fullmatch(r"(\+?)(\d+)VP", vp[0].text())
    if not match:
        raise ValueError("Unknown VP format")
    labels = [n.text() for n in node.nodes("caPmCumul")]
    selected = [m for m in modes if m.upper() in labels] or modes
    cap = [re.fullmatch(r"\(UP TO (\d+)VP\)", s) for s in labels]
    cap = [int(m[1]) for m in cap if m]
    unexpected = [s for s in labels if s not in {"FIXED", "TACTICAL", "CUMULATIVE"}
                  and not re.fullmatch(r"\(UP TO \d+VP\)", s)]
    if unexpected:
        raise ValueError(f"Unknown scoring labels: {unexpected}")
    return {"modes": selected, "vp": int(match[2]), "bonus": bool(match[1]),
            "cumulative": "CUMULATIVE" in labels, "capVp": cap[0] if cap else None}


def scoring(block, modes, card_id, group):
    rows = []
    for index, row in enumerate(block.nodes("caPmScore"), 1):
        children = [n for n in row.children if isinstance(n, Node)]
        condition = [n for n in children if not any(n.has(c) for c in
                     ("caPmOr", "caPmPlus", "caPmVP", "caPmVPCol", "caPmVPPair"))]
        if len(condition) != 1:
            raise ValueError("Scoring condition is missing or ambiguous")
        pair = row.nodes("caPmVPPair")
        containers = pair[0].nodes("caPmVPCol") if pair else row.nodes("caPmVPCol")
        if not containers:
            containers = row.nodes("caPmVP")
        values = [points(n, modes) for n in containers]
        if len(values) not in (1, 2):
            raise ValueError("Unexpected number of scoring variants")
        relation = "alternative" if row.nodes("caPmOr") else "cumulative" if row.nodes("caPmPlus") else "independent"
        rows.append({"id": f"{card_id}-g{group}-s{index}", "relationToPrevious": relation,
                     "condition": requirement(condition[0]), "points": values})
    return {"kind": "scoring", "roundsText": block.one("caPmHdr").text(),
            "timingText": block.one("caPmWhen").text(), "conditions": rows}


def parse(html):
    begin = html.index('<a name="Secondary-Mission-deck"')
    end = html.index('<div class="tooltip_templates"', begin)
    tree = Tree()
    tree.feed(html[begin:end])
    cards = []
    for node in tree.root.nodes("cgCardCA7"):
        name = node.one("ca7Name").text()
        card_id = "ca2026-secondary-" + re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
        fixed = bool(node.nodes("ca7Fixed"))
        modes = ["Fixed", "Tactical"] if fixed else ["Tactical"]
        roles = [role for role, cls in (("Attacker", "ca7TypeAtk"), ("Defender", "ca7TypeDef")) if node.nodes(cls)]
        body = node.one("ca7Body")
        sections = []
        group = 0
        for child in body.children:
            if isinstance(child, str):
                if child.strip():
                    raise ValueError("Unclassified card text")
                continue
            if child.has("caPmIntro"):
                sections.append({"kind": "instruction", **requirement(child)})
            elif child.has("caAct"):
                fields = []
                for row in child.nodes("caActRow"):
                    label = row.one("caActLbl").text()
                    rule = requirement(row)
                    rule["text"] = rule["text"].removeprefix(label).strip()
                    fields.append({"label": label.rstrip(":"), **rule})
                sections.append({"kind": "action", "name": child.one("caActHdr").text(),
                                 "actionType": child.one("caActType").text(), "fields": fields})
            elif child.has("caPmBlock"):
                group += 1
                sections.append(scoring(child, modes, card_id, group))
            elif child.has("caPmNote"):
                sections.append({"kind": "note", **requirement(child)})
            else:
                raise ValueError(f"Unclassified card section in {name}: {child.attrs}")
        cards.append({"id": card_id, "name": name, "modes": modes, "deckRoles": roles,
                      "sections": sections, "sourceUrl": SOURCE,
                      "automationStatus": "reference-collected; scoring-predicates-not-implemented"})
    if len(cards) != 18 or {c["name"] for c in cards} != EXPECTED:
        raise ValueError("Secondary card inventory differs from the verified 18-card deck")
    if {c["name"] for c in cards if "Fixed" in c["modes"]} != FIXED:
        raise ValueError("Fixed eligibility differs from the verified four-card set")
    for card in cards:
        if card["deckRoles"] != ["Attacker", "Defender"]:
            raise ValueError("Deck roles changed; review attacker/defender variants")
        if not any(s["kind"] == "scoring" and s["conditions"] for s in card["sections"]):
            raise ValueError("Card has no scoring conditions")
    return sorted(cards, key=lambda c: c["name"])


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--source-html", type=Path, required=True)
    cli.add_argument("--output", type=Path, required=True)
    args = cli.parse_args()
    raw = args.source_html.read_bytes()
    cards = parse(raw.decode("utf-8-sig"))
    dataset = {
        "schemaVersion": 1, "game": "Warhammer 40,000", "edition": "11", "season": "2026-27",
        "missionPack": "Chapter Approved 2026-27", "uniqueCardCount": len(cards),
        "decks": {"Attacker": [c["id"] for c in cards], "Defender": [c["id"] for c in cards]},
        "source": {"name": "Wahapedia community transcription", "url": SOURCE,
                   "retrievedAt": datetime.fromtimestamp(args.source_html.stat().st_mtime, timezone.utc).isoformat(),
                   "sha256": hashlib.sha256(raw).hexdigest()},
        "verification": {"inventoryAndFixedEligibility": INVENTORY,
                         "officialAuthority": OFFICIAL, "officialVersion": "1.2",
                         "scope": "Inventory and eligibility cross-checked; official scoring limits and FAQ checked. Full card wording is a community transcription, not an independent audit of the physical deck."},
        "scoringLimits": {"secondaryVpPerPlayerPerBattleRound": 15, "secondaryVpPerPlayerPerBattle": 45,
                          "vpPerFixedCardPerPlayerPerBattle": 20, "sourceUrl": OFFICIAL, "sourcePage": 2},
        "clarifications": [
            {"cardId": "ca2026-secondary-plunder", "summary": "The territory restriction applies to the terrain area, not the acting unit.", "sourceUrl": OFFICIAL, "sourcePage": 4},
            {"cardId": "ca2026-secondary-beacon", "summary": "A destroyed Beacon unit cannot be replaced by selecting another unit.", "sourceUrl": OFFICIAL, "sourcePage": 4},
        ],
        "cards": cards,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Validate before replacing a previously collected deck.
    temporary = args.output.with_suffix(".tmp")
    temporary.write_text(json.dumps(dataset, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(args.output)
    rows = sum(len(s["conditions"]) for c in cards for s in c["sections"] if s["kind"] == "scoring")
    print(f"Collected {len(cards)} unique cards, {len(FIXED)} Fixed options, {rows} scoring conditions. Both deck roles verified.")
    print(args.output)


if __name__ == "__main__":
    main()
