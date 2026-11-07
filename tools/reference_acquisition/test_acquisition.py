import copy
import contextlib
import io
import json
import pathlib
import tempfile
import unittest

from tools.reference_acquisition.adapters import WahapediaReferenceAdapter
from tools.reference_acquisition.cli import parser, run
from tools.reference_acquisition.http import AccessDenied, AcquisitionError, Fetcher
from tools.reference_acquisition.matching import Universe, Matcher
from tools.reference_acquisition.models import ParsedDocument, ParsedReferenceRecord, clean, key, labels, read, write
from tools.reference_acquisition.pack import build, diff
from tools.reference_acquisition.snapshots import SnapshotStore

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = pathlib.Path(__file__).parent / 'fixtures'


def universe():
    source = {'Name': 'BSData fixture', 'Edition': '11', 'Version': 'r1', 'ContentHash': 'hash1'}
    dataset = {'Source': source, 'Units': [{'Faction': 'Alpha'}, {'Faction': 'Beta'}],
               'Detachments': [{'Faction': f, 'Name': 'Shared Name', 'Cost': '2 DP', 'Source': source} for f in ('Alpha', 'Beta')]}
    audit = {'Edition': '11', 'Source': source, 'Canonical': [
        {'Edition': '11', 'Faction': f, 'FactionId': 'id-' + f, 'Detachment': 'Shared Name', 'DetachmentSourceId': 'det-' + f,
         'Selectable': True, 'IdentityResolved': True} for f in ('Alpha', 'Beta')]}
    return Universe(dataset, audit), audit


def card(faction='Alpha', detachment='Shared Name', name='TEST RULE', effect='Synthetic effect.', source_id='rule'):
    return ParsedReferenceRecord(source_id, faction, detachment, name, '1 CP', 'Any phase.', '', effect, '', '#rule',
                                 text='WHEN: Any phase.\nEFFECT: ' + effect)


def text_card(faction='Shared Name', name='TEST RULE', target='', effect='Synthetic effect.'):
    return f'{name}\n\n1CP\n\n{faction} – Battle Tactic Stratagem\n\nWHEN: Any phase.\n{target}EFFECT: {effect}\n'


def html_card(name='TEST RULE', target='', effect='Synthetic effect.'):
    return f'''<div class="strat10" id="rule"><div class="stratName">{name}</div>
    <div class="stratCP">1CP</div><div class="stratFaction">Shared Name – Battle Tactic Stratagem</div>
    <div class="stratText"><b>WHEN:</b> Any phase.<br>{target}<b>EFFECT:</b> {effect}</div></div>'''


class AcquisitionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.store = SnapshotStore(self.root / 'cache')
        self.adapter = WahapediaReferenceAdapter()
        self.universe, self.audit = universe()
        self.matcher = Matcher(self.universe, 'wahapedia')

    def snapshot(self, raw=None, faction='Alpha', uri='https://example.org/wh40k11ed/factions/alpha/', media='text/plain'):
        return self.store.save('wahapedia', '11', uri, (raw or text_card()).encode(), faction=faction, media_type=media, origin='manually supplied')

    def test_snapshot_reuses_identical_evidence(self):
        first, second = self.snapshot(), self.snapshot()
        self.assertEqual(first, second)
        self.assertEqual(1, len(next(iter(self.store.index().values()))['History']))

    def test_changed_content_preserves_old_revision(self):
        first = self.snapshot()
        second = self.snapshot(text_card(effect='New effect.'))
        self.assertNotEqual(first['Path'], second['Path'])
        self.assertEqual(first, self.store.load(first['Path']))
        self.assertEqual(2, len(next(iter(self.store.index().values()))['History']))

    def test_changed_etag_preserves_metadata_history(self):
        uri = 'https://example.org/a'
        a = self.store.save('wahapedia', '11', uri, b'page', etag='a')
        b = self.store.save('wahapedia', '11', uri, b'page', etag='b')
        self.assertNotEqual(a['Path'], b['Path'])
        self.assertEqual('a', self.store.load(a['Path'])['Metadata']['ETag'])

    def test_tampered_snapshot_rejected(self):
        saved = self.snapshot()
        (self.store.root / saved['Path'] / 'raw').write_text('tampered')
        with self.assertRaises(ValueError): self.store.load(saved['Path'])

    def test_snapshot_path_escape_rejected(self):
        with self.assertRaises(ValueError): self.store.load('../outside')

    def test_generic_faction_discovery(self):
        snapshot = self.snapshot('<a href="/wh40k11ed/factions/alpha/">Alpha</a><a href="/wh40k11ed/factions/beta/">Beta</a>'
            '<a href="/wh40k10ed/factions/old/">Old</a><a href="/wh40k11ed/factions/alpha/unit">Unit</a>', media='text/html')
        self.assertEqual(['Alpha', 'Beta'], [p['Name'] for p in self.adapter.discover(snapshot)])

    def test_sitemap_discovery(self):
        saved = self.snapshot('<urlset><url><loc>https://example.org/wh40k11ed/factions/alpha/</loc></url></urlset>', media='application/xml')
        self.assertEqual('alpha', self.adapter.discover(saved)[0]['SourceFactionId'])

    def test_detachment_discovery_from_cards(self):
        parsed = self.adapter.parse(self.snapshot())
        self.assertEqual(['Shared Name'], [d['Name'] for d in parsed.detachments])

    def test_html_semantic_classes_and_nonfixed_count(self):
        document = '<html><h1>Alpha</h1>' + html_card() + '</html>'
        parsed = self.adapter.parse(self.snapshot(document, media='text/html'))
        self.assertEqual([], parsed.errors)
        self.assertEqual(1, len(parsed.records))
        self.assertEqual('1 CP', parsed.records[0].cost)
        self.assertFalse(parsed.detachments[0]['Complete'])

    def test_explicit_complete_section(self):
        document = '<html><h1>Alpha</h1><section class="stratagems" data-detachment="Shared Name">' + html_card() + '</section></html>'
        parsed = self.adapter.parse(self.snapshot(document, media='text/html'))
        self.assertTrue(parsed.detachments[0]['Complete'])

    def test_truncated_html_never_complete(self):
        document = '<html><h1>Alpha</h1><section class="stratagems" data-detachment="Shared Name">' + html_card() + '</section>'
        self.assertFalse(self.adapter.parse(self.snapshot(document, media='text/html')).detachments[0]['Complete'])

    def test_html_missing_optional_cost_is_not_rejected(self):
        document = '<html><h1>Alpha</h1>' + html_card().replace('<div class="stratCP">1CP</div>', '') + '</html>'
        parsed = self.adapter.parse(self.snapshot(document, media='text/html'))
        self.assertFalse(parsed.errors)
        self.assertEqual('', parsed.records[0].cost)

    def test_semantic_attributes_not_visual_position(self):
        document = '''<html><h1>Alpha</h1><article data-reference-card="stratagem" data-detachment="Shared Name">
        <p data-reference-field="cost">2CP</p><span>Unrelated content</span><h4 data-reference-field="name">TEST RULE</h4>
        <p data-reference-field="body">EFFECT: Synthetic effect.</p></article></html>'''
        parsed = self.adapter.parse(self.snapshot(document, media='text/html'))
        self.assertFalse(parsed.errors)
        self.assertEqual(('2 CP', 'Synthetic effect.'), (parsed.records[0].cost, parsed.records[0].effect))

    def test_text_blank_lines_do_not_control_card_parsing(self):
        parsed = self.adapter.parse(self.snapshot(text_card().replace('\n\n', '\n' * 15)))
        self.assertFalse(parsed.errors)
        self.assertEqual(1, len(parsed.records))

    def test_cache_keys_cannot_escape_root(self):
        with self.assertRaises(ValueError): self.store.save('../bad', '11', 'https://example.org', b'bad')
        with self.assertRaises(ValueError): self.store.save('wahapedia', '../../bad', 'https://example.org', b'bad')

    def test_text_is_never_complete(self):
        parsed = self.adapter.parse(self.snapshot())
        self.assertTrue(parsed.records)
        self.assertFalse(any(d['Complete'] for d in parsed.detachments))

    def test_plain_scope_header_variation(self):
        parsed = self.adapter.parse(self.snapshot(text_card().replace(' – Battle Tactic', '')))
        self.assertEqual('Shared Name', parsed.records[0].detachment)

    def test_optional_target_restrictions_and_repeated_when(self):
        parsed = self.adapter.parse(self.snapshot(text_card(effect='First.\nWHEN: Another phase.\nEFFECT: Second.')))
        self.assertEqual('', parsed.records[0].target)
        self.assertEqual('', parsed.records[0].restrictions)
        self.assertEqual('Any phase.\n\nAnother phase.', parsed.records[0].timing)
        self.assertEqual('First.\n\nSecond.', parsed.records[0].effect)

    def test_singular_restriction_label(self):
        parsed = self.adapter.parse(self.snapshot(text_card(effect='Do this.\nRESTRICTION: Once per battle.')))
        self.assertEqual('Once per battle.', parsed.records[0].restrictions)

    def test_multimode_pack_preserves_label_order(self):
        saved = self.snapshot(text_card(effect='First.\nWHEN: Another phase.\nEFFECT: Second.'))
        parsed = self.adapter.parse(saved)
        pack, matches, _ = build(self.universe, self.matcher, [(saved, parsed)])
        self.assertEqual(parsed.records[0].text, pack['ReferenceCards'][0]['Text'])
        self.assertEqual('', pack['ReferenceCards'][0]['Details']['Effect'])
        self.assertEqual('First.\n\nSecond.', matches[0]['Fields']['Effect'])

    def test_source_slug_alias_resolves_card_and_coverage(self):
        matcher = Matcher(self.universe, 'wahapedia', {'source': 'wahapedia', 'edition': '11',
            'factionAliases': {'alpha': 'id-Alpha'}})
        saved = self.snapshot(faction='Different display title')
        parsed = self.adapter.parse(saved)
        pack, matches, _ = build(self.universe, matcher, [(saved, parsed)])
        self.assertEqual('Alias', matches[0]['FactionMatch'])
        self.assertEqual('Alpha', pack['ReferenceCoverage'][0]['Faction'])

    def test_missing_effect_rejected(self):
        parsed = self.adapter.parse(self.snapshot(text_card().replace('EFFECT:', 'UNRECOGNIZED:')))
        self.assertEqual([], parsed.records)
        self.assertTrue(parsed.errors)

    def test_bad_cost_rejected(self):
        parsed = self.adapter.parse(self.snapshot(text_card().replace('1CP', '-1CP')))
        self.assertEqual([], parsed.records)
        self.assertTrue(parsed.errors)

    def test_errata_not_appended_to_operative_rule(self):
        parsed = self.adapter.parse(self.snapshot(text_card() + '### Errata\nEFFECT: Historical wording.'))
        self.assertEqual('Synthetic effect.', parsed.records[0].effect)

    def test_embedded_other_game_mode_is_excluded(self):
        document = text_card() + '\n## Boarding Actions\n' + text_card(name='OTHER MODE')
        parsed = self.adapter.parse(self.snapshot(document))
        self.assertEqual(['TEST RULE'], [r.name for r in parsed.records])

    def test_unknown_html_fails_instead_of_guessing(self):
        parsed = self.adapter.parse(self.snapshot('<html><p>Some unrelated content</p></html>', media='text/html'))
        self.assertTrue(parsed.errors)
        self.assertFalse(parsed.records)

    def test_edition_mismatch_rejected(self):
        with self.assertRaises(ValueError): self.adapter.parse(self.snapshot(uri='https://example.org/wh40k10ed/factions/alpha/'))

    def test_duplicate_source_identity_reported(self):
        parsed = self.adapter.parse(self.snapshot(text_card() + text_card()))
        self.assertTrue(any('Duplicate' in e for e in parsed.errors))
        pack, _, issues = build(self.universe, self.matcher, [(self.snapshot(), parsed)])
        self.assertFalse(pack['ReferenceCards'])
        self.assertTrue(issues)

    def test_exact_id_match(self):
        self.assertEqual('ExactId', self.matcher.faction('Renamed', 'id-Alpha')[1])

    def test_exact_name_match(self):
        self.assertEqual('ExactName', self.matcher.faction('Alpha')[1])

    def test_normalized_match(self):
        self.assertEqual('NormalizedName', self.matcher.faction(' AL-PHA ')[1])

    def test_aliases_are_scoped_data(self):
        matcher = Matcher(self.universe, 'wahapedia', {'source': 'wahapedia', 'edition': '11',
            'factionAliases': {'Other': 'id-Alpha'}, 'detachmentAliases': {'id-Alpha': {'Different': 'det-Alpha'}}})
        faction, kind = matcher.faction('Other')
        self.assertEqual('Alias', kind)
        self.assertEqual('Alias', matcher.detachment(faction, 'Different')[1])
        self.assertEqual('Unresolved', matcher.detachment({'Id': 'id-Beta'}, 'Different')[1])

    def test_ambiguous_never_chosen(self):
        self.assertEqual((None, 'Ambiguous'), self.matcher.resolve('Same', '', [{'Name': 'Same', 'Id': '1'}, {'Name': 'Same', 'Id': '2'}], {}))

    def test_unresolved_match(self):
        self.assertEqual((None, 'Unresolved'), self.matcher.faction('Not Present'))

    def test_same_detachment_name_is_faction_scoped(self):
        first = self.matcher.record(card())
        second = self.matcher.record(card(faction='Beta'))
        self.assertEqual('det-Alpha', first['Detachment']['Id'])
        self.assertEqual('det-Beta', second['Detachment']['Id'])

    def test_stale_universe_rejected(self):
        audit = copy.deepcopy(self.audit)
        audit['Source']['Version'] = 'other'
        with self.assertRaises(ValueError): Universe(self.universe.dataset, audit)

    def test_hidden_projection_does_not_expand_universe(self):
        audit = copy.deepcopy(self.audit)
        audit['Canonical'].append({**audit['Canonical'][0], 'DetachmentSourceId': 'hidden', 'Selectable': False})
        self.assertEqual(2, len(Universe(self.universe.dataset, audit).scopes))

    def test_multifaction_pack_and_provenance(self):
        first = self.snapshot()
        second = self.snapshot(faction='Beta', uri='https://example.org/wh40k11ed/factions/beta/')
        pack, matches, issues = build(self.universe, self.matcher, [(s, self.adapter.parse(s)) for s in (first, second)])
        self.assertFalse(issues)
        self.assertEqual({'Alpha', 'Beta'}, {c['Faction'] for c in pack['ReferenceCards']})
        self.assertTrue(all(c['Source']['ContentHash'] and 'parser=' in c['Source']['Version'] for c in pack['ReferenceCards']))
        self.assertEqual([], pack['Units'])
        self.assertNotIn('Raw', json.dumps(pack))
        self.assertTrue(all(c['Details']['Effect'] for c in pack['ReferenceCards']))
        self.assertTrue(all(c['State'] == 'Partial' for c in pack['ReferenceCoverage']))

    def test_duplicate_alternate_page_deduplicated(self):
        first, second = self.snapshot(), self.snapshot(uri='https://example.org/wh40k11ed/factions/alternate/')
        pack, _, issues = build(self.universe, self.matcher, [(s, self.adapter.parse(s)) for s in (first, second)])
        self.assertEqual(1, len(pack['ReferenceCards']))
        self.assertIn('identical alternate', issues[0]['Errors'][0])

    def test_conflicting_alternate_page_quarantined(self):
        first, second = self.snapshot(), self.snapshot(text_card(effect='Different.'), uri='https://example.org/wh40k11ed/factions/alternate/')
        pack, _, issues = build(self.universe, self.matcher, [(s, self.adapter.parse(s)) for s in (first, second)])
        self.assertFalse(pack['ReferenceCards'])
        self.assertIn('Conflicting', issues[0]['Errors'][0])

    def test_diff_unchanged_updated_added_removed(self):
        first = self.snapshot()
        old = build(self.universe, self.matcher, [(first, self.adapter.parse(first))])[0]
        self.assertEqual(['Unchanged'], [d['Status'] for d in diff(old, old)])
        changed = copy.deepcopy(old)
        changed['ReferenceCards'][0]['Details']['Effect'] = 'Updated.'
        self.assertEqual(['Updated'], [d['Status'] for d in diff(old, changed)])
        self.assertEqual(['Added'], [d['Status'] for d in diff(None, old)])
        self.assertEqual(['RemovedFromSource'], [d['Status'] for d in diff(old, {'ReferenceCards': []})])

    def transport(self, responses):
        calls = []
        def send(uri, headers):
            calls.append((uri, headers))
            return responses.pop(0)
        return send, calls

    def test_http_conditional_get_reuses_snapshot(self):
        uri = 'https://example.org/wh40k11ed/factions/alpha/'
        self.store.save('wahapedia', '11', uri, b'page', etag='revision-1', modified='yesterday')
        send, calls = self.transport([(200, {}, b'User-agent: *\nAllow: /'), (304, {}, b'')])
        fetch = Fetcher(self.store, transport=send, sleep=lambda _: None)
        snapshot, status = fetch.acquire('wahapedia', '11', uri)
        self.assertEqual('cached', status)
        self.assertEqual('revision-1', calls[1][1]['If-None-Match'])
        self.assertEqual(b'page', snapshot['Raw'])

    def test_http_changed_content_new_snapshot(self):
        uri = 'https://example.org/wh40k11ed/factions/alpha/'
        old = self.store.save('wahapedia', '11', uri, b'old')
        send, _ = self.transport([(200, {}, b'User-agent: *\nAllow: /'), (200, {'ETag': 'v2'}, b'new')])
        saved, status = Fetcher(self.store, transport=send, sleep=lambda _: None).acquire('wahapedia', '11', uri)
        self.assertEqual('acquired', status)
        self.assertNotEqual(old['Path'], saved['Path'])

    def test_robots_denial_prevents_page_request(self):
        send, calls = self.transport([(200, {}, b'User-agent: *\nDisallow: /')])
        with self.assertRaises(AccessDenied): Fetcher(self.store, transport=send, sleep=lambda _: None).acquire('wahapedia', '11', 'https://example.org/a')
        self.assertEqual(1, len(calls))

    def test_denied_challenge_rate_limit_and_redirect_stop(self):
        for status, raw in [(401, b''), (403, b''), (429, b''), (302, b''), (200, b'Verify you are human')]:
            with self.subTest(status=status, raw=raw):
                send, calls = self.transport([(status, {}, raw)])
                fetch = Fetcher(self.store, transport=send, sleep=lambda _: None)
                with self.assertRaises(AccessDenied): fetch.request('https://example.org/a')
                with self.assertRaises(AccessDenied): fetch.request('https://example.org/b')
                self.assertEqual(1, len(calls))

    def test_transient_retry_is_bounded(self):
        send, calls = self.transport([(503, {}, b'')] * 3)
        status, _, _ = Fetcher(self.store, transport=send, sleep=lambda _: None).request('https://example.org/a')
        self.assertEqual(503, status)
        self.assertEqual(3, len(calls))

    def test_network_denial_rate_limit_and_not_found_are_distinct(self):
        uri = 'https://example.org/a'
        def unreachable(uri, headers):
            raise ConnectionRefusedError('Test connection refused')
        with self.assertRaises(AcquisitionError) as caught:
            Fetcher(self.store, retries=0, transport=unreachable, sleep=lambda _: None).request(uri)
        self.assertEqual('NetworkUnavailable', caught.exception.result)
        self.assertIsNone(caught.exception.status)
        for status, result in [(403, 'AccessDenied'), (429, 'RateLimited')]:
            send, calls = self.transport([(status, {}, b'')])
            fetch = Fetcher(self.store, transport=send, sleep=lambda _: None)
            for path in ('a', 'b'):
                with self.assertRaises(AcquisitionError) as caught:
                    fetch.request('https://example.org/' + path)
                self.assertEqual(result, caught.exception.result)
                self.assertEqual(status, caught.exception.status)
            self.assertEqual(1, len(calls))
        send, _ = self.transport([(200, {}, b'User-agent: *\nAllow: /'), (404, {}, b'')])
        with self.assertRaises(AcquisitionError) as caught:
            Fetcher(self.store, transport=send, sleep=lambda _: None).acquire('wahapedia', '11', uri)
        self.assertEqual('NotFound', caught.exception.result)
        self.assertEqual(404, caught.exception.status)

    def test_preflight_manifest_all_scopes_without_http(self):
        from unittest.mock import patch
        self.snapshot()
        args = self.cli_args('--faction', 'Alpha')
        args.command = 'plan'
        args.offline = False
        with patch.object(Fetcher, 'request', side_effect=AssertionError('HTTP called')):
            with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(0, run(args))
        manifest = read(self.root / 'out/reference-source-manifest.json')
        self.assertEqual(2, manifest['CanonicalCombinations'])
        alpha, beta = manifest['Scopes']
        self.assertEqual('Present', alpha['Snapshot'])
        self.assertTrue(alpha['SelectedForThisRun'])
        self.assertEqual('Missing', beta['Snapshot'])
        self.assertFalse(beta['SelectedForThisRun'])
        self.assertEqual([], beta['ExpectedSourceLocations'])

    def test_nullable_dataset_coverage_does_not_block_missing_acquisition(self):
        self.universe.dataset['ReferenceCoverage'] = None
        self.universe.dataset['ReferenceCards'] = None
        args = self.cli_args('--missing')
        args.command = 'plan'
        with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(0, run(args))
        manifest = read(self.root / 'out/reference-source-manifest.json')
        self.assertTrue(all(r['AcquisitionRequired'] == 'Yes' for r in manifest['Scopes']))

    def test_explicit_manifest_update_skips_index_and_reports_failure_separately(self):
        from unittest.mock import patch
        args = self.cli_args()
        args.command, args.offline = 'update', False
        args.source_manifest = str(self.root / 'targets.json')
        write(args.source_manifest, {'Edition': '11', 'Adapter': 'wahapedia', 'Factions': [
            {'Name': f, 'SourceUri': f'https://example.org/{f}'} for f in ('Alpha', 'Beta')]})
        beta = self.snapshot(faction='Beta', uri='https://example.org/Beta')
        def acquire(fetcher, adapter, edition, uri, faction=''):
            if uri == 'https://example.org/Alpha':
                raise AcquisitionError('Connection refused', 'NetworkUnavailable', uri)
            self.assertEqual('https://example.org/Beta', uri)
            return beta, 'cached'
        with patch.object(Fetcher, 'acquire', acquire):
            with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(2, run(args))
        report = read(self.root / 'out/reference-acquisition-report.json')
        self.assertEqual(1, report['Totals']['FailedAcquisition'])
        self.assertEqual(1, report['Totals']['CardsGenerated'])
        self.assertEqual('NetworkUnavailable', report['Sources'][0]['Result'])
        self.assertTrue((self.root / 'out/reference-acquisition-report.txt').exists())

    def test_parse_and_match_failures_are_separate_outcomes(self):
        self.snapshot('Not a recognizable source')
        self.snapshot(text_card(faction='Unmatched detachment'), faction='Beta', uri='https://example.org/Beta')
        with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(2, run(self.cli_args()))
        report = read(self.root / 'out/reference-acquisition-report.json')
        self.assertEqual({'ParseFailure', 'MatchFailure'}, {r['Result'] for r in report['Sources']})
        self.assertEqual(0, report['Totals']['FailedAcquisition'])

    def cli_args(self, *extra):
        write(self.root / 'data.json', self.universe.dataset)
        write(self.root / 'universe.json', self.audit)
        return parser().parse_args(['build-pack', '--dataset', str(self.root / 'data.json'), '--universe', str(self.root / 'universe.json'),
            '--cache', str(self.store.root), '--output', str(self.root / 'out'), '--offline', *extra])

    def test_offline_build_and_cached_parse(self):
        self.snapshot()
        self.snapshot(faction='Beta', uri='https://example.org/wh40k11ed/factions/beta/')
        args = self.cli_args()
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, run(args))
            self.assertEqual(0, run(args))
        result = read(self.root / 'out/reference-acquisition-report.json')
        self.assertTrue(all(r['ParseCache'] == 'reused' for r in result['Sources']))

    def test_dry_run_makes_no_files(self):
        self.snapshot()
        args = self.cli_args('--dry-run')
        before = {str(p): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}
        with contextlib.redirect_stdout(io.StringIO()): run(args)
        after = {str(p): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}
        self.assertEqual(before, after)

    def test_one_failed_page_preserves_good_cards(self):
        self.snapshot()
        self.snapshot('unrecognized source', faction='Beta', uri='https://example.org/wh40k11ed/factions/beta/')
        with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(2, run(self.cli_args()))
        pack = read(self.root / 'out/GrimList-References-11e.candidate.json')
        self.assertEqual(1, len(pack['ReferenceCards']))
        report = read(self.root / 'out/reference-acquisition-report.json')
        self.assertEqual(1, report['Totals']['FailedParsing'])
        self.assertFalse(report['Complete'])

    def publish_fixture_candidate(self):
        source = self.root / 'out/GrimList-References-11e.candidate.json'
        target = self.root / 'out/GrimList-References-11e.json'
        target.write_bytes(source.read_bytes())

    def test_failed_reparse_retains_previous_cards_without_false_removal(self):
        self.snapshot()
        args = self.cli_args('--faction', 'Alpha')
        with contextlib.redirect_stdout(io.StringIO()): run(args)
        self.publish_fixture_candidate()
        self.snapshot('Broken structure')
        with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(2, run(args))
        pack = read(self.root / 'out/GrimList-References-11e.candidate.json')
        self.assertEqual(1, len(pack['ReferenceCards']))
        changes = read(self.root / 'out/reference-source-diff.json')
        self.assertEqual(['FailedParsing'], [r['Status'] for r in changes])

    def test_absence_from_partial_snapshot_is_reported_but_retained(self):
        self.snapshot(text_card() + text_card(name='SECOND RULE'))
        args = self.cli_args('--faction', 'Alpha')
        with contextlib.redirect_stdout(io.StringIO()): run(args)
        self.publish_fixture_candidate()
        self.snapshot(text_card())
        with contextlib.redirect_stdout(io.StringIO()): run(args)
        pack = read(self.root / 'out/GrimList-References-11e.candidate.json')
        self.assertEqual(2, len(pack['ReferenceCards']))
        changes = read(self.root / 'out/reference-source-diff.json')
        self.assertEqual({'RemovedFromSource', 'Unchanged'}, {r['Status'] for r in changes})
        self.assertFalse(any(c['Authoritative'] for c in pack['ReferenceCoverage']))

    def test_selected_faction_build_retains_other_faction(self):
        self.snapshot()
        self.snapshot(faction='Beta', uri='https://example.org/wh40k11ed/factions/beta/')
        with contextlib.redirect_stdout(io.StringIO()): run(self.cli_args())
        self.publish_fixture_candidate()
        self.snapshot(text_card(effect='Updated.'))
        with contextlib.redirect_stdout(io.StringIO()): run(self.cli_args('--faction', 'Alpha'))
        pack = read(self.root / 'out/GrimList-References-11e.candidate.json')
        self.assertEqual({'Alpha', 'Beta'}, {c['Faction'] for c in pack['ReferenceCards']})

    def test_missing_filter_skips_complete_scopes(self):
        document = '<html><h1>Alpha</h1><section class="stratagems" data-detachment="Shared Name">' + html_card() + '</section></html>'
        self.snapshot(document, media='text/html')
        args = self.cli_args('--faction', 'Alpha')
        with contextlib.redirect_stdout(io.StringIO()): run(args)
        self.publish_fixture_candidate()
        args = self.cli_args('--missing', '--dry-run')
        output = io.StringIO()
        with contextlib.redirect_stdout(output): run(args)
        self.assertEqual(['Beta'], json.loads(output.getvalue())['SelectedFactions'])

    def test_acquisition_never_calls_transport_offline_or_dry_run(self):
        from unittest.mock import patch
        self.snapshot()
        for extra in ([], ['--dry-run']):
            args = self.cli_args(*extra)
            args.command = 'update'
            with patch.object(Fetcher, 'request', side_effect=AssertionError('HTTP called')):
                with contextlib.redirect_stdout(io.StringIO()): run(args)

    def test_cp_zero_and_optional_restriction(self):
        parsed = self.adapter.parse(self.snapshot(text_card().replace('1CP', '0CP')))
        self.assertEqual('0 CP', parsed.records[0].cost)

    def test_faction_wide_duplicates_do_not_become_detachment_cards(self):
        first = self.snapshot()
        parsed = ParsedDocument('Alpha', '11', records=[card(detachment='')])
        pack, _, _ = build(self.universe, self.matcher, [(first, parsed)])
        self.assertEqual('Faction', pack['ReferenceCards'][0]['Details']['Scope'])
        self.assertEqual([], pack['ReferenceCards'][0]['Details']['Detachments'])

    def test_same_name_separate_detachments_stay_separate(self):
        self.universe.scopes.append({**self.universe.scopes[0], 'Detachment': 'Another', 'DetachmentSourceId': 'another'})
        self.universe.dataset['Detachments'].append({'Faction': 'Alpha', 'Name': 'Another', 'Cost': '1 DP'})
        parsed = ParsedDocument('Alpha', '11', records=[card(), card(detachment='Another', source_id='other-rule')])
        pack, _, issues = build(self.universe, self.matcher, [(self.snapshot(), parsed)])
        self.assertFalse(issues)
        self.assertEqual(2, len(pack['ReferenceCards']))

    def test_missing_dataset_definition_isolated_from_other_faction(self):
        self.universe.dataset['Detachments'] = [d for d in self.universe.dataset['Detachments'] if d['Faction'] == 'Beta']
        a, b = self.snapshot(), self.snapshot(faction='Beta', uri='https://example.org/wh40k11ed/factions/beta/')
        pack, _, issues = build(self.universe, self.matcher, [(s, self.adapter.parse(s)) for s in (a, b)])
        self.assertEqual(['Beta'], [c['Faction'] for c in pack['ReferenceCards']])
        self.assertIn('refresh', issues[0]['Errors'][0])


