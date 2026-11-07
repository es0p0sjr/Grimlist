"""Offline source evidence, not a roster rule engine or runtime dependency.

Run against a pinned archive, a fresh shared-importer export and the phone export.
No downloads, database writes, or inference of rules text/completeness.
"""
import argparse
import collections
import hashlib
import json
import pathlib
import re
import zipfile


def read(path):
    return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))


def write(path, value):
    pathlib.Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def key(value):
    return ''.join(c for c in value.upper() if c.isalnum())


def walk(node, path='', ancestors=()):
    if isinstance(node, dict):
        yield node, path, ancestors
        for k, v in node.items():
            yield from walk(v, path + '/' + k, ancestors + (node,))
    elif isinstance(node, list):
        for i, v in enumerate(node):
            yield from walk(v, path + '/' + str(i), ancestors)


def truth_group(node, primary, force):
    values = [condition(c, primary, force) for c in node.get('conditions', [])]
    values += [truth_group(c, primary, force) for c in node.get('conditionGroups', [])]
    if node.get('type') == 'or':
        return True if True in values else None if None in values else False
    return False if False in values else None if None in values else True


def condition(c, primary, force):
    scope = c.get('scope')
    if scope not in ('primary-catalogue', 'force') or c.get('type') not in ('instanceOf', 'notInstanceOf'):
        return None
    value = c.get('childId') == (primary if scope == 'primary-catalogue' else force)
    return not value if c['type'] == 'notInstanceOf' else value


def hidden_state(node, primary, force):
    hidden = str(node.get('hidden', False)).lower() == 'true'
    notes = []
    def modifiers(group, inherited=True):
        for m in group.get('modifiers', []):
            truth = truth_group(m, primary, force)
            yield m, False if inherited is False or truth is False else None if inherited is None or truth is None else True
        for nested in group.get('modifierGroups', []):
            truth = truth_group(nested, primary, force)
            state = False if inherited is False or truth is False else None if inherited is None or truth is None else True
            yield from modifiers(nested, state)
    for m, truth in modifiers(node):
        if m.get('field') != 'hidden':
            continue
        if truth is False:
            continue
        if truth is None or m.get('type') != 'set' or str(m.get('value')).lower() not in ('true', 'false'):
            hidden = None
            notes.append('Unresolved visibility: ' + json.dumps(m, sort_keys=True))
        else:
            hidden = str(m['value']).lower() == 'true'
    return hidden, notes


