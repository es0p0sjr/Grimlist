"""Shared URI acquisition regressions; synthetic metadata, no external HTTP."""
import contextlib
import copy
import io
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from .cli import parser, run
from .http import AcquisitionError
from .models import read, write
from .snapshots import SnapshotStore
from .test_acquisition import universe
from .adapters import WahapediaReferenceAdapter
from .coverage import confirmed_empty_detachments
from .matching import Universe, Matcher
from .models import digest, key
from .pack import build
from .sections import select_section, parse_section

ROOT = Path(__file__).resolve().parents[2]


class RealSharedSectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        fixture = read(Path(__file__).parent / 'fixtures/space-marines.snapshot.json')
        cls.snapshot = SnapshotStore(ROOT / 'tests/fixtures/wahapedia/snapshots').load(fixture['Snapshot'])
        if digest(cls.snapshot['Raw']) != fixture['Hash']: raise AssertionError('Shared fixture changed')
        cls.adapter = WahapediaReferenceAdapter()
        cls.page = next(r for r in read(ROOT / 'tests/fixtures/references/11e/reference-faction-source-manifest.json')['Factions'] if r['Name'] == 'Black Templars')
        # Preserve the accepted supplement-only selector's separate regressions.
        cls.page.pop('SourceComposition', None)
        cls.universe = Universe.load(ROOT / 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json', ROOT / 'tests/fixtures/references/11e/detachment-universe.json')

    def parse(self, snapshot=None, section='Black Templars'):
        selected, evidence = select_section(self.adapter, snapshot or self.snapshot, {**self.page, 'SourceFactionSection': section})
        return selected, evidence, parse_section(self.adapter, selected, section)

    def test_black_templars_real_scope_and_zero_card_coverage(self):
        selected, evidence, parsed = self.parse()
        self.assertEqual('Blood-Angels-Codex-Supplement', evidence['EndAnchor'])
        self.assertEqual([], parsed.errors)
        expected = {'Companions of Vehemence', 'Vindication Task Force', 'Godhammer Assault Force',
                    'Marshal\u2019s Household', 'The Living Miracle', 'Wrathful Procession'}
        self.assertEqual(expected, {d['Name'] for d in parsed.detachments})
        self.assertEqual(24, len(parsed.records))
        self.assertEqual({'Black Templars'}, {r.faction for r in parsed.records})
        self.assertEqual({key('The Living Miracle')}, confirmed_empty_detachments(selected, parsed))
        pack, matches, issues = build(self.universe, Matcher(self.universe, 'wahapedia'), [(selected, parsed)])
        self.assertEqual([], issues)
        self.assertEqual(24, len(pack['ReferenceCards']))
        self.assertTrue(all(m['Resolved'] for m in matches))
        self.assertEqual(21, sum(m['DetachmentMatch'] == 'ExactName' for m in matches))
        self.assertEqual(3, sum(m['DetachmentMatch'] == 'NormalizedName' for m in matches))
        self.assertEqual(6, sum(c['State'] == 'Complete' for c in pack['ReferenceCoverage']))
        self.assertEqual(self.snapshot['Raw'], selected['Raw'])
        self.assertEqual(self.snapshot['Metadata'], selected['Metadata'])

    def test_no_cards_from_any_other_section_and_card_semantics_unchanged(self):
        _, _, selected = self.parse()
        whole = self.adapter.parse(self.snapshot)
        allowed = {d['Name'] for d in selected.detachments}
        original = {(r.detachment, r.name): r for r in whole.records if r.detachment in allowed}
        excluded = {(r.detachment, r.name) for r in whole.records if r.detachment not in allowed}
        self.assertTrue(excluded)
        self.assertEqual(set(original), {(r.detachment, r.name) for r in selected.records})
        self.assertFalse(excluded & {(r.detachment, r.name) for r in selected.records})
        for record in selected.records:
            for field in ('name', 'cost', 'timing', 'target', 'effect', 'restrictions', 'text', 'location'):
                self.assertEqual(getattr(original[(record.detachment, record.name)], field), getattr(record, field))

    def test_same_selector_works_for_another_supplement_without_branches(self):
        _, _, templars = self.parse()
        _, evidence, angels = self.parse(section='Blood Angels')
        self.assertEqual([], angels.errors)
        self.assertEqual('Dark-Angels-Codex-Supplement', evidence['EndAnchor'])
        self.assertTrue(angels.records)
        self.assertEqual({'Blood Angels'}, {r.faction for r in angels.records})
        self.assertFalse({r.source_id for r in templars.records} & {r.source_id for r in angels.records})

    def test_missing_or_duplicate_section_fails_closed(self):
        with self.assertRaisesRegex(ValueError, 'found 0'): self.parse(section='Missing Chapter')
        snapshot = copy.deepcopy(self.snapshot)
        snapshot['Raw'] = snapshot['Raw'].replace(b'Blood Angels Codex Supplement</h2>', b'Black Templars Codex Supplement</h2>')
        with self.assertRaisesRegex(ValueError, 'found 2'): self.parse(snapshot)

    def test_selector_has_no_dependency_on_known_faction_names(self):
        snapshot = {**self.snapshot, 'Raw': self.snapshot['Raw'].replace(b'Black Templars Codex Supplement', b'Example Chapter Codex Supplement')}
        _, _, parsed = self.parse(snapshot, section='Example Chapter')
        self.assertEqual([], parsed.errors)
        self.assertEqual(24, len(parsed.records))
        self.assertEqual({'Example Chapter'}, {r.faction for r in parsed.records})

    def test_wrong_identity_edition_truncation_and_chapter_filter_rejected(self):
        for before, after, message in [
            (b'<span class="page_header_span">Space Marines</span>', b'<span class="page_header_span">Wrong Faction</span>', 'identity mismatch'),
            (b'[Warhammer 40,000 11th edition]</title>', b'[Warhammer 40,000 10th edition]</title>', 'edition identity'),
            (b'</html>', b'', 'truncated'),
            (b'CH:9,AA:4001', b'CH:3,AA:4001', 'filter conflicts'),
        ]:
            with self.subTest(message=message):
                self.assertTrue(before in self.snapshot['Raw'], 'Mutation marker missing: ' + str(before))
                snapshot = {**self.snapshot, 'Raw': self.snapshot['Raw'].replace(before, after)}
                with self.assertRaisesRegex(ValueError, message): self.parse(snapshot)

    def test_offline_cli_cache_separates_subsections_and_unscoped_parse(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            store = SnapshotStore(root / 'cache')
            snapshot = store.save('wahapedia', '11', self.page['SourceUri'], self.snapshot['Raw'], faction='Space Marines')
            # Deliberately prime the old unscoped cache; neither chapter may use it.
            old_cache = store.root / 'parsed' / (digest(snapshot['Path'] + '|' + self.adapter.version) + '.json')
            write(old_cache, self.adapter.parse(snapshot).json())
            manifest = {'Edition': '11', 'Adapter': 'wahapedia', 'Factions': [
                {**self.page, 'Name': name, 'SourceFactionSection': name, 'CanonicalFactionId': ''}
                for name in ('Black Templars', 'Blood Angels')]}
            write(root / 'manifest.json', manifest)
            args = parser().parse_args(['parse', '--offline', '--dataset', str(ROOT / 'tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json'),
                '--universe', str(ROOT / 'tests/fixtures/references/11e/detachment-universe.json'), '--cache', str(store.root),
                '--output', str(root / 'out'), '--source-manifest', str(root / 'manifest.json'),
                '--faction', 'Black Templars', '--faction', 'Blood Angels'])
            with patch('tools.reference_acquisition.cli.Fetcher.acquire', side_effect=AssertionError('HTTP forbidden')):
                with contextlib.redirect_stdout(io.StringIO()): run(args)
                first = read(root / 'out/reference-acquisition-report.json')
                with contextlib.redirect_stdout(io.StringIO()): run(args)
            second = read(root / 'out/reference-acquisition-report.json')
            self.assertEqual(['new', 'new'], [r['ParseCache'] for r in first['Sources']])
            self.assertEqual(['reused', 'reused'], [r['ParseCache'] for r in second['Sources']])
            self.assertEqual(3, len(list((store.root / 'parsed').glob('*.json'))))
            rows = {r['CanonicalFaction']: r for r in second['Sources']}
            self.assertEqual(24, rows['Black Templars']['Cards'])
            self.assertNotEqual(rows['Black Templars']['Detachments'], rows['Blood Angels']['Detachments'])
            # An erroneous manifest mapping may not publish a different faction.
            manifest['Factions'] = [{**manifest['Factions'][0], 'SourceFactionSection': 'Blood Angels'}]
            write(root / 'manifest.json', manifest)
            with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(2, run(args))
            report = read(root / 'out/reference-acquisition-report.json')
            self.assertEqual(0, report['Totals']['CardsGenerated'])
            self.assertTrue(any('requested canonical faction' in e for r in report['Sources'] for e in r['Errors']))


class SharedSourceAcquisitionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        canonical, audit = universe()
        write(self.root / 'dataset.json', canonical.dataset)
        write(self.root / 'universe.json', audit)
        self.uri = 'https://example.org/wh40k11ed/factions/shared/'
        self.manifest = {'Edition': '11', 'Adapter': 'wahapedia', 'Factions': [
            {'Name': name, 'CanonicalFactionId': 'id-' + name, 'SourceUri': self.uri,
             'SourcePageFaction': 'Shared', 'SourceFactionSection': name, 'Enabled': True}
            for name in ('Alpha', 'Beta')]}
        write(self.root / 'manifest.json', self.manifest)
        self.store = SnapshotStore(self.root / 'cache')
        self.snapshot = self.store.save('wahapedia', '11', self.uri, b'<html></html>', faction='Shared')

    def args(self, *extra):
        return parser().parse_args(['acquire', '--dataset', str(self.root / 'dataset.json'),
            '--universe', str(self.root / 'universe.json'), '--source-manifest', str(self.root / 'manifest.json'),
            '--cache', str(self.store.root), '--output', str(self.root / 'out'), *extra])

    def execute(self, *extra):
        with contextlib.redirect_stdout(io.StringIO()): return run(self.args(*extra))

    def test_cached_shared_uri_keeps_both_canonical_manifest_rows(self):
        self.assertEqual(0, self.execute('--offline'))
        report = read(self.root / 'out/reference-acquisition-report.json')
        self.assertEqual({'Alpha', 'Beta'}, {r['CanonicalFaction'] for r in report['Sources']})
        self.assertEqual(2, len(read(self.root / 'out/reference-source-manifest.json')['Factions']))

    def test_download_once_then_reuse_snapshot_for_second_faction(self):
        with patch('tools.reference_acquisition.cli.Fetcher.acquire', return_value=(self.snapshot, 'acquired')) as fetch:
            self.assertEqual(0, self.execute())
        self.assertEqual(1, fetch.call_count)
        self.assertEqual('Shared', fetch.call_args.args[-1])
        rows = read(self.root / 'out/reference-acquisition-report.json')['Sources']
        self.assertEqual(['Downloaded', 'Cached'], [r['Acquisition'] for r in rows])
        self.assertEqual(rows[0]['Snapshot'], rows[1]['Snapshot'])

    def test_shared_failure_not_retried_for_each_canonical_faction(self):
        with patch('tools.reference_acquisition.cli.Fetcher.acquire', side_effect=AcquisitionError('test failure', 'NotFound', self.uri, 404)) as fetch:
            self.assertEqual(2, self.execute())
        self.assertEqual(1, fetch.call_count)
        rows = read(self.root / 'out/reference-acquisition-report.json')['Sources']
        self.assertEqual(['NotFound', 'NotFound'], [r['Result'] for r in rows])

    def test_single_canonical_filter_does_not_select_other_shared_users(self):
        self.assertEqual(0, self.execute('--offline', '--faction', 'Beta'))
        rows = read(self.root / 'out/reference-acquisition-report.json')['Sources']
        self.assertEqual(['Beta'], [r['CanonicalFaction'] for r in rows])

    def test_disabled_shared_user_stays_disabled(self):
        self.manifest['Factions'][0]['Enabled'] = False
        write(self.root / 'manifest.json', self.manifest)
        self.assertEqual(0, self.execute('--offline'))
        rows = read(self.root / 'out/reference-acquisition-report.json')['Sources']
        self.assertEqual(['Beta'], [r['CanonicalFaction'] for r in rows])

    def test_unvalidated_shared_scope_cannot_parse_entire_page(self):
        args = self.args('--offline')
        args.command = 'parse'
        with patch('tools.reference_acquisition.cli.ADAPTERS') as adapters:
            from .adapters import WahapediaReferenceAdapter
            adapter = WahapediaReferenceAdapter()
            adapters.__getitem__.return_value = adapter
            with patch.object(adapter, 'parse', side_effect=AssertionError('Unscoped parse')):
                with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(2, run(args))
        rows = read(self.root / 'out/reference-acquisition-report.json')['Sources']
        self.assertTrue(all('identity mismatch' in r['Errors'][0] for r in rows))


if __name__ == '__main__': unittest.main()
