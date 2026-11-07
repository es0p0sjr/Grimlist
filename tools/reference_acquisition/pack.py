"""Serialize the existing portable schema; the C# entry point validates and previews it."""
import copy
import json
import re
from .models import VERSION, clean, digest, key, validate
from .adapters import ADAPTERS
from .coverage import confirmed_empty_detachments


def owner(uri):
    return 'source-' + digest(uri.strip().rstrip('/'))


def source_info(metadata, parsed):
    return {'Name': metadata['Adapter'] + ' reference source',
            'Version': (parsed.version or metadata['SourceRevision'] or metadata['ContentHash']) + ' | parser=' + metadata['Adapter'] + '/' + (ADAPTERS[metadata['Adapter']].version if metadata['Adapter'] in ADAPTERS else VERSION),
            'Date': parsed.date or metadata['SourceDate'], 'Url': metadata['SourceUri'],
            'ImportedAt': metadata['AcquiredAt'], 'Edition': metadata['Edition'], 'ContentHash': metadata['ContentHash']}


def build(universe, matcher, documents):
    cards, coverage, matches, issues = [], [], [], []
    source_seen, logical_seen, definitions = {}, {}, {}
    for snapshot, parsed in documents:
        meta = snapshot['Metadata']
        if parsed.edition != universe.edition: raise ValueError('Parsed edition mismatch')
        source = source_info(meta, parsed)
        source_owner = owner(meta['SourceUri'])
        accepted_scopes, failed_scopes = set(), set()
        confirmed_empty = confirmed_empty_detachments(snapshot, parsed)
        # Duplicate IDs within a page are parser failures, not silently accepted cards.
        ids = [r.source_id for r in parsed.records]
        duplicate_ids = {identity for identity in ids if ids.count(identity) > 1}
        for record in parsed.records:
            match = matcher.record(record)
            match.update({'Snapshot': meta['ContentHash'], 'SourceUri': meta['SourceUri'], 'Location': record.location})
            matches.append(match)
            errors = validate(record)
            if record.source_id in duplicate_ids: errors.append('Duplicate source identity')
            if not match['Resolved']: errors.append('Unresolved or ambiguous canonical applicability')
            definition = None
            if match['Resolved'] and match['Detachment']:
                candidates = [d for d in universe.dataset.get('Detachments', [])
                    if d['Faction'] == match['Faction']['Name'] and key(d['Name']) == key(match['Detachment']['Name'])]
                if len(candidates) != 1:
                    errors.append('Canonical detachment lacks one active dataset definition; refresh the BSData import')
                else:
                    definition = copy.deepcopy(candidates[0])
                    definition.update(Id=match['Detachment']['Id'], FactionId=match['Faction']['Id'])
            if errors:
                issues.append({'SourceUri': meta['SourceUri'], 'Name': record.name, 'Errors': errors})
                failed_scopes.add(record.detachment)
                continue
            faction, detachment = match['Faction'], match['Detachment']
            identity = (universe.edition, faction['Id'] or faction['Name'], detachment['Id'] if detachment else '', key(record.name))
            content = digest(json.dumps([clean(record.name), record.cost, clean(record.text)], ensure_ascii=False))
            source_identity = (source_owner, record.source_id)
            if source_identity in source_seen and source_seen[source_identity] != identity:
                issues.append({'SourceUri': meta['SourceUri'], 'Name': record.name, 'Errors': ['Source identity reused across canonical scopes']})
                failed_scopes.add(record.detachment)
                continue
            source_seen[source_identity] = identity
            if identity in logical_seen:
                previous_content = logical_seen[identity]
                issues.append({'SourceUri': meta['SourceUri'], 'Name': record.name,
                               'Errors': ['Duplicate canonical card; identical alternate occurrence' if previous_content == content else 'Conflicting alternate source card']})
                failed_scopes.add(record.detachment)
                if previous_content != content:
                    cards = [c for c in cards if (c['Details']['Edition'], c['Details']['FactionId'] or c['Faction'],
                        c['Details']['Detachments'][0]['Id'] if c['Details']['Detachments'] else '', key(c['Name'])) != identity]
                continue
            logical_seen[identity] = content
            card = {'Faction': faction['Name'], 'Section': record.category, 'Name': record.name, 'Text': record.text,
                'Detachment': detachment['Name'] if detachment else '', 'Cost': record.cost, 'Source': source, 'IsUpgrade': False,
                'Details': {'Id': '', 'Edition': universe.edition, 'SourceId': source_owner,
                    'SourceCardId': record.source_id, 'SourceType': meta['Adapter'], 'Scope': 'Detachment' if detachment else 'Faction',
                    'FactionId': faction['Id'], 'Detachments': [dict(Name=detachment['Name'], Id=detachment['Id'], Match=match['DetachmentMatch'])] if detachment else [],
                    'Timing': record.timing, 'Target': record.target, 'Effect': record.effect, 'Restrictions': record.restrictions,
                    'Status': 'Active', 'Match': match['FactionMatch']}}
            card['Text'] = ''  # Description renders structured fields; raw order remains in the immutable snapshot.
            block_labels = re.findall(r'(?m)^(WHEN|TARGET|EFFECT|RESTRICTIONS):', record.text)
            if len(block_labels) != len(set(block_labels)):
                # The existing display groups structured fields. Preserve the order of
                # multi-mode WHEN/TARGET/EFFECT groups as operative text instead.
                card['Text'] = record.text
                for field in ('Timing', 'Target', 'Effect', 'Restrictions'): card['Details'][field] = ''
            cards.append(card)
            match['Fields'] = {'Timing': record.timing, 'Target': record.target, 'Effect': record.effect, 'Restrictions': record.restrictions}
            if detachment:
                accepted_scopes.add(record.detachment)
                definitions[(faction['Id'], detachment['Id'])] = definition
        for scope in parsed.detachments:
            faction, _ = matcher.faction(parsed.faction, parsed.faction_id)
            if not faction: continue
            detachment, _ = matcher.detachment(faction, scope['Name'], scope.get('SourceDetachmentId', ''))
            if not detachment: continue
            empty = key(scope['Name']) in confirmed_empty
            complete = (scope['Complete'] or empty) and not parsed.errors and scope['Name'] not in failed_scopes
            if complete and empty:
                candidates = [d for d in universe.dataset.get('Detachments', [])
                              if d['Faction'] == faction['Name'] and key(d['Name']) == key(detachment['Name'])]
                if len(candidates) != 1:
                    issues.append({'SourceUri': meta['SourceUri'], 'Name': scope['Name'], 'Errors': ['Empty scope lacks one canonical dataset definition']})
                    complete = False
                else:
                    definition = copy.deepcopy(candidates[0])
                    definition.update(Id=detachment['Id'], FactionId=faction['Id'])
                    definitions[(faction['Id'], detachment['Id'])] = definition
            coverage.append({'SourceId': source_owner, 'Edition': universe.edition, 'Category': 'Stratagems',
                'Faction': faction['Name'], 'Detachment': detachment['Name'], 'State': 'Complete' if complete else 'Partial' if scope['Name'] in accepted_scopes else 'Unknown',
                'Mode': 'CompleteForDetachment' if complete else 'Partial', 'Authoritative': complete, 'Source': source})
    # Any duplicate/conflict prevents deletion-authorizing declarations for this build.
    if issues:
        for declaration in coverage:
            declaration.update(State='Partial' if declaration['State'] != 'Unknown' else 'Unknown', Mode='Partial', Authoritative=False)
    revision = digest(json.dumps(cards, sort_keys=True, ensure_ascii=False))
    pack = {'Source': {'Name': 'GrimList acquired reference collection', 'Version': revision, 'Date': '', 'Url': '', 'Edition': universe.edition},
            'Units': [], 'Game': universe.dataset.get('Game', 'Warhammer 40,000'), 'Detachments': list(definitions.values()),
            'ReferenceCards': cards, 'ReferenceOnly': True, 'SchemaVersion': 2, 'ReferenceCoverage': coverage}
    return pack, matches, issues


def diff(previous, current):
    def identity(c):
        return (c['Details']['SourceId'], c['Details']['SourceCardId'], c['Details']['FactionId'], c['Detachment'])
    def semantic(c):
        return [c['Name'], c['Text'], c['Cost'], c['Faction'], c['Detachment'],
                *[c['Details'].get(k, '') for k in ('Timing', 'Target', 'Effect', 'Restrictions')]]
    old = {identity(c): c for c in (previous or {}).get('ReferenceCards', [])}
    new = {identity(c): c for c in current['ReferenceCards']}
    result = []
    for identity_key in sorted(old.keys() | new.keys()):
        before, after = old.get(identity_key), new.get(identity_key)
        status = 'RemovedFromSource' if after is None else 'Added' if before is None else 'Unchanged' if semantic(before) == semantic(after) else 'Updated'
        card = after or before
        result.append({'Status': status, 'Faction': card['Faction'], 'Detachment': card['Detachment'], 'Name': card['Name'],
                       'SourceId': card['Details']['SourceId'], 'SourceCardId': card['Details']['SourceCardId']})
    return result