class Graph:
    def __init__(self, documents, names, force):
        self.docs = {d['id']: d for d in documents}
        self.names = names
        self.force = force
        self.nodes = collections.defaultdict(list)
        self.rows = []
        self.issues = []
        for d in documents:
            for n, p, _ in walk(d):
                if n.get('id'):
                    self.nodes[n['id']].append((d['id'], p, n))

    def reachable(self, primary):
        ids = set()
        def visit(ident):
            if ident in ids or ident not in self.docs:
                return
            ids.add(ident)
            for link in self.docs[ident].get('catalogueLinks', []):
                visit(link.get('targetId'))
        visit(primary)
        visit(self.docs[primary].get('gameSystemId'))
        return ids

    def resolve(self, node, document, reachable, path):
        chain = []
        while node.get('targetId') and node.get('type') in ('selectionEntry', 'selectionEntryGroup'):
            target = node['targetId']
            if target in chain:
                return None, document, path, chain, 'Cyclic source link'
            chain.append(target)
            candidates = [v for v in self.nodes.get(target, []) if v[0] in reachable]
            # Prefer the definition in the referring catalogue, as the actual importer does.
            local = [v for v in candidates if v[0] == document]
            candidates = local or candidates
            if len(candidates) != 1:
                return None, document, path, chain, 'Missing/ambiguous target ' + target
            document, path, target_node = candidates[0]
            merged = dict(target_node)
            # A link's name/hidden and local modifiers override/add to its target.
            for field in ('name', 'hidden', 'defaultSelectionEntryId'):
                if field in node:
                    merged[field] = node[field]
            for field in ('constraints','modifiers','modifierGroups','selectionEntries','selectionEntryGroups','entryLinks','profiles','rules','infoLinks','infoGroups','categoryLinks'):
                merged[field] = target_node.get(field, []) + node.get(field, [])
            node = merged
        return node, document, path, chain, ''

    def audit_faction(self, primary):
        reachable = self.reachable(primary)
        roots = set()
        def root_catalogue(ident):
            if ident in roots or ident not in self.docs:
                return
            roots.add(ident)
            for link in self.docs[ident].get('catalogueLinks', []):
                if link.get('importRootEntries') is True:
                    root_catalogue(link['targetId'])
        root_catalogue(primary)
        native_roots = set(self.names[primary].get('NativeRootIds', [primary]))
        def visit(node, document, path, det_group=False, inherited=False, reasons=(), trail=(), root_id=''):
            original = node
            node, document, definition_path, chain, error = self.resolve(node, document, reachable, path)
            if error:
                self.issues.append(dict(FactionId=primary, Path=path, Error=error))
                return
            ident = node.get('id', definition_path)
            if (document, ident) in trail:
                return
            trail += ((document, ident),)
            hidden, notes = hidden_state(node, primary, self.force)
            effective = True if inherited is True or hidden is True else None if inherited is None or hidden is None else False
            reasons = reasons + tuple(notes)
            name = node.get('name', '')
            if det_group and node.get('type') == 'upgrade' and key(name) not in ('DETACHMENT', 'DETACHMENTS'):
                self.rows.append(dict(FactionId=primary, Faction=self.names[primary]['Faction'],
                    SourceId=node.get('id', ''), Name=name, CatalogueId=document,
                    Catalogue=self.docs[document]['name'], CatalogueRevision=str(self.docs[document].get('revision', '')),
                    DefinitionPath=definition_path, LinkId=original.get('id', ''), LinkTargets=chain,
                    Path=path, RootCatalogueId=root_id,
                    Status='Excluded' if effective is True or root_id not in native_roots else 'Unresolved' if effective is None else 'Selectable',
                    Reason='Allied imported root; not a native detachment route under existing shared importer semantics' if root_id not in native_roots else 'Hidden for selected catalogue/force context' if effective is True else '; '.join(reasons),
                    HistoricalNameSignal=bool(re.search(r'legends|obsolete|historical', name, re.I)),
                    HistoricalSourceSignal=bool(re.search(r'legends|obsolete|historical', self.docs[document]['name'], re.I))))
                return
            is_group = det_group or key(name) in ('DETACHMENT', 'DETACHMENTS')
            for field in ('selectionEntries', 'selectionEntryGroups', 'entryLinks'):
                for i, child in enumerate(node.get(field, [])):
                    visit(child, document, definition_path + '/' + field + '/' + str(i), is_group, effective, reasons, trail, root_id)
        for root in sorted(roots):
            for field in ('selectionEntries', 'entryLinks'):
                for i, node in enumerate(self.docs[root].get(field, [])):
                    visit(node, root, '/' + field + '/' + str(i), root_id=root)


def canonical_rows(rows, edition):
    groups = collections.defaultdict(list)
    for r in rows:
        # Source identity is authoritative. Display names never collapse distinct IDs.
        identity = (r['FactionId'], r['SourceId']) if r['SourceId'] else (r['FactionId'], r['CatalogueId'], r['DefinitionPath'])
        groups[identity].append(r)
    result = []
    for identity, entries in sorted(groups.items()):
        first = entries[0]
        status = 'Selectable' if any(e['Status'] == 'Selectable' for e in entries) else 'Unresolved' if any(e['Status'] == 'Unresolved' for e in entries) else 'Excluded'
        result.append(dict(Edition=edition, FactionId=identity[0], Faction=first['Faction'],
            DetachmentSourceId=first['SourceId'], Detachment=first['Name'], Status=status,
            IdentityResolved=bool(first['SourceId']), Catalogue=first['Catalogue'], CatalogueId=first['CatalogueId'],
            CatalogueRevision=first['CatalogueRevision'], Aliases=sorted(set(e['Name'] for e in entries)),
            Occurrences=entries, DuplicateAliasCount=len(entries)-1))
    for row in result:
        row['OtherFactionProjections'] = [dict(Faction=r['Faction'],FactionId=r['FactionId'],Status=r['Status'])
            for r in result if r['DetachmentSourceId']==row['DetachmentSourceId'] and r['FactionId']!=row['FactionId']]
        row['SameNameDifferentSourceIds'] = [r['DetachmentSourceId'] for r in result
            if r['FactionId']==row['FactionId'] and key(r['Detachment'])==key(row['Detachment']) and r['DetachmentSourceId']!=row['DetachmentSourceId']]
    return result


