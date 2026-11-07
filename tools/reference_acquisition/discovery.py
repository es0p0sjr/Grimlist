"""Reviewable faction-page planning; this module never makes HTTP requests."""
import re
import unicodedata
from urllib.parse import urlparse
from .models import key


def faction_slug(name):
    return re.sub(r'[^a-z0-9]+', '-', unicodedata.normalize('NFKC', name).casefold()).strip('-')


def plan_factions(universe, catalogues, store, mappings=None):
    mappings = mappings or {'Edition': universe.edition, 'Factions': {}}
    if mappings['Edition'] != universe.edition: raise ValueError('URL mapping edition mismatch')
    cached = {}
    for item in store.index().values():
        snapshot = store.load(item['Snapshot'])
        meta = snapshot['Metadata']
        if meta['Adapter'] == 'wahapedia' and meta['Edition'] == universe.edition and 'html' in meta['MediaType']:
            cached[key(meta['SourceFaction'])] = meta
    rows = []
    for faction in universe.factions:
        name, identity = faction['Name'], faction['Id']
        if not identity:
            ids = {c['Id'] for c in catalogues if c['Faction'] == name and c['ArmyCatalogue']}
            if len(ids) != 1: raise ValueError('Cannot resolve one canonical catalogue ID: ' + name)
            identity = next(iter(ids))
        count = sum(s['FactionId'] == identity for s in universe.scopes)
        override = mappings.get('Factions', {}).get(identity, {})
        meta = cached.get(key(name))
        uri = override.get('SourceUri') or (meta['SourceUri'] if meta else '')
        method = override.get('DiscoveryMethod', 'ManifestMapping') if override else 'CachedHtml' if meta else 'GeneratedSlug'
        if not uri: uri = f'https://wahapedia.ru/wh40k{universe.edition}ed/factions/{faction_slug(name)}/'
        url = urlparse(uri)
        if url.scheme != 'https' or url.netloc not in ('wahapedia.ru', 'www.wahapedia.ru') or not re.fullmatch(
                '/wh40k' + re.escape(universe.edition) + r'ed/factions/[a-z0-9-]+/', url.path) or url.query or url.fragment:
            raise ValueError('Expected an edition-specific faction landing page: ' + uri)
        enabled = override.get('Enabled', count > 0)
        rows.append({'CanonicalFactionId': identity, 'CanonicalFaction': name, 'Name': name,
                     'SourceUri': uri, 'SourceSlug': url.path.rstrip('/').split('/')[-1],
                     'SourceFactionId': url.path.rstrip('/').split('/')[-1], 'DiscoveryMethod': method,
                     'Enabled': enabled, 'Status': 'Enabled' if enabled else 'Disabled',
                     'Reason': override.get('Reason', 'No canonical selectable detachments in pinned BSData universe' if not count else
                         'Generated faction-page candidate; existence and applicability unverified' if method == 'GeneratedSlug' else 'Source URL backed by recorded evidence'),
                     'Evidence': override.get('Evidence', meta['ContentHash'] if meta else ''),
                     'Snapshot': 'Present' if meta else 'Missing', 'CanonicalDetachments': count,
                     'RequiresUrlVerification': method != 'CachedHtml'})
        for field in ('SourcePageFaction', 'SourceFactionSection', 'SourceComposition'):
            if field in override: rows[-1][field] = override[field]
        # A shared snapshot belongs to the URI, not to its first canonical user.
        rows[-1]['Snapshot'] = 'Present' if store.latest('wahapedia', universe.edition, uri) else 'Missing'
    totals = {'CanonicalFactions': len(rows), 'CanonicalDetachments': len(universe.scopes),
              'SourceUrlsAssigned': len(rows), 'SourceUrlsDiscovered': sum(r['DiscoveryMethod'] != 'GeneratedSlug' for r in rows),
              'GeneratedUnverifiedUrls': sum(r['DiscoveryMethod'] == 'GeneratedSlug' for r in rows),
              'MissingFactionUrls': sum(not r['SourceUri'] for r in rows),
              'Enabled': sum(r['Enabled'] for r in rows), 'Disabled': sum(not r['Enabled'] for r in rows),
              'AliasManualMappingsRequired': 0}
    totals['UniqueEnabledSourceUris'] = len({r['SourceUri'] for r in rows if r['Enabled']})
    return {'Edition': universe.edition, 'Adapter': 'wahapedia', 'Status': 'ReadyForReview',
            'UniverseSource': universe.dataset['Source'], 'Totals': totals, 'Factions': rows,
            'Notes': ['Generated URLs are candidates, not confirmed pages. No bulk requests have been made.',
                      'No semantic URL aliases are established as necessary yet; failed or unexpected pages may require data mappings.',
                      'Disabled zero-detachment factions remain listed; they contribute no entries to the 444-detachment coverage audit.']}