class RealSnapshotTests(unittest.TestCase):
    def test_death_guard_all_45_fields_match_historical_pack(self):
        expected = read(ROOT / 'tests/fixtures/references/death-guard-rules-11.json')
        meta = {'Edition': '11', 'SourceUri': expected['Source']['Url'], 'SourceFaction': 'Death Guard', 'MediaType': 'text/plain'}
        parsed = WahapediaReferenceAdapter().parse({'Metadata': meta, 'Raw': (ROOT / 'tests/fixtures/references/death-guard-reference-text.txt').read_bytes()})
        self.assertFalse(parsed.errors)
        actual = {(key(r.detachment), key(r.name)): r for r in parsed.records}
        cards = [c for c in expected['ReferenceCards'] if c['Section'] == 'Stratagems']
        self.assertEqual(len(cards), len(actual))
        for c in cards:
            with self.subTest(name=c['Name']):
                r = actual[(key(c['Detachment']), key(c['Name']))]
                fields, _ = labels(c['Text'])
                self.assertEqual(c['Cost'], r.cost)
                for field, label in [('timing', 'WHEN'), ('target', 'TARGET'), ('effect', 'EFFECT'), ('restrictions', 'RESTRICTIONS')]:
                    self.assertEqual(clean(fields[label]), clean(getattr(r, field)))

    def test_sisters_real_browser_excerpt(self):
        self.real_excerpt('sisters', 'Adepta Sororitas', 'adepta-sororitas', 'Hallowed Martyrs', 'SUFFERING AND SACRIFICE')

    def test_world_eaters_real_browser_excerpt(self):
        self.real_excerpt('world-eaters', 'World Eaters', 'world-eaters', 'Berzerker Warband', 'HACK AND SLASH')

    def real_excerpt(self, file, faction, slug, detachment, name):
        meta = {'Edition': '11', 'SourceUri': 'https://wahapedia.ru/wh40k11ed/factions/' + slug + '/', 'SourceFaction': faction, 'MediaType': 'text/plain'}
        parsed = WahapediaReferenceAdapter().parse({'Metadata': meta, 'Raw': (FIXTURES / (file + '-browser-excerpt.txt')).read_bytes()})
        self.assertFalse(parsed.errors)
        self.assertEqual(1, len(parsed.records))
        r = parsed.records[0]
        self.assertEqual((name, detachment, '1 CP'), (r.name, r.detachment, r.cost))
        self.assertTrue(r.timing and r.target and r.effect)
        self.assertFalse(parsed.detachments[0]['Complete'])


if __name__ == '__main__':
    unittest.main()