def direct_text(n):
    parts = [str(n[k]) for k in ('description', '$text', 'comment') if n.get(k)]
    parts += [str(c.get('$text', '')) for c in n.get('characteristics', [])]
    return '\n'.join(parts)


def candidate_type(node, path, ancestors):
    field = path.split('/')[-2] if '/' in path else ''
    text = direct_text(node)
    type_key = key(node.get('typeName', ''))
    context = ' '.join(a.get('name', '') for a in ancestors[-3:])
    fields = {key(c.get('name', '')) for c in node.get('characteristics', [])}
    # Characteristic text is audited on its owning profile, not counted twice.
    if field == 'characteristics':
        return None
    if type_key in ('STRATAGEM', 'STRATAGEMS'):
        return 'TypedStratagem', bool(text), 'Explicit stratagem profile type'
    if (('WHEN' in fields or 'TIMING' in fields) and 'TARGET' in fields and 'EFFECT' in fields
            and fields.intersection({'CP', 'CPCOST', 'COST'})):
        return 'StructuredStratagemCandidate', bool(text), 'Timing/target/effect/cost fields need source review'
    if all(re.search(r'\b'+label+r'\s*:', text, re.I) for label in ('WHEN', 'TARGET', 'EFFECT')):
        return 'StructuredTextCandidate', bool(text), 'Labelled timing/target/effect text needs source review'
    if key(node.get('name', '')) in ('STRATAGEM', 'STRATAGEMS') or re.search(r'\bstratagems?\b', context, re.I):
        return 'StratagemContainerCandidate', bool(text), 'Explicit container/name signal; review before treating as card'
    if re.search(r'\bstratagems?\b', text, re.I):
        return 'StratagemMention', bool(text), 'Ability/rule mentions a stratagem; mention does not supply the card'
    if field in ('rules', 'sharedRules'):
        return 'Rule', bool(text), 'General rule; not declared a stratagem'
    if field in ('profiles', 'sharedProfiles'):
        return node.get('typeName', 'Profile'), bool(text), 'Typed profile; preserve declared source type'
    if field in ('publications', 'categoryEntries', 'infoLinks', 'infoGroups') or text or any(node.get(k) for k in ('rules','profiles','infoLinks')):
        return field or 'OtherText', bool(text), 'Structural/metadata node or other text, not a stratagem declaration'
    return None


def audit_candidates(graph, fresh, edition):
    imported = collections.defaultdict(list)
    for card in fresh.get('ReferenceCards', []):
        ident = (card.get('Details') or {}).get('SourceCardId')
        if ident:
            imported[ident].append(dict(Faction=card['Faction'], Detachment=card.get('Detachment', ''), Section=card['Section']))
    owners = collections.defaultdict(list)
    for row in graph.rows:
        scope = dict(Faction=row['Faction'], FactionId=row['FactionId'], Detachment=row['Name'], Status=row['Status'])
        definitions = [n for doc, _, n in graph.nodes.get(row['SourceId'], []) if doc == row['CatalogueId']]
        ids = {row['SourceId']}
        for definition in definitions:
            for n, path, _ in walk(definition):
                if n.get('id'):
                    ids.add(n['id'])
                if n.get('targetId') and n.get('type') in ('rule', 'profile'):
                    ids.add(n['targetId'])
        for ident in ids:
            if scope not in owners[ident]:
                owners[ident].append(scope)
    candidates = []
    for d in graph.docs.values():
        for n, path, ancestors in walk(d):
            classification = candidate_type(n, path, ancestors)
            if classification is None:
                continue
            kind, text_present, reason = classification
            scopes = []
            for a in (n,) + tuple(reversed(ancestors)):
                if a.get('id') in owners:
                    scopes = owners[a['id']]
                    break
            actual = imported.get(n.get('id'), [])
            result = 'ImportedReference' if actual else 'NotImportedAsReference'
            if kind == 'TypedStratagem' and text_present and not actual:
                result = 'AdapterMissOrUnreachableDefinition'
            candidates.append(dict(SourceId=n.get('id', ''), TargetId=n.get('targetId', ''),
                Edition=edition, CatalogueId=d['id'], Catalogue=d['name'], CatalogueRevision=str(d.get('revision', '')),
                SourcePath=path, Faction=graph.names.get(d['id'], {}).get('Faction', ''), DetachmentScopes=scopes,
                Name=n.get('name', ''), ApparentType=kind, TextAvailable=text_present,
                TextLength=len(direct_text(n)), ContentHash=hashlib.sha256(direct_text(n).encode()).hexdigest() if text_present else '',
                ImporterResult=result, ImportedAs=actual, Reason=reason,
                ReviewExcerpt=direct_text(n)[:350] if 'Candidate' in kind else ''))
    return candidates


