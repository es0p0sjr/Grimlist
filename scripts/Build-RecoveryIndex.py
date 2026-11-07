"""Build source/record-indexed recovery resources from canonical importer metadata.

Build-time only: the app never reads the historical aggregate. Deterministic ZIP
entries keep full rule semantics and source hashes; identical semantics share one
payload even when historical profile fingerprints differ.
"""
import gzip
import hashlib
import json
import pathlib
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
resources = root / 'src/GrimList.Application/Resources'
target = resources / 'effective-recovery'
target.mkdir(exist_ok=True)
packs = json.loads(gzip.decompress((resources / 'effective-datasheets.json.gz').read_bytes()))
manifest = {}

def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode('utf-8')

for pack in packs:
    source = pack['SourceHash'].upper()
    path = target / (source + '.zip')
    entries = {}
    records = {}
    for kind, items in [('unit', pack['Units']), ('card', pack['Cards'])]:
        for item in items:
            key = (item['Alias'] + '/' + item['ProfileHash']) if kind == 'unit' else (item['Faction'] + '/' + item['Key'])
            payload = encode(item['Semantics'] if kind == 'unit' else item)
            digest = hashlib.sha256(payload).hexdigest().upper()
            entries[digest] = payload
            records[kind + '/' + key.upper()] = {'Payload': digest, 'AbilitiesHash': item.get('AbilitiesHash'),
                'Version': item['Semantics']['Version'] if kind == 'unit' else 0}
    entries['index.json'] = encode(records)
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, payload in sorted(entries.items()):
            entry = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, payload)
    manifest[source] = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    print(source, len(records), 'records;', len(entries) - 1, 'unique payloads;', path.stat().st_size, 'bytes')
(target / 'index.json').write_bytes(encode(manifest))
