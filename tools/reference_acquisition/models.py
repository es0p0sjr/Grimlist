from dataclasses import dataclass, field, asdict
import hashlib
import json
import pathlib
import re
import unicodedata

VERSION = '1.2'


def read(path):
    return json.loads(pathlib.Path(path).read_text(encoding='utf-8-sig'))


def write(path, value):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + '.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    temporary.replace(path)


def digest(value):
    return hashlib.sha256(value if isinstance(value, bytes) else value.encode('utf-8')).hexdigest()


def key(value):
    return ''.join(c for c in unicodedata.normalize('NFKC', value).casefold() if c.isalnum())


def clean(value):
    return re.sub(r'\s+([,.;:!?])', r'\1', re.sub(r'\s+', ' ', value)).strip()


@dataclass
class ParsedReferenceRecord:
    source_id: str
    faction: str
    detachment: str
    name: str
    cost: str
    timing: str
    target: str
    effect: str
    restrictions: str
    location: str
    category: str = 'Stratagems'
    faction_id: str = ''
    detachment_id: str = ''
    # Preserve ordered label blocks, including repeated WHEN/EFFECT pairs.
    text: str = ''


@dataclass
class ParsedDocument:
    faction: str
    edition: str
    records: list[ParsedReferenceRecord] = field(default_factory=list)
    detachments: list[dict] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    version: str = ''
    date: str = ''
    faction_id: str = ''

    def json(self):
        return asdict(self)


def labels(text):
    """Labels, not their ordinal positions, delimit fields; repetitions survive."""
    matches = list(re.finditer(r'\b(WHEN|TARGET|EFFECT|RESTRICTIONS?)\s*:', text))
    fields = {name: [] for name in ('WHEN', 'TARGET', 'EFFECT', 'RESTRICTIONS')}
    blocks = []
    for i, match in enumerate(matches):
        value = clean(text[match.end():matches[i+1].start() if i+1 < len(matches) else len(text)])
        label = 'RESTRICTIONS' if match[1] == 'RESTRICTION' else match[1]
        fields[label].append(value)
        blocks.append(label + ': ' + value)
    return {name: '\n\n'.join(values) for name, values in fields.items()}, '\n'.join(blocks)


def validate(record):
    errors = []
    if not record.name.strip(): errors.append('missing name')
    if not record.faction.strip(): errors.append('missing source faction')
    if not record.effect.strip(): errors.append('missing EFFECT; cannot safely construct a stratagem')
    if record.cost and not re.fullmatch(r'\d+ CP', record.cost): errors.append('invalid/nonnegative integer CP cost required')
    return errors