def applicable(card, row, edition):
    d = card.get('Details') or {}
    if d.get('Edition', (card.get('Source') or {}).get('Edition', edition)) != edition:
        return False
    if d.get('Status', 'Active') != 'Active' or d.get('Match') in ('Unresolved', 'Ambiguous'):
        return False
    if d.get('Scope') == 'Global':
        return True
    if key(card['Faction']) != key(row['Faction']):
        return False
    targets = d.get('Detachments') or ([{'Name': card['Detachment']}] if card.get('Detachment') else [])
    return not targets or any(t.get('Id') == row['DetachmentSourceId'] or (not t.get('Id') and key(t['Name']) == key(row['Detachment'])) for t in targets)


def coverage(cards, declarations, row, edition):
    selected = [c for c in cards if key(c['Section']) in ('STRATAGEM', 'STRATAGEMS') and applicable(c, row, edition)]
    claims = [c for c in declarations if c.get('Edition') == edition and key(c.get('Category', '')) == 'STRATAGEMS'
              and (not c.get('Faction') or key(c['Faction']) == key(row['Faction']))
              and (not c.get('Detachment') or key(c['Detachment']) == key(row['Detachment']))]
    complete = [c for c in claims if c.get('Authoritative') and c.get('Mode') != 'Partial' and c.get('State') in ('Complete', 'VerifiedComplete')]
    state = 'VerifiedComplete' if any(c['State'] == 'VerifiedComplete' for c in complete) else 'Complete' if complete else 'Partial' if selected else 'Unknown'
    return selected, state


