"""Real repeated anchors plus semantic duplicate/conflict regressions."""
import json
import unittest
from pathlib import Path
from unittest.mock import patch

from .adapters import HtmlTree, WahapediaReferenceAdapter
from .matching import Matcher, Universe
from .models import digest, read
from .pack import build
from .sections import select_section, parse_section
from .snapshots import SnapshotStore
from .test_real_html import modern_card, modern_page

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = Path(__file__).parent / 'fixtures'


class SemanticIdentityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.adapter = WahapediaReferenceAdapter()
        cls.store = SnapshotStore(ROOT / 'tests/fixtures/wahapedia/snapshots')
        fixture = read(FIXTURES / 'adeptus-custodes.snapshot.json')
        cls.snapshot = cls.store.load(fixture['Snapshot'])
        if digest(cls.snapshot['Raw']) != fixture['Hash']: raise AssertionError('Custodes fixture changed')

    def parse(self, html):
        return self.adapter.parse({'Raw': html.encode(), 'Metadata': {
            'Edition': '11', 'MediaType': 'text/html', 'SourceUri': 'https://example.org/wh40k11ed/factions/jade-owls/'}})

    def test_real_repeated_anchors_are_distinct_content_cards_in_distinct_detachments(self):
        parsed = self.adapter.parse(self.snapshot)
        self.assertEqual([], parsed.errors)
        self.assertEqual(45, len(parsed.records))
        self.assertEqual(45, len({r.source_id for r in parsed.records}))
        self.assertTrue(all(d['Complete'] for d in parsed.detachments))
        nodes = list(HtmlTree(self.adapter.decode(self.snapshot)).root.nodes())
        for anchor, scopes in {
            'FLAWLESS-CONSTRUCTION': {'Solar Spearhead', 'Might of the Moritoi'},
            'UNLEASH-THE-LIONS': {'Lions of the Emperor', 'Tharanatoi Hammerblow'},
        }.items():
            with self.subTest(anchor=anchor):
                titles = [n for n in nodes if n.attrs.get('id') == anchor]
                self.assertEqual(2, len(titles))
                self.assertTrue(all(n.has('str11Name') for n in titles))
                records = [r for r in parsed.records if r.location == '#' + anchor]
                self.assertEqual(scopes, {r.detachment for r in records})
                self.assertEqual(2, len({r.source_id for r in records}))
                self.assertEqual(2, len({r.text for r in records}))
        canonical = Universe.load(ROOT / 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json', ROOT / 'tests/fixtures/references/11e/detachment-universe.json')
        pack, matches, issues = build(canonical, Matcher(canonical, 'wahapedia'), [(self.snapshot, parsed)])
        self.assertEqual([], issues)
        self.assertEqual(45, len(pack['ReferenceCards']))
        self.assertTrue(all(m['Resolved'] for m in matches))
        self.assertEqual(9, sum(c['State'] == 'Complete' for c in pack['ReferenceCoverage']))

    def test_four_accepted_sources_have_identical_pre_1_4_semantics(self):
        controls = read(FIXTURES / 'parser-1.3-semantic-controls.json')
        manifest = read(ROOT / 'tests/fixtures/references/11e/reference-faction-source-manifest.json')
        with patch('urllib.request.OpenerDirector.open', side_effect=AssertionError('Network forbidden')):
            for slug, control in controls.items():
                with self.subTest(source=slug):
                    snapshot = self.store.load(control['Snapshot'])
                    self.assertEqual(control['SnapshotHash'], snapshot['Metadata']['ContentHash'])
                    if slug == 'black-templars':
                        page = next(p for p in manifest['Factions'] if p['Name'] == 'Black Templars')
                        page = {k: v for k, v in page.items() if k != 'SourceComposition'}
                        selected, _ = select_section(self.adapter, snapshot, page)
                        parsed = parse_section(self.adapter, selected, page['SourceFactionSection'])
                    else:
                        parsed = self.adapter.parse(snapshot)
                    doc = parsed.json()
                    for record in doc['records']: record.pop('source_id')
                    self.assertEqual(control['Cards'], len(parsed.records))
                    self.assertEqual(control['DocumentSemanticHash'], digest(json.dumps(doc, sort_keys=True, ensure_ascii=False)))

    def test_same_scope_same_card_different_anchors_is_still_duplicate(self):
        original = modern_card()
        duplicate = original.replace('id="TEST CARD"', 'id="OTHER-ANCHOR"')
        parsed = self.parse(modern_page(original + duplicate))
        self.assertTrue(any('Duplicate semantic card' in e for e in parsed.errors))
        self.assertEqual(1, len({r.source_id for r in parsed.records}))
        self.assertFalse(parsed.detachments[0]['Complete'])

    def test_same_scope_different_cp_or_ordered_blocks_is_conflict(self):
        body = '<b>WHEN:</b> First.<b>EFFECT:</b> One.<b>WHEN:</b> Second.<b>EFFECT:</b> Two.'
        reordered = '<b>WHEN:</b> Second.<b>EFFECT:</b> Two.<b>WHEN:</b> First.<b>EFFECT:</b> One.'
        original = modern_card(body=body)
        for other in (original.replace('>1CP<', '>2CP<'), modern_card(body=reordered)):
            parsed = self.parse(modern_page(original + other))
            self.assertTrue(any('Conflicting semantic card' in e for e in parsed.errors))
            self.assertFalse(parsed.detachments[0]['Complete'])
            self.assertEqual(1, len({r.source_id for r in parsed.records}))

    def test_shared_anchor_for_different_names_and_navigation_is_not_a_duplicate(self):
        first = modern_card()
        second = modern_card(name='SECOND CARD').replace('id="SECOND CARD"', 'id="TEST CARD"')
        parsed = self.parse(modern_page('<a id="TEST CARD" href="#TEST CARD">Navigation</a>' + first + second))
        self.assertEqual([], parsed.errors)
        self.assertEqual(2, len({r.source_id for r in parsed.records}))
        self.assertEqual({'#TEST CARD'}, {r.location for r in parsed.records})

    def test_anchor_and_content_edits_keep_stable_identity(self):
        first = self.parse(modern_page()).records[0]
        other = modern_card().replace('id="TEST CARD"', 'id="NEW-ANCHOR"').replace('>1CP<', '>2CP<').replace('Synthetic effect.', 'Updated effect.')
        second = self.parse(modern_page(other)).records[0]
        self.assertEqual(first.source_id, second.source_id)
        self.assertNotEqual(first.location, second.location)
        self.assertNotEqual(first.text, second.text)

    def test_same_card_in_different_source_subsections_has_distinct_identity(self):
        first = self.parse(modern_page()).records[0]
        second = self.parse(modern_page().replace('<h1>Jade Owls</h1>', '<h1>Amber Owls</h1>')).records[0]
        self.assertNotEqual(first.source_id, second.source_id)


if __name__ == '__main__': unittest.main()
