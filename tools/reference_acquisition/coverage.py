"""Coverage evidence layered over the unchanged source-format parser."""
from .adapters import HtmlTree, WahapediaReferenceAdapter
from .models import clean, key


def confirmed_empty_detachments(snapshot, parsed):
    """A bounded detachment in a complete HTML page may legitimately have no cards.

    Require the parser's explicit no-section result plus its actual DOM boundary,
    detachment rules and enhancements. Missing/truncated/unrecognized inputs cannot
    authorize absence-based removal. This does not infer any missing rule text.
    """
    if parsed.errors or 'html' not in snapshot['Metadata']['MediaType']: return set()
    eligible = {key(d['Name']) for d in parsed.detachments if d.get('Reason') == 'No stratagem section in this detachment'}
    if not eligible: return set()
    nodes = snapshot.get('SectionNodes')
    if nodes is None: nodes = list(HtmlTree(WahapediaReferenceAdapter.decode(snapshot)).root.nodes())
    if not any(n.tag == 'html' and n.closed for n in nodes): return set()
    confirmed, current, headings, has_cards = set(), '', set(), False
    for node in nodes:
        if node.tag == 'h2' and node.has('outline_header'):
            if current in eligible and not has_cards and 'enhancements' in headings and headings & {'detachmentrule', 'detachmentrules'}:
                confirmed.add(current)
            current, headings, has_cards = '', set(), False
            if clean(node.text()).casefold() in ('crusade rules', 'boarding actions', 'combat patrol'): break
            if node.closed and node.attrs.get('id') and any(n.has('dpPts') for n in node.nodes()):
                current = key(WahapediaReferenceAdapter.operative_text(node, skip_points=True))
        elif current:
            if node.tag == 'h2':
                headings.add(key(node.text()))
                if key(node.text()) in ('stratagem', 'stratagems'): has_cards = True
            if node.has('str11Wrap', 'str11Name'): has_cards = True
    return confirmed
