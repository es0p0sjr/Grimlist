"""Build the reviewed 2026-10-14 Space Marine snapshot from captured page lines.

Input is a JSON object mapping page line numbers to extracted text. This script
does not download or auto-publish later rules: each new snapshot needs review.
"""
import argparse
import collections
import gzip
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('capture', type=Path)
args = parser.parse_args()
lines = {int(n): text for n, text in json.loads(args.capture.read_text(encoding='utf-8')).items()}
assert all(n in lines for n in range(738, 3342)), 'Incomplete captured source'

def clean(text):
    text = re.sub(r'\ue200cite\ue202[^\u2020]+\u2020([^\ue201]+)\ue201', r'\1', text)
    return re.sub(r' +([,.;:])', r'\1', text).strip()

owners = [n for n in range(738, 3342) if lines[n].endswith(' Stratagem')]
cards = []
starts = []
for n in owners:
    before = [p for p in range(n - 8, n) if lines.get(p, '').strip()]
    title, cp = before[-2:]
    assert re.fullmatch(r'\d+CP', lines[cp]), (n, 'Invalid CP')
    assert lines[title] == lines[title].upper(), (n, 'Invalid title')
    starts.append(title)
for i, n in enumerate(owners):
    end = starts[i + 1] if i + 1 < len(starts) else 3342
    body = []
    for p in range(n + 1, end):
        if lines[p].startswith(('##', 'UNIQUE:')):
            break  # Includes an unrelated, nested legacy Errata tooltip at line 784.
        body.append((p, clean(lines[p])))
    fields = {}
    active = None
    for p, text in body:
        match = re.match(r'^(WHEN|TARGET|EFFECT|RESTRICTIONS):\s*(.*)', text)
        if match:
            active = match[1]
            assert active not in fields, (n, 'Duplicate field', active)
            fields[active] = [match[2]]
        elif active and text:
            fields[active].append(text)
    assert all(fields.get(k) for k in ('WHEN', 'TARGET', 'EFFECT')), n
    card = dict(Name=clean(lines[starts[i]]), Detachment=lines[n][:-10],
                Cost=next(clean(lines[p]).replace('CP', ' CP') for p in range(starts[i]+1, n) if 'CP' in lines[p]),
                Timing='\n'.join(fields['WHEN']), Target='\n'.join(fields['TARGET']),
                Effect='\n'.join(fields['EFFECT']), Restrictions='\n'.join(fields.get('RESTRICTIONS', [])),
                SourceLines=[starts[i], body[-1][0]])
    assert not any(s in json.dumps(card) for s in ('Errata', 'Show', 'UNIQUE:', '\\ue200', '##'))
    cards.append(card)
counts = collections.Counter(c['Detachment'] for c in cards)
assert len(cards) == 142 and len(counts) == 35
assert len({(c['Detachment'], c['Name']) for c in cards}) == 142
assert counts['Gladius Task Force'] == 6 and counts['Ironstorm Spearhead'] == 3
assert counts['Wrath of the Rock'] == 6 and counts['Deathwatch Support'] == 4
army_rule = json.loads((ROOT / 'src/GrimList.Core/Resources/space-marine-army-rule-11.json').read_text(encoding='utf-8'))
legacy = json.load(gzip.open(ROOT / 'src/GrimList.Application/Resources/references-11.json.gz'))
old_hashes = sorted({c['Source']['ContentHash'] for c in legacy['ReferenceCards'] if c['Faction'] in army_rule['Factions']})
payload_hash = hashlib.sha256(json.dumps(cards, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
pack = dict(Source=dict(Name='Wahapedia - Space Marines', Version='2026-10-14.1', Date='2026-10-14',
                        Url='https://wahapedia.ru/wh40k11ed/factions/space-marines/', Edition='11', ContentHash=payload_hash),
            PageRevision='App 11th edition 2.7.1, October 2026',
            Notes='Dated reference snapshot. Complete for the listed standard detachments on this page; not an official rules certification. Boarding Actions excluded. Deathwatch Support references do not implement its army-building rules.',
            Contexts=army_rule['Contexts'], LegacySourceHashes=old_hashes, Counts=dict(counts), Cards=cards)
output = ROOT / 'src/GrimList.Core/Resources/space-marine-stratagems-11.json'
output.write_text(json.dumps(pack, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(dict(Cards=len(cards), Detachments=len(counts), ContentHash=payload_hash)))
