"""Wahapedia shared-page selection, before the common card parser.

Only manifest-scoped pages use this path. A supplement heading supplies the
chapter filter; both heading boundaries and container filters must agree.
"""
import re
import json
from .adapters import HtmlTree
from .models import clean, key

VERSION = '1.1'


def chapter_container(node):
    while node is not None:
        if node.has('clFl') and 'data-f' in node.attrs:
            fields = dict(part.split(':', 1) for part in node.attrs['data-f'].split(',') if ':' in part)
            if 'CH' in fields: return node, fields['CH']
        node = node.parent
    raise ValueError('Shared section lacks a chapter filter container')


def select_section(adapter, snapshot, page):
    """Return original DOM nodes and evidence; never rewrite immutable HTML."""
    section = page['SourceFactionSection']
    expected = page.get('SourcePageFaction', '')
    meta = snapshot['Metadata']
    if not expected: raise ValueError('Shared page requires SourcePageFaction identity')
    if meta['SourceUri'] != page['SourceUri']: raise ValueError('Shared source URI mismatch')
    if 'html' not in meta['MediaType']: raise ValueError('Shared subsections require complete HTML')
    if not re.search(r'/wh40k' + re.escape(meta['Edition']) + r'ed/factions/[^/]+/$', meta['SourceUri']):
        raise ValueError('Shared source URI edition/path mismatch')
    nodes = list(HtmlTree(adapter.decode(snapshot)).root.nodes())
    html = [n for n in nodes if n.tag == 'html']
    if len(html) != 1 or not html[0].closed: raise ValueError('Shared source HTML is truncated')
    headings = [clean(n.text()) for n in nodes if n.tag == 'h1']
    titles = [clean(n.text()) for n in nodes if n.tag == 'title']
    if len(headings) != 1 or key(headings[0]) != key(expected): raise ValueError('Shared page h1 identity mismatch')
    if len(titles) != 1 or titles[0] != f'{expected} [Warhammer 40,000 {meta["Edition"]}th edition]':
        raise ValueError('Shared page title/edition identity mismatch')
    if page.get('SourceComposition'):
        return select_composition(adapter, snapshot, page, nodes, html[0], titles[0])
    starts = [(i, n) for i, n in enumerate(nodes) if n.tag == 'h2' and n.has('outline_header')
              and key(n.text()) == key(section + ' Codex Supplement')]
    if len(starts) != 1:
        raise ValueError(f'Expected one shared subsection heading for {section}; found {len(starts)}')
    start, heading = starts[0]
    container, chapter = chapter_container(heading)
    if not heading.closed or not heading.attrs.get('id') or not container.closed:
        raise ValueError('Shared subsection heading/container is incomplete')
    # Stop at the next top-level non-detachment heading (another supplement or
    # game mode). Never assume the next supplement has any particular name.
    ends = [(i, n) for i, n in enumerate(nodes[start + 1:], start + 1)
            if n.tag == 'h2' and n.has('outline_header') and not any(c.has('dpPts') for c in n.nodes())]
    if not ends: raise ValueError('Shared subsection lacks its closing heading boundary')
    end, boundary = ends[0]
    if not boundary.closed: raise ValueError('Shared subsection closing boundary is incomplete')
    detachments = []
    for n in nodes[start + 1:end]:
        if n.tag != 'h2' or not n.has('outline_header') or not any(c.has('dpPts') for c in n.nodes()): continue
        parent, marker = chapter_container(n)
        if marker != chapter:
            raise ValueError('Detachment chapter filter conflicts with subsection: ' + clean(n.text()))
        if not parent.closed: raise ValueError('Unclosed shared detachment container')
        detachments.append({'Name': clean(adapter.operative_text(n, skip_points=True)),
                            'Anchor': n.attrs.get('id', ''), 'ChapterFilter': marker})
    if not detachments: raise ValueError('No detachment headings in requested shared subsection')
    for n in nodes[start:end]:
        if n.has('str11Wrap') and chapter_container(n)[1] != chapter:
            raise ValueError('Stratagem chapter filter conflicts with requested subsection')
    # Original closing boundary lets the parser bound the final stratagem section;
    # including the original html node preserves its complete-document evidence.
    selected = [html[0], *nodes[start:end + 1]]
    evidence = {'SubsectionFound': True, 'SourcePageFaction': headings[0], 'SourceFactionSection': section,
                'PageIdentity': 'Success', 'Title': titles[0], 'StartAnchor': heading.attrs['id'],
                'EndAnchor': boundary.attrs.get('id', ''), 'ChapterFilter': chapter,
                'Detachments': detachments, 'SectionParserVersion': '1.0'}
    return {**snapshot, 'SectionNodes': selected}, evidence


