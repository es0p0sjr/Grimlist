"""Content-addressed immutable evidence plus a replaceable acquisition index."""
from datetime import date as calendar_date, datetime, timezone
import pathlib
import re
from .models import VERSION, digest, read, write


def now():
    return datetime.now(timezone.utc).isoformat()


class SnapshotStore:
    def __init__(self, root):
        self.root = pathlib.Path(root)

    def index(self):
        path = self.root / 'index.json'
        return read(path) if path.exists() else {}

    @staticmethod
    def identity(adapter, edition, uri):
        return digest(adapter + '|' + edition + '|' + uri)

    def latest(self, adapter, edition, uri):
        item = self.index().get(self.identity(adapter, edition, uri))
        return self.load(item['Snapshot']) if item else None

    def load(self, relative):
        directory = (self.root / relative).resolve()
        if not directory.is_relative_to(self.root.resolve()):
            raise ValueError('Snapshot path escapes cache')
        metadata = read(directory / 'metadata.json')
        raw = (directory / 'raw').read_bytes()
        if digest(raw) != metadata['ContentHash']:
            raise ValueError('Immutable snapshot hash mismatch: ' + str(directory))
        return {'Path': str(directory.relative_to(self.root.resolve())), 'Metadata': metadata, 'Raw': raw}

    def save(self, adapter, edition, uri, raw, *, media_type='text/html', origin='acquired',
             faction='', etag='', modified='', revision='', date='', parser_version=VERSION):
        if not re.fullmatch(r'[a-z0-9-]+', adapter) or not re.fullmatch(r'\d+', edition):
            raise ValueError('Invalid adapter or edition cache key')
        if date and calendar_date.fromisoformat(date).isoformat() != date:
            raise ValueError('Source date must be YYYY-MM-DD')
        identity = self.identity(adapter, edition, uri)
        content_hash = digest(raw)
        # Metadata revisions are evidence too; changed ETags never overwrite old evidence.
        evidence = digest('|'.join([content_hash, etag, modified, revision, date, origin, faction, media_type, parser_version]))[:16]
        relative = str(pathlib.Path(adapter) / (edition + 'e') / identity / (content_hash + '-' + evidence))
        destination = self.root / relative
        if not destination.exists():
            destination.mkdir(parents=True)
            (destination / 'raw').write_bytes(raw)
            write(destination / 'metadata.json', {
                'SourceUri': uri, 'AcquiredAt': now(), 'SourceRevision': revision, 'SourceDate': date,
                'ETag': etag, 'LastModified': modified, 'ContentHash': content_hash, 'Edition': edition,
                'Adapter': adapter, 'AdapterVersion': parser_version, 'MediaType': media_type,
                'Origin': origin, 'SourceFaction': faction})
        snapshot = self.load(relative)
        index = self.index()
        previous = index.get(identity, {})
        history = list(dict.fromkeys(previous.get('History', []) + [relative]))
        index[identity] = {'Snapshot': relative, 'History': history, 'CheckedAt': now(),
                           'ETag': etag, 'LastModified': modified}
        write(self.root / 'index.json', index)
        return snapshot

    def checked(self, adapter, edition, uri):
        index = self.index()
        identity = self.identity(adapter, edition, uri)
        if identity in index:
            index[identity]['CheckedAt'] = now()
            write(self.root / 'index.json', index)
