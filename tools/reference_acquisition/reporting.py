"""Preparation and outcome reports; no acquisition, parser or application imports."""
import json
from .models import write, digest, key


def source_card_index(pack):
    """Audit source rules separately from single-faction portable applications."""
    groups = {}
    for card in pack['ReferenceCards']:
        details = card['Details']
        signature = [card['Source']['Url'], key(card['Detachment']), key(card['Name']), card['Cost'], card['Text'],
                     *[details.get(k, '') for k in ('Timing', 'Target', 'Effect', 'Restrictions')]]
        identity = digest(json.dumps(signature, ensure_ascii=False))
        entry = groups.setdefault(identity, {'SourceCardKey': identity, 'SourceUri': card['Source']['Url'],
            'SnapshotHash': card['Source']['ContentHash'], 'Detachment': card['Detachment'], 'Name': card['Name'],
            'Applications': []})
        entry['Applications'].append({'Faction': card['Faction'], 'FactionId': details['FactionId'],
                                      'Detachment': card['Detachment'], 'SourceCardId': details['SourceCardId']})
    return list(groups.values())


def manifest(universe, pages, store, adapter, selected, packs=()):
    cards = list(universe.dataset.get('ReferenceCards') or [])
    coverage = list(universe.dataset.get('ReferenceCoverage') or [])
    for pack in packs:
        if pack['Source']['Edition'] != universe.edition:
            raise ValueError('Coverage pack edition mismatch')
        cards.extend(pack.get('ReferenceCards') or [])
        coverage.extend(pack.get('ReferenceCoverage') or [])
    rows = []
    for scope in universe.scopes:
        faction, detachment = scope['Faction'], scope['Detachment']
        locations = [p['SourceUri'] for p in pages if p.get('CanonicalFaction') == faction]
        snapshots = [s['Path'] for uri in locations if (s := store.latest(adapter.name, universe.edition, uri))]
        declarations = [c for c in coverage if c.get('Edition') == universe.edition and
                        c.get('Faction') == faction and c.get('Detachment') == detachment and c.get('Category') == 'Stratagems']
        complete = any(c.get('State') in ('Complete', 'VerifiedComplete') and c.get('Authoritative') for c in declarations)
        present = any(c.get('Faction') == faction and c.get('Detachment') == detachment and
                      c.get('Section') == 'Stratagems' for c in cards)
        state = 'Complete' if complete else 'Partial' if present else 'Unknown'
        rows.append({'Edition': universe.edition, 'FactionId': scope['FactionId'], 'Faction': faction,
                     'DetachmentId': scope['DetachmentSourceId'], 'Detachment': detachment,
                     'ExpectedSourceLocations': locations, 'LocationEvidence': 'Known faction page; detachment section unverified' if locations else 'No suitable source identified',
                     'Snapshot': 'Present' if snapshots else 'Missing', 'SnapshotPaths': snapshots,
                     'Coverage': state, 'AcquisitionRequired': 'No' if complete else 'Yes',
                     'SelectedForThisRun': faction in selected})
    return {'Edition': universe.edition, 'Adapter': adapter.name, 'Factions': pages,
            'CanonicalCombinations': len(rows), 'Scopes': rows,
            'CoverageMeaning': 'Baseline dataset and supplied coverage packs; not fresh real-page acceptance'}


