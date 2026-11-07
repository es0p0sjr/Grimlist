"""Read-only source inventory. Reimport/runtime validation is done by CatalogueAttachmentChecks.cs."""
import argparse
import hashlib
import json
import re
import sqlite3
import zipfile
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('--data-dir', required=True)
p.add_argument('--catalogue', required=True)
p.add_argument('--faction', required=True)
p.add_argument('--unit', required=True)
p.add_argument('--bodyguard', help='Also trace the target bodyguard and its structural association rules')
p.add_argument('--out', required=True)
a = p.parse_args()
data_dir, out = Path(a.data_dir).resolve(), Path(a.out).resolve()
out.mkdir(parents=True, exist_ok=True)
db_path = data_dir / 'grimlist.db'
with sqlite3.connect(db_path.as_uri() + '?mode=ro', uri=True) as db:
    active = db.execute("select value from settings where key='active_source'").fetchone()[0]
    dataset = json.loads(db.execute('select content from dataset_snapshots where source_id=?', (active,)).fetchone()[0])
unit = next(u for u in dataset['Units'] if u['Faction'] == a.faction and u['Name'] == a.unit)
source = unit.get('ProfileSource') or dataset['Source']
cache = data_dir / 'sources' / f"bsdata-{source['Edition']}-{source['Version']}"
archive = cache / 'repository.zip'
archive_bytes = archive.read_bytes()
archive_hash = hashlib.sha256(archive_bytes).hexdigest()
assert archive_hash.lower() == source['ContentHash'].lower(), 'Archive does not match runtime profile source'
documents, raw_files = {}, {}
with zipfile.ZipFile(archive) as z:
    for name in z.namelist():
        if not name.endswith('.json'):
            continue
        raw = z.read(name)
        try:
            parsed = json.loads(raw)
        except (ValueError, UnicodeDecodeError):
            continue
        root = parsed.get('catalogue') or parsed.get('gameSystem')
        if root and root.get('id'):
            documents[root['id']] = (name, root)
            raw_files[name] = raw
catalogue_file, catalogue = next(v for v in documents.values() if v[1].get('name') == a.catalogue)
# Match registry precedence used by ImportCatalogue.Include and Walk.
groups = ('selectionEntries', 'sharedSelectionEntries', 'selectionEntryGroups', 'sharedSelectionEntryGroups',
          'profiles', 'sharedProfiles', 'rules', 'sharedRules', 'categoryEntries', 'infoGroups', 'sharedInfoGroups', 'forceEntries')
registry, included, duplicates = {}, set(), []

def register(node, filename, path):
    if node.get('id'):
        if node['id'] in registry:
            duplicates.append({'id': node['id'], 'ignored': filename + path})
        else:
            registry[node['id']] = (filename, path, node)
    for group in groups:
        for i, child in enumerate(node.get(group, [])):
            register(child, filename, f'{path}/{group}/{i}')

def include(doc_id):
    if doc_id in included or doc_id not in documents:
        return
    included.add(doc_id)
    filename, node = documents[doc_id]
    register(node, filename, '')
    for link in node.get('catalogueLinks', []):
        include(link['targetId'])

include(catalogue['id'])
include(catalogue['gameSystemId'])
# Audit even reference kinds not interpreted by GrimList (e.g. 11e associationLinks).
# Keep their provenance separate so a complete source inventory is not confused with importer support.
extra_registry = {}
def index_all(value, filename, path=''):
    if isinstance(value, dict):
        if value.get('id'):
            extra_registry.setdefault(value['id'], (filename, path, value))
        for key, child in value.items():
            index_all(child, filename, path + '/' + key)
    elif isinstance(value, list):
        for i, child in enumerate(value):
            index_all(child, filename, path + '/' + str(i))
for doc_id in included:
    index_all(documents[doc_id][1], documents[doc_id][0])
filename, entry_path, entry = next(v for v in registry.values()
    if v[0] == catalogue_file and v[2].get('name') == a.unit and v[2].get('type') in ('model', 'unit'))
root_links = [n for n in catalogue.get('entryLinks', []) if n.get('targetId') == entry['id']]
phrases = ['one other Leader', 'another Leader', 'Leader unit', 'attached', 'Plague Marines']
nodes, edges, matches, seen = [], [], [], set()

def fields(value, path):
    if isinstance(value, dict):
        for key, child in value.items():
            yield from fields(child, path + '/' + key)
    elif isinstance(value, list):
        for i, child in enumerate(value):
            yield from fields(child, path + '/' + str(i))
    elif isinstance(value, str):
        normalized = re.sub(r'\s+', ' ', value.replace('*', '').replace('^^', '').replace('__', ''))
        hits = [phrase for phrase in phrases if phrase.casefold() in normalized.casefold()]
        if hits:
            yield {'path': path, 'phrases': hits, 'text': value}

