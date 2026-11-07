"""Run through GrimList.Data references so the shared C# adapter validates packs."""
import argparse
import copy
import json
import pathlib
import sys
from urllib.parse import urlparse
from .adapters import ADAPTERS
from .http import AcquisitionError, Fetcher
from .matching import Universe, Matcher
from .models import VERSION, ParsedDocument, ParsedReferenceRecord, digest, key, read, write
from .pack import build, diff, owner
from .snapshots import SnapshotStore
from .reporting import manifest as source_manifest, enrich, save_report, source_card_index
from .discovery import plan_factions
from .sections import select_section, parse_section, VERSION as SECTION_VERSION
from .coverage import confirmed_empty_detachments


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('command', choices=['plan-factions', 'plan', 'discover', 'acquire', 'import-snapshot', 'parse', 'build-pack', 'audit', 'update'])
    p.add_argument('--dataset', help='Active/exported BSData dataset JSON')
    p.add_argument('--universe', help='Pinned source-selectability audit (detachment-universe.json)')
    p.add_argument('--adapter', choices=sorted(ADAPTERS), default='wahapedia')
    p.add_argument('--edition', default='11')
    p.add_argument('--cache', default='.local/reference-sources')
    p.add_argument('--output', default='artifacts/references')
    p.add_argument('--aliases', default=str(pathlib.Path(__file__).with_name('reference-aliases.json')))
    p.add_argument('--source-manifest', help='Previously discovered pages; aliases are separate data')
    p.add_argument('--coverage-pack', help='Baseline coverage evidence for the preflight manifest only; never copied into candidate cards')
    p.add_argument('--catalogues', help='Pinned BSData catalogue inventory, needed by plan-factions for zero-detachment factions')
    p.add_argument('--url-mappings', help='Edition-scoped faction URL mapping data for plan-factions')
    p.add_argument('--index-uri', help='Faction index or sitemap URI; default is the adapter edition index')
    p.add_argument('--all', action='store_true')
    p.add_argument('--missing', action='store_true')
    p.add_argument('--faction', action='append', default=[])
    p.add_argument('--offline', action='store_true')
    p.add_argument('--dry-run', action='store_true')
    p.add_argument('--delay', type=float, default=3.0)
    p.add_argument('--timeout', type=float, default=30.0)
    p.add_argument('--retries', type=int, default=2)
    p.add_argument('--file', help='Manually saved source snapshot')
    p.add_argument('--uri', help='Original source URI for a manually supplied snapshot')
    p.add_argument('--source-faction', default='')
    p.add_argument('--media-type', choices=['text/html', 'text/plain', 'application/xml'], default='text/html')
    p.add_argument('--source-revision', default='')
    p.add_argument('--source-date', default='')
    p.add_argument('--origin', choices=['manually supplied', 'browser-excerpt'], default='manually supplied')
    return p


def page_for(snapshot):
    meta = snapshot['Metadata']
    if not meta.get('SourceFaction'): return None
    return {'SourceUri': meta['SourceUri'], 'Name': meta['SourceFaction'],
            'SourceFactionId': urlparse(meta['SourceUri']).path.rstrip('/').split('/')[-1], 'Detachments': []}


def selection(universe, args):
    known = {key(f['Name']) for f in universe.factions}
    unknown = [f for f in args.faction if key(f) not in known]
    if unknown: raise ValueError('Unknown canonical faction filter: ' + ', '.join(unknown))
    names = {f['Name'] for f in universe.factions if not args.faction or key(f['Name']) in {key(n) for n in args.faction}}
    if args.missing:
        complete = {(c['Faction'], c['Detachment']) for c in (universe.dataset.get('ReferenceCoverage') or [])
                    if c['State'] in ('Complete', 'VerifiedComplete') and c['Authoritative'] and c['Edition'] == universe.edition}
        previous_path = pathlib.Path(args.output) / f'GrimList-References-{universe.edition}e.json'
        if previous_path.exists():
            complete |= {(c['Faction'], c['Detachment']) for c in read(previous_path).get('ReferenceCoverage', [])
                         if c['State'] in ('Complete', 'VerifiedComplete') and c['Authoritative'] and c['Edition'] == universe.edition}
        names = {name for name in names if any(r['Faction'] == name and (name, r['Detachment']) not in complete for r in universe.scopes)}
    return names