def select_composition(adapter, snapshot, page, nodes, html, title):
    """Compose only source-declared applicable detachments, in source order.

    Read the site's JSON bit table as data (never execute JavaScript). A chapter
    bit selects both generic containers and its own exclusive containers. This
    also respects source exclusions from otherwise-generic detachments.
    """
    config = page['SourceComposition']
    group, option = config['FilterGroup'], config['FilterOption']
    text = adapter.decode(snapshot)
    declarations = list(re.finditer(r'window\._wahaFBits\s*=\s*', text))
    if len(declarations) != 1: raise ValueError('Expected one source filter bit declaration')
    bits, _ = json.JSONDecoder().raw_decode(text[declarations[0].end():])
    bit_index = bits[group][option]
    if not isinstance(bit_index, int) or bit_index < 0: raise ValueError('Invalid source filter bit')
    selectors = [n for n in nodes if n.tag == 'select' and n.has('FilterSelect' + group)]
    options = [n for selector in selectors for n in selector.nodes() if n.tag == 'option' and n.attrs.get('value') == option]
    if len(options) != 1 or key(options[0].text()) != key(config['FilterLabel']):
        raise ValueError('Source filter option identity mismatch')
    main = [n for n in nodes if n.attrs.get('id') == 'wahMainContent']
    if len(main) != 1 or not main[0].closed: raise ValueError('Missing/ambiguous shared main content')
    content = list(main[0].nodes())
    end = next((i for i, n in enumerate(content) if n.tag == 'h2' and key(n.text()) in ('crusaderules', 'boardingactions', 'combatpatrol')), None)
    if end is None or not content[end].closed: raise ValueError('Shared detachment content lacks closing game-mode boundary')
    headings = [i for i, n in enumerate(content[:end + 1]) if n.tag == 'h2' and n.has('outline_header')]
    selected, detachments, excluded = [html], [], []
    seen = set()
    for i, j in zip(headings, headings[1:]):
        heading = content[i]
        if not any(n.has('dpPts') for n in heading.nodes()): continue
        parent, marker = chapter_container(heading)
        name = clean(adapter.operative_text(heading, skip_points=True))
        applicable = bool(int(marker, 16) & (1 << bit_index))
        if not applicable:
            excluded.append(name)
            continue
        if not parent.closed or not content[j].closed or not heading.closed or not heading.attrs.get('id'):
            raise ValueError('Incomplete applicable detachment container: ' + name)
        if key(name) in seen: raise ValueError('Duplicate applicable detachment: ' + name)
        seen.add(key(name))
        for n in content[i:j]:
            if n.has('str11Wrap') and not int(chapter_container(n)[1], 16) & (1 << bit_index):
                raise ValueError('Card filter conflicts with applicable detachment: ' + name)
        selected.extend(content[i:j])
        detachments.append({'Name': name, 'Anchor': heading.attrs['id'], 'ChapterFilter': marker})
    if not detachments: raise ValueError('Source composition selected no detachments')
    selected.append(content[end])
    evidence = {'SubsectionFound': True, 'SourcePageFaction': page['SourcePageFaction'],
                'SourceFactionSection': page['SourceFactionSection'], 'PageIdentity': 'Success', 'Title': title,
                'SectionParserVersion': VERSION, 'SourceComposition': config, 'FilterBit': bit_index,
                'Detachments': detachments, 'ExcludedDetachments': excluded}
    return {**snapshot, 'SectionNodes': selected, 'SectionParserVersion': VERSION}, evidence


def parse_section(adapter, snapshot, section):
    meta = {**snapshot['Metadata'], 'SourceFaction': section}
    parsed = adapter.parse_faction_html(snapshot['SectionNodes'], meta)
    parsed.faction_id = key(section)
    selector_version = snapshot.get('SectionParserVersion', '1.0')
    parsed.version = (parsed.version or meta['ContentHash']) + f' | subsection=wahapedia/{selector_version}; section={section}'
    for record in parsed.records:
        record.faction_id = parsed.faction_id
        record.detachment_id = record.detachment_id or key(record.detachment)
    return parsed
