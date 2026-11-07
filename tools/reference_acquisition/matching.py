"""BSData defines the universe; external sources only supply matching evidence."""
from .models import key, read


class Universe:
    def __init__(self, dataset, audit):
        self.dataset, self.edition = dataset, dataset['Source']['Edition']
        source = dataset['Source']
        if audit['Edition'] != self.edition or any(audit['Source'].get(k) != source.get(k) for k in ('Version', 'ContentHash')):
            raise ValueError('Playable-universe audit must match the dataset edition, revision and archive hash')
        self.scopes = [row for row in audit['Canonical'] if row.get('Selectable') is True and row.get('IdentityResolved') is True]
        identities = [(r['Edition'], r['FactionId'], r['DetachmentSourceId']) for r in self.scopes]
        if len(identities) != len(set(identities)): raise ValueError('Duplicate canonical BSData identities')
        self.factions = []
        names = {u['Faction'] for u in dataset['Units']} | {r['Faction'] for r in self.scopes}
        for name in sorted(names):
            ids = {r['FactionId'] for r in self.scopes if r['Faction'] == name}
            if len(ids) > 1: raise ValueError('Ambiguous BSData faction identity: ' + name)
            self.factions.append({'Name': name, 'Id': next(iter(ids), '')})

    @classmethod
    def load(cls, dataset, audit):
        return cls(read(dataset), read(audit))


class Matcher:
    def __init__(self, universe, adapter, aliases=None):
        self.universe = universe
        self.aliases = aliases or {}
        if self.aliases and (self.aliases.get('source') != adapter or self.aliases.get('edition') != universe.edition):
            raise ValueError('Alias configuration source/edition mismatch')

    @staticmethod
    def choose(candidates, kind):
        return (candidates[0], kind) if len(candidates) == 1 else (None, 'Ambiguous' if candidates else 'Unresolved')

    def resolve(self, name, source_id, candidates, aliases):
        override = aliases.get(source_id) or aliases.get(name)
        if override:
            matches = [c for c in candidates if c['Id'] == override or c['Name'] == override]
            return self.choose(matches, 'Alias')
        matches = [c for c in candidates if source_id and c['Id'] == source_id]
        if matches: return self.choose(matches, 'ExactId')
        matches = [c for c in candidates if c['Name'] == name]
        if matches: return self.choose(matches, 'ExactName')
        return self.choose([c for c in candidates if key(c['Name']) == key(name)], 'NormalizedName')

    def faction(self, name, source_id=''):
        return self.resolve(name, source_id, self.universe.factions, self.aliases.get('factionAliases', {}))

    def detachment(self, faction, name, source_id=''):
        rows = [r for r in self.universe.scopes if r['FactionId'] == faction['Id'] and r['Edition'] == self.universe.edition]
        candidates = [{'Name': r['Detachment'], 'Id': r['DetachmentSourceId']} for r in rows]
        aliases = self.aliases.get('detachmentAliases', {}).get(faction['Id'], {})
        return self.resolve(name, source_id, candidates, aliases)

    def record(self, record):
        faction, faction_match = self.faction(record.faction, record.faction_id)
        detachment, detachment_match = (None, 'Unresolved')
        if faction:
            if record.detachment:
                detachment, detachment_match = self.detachment(faction, record.detachment, record.detachment_id)
            else: detachment_match = 'NotApplicable'
        return {'SourceRecord': record.source_id, 'SourceFaction': record.faction, 'SourceDetachment': record.detachment,
                'Faction': faction, 'Detachment': detachment, 'FactionMatch': faction_match, 'DetachmentMatch': detachment_match,
                'Resolved': faction is not None and (not record.detachment or detachment is not None)}
