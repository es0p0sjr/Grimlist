"""Rebuild the reviewed Combat Doctrines fallback from the pinned BSData dataset."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('dataset', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
target = root / 'src/GrimList.Core/Resources/space-marine-army-rule-11.json'
pack = json.loads(target.read_text(encoding='utf-8'))
data = json.loads(args.dataset.read_text(encoding='utf-8-sig'))
if data['Source'] != pack['Source']:
    raise SystemExit('Source differs from the reviewed pinned catalogue.')
rules = {a['Text'] for u in data['Units'] if u['Faction'] == 'Space Marines'
         for a in (u.get('Profile') or {}).get('Abilities') or [] if a['Name'] == pack['Name']}
if len(rules) != 1:
    raise SystemExit('Expected exactly one consistent Combat Doctrines rule in the source units.')
pack['Text'] = rules.pop()
pack['Contexts'] = [c for c in data['FactionContexts'] if c['Faction'] in pack['Factions']]
target.write_text(json.dumps(pack, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print('Rebuilt pinned army rule for', len(pack['Contexts']), 'catalogues.')
