"""Inventory explicitly supplied local structured files; never fetch content."""
import argparse
import hashlib
import json
import pathlib

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--output', required=True)
p.add_argument('files', nargs='+')
args = p.parse_args()
rows = []
for value in args.files:
    path = pathlib.Path(value)
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    if not isinstance(data, dict):
        continue
    cards = data.get('ReferenceCards', [])
    rows.append(dict(Path=path.as_posix(), SHA256=hashlib.sha256(path.read_bytes()).hexdigest(),
        Source=data.get('Source'), ReferenceCards=len(cards),
        StratagemCards=sum(c.get('Section')=='Stratagems' for c in cards),
        StratagemFactions=sorted({c['Faction'] for c in cards if c.get('Section')=='Stratagems'})))
pathlib.Path(args.output).write_text(json.dumps(dict(
    Scope='Explicit local source files and user exports only; no discovery claim about external sites; no cross-edition content ingestion.',
    Sources=rows),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
