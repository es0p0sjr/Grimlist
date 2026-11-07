import contextlib
import io
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from .adapters import WahapediaReferenceAdapter
from .cli import parser, run
from .coverage import confirmed_empty_detachments
from .discovery import faction_slug, plan_factions
from .matching import Universe, Matcher
from .models import key, read, write
from .pack import build
from .snapshots import SnapshotStore
from .test_real_html import ROOT, load_case


class CollectionPreparationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.universe = Universe.load(ROOT / 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json', ROOT / 'tests/fixtures/references/11e/detachment-universe.json')
        cls.catalogues = read(ROOT / 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/catalogues.json')
        cls.store = SnapshotStore(ROOT / 'tests/fixtures/wahapedia/snapshots')
        cls.adapter = WahapediaReferenceAdapter()

    def test_sanctified_orators_complete_with_zero_cards(self):
        snapshot = load_case('adepta-sororitas')
        parsed = self.adapter.parse(snapshot)
        pack, matches, issues = build(self.universe, Matcher(self.universe, 'wahapedia'), [(snapshot, parsed)])
        self.assertEqual([], issues)
        self.assertEqual(36, len(matches))
        coverage = next(c for c in pack['ReferenceCoverage'] if c['Detachment'] == 'Sanctified Orators')
        self.assertEqual(('Complete', True, 'CompleteForDetachment'), (coverage['State'], coverage['Authoritative'], coverage['Mode']))
        self.assertFalse(any(c['Detachment'] == 'Sanctified Orators' for c in pack['ReferenceCards']))
        self.assertTrue(any(d['Name'] == 'Sanctified Orators' for d in pack['Detachments']))
        self.assertTrue(all('parser=wahapedia/' + self.adapter.version in c['Source']['Version'] for c in pack['ReferenceCoverage']))

    def test_empty_declaration_requires_complete_bounded_html(self):
        snapshot = load_case('adepta-sororitas')
        original = snapshot['Raw']
        for raw in (original.replace(b'</html>', b''), original[:original.index(b'<h2 id="Crusade-Rules"')] + b'</body></html>'):
            snapshot['Raw'] = raw
            parsed = self.adapter.parse(snapshot)
            self.assertEqual(set(), confirmed_empty_detachments(snapshot, parsed))

    def test_text_or_failed_parser_cannot_declare_empty_complete(self):
        snapshot = load_case('adepta-sororitas')
        parsed = self.adapter.parse(snapshot)
        parsed.errors.append('Test: unknown source structure')
        self.assertEqual(set(), confirmed_empty_detachments(snapshot, parsed))
        parsed.errors.clear()
        snapshot['Metadata']['MediaType'] = 'text/plain'
        self.assertEqual(set(), confirmed_empty_detachments(snapshot, parsed))

    def test_full_manifest_has_36_ids_444_scopes_and_no_http(self):
        # Keep this assertion pinned to the three control fixtures, independent
        # of additional pages acquired into the developer's live source cache.
        with tempfile.TemporaryDirectory() as directory:
            pinned = SnapshotStore(directory)
            for slug in ('death-guard', 'adepta-sororitas', 'world-eaters'):
                snapshot = load_case(slug)
                meta = snapshot['Metadata']
                pinned.save('wahapedia', '11', meta['SourceUri'], snapshot['Raw'], faction=meta['SourceFaction'])
            with patch('urllib.request.OpenerDirector.open', side_effect=AssertionError('HTTP called')):
                manifest = plan_factions(self.universe, self.catalogues, pinned)
        self.assertEqual(36, manifest['Totals']['CanonicalFactions'])
        self.assertEqual(444, sum(r['CanonicalDetachments'] for r in manifest['Factions']))
        self.assertEqual(36, len({r['CanonicalFactionId'] for r in manifest['Factions']}))
        self.assertTrue(all(r['CanonicalFactionId'] for r in manifest['Factions']))
        self.assertEqual({'Chaos Titan Legions', 'Titan Legions'}, {r['Name'] for r in manifest['Factions'] if not r['Enabled']})
        self.assertEqual(3, manifest['Totals']['SourceUrlsDiscovered'])

    def test_generic_slug_and_explicit_mapping_data(self):
        self.assertEqual('t-au-empire', faction_slug('T\u2019au Empire'))
        self.assertEqual('emperor-s-children', faction_slug('Emperor\u2019s Children'))
        f = self.universe.factions[0]
        mappings = {'Edition': '11', 'Factions': {f['Id']: {'SourceUri': 'https://wahapedia.ru/wh40k11ed/factions/custom-source/'}}}
        manifest = plan_factions(self.universe, self.catalogues, self.store, mappings)
        row = next(r for r in manifest['Factions'] if r['CanonicalFactionId'] == f['Id'])
        self.assertEqual('custom-source', row['SourceSlug'])
        self.assertEqual('ManifestMapping', row['DiscoveryMethod'])

    def test_disabled_manifest_entry_stays_disabled_when_snapshot_is_cached(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = plan_factions(self.universe, self.catalogues, self.store)
            for row in manifest['Factions']: row['Enabled'] = False
            write(root / 'manifest.json', manifest)
            args = parser().parse_args(['update', '--dataset', str(ROOT / 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json'),
                '--universe', str(ROOT / 'tests/fixtures/references/11e/detachment-universe.json'), '--cache', str(self.store.root),
                '--source-manifest', str(root / 'manifest.json'), '--output', str(root / 'out'), '--dry-run'])
            captured = io.StringIO()
            with contextlib.redirect_stdout(captured): self.assertEqual(0, run(args))
            self.assertIn('"Pages": []', captured.getvalue())


if __name__ == '__main__':
    unittest.main()
