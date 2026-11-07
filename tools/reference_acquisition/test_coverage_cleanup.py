"""Pinned source-format regressions for the offline full-faction cleanup."""
import copy
import unittest
from pathlib import Path
from unittest.mock import patch

from .adapters import WahapediaReferenceAdapter
from .coverage import confirmed_empty_detachments
from .matching import Matcher, Universe
from .models import read, key
from .pack import build
from .sections import select_section, parse_section
from .snapshots import SnapshotStore
from .test_real_html import modern_card, modern_page

ROOT = Path(__file__).resolve().parents[2]


class CoverageCleanupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.aliases = read(Path(__file__).with_name('reference-aliases.json'))
        cls.adapter = WahapediaReferenceAdapter(cls.aliases['sourceScopeAliases'])
        store = SnapshotStore(ROOT / 'tests/fixtures/wahapedia/snapshots')
        cls.snapshots = {}
        for slug, fixture in read(Path(__file__).parent / 'fixtures/cleanup-source-snapshots.json').items():
            snapshot = store.load(fixture['Snapshot'])
            if snapshot['Metadata']['ContentHash'] != fixture['Hash']: raise AssertionError('Pinned fixture changed')
            cls.snapshots[slug] = snapshot
        cls.universe = Universe.load(ROOT / 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json', ROOT / 'tests/fixtures/references/11e/detachment-universe.json')
        cls.matcher = Matcher(cls.universe, 'wahapedia', cls.aliases)
        cls.pages = [p for p in read(ROOT / 'tests/fixtures/references/11e/reference-faction-source-manifest.json')['Factions'] if p.get('SourceComposition')]

    def test_all_chapter_compositions_match_canonical_applicability_without_duplicates(self):
        self.assertEqual(12, len(self.pages))
        documents = []
        for page in self.pages:
            selected, evidence = select_section(self.adapter, self.snapshots['space-marines'], page)
            parsed = parse_section(self.adapter, selected, page['SourceFactionSection'])
            self.assertEqual([], parsed.errors, page['Name'])
            expected = {key(r['Detachment']) for r in self.universe.scopes if r['Faction'] == page['Name']}
            self.assertEqual(expected, {key(d['Name']) for d in parsed.detachments}, page['Name'])
            self.assertEqual(len(parsed.records), len({(r.detachment, r.name) for r in parsed.records}))
            self.assertIn('Gladius Task Force', {d['Name'] for d in parsed.detachments})
            self.assertEqual(self.snapshots['space-marines']['Raw'], selected['Raw'])
            if page['Name'] == 'Black Templars':
                self.assertEqual(99, len(parsed.records))
                self.assertNotIn('Librarius Conclave', {d['Name'] for d in parsed.detachments})
                self.assertNotIn('1st Company Task Force', {d['Name'] for d in parsed.detachments})
            documents.append((selected, parsed))
        pack, matches, issues = build(self.universe, self.matcher, documents)
        self.assertEqual([], issues)
        self.assertTrue(all(m['Resolved'] for m in matches))
        logical = {(c['Faction'], c['Detachment'], c['Name']) for c in pack['ReferenceCards']}
        self.assertEqual(len(pack['ReferenceCards']), len(logical))
        self.assertTrue(all(c['State'] == 'Complete' for c in pack['ReferenceCoverage']))

    def test_composition_option_and_truncated_boundaries_fail_closed(self):
        page = copy.deepcopy(self.pages[0])
        page['SourceComposition']['FilterLabel'] = 'Wrong chapter'
        with self.assertRaisesRegex(ValueError, 'identity mismatch'):
            select_section(self.adapter, self.snapshots['space-marines'], page)
        snapshot = {**self.snapshots['space-marines'], 'Raw': self.snapshots['space-marines']['Raw'].replace(b'</html>', b'')}
        with self.assertRaisesRegex(ValueError, 'truncated'):
            select_section(self.adapter, snapshot, self.pages[0])

    def test_orks_keeps_real_embedded_stratagem_and_excludes_movement_panels(self):
        snapshot = self.snapshots['orks']
        parsed = self.adapter.parse(snapshot)
        self.assertEqual([], parsed.errors)
        self.assertEqual(40, len(parsed.records))
        self.assertEqual(['VINDICTIVE ARTILLERISTS'], [r.name for r in parsed.records if r.detachment == 'Madcap Meks'])
        self.assertTrue(next(d for d in parsed.detachments if d['Name'] == 'Madcap Meks')['Complete'])
        self.assertEqual({'brutebosses', 'wurrband'}, confirmed_empty_detachments(snapshot, parsed))
        self.assertFalse({'PULSE JET MOVE', 'ASSAULT DISEMBARK MOVE'} & {r.name for r in parsed.records})
        self.assertEqual(2, sum(w.startswith('Excluded army-rule movement panel:') for w in parsed.warnings))
        pack, matches, issues = build(self.universe, self.matcher, [(snapshot, parsed)])
        self.assertEqual([], issues)
        self.assertEqual(15, sum(c['State'] == 'Complete' for c in pack['ReferenceCoverage']))

    def test_agents_and_daemons_accept_only_explicit_aliases(self):
        for slug, count, alias_count in [('imperial-agents', 30, 18), ('chaos-daemons', 46, 6)]:
            with self.subTest(source=slug):
                snapshot = self.snapshots[slug]
                parsed = self.adapter.parse(snapshot)
                self.assertEqual([], parsed.errors)
                self.assertEqual(count, len(parsed.records))
                pack, matches, issues = build(self.universe, self.matcher, [(snapshot, parsed)])
                self.assertEqual([], issues)
                self.assertEqual(alias_count, sum(m['DetachmentMatch'] == 'Alias' for m in matches))
                self.assertTrue(all(c['State'] == 'Complete' for c in pack['ReferenceCoverage']))
                self.assertTrue(WahapediaReferenceAdapter().parse(snapshot).errors)

    def test_unconfigured_scope_mismatch_is_still_rejected(self):
        html = modern_page(modern_card(scope='Unrelated Formation'))
        parsed = self.adapter.parse({'Raw': html.encode(), 'Metadata': {'Edition': '11', 'SourceUri': 'https://example.org/wh40k11ed/factions/example/', 'MediaType': 'text/html'}})
        self.assertTrue(any('conflicts' in e for e in parsed.errors))
        self.assertEqual([], parsed.records)

    def test_embedded_cards_require_matching_scope_and_closed_boundaries(self):
        html = modern_page().replace('<h2 id="Stratagems">Stratagems</h2>', '<h2>Detachment Rules</h2>')
        def parse(value):
            return self.adapter.parse({'Raw': value.encode(), 'Metadata': {'Edition': '11', 'SourceUri': 'https://example.org/wh40k11ed/factions/example/', 'MediaType': 'text/html'}})
        good = parse(html)
        self.assertEqual([], good.errors)
        self.assertTrue(good.detachments[0]['Complete'])
        bad = parse(html.replace('Mobile Reserve \u2013', 'Other Formation \u2013'))
        self.assertTrue(bad.errors)
        self.assertFalse(parse(html.replace('</html>', '')).detachments[0]['Complete'])


if __name__ == '__main__': unittest.main()