def report(universe, pack, pages, runs, matches, issues, selected):
    rows = []
    for scope in universe.scopes:
        name, detachment = scope['Faction'], scope['Detachment']
        sources = [p for p in pages if p.get('CanonicalFaction') == name]
        run = [r for r in runs if r.get('CanonicalFaction') == name]
        cards = [c for c in pack['ReferenceCards'] if c['Faction'] == name and (c['Detachment'] == detachment or not c['Detachment'])]
        declarations = [c for c in pack['ReferenceCoverage'] if c['Faction'] == name and c['Detachment'] == detachment]
        state = 'Complete' if any(c['State'] == 'Complete' and c['Authoritative'] for c in declarations) else 'Partial' if cards else 'Unknown'
        relevant_matches = [m for m in matches if (m.get('Faction') or {}).get('Name') == name and m.get('Detachment') and m['Detachment']['Name'] == detachment]
        errors = [error for r in run for error in r.get('Errors', [])]
        errors += [str(i['Errors']) for i in issues if any(c['Source']['Url'] == i['SourceUri'] for c in cards)]
        rows.append({'CanonicalFaction': name, 'FactionId': scope['FactionId'], 'CanonicalDetachment': detachment,
            'DetachmentId': scope['DetachmentSourceId'], 'Selected': name in selected, 'SourceDiscovered': bool(sources),
            'Snapshot': [r['SnapshotStatus'] for r in run] or ['unavailable'], 'Parser': [r['Parser'] for r in run] or ['not run'],
            'Match': sorted({m['DetachmentMatch'] for m in relevant_matches}) or ['Unresolved'],
            'Cards': len(cards), 'Coverage': state, 'Errors': errors})
    aggregates = {'CanonicalFactions': len(universe.factions), 'CanonicalDetachments': len(rows),
        'SourcesDiscovered': len(pages), 'SnapshotsAcquired': sum(r['SnapshotStatus'] == 'acquired' for r in runs),
        'CachedUnchanged': sum(r['SnapshotStatus'] == 'cached' for r in runs),
        'ParsedFromCache': sum(r.get('ParseCache') == 'reused' for r in runs),
        'ParsedSuccessfully': sum(r['Parser'] == 'success' for r in runs), 'FailedParsing': sum(r['Parser'] == 'failure' for r in runs),
        'Unavailable': sum(r['SnapshotStatus'] == 'unavailable' for r in runs),
        'ExactMatches': sum(m['Resolved'] and m['FactionMatch'] in ('ExactId', 'ExactName') and m['DetachmentMatch'] in ('ExactId', 'ExactName', 'NotApplicable') for m in matches),
        'FallbackMatches': sum(m['Resolved'] and 'NormalizedName' in (m['FactionMatch'], m['DetachmentMatch']) and 'Alias' not in (m['FactionMatch'], m['DetachmentMatch']) for m in matches),
        'Unresolved': sum(not m['Resolved'] for m in matches), 'CardsGenerated': len(pack['ReferenceCards']),
        **{state: sum(row['Coverage'] == state for row in rows) for state in ('Complete', 'Partial', 'Unknown')}}
    faction_summary = [{'Faction': f['Name'], 'FactionId': f['Id'],
        'CanonicalDetachments': sum(r['CanonicalFaction'] == f['Name'] for r in rows),
        'SourceDiscovered': any(p.get('CanonicalFaction') == f['Name'] for p in pages),
        'Sources': [r for r in runs if r.get('CanonicalFaction') == f['Name']]} for f in universe.factions]
    return {'Edition': universe.edition, 'UniverseSource': universe.dataset['Source'], 'Totals': aggregates, 'Factions': faction_summary,
            'Scopes': rows, 'Sources': runs, 'Issues': issues, 'Complete': bool(rows) and aggregates['Complete'] == len(rows)}


