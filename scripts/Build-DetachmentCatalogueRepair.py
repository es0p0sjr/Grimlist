"""Rebuild the bounded catalogue supplement from the pinned normalized BSData input."""
import argparse
import gzip
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('dataset', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
pack = json.loads((root / 'data/catalogue-corrections/space-marines-11.json').read_text())
data = json.loads(args.dataset.read_text(encoding='utf-8-sig'))
if data['Source'] != pack['Source']:
    raise SystemExit('Source differs from the reviewed pinned catalogue.')
factions = set(pack['Factions'])
names = set(pack['CurrentNames'])
pack['Detachments'] = [d for d in data['Detachments'] if d['Faction'] in factions and d['Name'] in names]
for faction in factions:
    if {d['Name'] for d in pack['Detachments'] if d['Faction'] == faction} != names:
        raise SystemExit('Incomplete current definitions for ' + faction)
pack['Cards'] = [c for c in data['ReferenceCards'] if c['Faction'] in factions and c['Detachment'] in names]
pack['Contexts'] = [c for c in data['FactionContexts'] if c['Faction'] in factions]
output = root / 'src/GrimList.Core/Resources/detachment-catalogue-11.json.gz'
output.write_bytes(gzip.compress(json.dumps(pack, separators=(',', ':')).encode(), mtime=0))
print(f'Wrote {len(pack["Detachments"])} definitions and {len(pack["Cards"])} source cards to {output}')