def enrich(report, matches):
    for row in report['Scopes']:
        runs = [r for r in report['Sources'] if r['CanonicalFaction'] == row['CanonicalFaction']]
        bad_matches = [m for m in matches if not m['Resolved'] and
                       any(r['SourceUri'] == m['SourceUri'] for r in runs)]
        results = sorted({r.get('Result', 'NotAttempted') for r in runs}) or ['NotAttempted']
        if row['Coverage'] in ('Complete', 'VerifiedComplete') and not row['Cards']:
            reason = 'Source confirms this detachment has no stratagem section'
        elif not row['SourceDiscovered']:
            reason = 'No suitable source identified'
        elif any(r.get('Result') in ('NetworkUnavailable', 'AccessDenied', 'RateLimited', 'NotFound', 'HttpFailure') for r in runs):
            reason = 'Source acquisition failed; manual snapshot required'
        elif 'ParseFailure' in results:
            reason = 'Source obtained but parser failed'
        elif row['Cards'] and row['Coverage'] == 'Partial':
            reason = 'Source contains partial content'
        elif row['Cards']:
            reason = 'Source exists and imported'
        elif bad_matches:
            reason = 'Source has unresolved/ambiguous matches; detachment applicability requires review'
        elif any(r['Parser'] == 'success' for r in runs):
            reason = 'No usable stratagem content matched this detachment in parsed snapshots'
        else:
            reason = 'Manual snapshot required or acquisition not yet attempted'
        row.update(Edition=report['Edition'], SourceResult=results, GapReason=reason,
                   Acquisition=sorted({r.get('Acquisition', 'NotAvailable') for r in runs}) or ['NotAvailable'],
                   HttpResults=[{'SourceUri': r['SourceUri'], 'ResultUri': r.get('ResultUri', r['SourceUri']),
                                 'Status': r.get('HttpStatus')} for r in runs if r.get('HttpStatus') is not None],
                   ManualSnapshotRequired=row['SourceDiscovered'] and any(r.get('Acquisition') in ('Failed', 'NotAvailable') for r in runs))
    totals = report['Totals']
    totals.update(CanonicalCombinations=len(report['Scopes']),
                  CompleteWithCards=sum(r['Coverage'] in ('Complete', 'VerifiedComplete') and r['Cards'] > 0 for r in report['Scopes']),
                  CompleteWithZeroCards=sum(r['Coverage'] in ('Complete', 'VerifiedComplete') and r['Cards'] == 0 for r in report['Scopes']),
                  Downloaded=sum(r.get('Acquisition') == 'Downloaded' for r in report['Sources']),
                  Cached=sum(r.get('Acquisition') == 'Cached' for r in report['Sources']),
                  ManualSnapshots=sum(r.get('Acquisition') == 'UserProvided' for r in report['Sources']),
                  FailedAcquisition=sum(r.get('Acquisition') == 'Failed' for r in report['Sources']),
                  Ambiguous=sum('Ambiguous' in (m['FactionMatch'], m['DetachmentMatch']) for m in matches),
                  AliasMatches=sum(m['Resolved'] and 'Alias' in (m['FactionMatch'], m['DetachmentMatch']) for m in matches))
    totals['UniqueSourcePages'] = len({r['SourceUri'] for r in report['Sources'] if r['SourceUri'] and r['SnapshotStatus'] != 'unavailable'})
    report['CountUnits'] = 'Coverage totals count canonical faction/detachment entries; acquisition/parse totals count canonical source mappings (shared pages may serve multiple mappings); UniqueSourcePages counts distinct cached page URIs; matches count canonical card applications.'


def save_report(output, report):
    groups = {}
    for row in report['Scopes']:
        if row['Coverage'] == 'Unknown':
            groups.setdefault(row['GapReason'], []).append({'Faction': row['CanonicalFaction'], 'Detachment': row['CanonicalDetachment']})
    report['RemainingUnknownByReason'] = [{'Reason': reason, 'Count': len(rows), 'Entries': rows} for reason, rows in sorted(groups.items())]
    write(output / 'reference-acquisition-report.json', report)
    lines = [f"Edition: {report['Edition']}", report['CountUnits']]
    lines.extend(f'{name}: {value}' for name, value in report['Totals'].items())
    for row in report['Scopes']:
        lines += ['', f"{row['CanonicalFaction']} [{row['FactionId']}] / {row['CanonicalDetachment']} [{row['DetachmentId']}]",
                  f"Source discovered: {'YES' if row['SourceDiscovered'] else 'NO'}; Acquisition: {', '.join(row['Acquisition'])}",
                  f"Source result: {', '.join(row['SourceResult'])}; HTTP: {row['HttpResults']}",
                  f"Parse: {', '.join(row['Parser'])}; Match: {', '.join(row['Match'])}",
                  f"Stratagems: {row['Cards']}; Coverage: {row['Coverage']}; Baseline: {row['BaselineCoverage']}; Reason: {row['GapReason']}"]
        lines.extend('Error/warning: ' + e for e in row['Errors'])
    lines += ['', 'Source diagnostics:']
    for source in report['Sources']:
        lines.append(source['CanonicalFaction'] + ': ' + source['Parser'])
        lines.extend('Warning: ' + w for w in source.get('Warnings', []))
        lines.extend('Error: ' + e for e in source.get('Errors', []))
    lines += ['', 'Remaining Unknown grouped by reason:']
    if not groups: lines.append('None')
    for reason, rows in sorted(groups.items()):
        lines.append(reason + ': ' + str(len(rows)))
        lines.extend('  ' + r['Faction'] + ' / ' + r['Detachment'] for r in rows)
    (output / 'reference-acquisition-report.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