def links(value, path):
    if isinstance(value, dict):
        if value.get('targetId'):
            yield path, value
        for key, child in value.items():
            yield from links(child, path + '/' + key)
    elif isinstance(value, list):
        for i, child in enumerate(value):
            yield from links(child, path + '/' + str(i))

def visit(filename, path, node):
    key = (filename, path)
    if key in seen:
        return
    seen.add(key)
    nodes.append({'file': filename, 'path': path, 'node': node})
    matches.extend(dict(file=filename, **m) for m in fields(node, path))
    for link_path, link in links(node, path):
        target = registry.get(link['targetId']) or extra_registry.get(link['targetId'])
        edges.append({'fromFile': filename, 'fromPath': link_path, 'link': link,
                      'targetFile': target[0] if target else None, 'targetPath': target[1] if target else None,
                      'indexedByImporter': link['targetId'] in registry})
        if target:
            visit(*target)

visit(filename, entry_path, entry)
for i, link in enumerate(root_links):
    visit(catalogue_file, f'/root-entry-link/{i}', link)
bodyguard = None
if a.bodyguard:
    body_file, body_path, bodyguard = next(v for v in registry.values()
        if v[0] == catalogue_file and v[2].get('name') == a.bodyguard and v[2].get('type') in ('model', 'unit'))
    visit(body_file, body_path, bodyguard)
    # Association conditions refer to categories with childId, not targetId.
    def association_references(value):
        if isinstance(value, dict):
            if value.get('field') == 'associations' and value.get('childId'):
                target = registry.get(value['childId']) or extra_registry.get(value['childId'])
                if target:
                    visit(*target)
            for child in value.values():
                association_references(child)
        elif isinstance(value, list):
            for child in value:
                association_references(child)
    association_references(bodyguard)
raw_matches = list(fields(entry, entry_path))
catalogue_matches = list(fields(catalogue, ''))
flag = lambda items: any(any(t in m['phrases'] for t in ('one other Leader', 'another Leader')) for m in items)
metadata = {
    'Database': str(db_path), 'ActiveSourceId': active, 'RuntimeSource': dataset['Source'], 'ProfileSource': source,
    'Archive': str(archive), 'ArchiveSha256': archive_hash, 'SourceMetadata': str(cache / 'source.json'),
    'CachedDataset': str(cache / 'dataset.json'), 'CatalogueFile': catalogue_file,
    'CatalogueId': catalogue['id'], 'CatalogueRevision': catalogue.get('revision'),
    'GameSystemRevision': catalogue.get('gameSystemRevision'), 'EntryId': entry['id'], 'EntryPath': entry_path,
    'RuntimeUnitId': unit['Id'], 'RootEntryLinks': root_links,
    'RawHasLeaderReference': any(x.get('name') == 'Leader' for x in entry.get('infoLinks', [])),
    'RawHasExtraLeaderPhrase': flag(raw_matches), 'ResolvedGraphHasExtraLeaderPhrase': flag(matches),
    'EntireCatalogueHasExtraLeaderPhrase': flag(catalogue_matches),
    'ReachableDefinitions': len(nodes), 'ReferenceEdges': len(edges),
    'UnresolvedReferences': [e for e in edges if e['targetFile'] is None],
    'DuplicateRegistryIds': duplicates,
    'IncludedFiles': [documents[k][0] for k in sorted(included)],
    'CataloguePhraseMatches': {s: sum(s in m['phrases'] for m in catalogue_matches) for s in phrases},
    'BodyguardEntryId': bodyguard['id'] if bodyguard else None,
    'RawBodyguardAssociationConstraints': [c for c in bodyguard.get('constraints', []) if c.get('field') == 'associations'] if bodyguard else [],
    'TextScanLimitation': 'Absence of an extra-Leader phrase does not imply absence of permission. Inspect bodyguard association constraints and their modifiers.',
}
def write(name, value):
    (out / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
write('source-trace.json', metadata)
write('raw-entry.json', entry)
write('reachable-raw-definitions.json', nodes)
write('reference-edges.json', edges)
write('reachable-text-matches.json', matches)
write('catalogue-text-matches.json', catalogue_matches)
write('runtime-dataset.json', dataset)
write('runtime-biologus.json', unit)
if bodyguard:
    write('raw-bodyguard.json', bodyguard)
    write('runtime-bodyguard.json', next(u for u in dataset['Units'] if u['Faction'] == a.faction and u['Name'] == a.bodyguard))
(out / 'raw-catalogue.json').write_bytes(raw_files[catalogue_file])
print(json.dumps({k: metadata[k] for k in ('Archive', 'CatalogueFile', 'CatalogueRevision', 'EntryId', 'RawHasExtraLeaderPhrase',
    'ResolvedGraphHasExtraLeaderPhrase', 'EntireCatalogueHasExtraLeaderPhrase', 'ReachableDefinitions', 'ReferenceEdges')}, indent=2))