def run(args):
    original, fresh, pack = read(args.dataset), read(args.imported), read(args.pack)
    names = {c['Id']: c for c in read(args.catalogues)}
    source = original['Source']; edition = source['Edition']
    archive = pathlib.Path(args.archive).read_bytes()
    if hashlib.sha256(archive).hexdigest().lower() != source['ContentHash'].lower():
        raise ValueError('Archive hash differs from the supplied dataset source; refusing revision mismatch')
    if any(fresh['Source'].get(k) != source.get(k) for k in ('Edition', 'Version', 'ContentHash')):
        raise ValueError('Shared-importer audit input is not the same pinned source')
    documents = []
    with zipfile.ZipFile(args.archive) as z:
        for name in z.namelist():
            if name.endswith('.json'):
                obj = json.loads(z.read(name))
                if 'catalogue' in obj or 'gameSystem' in obj:
                    documents.append(obj.get('catalogue', obj.get('gameSystem')))
    graph = Graph(documents, names, args.force_id)
    force = next((n for d in documents for n in d.get('forceEntries', []) if n['id'] == args.force_id), None)
    if force is None:
        raise ValueError('Explicit audit force context not found')
    for primary in sorted(c['Id'] for c in names.values() if c['ArmyCatalogue']):
        graph.audit_faction(primary)
    canonical = canonical_rows(graph.rows, edition)
    playable = [r for r in canonical if r['Status'] == 'Selectable' and r['IdentityResolved']]
    candidates = audit_candidates(graph, fresh, edition)
    # Match each old name-based projection to actual source identities, without collapsing same-name IDs.
    old_rows = []
    for d in original.get('Detachments', []):
        matches = [r for r in canonical if r['Faction'] == d['Faction'] and key(r['Detachment']) == key(d['Name'])]
        old_rows.append(dict(Edition=edition, Faction=d['Faction'], FactionId=matches[0]['FactionId'] if matches else '',
            Detachment=d['Name'], StoredSourceId=d.get('Id', ''), Match='NameEvidenceOnly' if len(matches) == 1 else 'Ambiguous' if matches else 'Unresolved',
            SourceIds=[m['DetachmentSourceId'] for m in matches], Status=matches[0]['Status'] if len(matches) == 1 else 'Unresolved',
            Catalogue=matches[0]['Catalogue'] if len(matches) == 1 else '', SourceRevision=(d.get('Source') or source)['Version']))
    all_cards = pack.get('ReferenceCards', [])
    for record in canonical:
        record['SourceRevision'] = source['Version']
        record['Selectable'] = record['Status'] == 'Selectable'
        record['ReferenceCardCount'] = sum(applicable(c,record,edition) for c in all_cards)
        selected_cards, state = coverage(all_cards,pack.get('ReferenceCoverage',[]),record,edition)
        record['StratagemCount'] = len(selected_cards)
        record['Coverage'] = state
    for record in old_rows:
        matches = [r for r in canonical if r['Faction'] == record['Faction'] and r['DetachmentSourceId'] in record['SourceIds']]
        record['AppearsSameAs'] = [r['DetachmentSourceId'] for r in matches]
        record['ResolvedSourceId'] = matches[0]['DetachmentSourceId'] if len(matches)==1 else ''
        record['DuplicateAliasCount'] = sum(r['DuplicateAliasCount'] for r in matches)
        record['ReferenceCardCount'] = sum(any(applicable(c,r,edition) for r in matches) for c in all_cards)
        record['Coverage'] = coverage(all_cards, pack.get('ReferenceCoverage', []), matches[0], edition)[1] if len(matches)==1 else 'Unknown'
    factions = []
    for f in sorted((c for c in names.values() if c['ArmyCatalogue']), key=lambda c: c['Faction']):
        rows = []
        for row in [r for r in playable if r['FactionId'] == f['Id']]:
            cards, state = coverage(all_cards, pack.get('ReferenceCoverage', []), row, edition)
            bs = [c for c in cards if (c.get('Details') or {}).get('SourceType') == 'BSData']
            supplements = [c for c in cards if c not in bs]
            usable = [c for c in candidates if c['ApparentType'] == 'TypedStratagem' and c['TextAvailable']
                      and any(s['FactionId'] == f['Id'] and s['Detachment'] == row['Detachment'] for s in c['DetachmentScopes'])]
            classification = ['A' if bs else 'B' if usable else 'D']
            if bs and state not in ('Complete', 'VerifiedComplete'):
                classification.append('C')
            classification.append('E' if supplements else 'F' if not bs else 'A')
            rows.append(dict(Edition=edition, FactionId=f['Id'], DetachmentSourceId=row['DetachmentSourceId'],
                Detachment=row['Detachment'], Catalogue=row['Catalogue'], CatalogueRevision=row['CatalogueRevision'],
                SourceRevision=source['Version'], Selectable=True, Aliases=row['Aliases'],
                SourceAvailability=['BSData'] * bool(bs) + ['Supplemental'] * bool(supplements) or ['None'],
                ReferenceCardCount=sum(applicable(c, row, edition) for c in all_cards),
                StratagemCount=len(cards), Coverage=state, Classification=sorted(set(classification)),
                Missing=['No usable stratagem source in the audited inputs'] if not cards else ['No authoritative completeness declaration'] if state == 'Partial' else [],
                Unresolved=[], Provenance=list({json.dumps(c.get('Source'), sort_keys=True): c.get('Source') for c in cards}.values())))
        factions.append(dict(FactionId=f['Id'], Faction=f['Faction'], Catalogue=f['Name'], Detachments=rows,
            UniverseWarning='' if rows else 'No resolved selectable detachment in this source/context; completeness of universe not established'))
    matrix_rows = [d for f in factions for d in f['Detachments']]
    totals = dict(RawFactions=len(set(u['Faction'] for u in original['Units'])), CanonicalFactions=len(factions),
        RawImportedDetachmentRecords=len(old_rows), ReachableSourceOccurrences=len(graph.rows),
        FreshImportedDetachmentRecords=len(fresh.get('Detachments', [])),
        CanonicalFactionDetachmentIdentities=len(canonical), CanonicalPlayableDetachments=len(playable),
        RawDistinctSourceDetachmentDefinitions=len({r['DetachmentSourceId'] for r in canonical}),
        DistinctPlayableSourceIds=len({r['DetachmentSourceId'] for r in playable}),
        DuplicatesAliasesCollapsed=len(graph.rows)-len(canonical), ExcludedIdentities=sum(r['Status']=='Excluded' for r in canonical),
        UnresolvedIdentities=sum(r['Status']=='Unresolved' or not r['IdentityResolved'] for r in canonical),
        UnresolvedImportedRecords=sum(r['Status']=='Unresolved' for r in old_rows),
        FactionsWithoutResolvedDetachments=sum(not f['Detachments'] for f in factions),
        FactionsWithPlayableDetachments=sum(bool(f['Detachments']) for f in factions),
        HistoricalSourceFlaggedSelectableCombinations=sum(any(o['HistoricalSourceSignal'] for o in r['Occurrences']) for r in playable),
        OriginalRecordsSelectable=sum(r['Status']=='Selectable' for r in old_rows),
        OriginalRecordsExcluded=sum(r['Status']=='Excluded' for r in old_rows),
        Coverage=dict(collections.Counter(r['Coverage'] for r in matrix_rows)),
        BSDataSourced=sum(r['SourceAvailability']==['BSData'] for r in matrix_rows),
        SupplementalSourced=sum(r['SourceAvailability']==['Supplemental'] for r in matrix_rows),
        MixedSource=sum(len(r['SourceAvailability'])==2 for r in matrix_rows),
        NoContent=sum(r['SourceAvailability']==['None'] for r in matrix_rows))
    totals['BSDataAudit'] = dict(UsableStratagemDefinitions=sum(c['ApparentType']=='TypedStratagem' and c['TextAvailable'] for c in candidates),
        StructuredCandidatesNeedingReview=sum('Candidate' in c['ApparentType'] and c['TextAvailable'] for c in candidates),
        DetachmentsImportedSuccessfully=sum('A' in r['Classification'] for r in matrix_rows),
        DetachmentAdapterMisses=sum('B' in r['Classification'] for r in matrix_rows),
        DetachmentsPartial=sum('C' in r['Classification'] for r in matrix_rows),
        DetachmentsNoContent=sum('D' in r['Classification'] for r in matrix_rows))
    totals['Supplemental'] = dict(Factions=sum(any('Supplemental' in r['SourceAvailability'] for r in f['Detachments']) for f in factions),
        Detachments=sum('Supplemental' in r['SourceAvailability'] for r in matrix_rows),
        Complete=sum('Supplemental' in r['SourceAvailability'] and r['Coverage'] in ('Complete','VerifiedComplete') for r in matrix_rows),
        Partial=sum('Supplemental' in r['SourceAvailability'] and r['Coverage']=='Partial' for r in matrix_rows))
    output = pathlib.Path(args.output); output.mkdir(parents=True, exist_ok=True)
    context = dict(Edition=edition, Source=source, ForceId=args.force_id, ForceName=force['name'],
        UniverseCaveat='Pinned-source visibility/native-route selectable identities, not independent verification of published current-edition legality or legal combinations at every points limit. Historical source names are flagged, not silently discarded. Unknown visibility excluded and reported; no name-only deduplication.')
    write(output/'detachment-universe.json', dict(**context, Totals=totals, ImportedRecords=old_rows, Canonical=canonical, GraphIssues=graph.issues))
    write(output/'BSDataReferenceAudit.json', dict(**context, Summary=dict(Candidates=len(candidates),
        ScannedObjects=sum(1 for d in documents for _ in walk(d)),
        ScannedContainers=dict(collections.Counter(p.split('/')[-2] for d in documents for _,p,_ in walk(d) if p and p.split('/')[-1].isdigit())),
        Types=dict(collections.Counter(c['ApparentType'] for c in candidates)),
        ImporterResults=dict(collections.Counter(c['ImporterResult'] for c in candidates))), Candidates=candidates))
    write(output/'reference-coverage.json', dict(**context, Totals=totals, Factions=factions))
    lines = ['GrimList pinned-source reference coverage audit', json.dumps(context, ensure_ascii=False), json.dumps(totals, indent=2),
             'A=BSData imported; B=usable BSData adapter miss; C=partial BSData; D=no usable BSData stratagem text; E=supplement exists; F=no usable source.',
             'Missing entries are explicitly listed below. Unknown source legality is not represented as playable.']
    for f in factions:
        lines += ['', f['Faction'] + ' [' + f['FactionId'] + ']']
        if f['UniverseWarning']:
            lines.append('  UNRESOLVED UNIVERSE: ' + f['UniverseWarning'])
        for r in f['Detachments']:
            lines += [f"  {r['Detachment']} [{r['DetachmentSourceId']}]: {r['Coverage']}; {r['StratagemCount']} stratagems; source {','.join(r['SourceAvailability'])}; classes {','.join(r['Classification'])}",
                      '    Missing: ' + '; '.join(r['Missing']), '    Provenance: ' + json.dumps(r['Provenance'], ensure_ascii=False)]
    (output/'reference-coverage.txt').write_text('\n'.join(lines)+'\n', encoding='utf-8')
    owners = collections.defaultdict(list)
    for c in all_cards:
        owners[(c.get('Details') or {}).get('SourceId', '')].append(c)
    manifest = dict(PackIdentity='GrimList-References', SchemaVersion=pack.get('SchemaVersion', 1), Edition=edition,
        Revision=hashlib.sha256(pathlib.Path(args.pack).read_bytes()).hexdigest(), Source=pack['Source'],
        Sources=[dict(SourceId=owner, SourceType=(cards[0].get('Details') or {}).get('SourceType', 'Unknown'),
            Edition=edition, Revisions=sorted({(c.get('Source') or {}).get('Version', '') for c in cards}),
            Cards=len(cards), CoverageMode='Partial',
            CoverageScopes=sorted({c['Faction']+' / '+c.get('Detachment','') for c in cards}),
            Provenance=list({json.dumps(c.get('Source'),sort_keys=True):c.get('Source') for c in cards}.values()),
            CoverageDeclarations=[d for d in pack.get('ReferenceCoverage', []) if d['SourceId']==owner]) for owner,cards in sorted(owners.items())])
    write(output/'reference-source-manifest.json', manifest)
    if args.before_imported:
        before = read(args.before_imported)
        delta = []
        for faction in sorted({c['Faction'] for c in fresh.get('ReferenceCards', [])}):
            old_cards = [c for c in before.get('ReferenceCards', []) if c['Faction']==faction and c['Section'] in ('Army Rules','Detachments','Stratagems')]
            new_cards = [c for c in fresh.get('ReferenceCards', []) if c['Faction']==faction and c['Section'] in ('Army Rules','Detachments','Stratagems')]
            delta.append(dict(Faction=faction, BeforeCards=len(old_cards), AfterCards=len(new_cards),
                BeforeDetachments=sum(d['Faction']==faction for d in before.get('Detachments',[])),
                AfterDetachments=sum(d['Faction']==faction for d in fresh.get('Detachments',[]))))
        write(output/'reference-extraction-delta.json',delta)
    print(json.dumps(totals, indent=2))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    for arg in ('archive', 'dataset', 'imported', 'catalogues', 'pack', 'force-id', 'output'):
        p.add_argument('--'+arg, required=True)
    p.add_argument('--before-imported')
    run(p.parse_args())