def run(args):
    adapter, store = ADAPTERS[args.adapter], SnapshotStore(args.cache)
    if args.command == 'import-snapshot':
        if not args.file or not args.uri: raise ValueError('import-snapshot requires --file and original --uri')
        if args.dry_run:
            print(json.dumps({'WouldImport': args.file, 'SourceUri': args.uri, 'Faction': args.source_faction}))
            return 0
        snapshot = store.save(adapter.name, args.edition, args.uri, pathlib.Path(args.file).read_bytes(),
            media_type=args.media_type, origin=args.origin, faction=args.source_faction, revision=args.source_revision, date=args.source_date)
        print(json.dumps({'Snapshot': snapshot['Path'], 'ContentHash': snapshot['Metadata']['ContentHash']}))
        return 0
    if not args.dataset or not args.universe: raise ValueError('--dataset and pinned --universe are required')
    universe = Universe.load(args.dataset, args.universe)
    if args.edition != universe.edition: raise ValueError('--edition must match BSData dataset')
    matcher = Matcher(universe, adapter.name, read(args.aliases) if args.aliases else None)
    adapter = copy.copy(adapter)
    adapter.scope_aliases = matcher.aliases.get('sourceScopeAliases', {})
    output = pathlib.Path(args.output)
    if args.command == 'plan-factions':
        if not args.catalogues: raise ValueError('plan-factions requires --catalogues')
        manifest = plan_factions(universe, read(args.catalogues), store, read(args.url_mappings) if args.url_mappings else None)
        if not args.dry_run:
            write(output / 'reference-faction-source-manifest.json', manifest)
            lines = [json.dumps(manifest['Totals'], indent=2), '', 'Status | Canonical faction ID | Faction | Slug | Discovery | URL | Reason']
            lines.extend(' | '.join(str(r[k]) for k in ('Status', 'CanonicalFactionId', 'Name', 'SourceSlug', 'DiscoveryMethod', 'SourceUri', 'Reason')) for r in manifest['Factions'])
            (output / 'reference-faction-source-manifest.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
        print(json.dumps(manifest['Totals'], indent=2))
        return 0
    selected = selection(universe, args)
    manifest_path = pathlib.Path(args.source_manifest) if args.source_manifest else output / 'reference-source-manifest.json'
    pages, discovery_errors, runs = [], [], []
    discovery_result = None
    if manifest_path.exists():
        manifest = read(manifest_path)
        if manifest['Edition'] != universe.edition or manifest['Adapter'] != adapter.name: raise ValueError('Manifest edition/adapter mismatch')
        pages = manifest['Factions']
    for item in store.index().values():
        snapshot = store.load(item['Snapshot'])
        meta = snapshot['Metadata']
        if meta['Adapter'] == adapter.name and meta['Edition'] == universe.edition:
            page = page_for(snapshot)
            if page:
                # Several canonical factions may share one physical page. Never
                # collapse their manifest scopes into the cached page's identity.
                if not any(p['SourceUri'] == page['SourceUri'] for p in pages): pages.append(page)
    fetcher = Fetcher(store, args.delay, args.timeout, args.retries)
    online = not args.offline and args.command in ('discover', 'acquire', 'update')
    index_uri = args.index_uri or adapter.index_uri(universe.edition)
    needs_discovery = args.command in ('discover', 'update', 'acquire') and not args.source_manifest
    if needs_discovery and not args.dry_run:
        try:
            snapshot = fetcher.acquire(adapter.name, universe.edition, index_uri)[0] if online else store.latest(adapter.name, universe.edition, index_uri)
            if snapshot:
                for page in adapter.discover(snapshot):
                    if not any(p['SourceUri'] == page['SourceUri'] for p in pages): pages.append(page)
            elif not pages: discovery_errors.append('No cached discovery index; supply saved index HTML or --source-manifest')
        except (ValueError, OSError, RuntimeError) as error:
            discovery_errors.append(str(error))
            discovery_result = {'Result': error.result if isinstance(error, AcquisitionError) else 'DiscoveryFailure',
                                'HttpStatus': getattr(error, 'status', None), 'SourceUri': getattr(error, 'uri', index_uri)}
            # A failed index is not license to probe every guessed URL.
            online = False
    for page in pages:
        faction, kind = matcher.faction(page['Name'], page.get('SourceFactionId', ''))
        page['CanonicalFaction'] = faction['Name'] if faction else ''
        page['FactionMatch'] = kind
    plan = {'Edition': universe.edition, 'SelectedFactions': sorted(selected), 'Pages': [p for p in pages if p['CanonicalFaction'] in selected and p.get('Enabled', True)],
            'WouldDiscover': index_uri if needs_discovery else None,
            'MissingSource': sorted(selected - {p['CanonicalFaction'] for p in pages}), 'Offline': args.offline, 'DryRun': args.dry_run}
    if args.dry_run:
        print(json.dumps(plan, ensure_ascii=False, indent=2))
        return 0
    baseline_packs = [read(args.coverage_pack)] if args.coverage_pack else []
    previous_pack = output / f'GrimList-References-{universe.edition}e.json'
    if previous_pack.exists(): baseline_packs.append(read(previous_pack))
    manifest = source_manifest(universe, pages, store, adapter, selected, baseline_packs)
    manifest['Errors'] = discovery_errors
    # Written before source-page acquisition; all canonical entries remain visible.
    write(output / 'reference-source-manifest.json', manifest)
    if args.command == 'plan':
        print(json.dumps({'Manifest': str(output / 'reference-source-manifest.json'),
                          'CanonicalCombinations': len(manifest['Scopes']), 'Plan': plan}, ensure_ascii=False, indent=2))
        return 0
    if args.command in ('build-pack', 'update'):
        (output / f'GrimList-References-{universe.edition}e.candidate.json').unlink(missing_ok=True)
    if args.command == 'discover':
        print(json.dumps(plan, ensure_ascii=False, indent=2))
        return 2 if discovery_errors else 0
    documents, acquisitions = [], {}
    for page in plan['Pages']:
        row = {'SourceUri': page['SourceUri'], 'CanonicalFaction': page['CanonicalFaction'], 'SnapshotStatus': 'unavailable',
               'Parser': 'not run', 'Acquisition': 'NotAvailable', 'Result': 'NotAttempted', 'Errors': []}
        runs.append(row)
        try:
            snapshot = store.latest(adapter.name, universe.edition, page['SourceUri'])
            if online:
                uri = page['SourceUri']
                if uri not in acquisitions:
                    try:
                        acquisitions[uri] = fetcher.acquire(adapter.name, universe.edition, uri, page.get('SourcePageFaction', page['Name']))
                    except (ValueError, OSError, RuntimeError) as error:
                        acquisitions[uri] = error
                acquired = acquisitions[uri]
                if isinstance(acquired, Exception): raise acquired
                snapshot, status = acquired
                if any(r is not row and r['SourceUri'] == uri and r.get('Result') == 'Success' for r in runs):
                    status = 'cached'
                    row['SharedSnapshotReused'] = True
            else:
                status = ('manually supplied' if snapshot and snapshot['Metadata']['Origin'] in ('manually supplied', 'browser-excerpt') else 'cached')
            if snapshot is None: raise ValueError('No cached source snapshot; acquisition unavailable/offline')
            row.update(SnapshotStatus=status, Snapshot=snapshot['Path'], ContentHash=snapshot['Metadata']['ContentHash'],
                       SnapshotOrigin=snapshot['Metadata']['Origin'], MediaType=snapshot['Metadata']['MediaType'],
                       Acquisition={'acquired': 'Downloaded', 'cached': 'Cached', 'manually supplied': 'UserProvided'}[status],
                       Result='Success', HttpStatus=fetcher.responses.get(page['SourceUri']))
            if args.command == 'acquire': continue
            context = ''
            if page.get('SourceFactionSection'):
                snapshot, evidence = select_section(adapter, snapshot, page)
                row.update(evidence)
                context = '|' + json.dumps([SECTION_VERSION, page['SourcePageFaction'], page['SourceFactionSection'], page.get('SourceComposition')], ensure_ascii=False)
            alias_context = '|' + digest(json.dumps(matcher.aliases, sort_keys=True)) if matcher.aliases else ''
            parse_cache = store.root / 'parsed' / (digest(snapshot['Path'] + '|' + adapter.version + context + alias_context) + '.json')
            if parse_cache.exists():
                cached = read(parse_cache)
                parsed = ParsedDocument(**{**cached, 'records': [ParsedReferenceRecord(**r) for r in cached['records']]})
                row['ParseCache'] = 'reused'
            else:
                parsed = parse_section(adapter, snapshot, page['SourceFactionSection']) if context else adapter.parse(snapshot)
                write(parse_cache, parsed.json())
                row['ParseCache'] = 'new'
            row.update(Parser='failure' if parsed.errors else 'success', Errors=list(parsed.errors), Warnings=parsed.warnings,
                       Cards=len(parsed.records), ParserVersion=adapter.version)
            faction, faction_kind = matcher.faction(parsed.faction, parsed.faction_id)
            if context and (not faction or faction['Name'] != page['CanonicalFaction']):
                raise ValueError('Shared subsection does not match the requested canonical faction')
            row['SectionMatches'] = []
            for scope in parsed.detachments:
                detachment, kind = matcher.detachment(faction, scope['Name'], scope.get('SourceDetachmentId', '')) if faction else (None, faction_kind)
                row['SectionMatches'].append({'SourceDetachment': scope['Name'], 'CanonicalDetachment': detachment,
                                              'Match': kind})
                if detachment is None: row['Errors'].append('Unresolved/ambiguous detachment section: ' + scope['Name'])
            empty = confirmed_empty_detachments(snapshot, parsed)
            row['ZeroStratagemDetachments'] = [s['Name'] for s in parsed.detachments if key(s['Name']) in empty]
            if parsed.errors: row['Result'] = 'ParseFailure'
            elif row['Errors']: row['Result'] = 'MatchFailure'
            documents.append((snapshot, parsed))
            page['Detachments'] = parsed.detachments
        except (ValueError, OSError, RuntimeError, UnicodeError) as error:
            row['Errors'].append(str(error))
            if isinstance(error, AcquisitionError):
                row.update(Acquisition='Failed', Result=error.result, HttpStatus=error.status, ResultUri=error.uri)
            elif row['SnapshotStatus'] != 'unavailable':
                row.update(Parser='failure', Result='ParseFailure')
    for faction in plan['MissingSource']:
        runs.append({'CanonicalFaction': faction, 'SourceUri': '', 'SnapshotStatus': 'unavailable', 'Parser': 'not run',
                     'Errors': ['No source discovered for canonical faction'] + discovery_errors})
    pack, matches, issues = build(universe, matcher, documents)
    for row in runs:
        if row.get('Result') == 'Success' and any(not m['Resolved'] and m['SourceUri'] == row['SourceUri'] for m in matches):
            row['Result'] = 'MatchFailure'
    pack_path = output / f'GrimList-References-{universe.edition}e.json'
    previous = read(pack_path) if pack_path.exists() else None
    if previous and previous['Source']['Edition'] != universe.edition: raise ValueError('Previous pack edition mismatch')
    processed = {owner(s['Metadata']['SourceUri']) for s, p in documents if not p.errors}
    differences = diff({'ReferenceCards': [c for c in (previous or {}).get('ReferenceCards', []) if c['Details']['SourceId'] in processed]},
                       {'ReferenceCards': [c for c in pack['ReferenceCards'] if c['Details']['SourceId'] in processed]})
    differences += [{'Status': 'FailedParsing', 'SourceUri': r['SourceUri'], 'Errors': r['Errors']} for r in runs if r['Parser'] == 'failure']
    # Retain unprocessed/failed sources and absent cards from partial sections.
    if previous:
        identities = {(c['Details']['SourceId'], c['Details']['SourceCardId'], c['Details']['FactionId'], c['Detachment']) for c in pack['ReferenceCards']}
        complete = {(c['SourceId'], c['Faction'], c['Detachment']) for c in pack['ReferenceCoverage'] if c['Authoritative']}
        for card in previous['ReferenceCards']:
            d = card['Details']
            if (d['SourceId'], d['SourceCardId'], d['FactionId'], card['Detachment']) not in identities and (d['SourceId'], card['Faction'], card['Detachment']) not in complete:
                pack['ReferenceCards'].append(card)
        definitions = {(d['FactionId'], d['Id']) for d in pack['Detachments']}
        pack['Detachments'] += [d for d in previous['Detachments'] if (d['FactionId'], d['Id']) not in definitions]
        declarations = {(c['SourceId'], c['Faction'], c['Detachment']) for c in pack['ReferenceCoverage']}
        for old in previous['ReferenceCoverage']:
            if (old['SourceId'], old['Faction'], old['Detachment']) not in declarations:
                old = copy.deepcopy(old)
                # Failed reacquisition cannot carry an unverified Complete assertion.
                if old['Faction'] in selected: old.update(State='Partial', Mode='Partial', Authoritative=False)
                pack['ReferenceCoverage'].append(old)
    acquisition = report(universe, pack, pages, runs, matches, issues, selected)
    acquisition['DiscoveryErrors'] = discovery_errors
    acquisition['DiscoveryResult'] = discovery_result
    acquisition['PortablePack'] = str(pack_path) if pack['ReferenceCards'] else None
    baseline = {(r['FactionId'], r['DetachmentId']): r['Coverage'] for r in manifest['Scopes']}
    for row in acquisition['Scopes']:
        row['BaselineCoverage'] = baseline[(row['FactionId'], row['DetachmentId'])]
        row['PreviouslyUncovered'] = row['BaselineCoverage'] == 'Unknown'
    acquisition['Totals']['BaselineUnknown'] = sum(r['PreviouslyUncovered'] for r in acquisition['Scopes'])
    enrich(acquisition, matches)
    source_cards = source_card_index(pack)
    acquisition['Totals']['DistinctSourceCards'] = len(source_cards)
    acquisition['Totals']['CanonicalCardApplications'] = len(pack['ReferenceCards'])
    write(output / 'reference-source-card-index.json', source_cards)
    save_report(output, acquisition)
    if not args.offline and args.command in ('acquire', 'update'):
        write(output / 'reference-acquisition-last-online.json', acquisition)
    write(output / 'reference-coverage.json', {'Edition': universe.edition, 'Totals': acquisition['Totals'], 'Scopes': acquisition['Scopes']})
    write(output / 'reference-matches.json', matches)
    write(output / 'reference-source-diff.json', differences)
    if args.command in ('build-pack', 'update') and pack['ReferenceCards']:
        pack['Source']['Version'] = digest(json.dumps(pack['ReferenceCards'], sort_keys=True, ensure_ascii=False))
        write(pack_path.with_suffix('.candidate.json'), pack)
    print(json.dumps(acquisition['Totals'], indent=2))
    return 2 if discovery_errors or issues or any(r['Errors'] for r in runs) else 0


def main():
    try: return run(parser().parse_args())
    except (ValueError, OSError, KeyError) as error:
        print('ERROR: ' + str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
