"""Source-format adapters. No canonical faction names or special faction branches."""
from html.parser import HTMLParser
import html
import json
import re
from typing import Protocol
from urllib.parse import urljoin, urlparse
from .models import ParsedDocument, ParsedReferenceRecord, clean, digest, key, labels, validate


class ReferenceAcquisitionAdapter(Protocol):
    name: str
    version: str
    def index_uri(self, edition: str) -> str: ...
    def discover(self, snapshot: dict) -> list[dict]: ...
    def parse(self, snapshot: dict) -> ParsedDocument: ...


class Node:
    def __init__(self, tag='', attrs=None, parent=None):
        self.tag, self.attrs, self.parent, self.children = tag, dict(attrs or []), parent, []
        self.closed = False

    def nodes(self):
        yield self
        for child in self.children:
            if isinstance(child, Node): yield from child.nodes()

    def has(self, *classes):
        return bool(set(self.attrs.get('class', '').split()) & set(classes))

    def text(self):
        if self.tag in ('script', 'style', 'noscript'): return ''
        if self.tag == 'br': return '\n'
        value = ''.join(c.text() if isinstance(c, Node) else c for c in self.children)
        return ('\n' + value + '\n') if self.tag in ('p', 'div', 'li', 'h1', 'h2', 'h3', 'h4', 'section', 'article', 'tr') else value


class HtmlTree(HTMLParser):
    void = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr'}
    def __init__(self, text):
        super().__init__(convert_charrefs=True)
        self.root = Node('root')
        self.current = self.root
        self.feed(text)
        self.close()

    def handle_starttag(self, tag, attrs):
        node = Node(tag, attrs, self.current)
        self.current.children.append(node)
        if tag not in self.void: self.current = node

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in self.void: self.handle_endtag(tag)

    def handle_endtag(self, tag):
        node = self.current
        while node.parent:
            if node.tag == tag:
                node.closed = True
                self.current = node.parent
                return
            node = node.parent

    def handle_data(self, data):
        self.current.children.append(data)


class WahapediaReferenceAdapter:
    name, version = 'wahapedia', '1.5'

    def __init__(self, scope_aliases=None):
        self.scope_aliases = scope_aliases or {}

    def index_uri(self, edition):
        return f'https://wahapedia.ru/wh40k{edition}ed/'

    @staticmethod
    def decode(snapshot):
        raw = snapshot['Raw']
        # Never replace undecodable bytes and silently corrupt rules.
        return raw.decode('utf-8-sig')

    def discover(self, snapshot):
        meta = snapshot['Metadata']
        edition, base = meta['Edition'], meta['SourceUri']
        tree = HtmlTree(self.decode(snapshot)).root
        pages = {}
        for node in tree.nodes():
            if node.tag != 'a' or not node.attrs.get('href'): continue
            uri = urljoin(base, node.attrs['href'])
            parsed = urlparse(uri)
            match = re.fullmatch(r'/wh40k' + re.escape(edition) + r'ed/factions/([^/]+)/?', parsed.path)
            if not match or parsed.netloc != urlparse(base).netloc or parsed.fragment: continue
            uri = parsed._replace(query='', fragment='').geturl().rstrip('/') + '/'
            name = clean(node.text())
            if not name or name.lower() in ('home', 'factions'): continue
            pages[uri] = {'SourceFactionId': match[1], 'Name': name, 'SourceUri': uri, 'Detachments': []}
        # XML sitemaps are another discovery input, not an alternate identity universe.
        for location in re.findall(r'<loc>\s*([^<]+)\s*</loc>', self.decode(snapshot)):
            uri = html.unescape(location.strip())
            parsed = urlparse(uri)
            match = re.fullmatch(r'/wh40k' + re.escape(edition) + r'ed/factions/([^/]+)/?', parsed.path)
            if match and parsed.netloc == urlparse(base).netloc:
                pages.setdefault(uri, {'SourceFactionId': match[1], 'Name': match[1].replace('-', ' '), 'SourceUri': uri, 'Detachments': []})
        return sorted(pages.values(), key=lambda row: row['SourceUri'])

    def parse(self, snapshot):
        meta = snapshot['Metadata']
        text = self.decode(snapshot)
        uri_edition = re.search(r'/wh40k(\d+)ed/', meta['SourceUri'])
        if uri_edition and uri_edition[1] != meta['Edition']:
            raise ValueError('Source URI edition conflicts with snapshot metadata')
        if not meta['Edition']: raise ValueError('Snapshot edition is required')
        parsed = self.parse_html(text, meta) if 'html' in meta['MediaType'] else self.parse_text(text, meta)
        parsed.faction_id = urlparse(meta['SourceUri']).path.rstrip('/').split('/')[-1]
        for record in parsed.records:
            record.faction_id = record.faction_id or parsed.faction_id
            record.detachment_id = record.detachment_id or key(record.detachment)
        return parsed

    def parse_text(self, text, meta):
        """Saved rendered text: card type lines and labels provide structural anchors.

        Text is always Partial: it cannot prove a page/section was saved completely.
        Errata/FAQ disclosures are excluded, not guessed or applied a second time.
        """
        lines = [line.strip() for line in text.splitlines()]
        # Faction pages may also embed other game modes/editions. They are not
        # evidence for the canonical matched-play universe requested here.
        for index, line in enumerate(lines):
            if re.fullmatch(r'##\s+(Boarding Actions|Crusade Rules|Combat Patrol)', line, re.I):
                lines = lines[:index]
                break
        faction = meta.get('SourceFaction', '')
        if not faction:
            faction = next((line[2:].strip() for line in lines if line.startswith('# ')), '')
        result = ParsedDocument(faction, meta['Edition'], version=meta.get('SourceRevision', ''), date=meta.get('SourceDate', ''))
        starts = []
        for i, line in enumerate(lines):
            match = re.fullmatch(r'(.+?)\s+[–—-]\s+.+?\bStratagem\s*', line, re.I) or re.fullmatch(r'(.+?)\s+Stratagem\s*', line)
            if not match: continue
            previous = [(j, lines[j]) for j in range(i) if lines[j]]
            cp = previous[-1] if previous and re.fullmatch(r'\d+\s*CP', previous[-1][1], re.I) else None
            if not cp:
                result.errors.append(f'line {i+1}: stratagem header has no recognizable CP marker')
                continue
            names = [(j, value) for j, value in previous if j < cp[0]]
            if not names:
                result.errors.append(f'line {i+1}: stratagem header has no name')
                continue
            name_line, name = names[-1]
            starts.append((name_line, i, match[1], name, re.sub(r'\s*CP$', ' CP', cp[1], flags=re.I)))
        for n, (start, header, detachment, name, cost) in enumerate(starts):
            end = starts[n+1][0] if n+1 < len(starts) else len(lines)
            body = []
            for line in lines[header+1:end]:
                if line.startswith('#'): break
                body.append(line)
            fields, canonical_text = labels('\n'.join(body))
            record = ParsedReferenceRecord(digest(detachment + '|' + name), faction, detachment, name, cost,
                fields['WHEN'], fields['TARGET'], fields['EFFECT'], fields['RESTRICTIONS'], f'line:{start+1}', text=canonical_text)
            errors = validate(record)
            if errors: result.errors.append(f'{name}: ' + '; '.join(errors))
            else: result.records.append(record)
        result.detachments = [{'Name': name, 'SourceDetachmentId': key(name), 'Complete': False,
                               'Reason': 'Rendered text cannot prove complete section boundaries'}
                              for name in sorted({r.detachment for r in result.records})]
        if not starts: result.errors.append('No recognized stratagem card headers in text')
        self.check_duplicates(result)
        return result

    def parse_html(self, text, meta):
        tree = HtmlTree(text).root
        nodes = list(tree.nodes())
        if any(n.has('str11Wrap') for n in nodes):
            return self.parse_faction_html(nodes, meta)
        for index, node in enumerate(nodes):
            if node.tag in ('h1', 'h2') and clean(node.text()).lower() in ('boarding actions', 'crusade rules', 'combat patrol'):
                nodes = nodes[:index]
                break
        faction = next((clean(n.text()) for n in nodes if n.tag == 'h1'), meta.get('SourceFaction', ''))
        result = ParsedDocument(faction, meta['Edition'], version=meta.get('SourceRevision', ''), date=meta.get('SourceDate', ''))
        # Recognize semantic card components, never an ordinal div or card count.
        titles = [n for n in nodes if n.has('stratName', 'stratagem-name') or n.attrs.get('data-reference-field') == 'name']
        scopes = {}
        for title in titles:
            container = title.parent
            while container and container.parent:
                descendants = list(container.nodes())
                if container.has('strat10', 'stratagem-card') or container.attrs.get('data-reference-card') == 'stratagem': break
                if any(n.has('stratCP', 'stratagem-cost') or n.attrs.get('data-reference-field') == 'cost' for n in descendants): break
                container = container.parent
            if not container or len([n for n in container.nodes() if n in titles]) != 1:
                result.errors.append('Ambiguous stratagem container for ' + clean(title.text()))
                continue
            children = list(container.nodes())
            def field(label, *classes):
                return next((clean(n.text()) for n in children if n.has(*classes) or n.attrs.get('data-reference-field') == label), '')
            name = clean(title.text())
            cost_raw = field('cost', 'stratCP', 'stratagem-cost')
            cost_match = re.fullmatch(r'(\d+)\s*CP', cost_raw, re.I)
            scope_text = field('scope', 'stratFaction', 'stratagem-type', 'stratType')
            scope_match = re.fullmatch(r'(.+?)\s+[–—-]\s+.+?\bStratagem', scope_text, re.I) or re.fullmatch(r'(.+?)\s+Stratagem', scope_text)
            detachment = container.attrs.get('data-detachment', '') or (scope_match[1] if scope_match else '')
            if not detachment:
                result.errors.append(name + ': unresolved source detachment marker')
                continue
            bodies = [n for n in children if n.has('stratText', 'stratagem-text') or n.attrs.get('data-reference-field') == 'body']
            body = bodies[0].text() if len(bodies) == 1 else container.text()
            # Do not merge hidden FAQ/errata disclosures into operative rule fields.
            if any(n.has('faq', 'errata', 'tooltip') for n in children):
                result.errors.append(name + ': nested FAQ/errata requires unambiguous operative body')
                continue
            fields, canonical_text = labels(body)
            anchor = container.attrs.get('id', '') or title.attrs.get('id', '')
            record = ParsedReferenceRecord(anchor or digest(detachment + '|' + name), faction, detachment, name,
                cost_match[1] + ' CP' if cost_match else cost_raw, fields['WHEN'], fields['TARGET'],
                fields['EFFECT'], fields['RESTRICTIONS'], '#' + anchor if anchor else 'card:' + name, text=canonical_text)
            errors = validate(record)
            scopes.setdefault(detachment, []).append(not errors)
            if errors: result.errors.append(name + ': ' + '; '.join(errors))
            else: result.records.append(record)
        # Default is Partial. Only explicitly bounded section containers allow Complete.
        # A page containing recognizable cards alone is never a completeness claim.
        for detachment, success in scopes.items():
            sections = [n for n in nodes if n.attrs.get('data-detachment') == detachment
                        and (n.has('stratagems') or n.attrs.get('data-reference-section') == 'stratagems')]
            complete = len(sections) == 1 and all(success) and not result.errors and '</html>' in text.lower()
            if complete:
                section_titles = [n for n in sections[0].nodes() if n in titles]
                complete = len(section_titles) == len(success) and len(success) > 0
            result.detachments.append({'Name': detachment, 'SourceDetachmentId': key(detachment), 'Complete': complete,
                'Reason': 'Complete explicit bounded section parsed' if complete else 'No verified complete bounded section'})
        if not titles: result.errors.append('Unrecognized HTML stratagem structure; use a saved text snapshot or extend source-format adapter')
        self.check_duplicates(result)
        return result

    @staticmethod
    def operative_text(node, skip_points=False):
        """Keep inline keyword/tooltip labels, not tooltip payloads or FAQ disclosures."""
        if node.tag in ('script', 'style', 'noscript') or node.has(
                'faq', 'faqErrataStrat', 'faqErrataSpoiler', 'tooltip-content', 'tooltip_content'):
            return ''
        if node.attrs.get('id', '').startswith('tooltip_content') or skip_points and node.has('dpPts'):
            return ''
        if node.tag == 'br': return '\n'
        value = ''.join(WahapediaReferenceAdapter.operative_text(c, skip_points) if isinstance(c, Node) else c
                        for c in node.children)
        if node.tag == 'li':
            marker = '*'
            if node.parent and node.parent.tag == 'ol':
                number = int(node.parent.attrs.get('start', '1'))
                for sibling in node.parent.children:
                    if not isinstance(sibling, Node) or sibling.tag != 'li': continue
                    number = int(sibling.attrs.get('value', str(number)))
                    if sibling is node: break
                    number += 1
                marker = str(number) + '.'
            return '\n' + marker + ' ' + value.strip() + '\n'
        return '\n' + value + '\n' if node.tag in ('p', 'div', 'li', 'h1', 'h2', 'h3', 'section', 'article', 'tr') else value

    def parse_faction_html(self, nodes, meta):
        """Edition-11 faction DOM: DP detachment heading -> Stratagems -> str11Wrap.

        Headings delimit document-order sections; layout columns and malformed
        presentational table nesting do not establish rule applicability.
        """
        faction = next((clean(n.text()) for n in nodes if n.tag == 'h1'), meta.get('SourceFaction', ''))
        result = ParsedDocument(faction, meta['Edition'], version=meta.get('SourceRevision', ''), date=meta.get('SourceDate', ''))
        document_closed = any(n.tag == 'html' and n.closed for n in nodes)
        detachments, current, section = [], None, None
        top_heading, sub_heading = '', ''
        owned_titles = set()
        for node in nodes:
            if node.tag in ('h1', 'h2') and clean(node.text()).casefold() in ('boarding actions', 'crusade rules', 'combat patrol'):
                if section is not None: section['Bounded'] = True
                break
            if node.tag == 'h2':
                sub_heading = key(node.text())
                if section is not None: section['Bounded'] = True
                section = None
                if node.has('outline_header'):
                    top_heading = key(node.text())
                    current = None
                    if any(n.has('dpPts') for n in node.nodes()):
                        name = clean(self.operative_text(node, skip_points=True))
                        current = {'Name': name, 'SourceDetachmentId': key(name), 'Sections': [], 'Anchor': node.attrs.get('id', '')}
                        if any(key(d['Name']) == key(name) for d in detachments):
                            result.errors.append('Duplicate detachment heading: ' + name)
                        detachments.append(current)
                if clean(node.text()).casefold() in ('stratagem', 'stratagems'):
                    if current is None:
                        result.errors.append('Stratagem section has no detachment heading: ' + node.attrs.get('id', ''))
                    else:
                        section = {'Bounded': False, 'Cards': 0, 'Valid': True}
                        current['Sections'].append(section)
                continue
            if node.has('str11Name') and node not in owned_titles:
                result.errors.append('Stratagem title outside its card: ' + clean(node.text()))
                if section is not None: section['Valid'] = False
            if not node.has('str11Wrap'): continue
            children = list(node.nodes())
            owned_titles.update(n for n in children if n.has('str11Name'))
            # Movement reference panels reuse the card wrapper in army rules,
            # but explicitly have a movement background and no CP/type fields.
            if current is None and top_heading == 'armyrules' and node.closed and any(n.has('str11AMoveBg') for n in children) and not any(n.has('str11CP', 'str11Type') for n in children):
                result.warnings.append('Excluded army-rule movement panel: ' + next((clean(n.text()) for n in children if n.has('str11Name')), ''))
                continue
            # Some detachments grant an actual stratagem within their rules,
            # without a separate Stratagems h2. The same component and enclosing
            # detachment checks below still apply; the next h2 must bound it.
            if current is not None and section is None and sub_heading in ('detachmentrule', 'detachmentrules'):
                section = {'Bounded': False, 'Cards': 0, 'Valid': True, 'Kind': 'Embedded'}
                current['Sections'].append(section)
            if current is None or section is None:
                result.errors.append('Stratagem card outside a detachment Stratagems section')
                continue
            section['Cards'] += 1
            errors = []
            fields = {}
            for name, class_name in (('name', 'str11Name'), ('cost', 'str11CP'), ('scope', 'str11Type'), ('body', 'str11Text')):
                found = [n for n in children if n.has(class_name)]
                if len(found) != 1:
                    errors.append(f'expected one {class_name}, found {len(found)}')
                elif not found[0].closed:
                    errors.append('unclosed ' + class_name)
                else:
                    fields[name] = found[0]
            if not node.closed: errors.append('unclosed str11Wrap')
            if errors:
                result.errors.append(current['Name'] + ': ' + '; '.join(errors))
                section['Valid'] = False
                continue
            name = clean(fields['name'].text())
            cost = clean(fields['cost'].text())
            cost_match = re.fullmatch(r'(\d+)\s*CP', cost, re.I)
            scope = clean(fields['scope'].text())
            scope_match = re.fullmatch(r'(.+?)\s+[\u2013\u2014-]\s+.+?\bStratagem', scope, re.I) or re.fullmatch(r'(.+?)\s+Stratagem', scope, re.I)
            scope_aliases = self.scope_aliases.get(key(faction), {})
            scope_alias = scope_aliases.get(key(scope_match[1])) if scope_match else None
            if not scope_match or (key(scope_match[1]) != key(current['Name']) and key(scope_alias or '') != key(current['Name'])):
                errors.append('card detachment marker conflicts with enclosing detachment heading: ' + scope)
            if not cost_match: errors.append('unrecognized CP marker: ' + cost)
            values, canonical_text = labels(self.operative_text(fields['body']))
            anchor = fields['name'].attrs.get('id', '')
            record = ParsedReferenceRecord(anchor or digest(current['Name'] + '|' + name), faction, current['Name'], name,
                cost_match[1] + ' CP' if cost_match else cost, values['WHEN'], values['TARGET'], values['EFFECT'],
                values['RESTRICTIONS'], '#' + anchor if anchor else 'card:' + name, text=canonical_text)
            if scope_alias and scope_match:
                # Preserve the short source marker for the data-driven canonical
                # matcher, which records this as Alias rather than ExactName.
                record.detachment_id = key(scope_match[1])
            errors.extend(validate(record))
            if errors:
                result.errors.append(name + ': ' + '; '.join(errors))
                section['Valid'] = False
            else:
                result.records.append(record)
        for detachment in detachments:
            sections = detachment['Sections']
            complete = bool(document_closed and detachment['Anchor'] and len(sections) == 1 and
                            sections[0]['Bounded'] and sections[0]['Valid'] and sections[0]['Cards'])
            reason = ('Complete heading-bounded stratagem section parsed' if complete else
                      'No stratagem section in this detachment' if not sections else
                      'Stratagem section is empty, ambiguous, truncated or contains rejected cards')
            result.detachments.append({'Name': detachment['Name'], 'SourceDetachmentId': detachment['SourceDetachmentId'],
                                       'Complete': complete, 'Reason': reason})
            if not complete: result.warnings.append(detachment['Name'] + ': ' + reason)
        if not detachments: result.errors.append('No recognized detachment headings in faction HTML')
        self.check_duplicates(result)
        return result

    @staticmethod
    def check_duplicates(result):
        # HTML ids are locators, not globally unique card identities. Scope and
        # name provide a stable identity across anchor and rule-text revisions.
        # CP and ordered blocks distinguish identical repeats from conflicts;
        # neither is allowed to silently become a second card in the same scope.
        seen = {}
        for record in result.records:
            scope = (key(record.faction), key(record.detachment), key(record.category), key(record.name))
            record.source_id = 'semantic-' + digest(json.dumps(scope, ensure_ascii=False))
            content = (record.cost, clean(record.text), clean(record.timing), clean(record.target),
                       clean(record.effect), clean(record.restrictions))
            if scope in seen:
                kind = 'Duplicate' if seen[scope] == content else 'Conflicting'
                result.errors.append(f'{kind} semantic card in {record.faction} / {record.detachment}: {record.name}')
            else:
                seen[scope] = content
        if result.errors:
            for scope in result.detachments: scope['Complete'] = False


ADAPTERS = {'wahapedia': WahapediaReferenceAdapter()}
