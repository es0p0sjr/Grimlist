"""Pinned, cached real-page acceptance and source-format boundary regressions.

Run: python -B -m unittest tools.reference_acquisition.test_real_html -v
No acquisition, matching, pack generation or application storage is invoked.
"""
from collections import Counter
from pathlib import Path
import re
import unittest
from unittest.mock import patch

from .adapters import WahapediaReferenceAdapter
from .models import clean, digest, key, labels, read
from .snapshots import SnapshotStore

ROOT = Path(__file__).resolve().parents[2]
CASES = {
    'death-guard': {
        'Faction': 'Death Guard',
        'Snapshot': 'wahapedia/11e/5e95f9e7cd328fb338098b0a47efc31bd982e52336e504aff4255894d0fad0e2/7a33dada28098029417fdb3a9c2ab72ef62973ac482439098d6e67e1d76b57df-dec617d8e80bb2fa',
        'Hash': '7a33dada28098029417fdb3a9c2ab72ef62973ac482439098d6e67e1d76b57df',
        'Counts': {'Virulent Vectorium': 6, 'Mortarion\u2019s Hammer': 6, 'Champions of Contagion': 6,
                   'Tallyband Summoners': 6, 'Shamblerot Vectorium': 6, 'Death Lord\u2019s Chosen': 6,
                   'Contagion Engines': 3, 'Flyblown Host': 3, 'Paragons of Putrescence': 3},
    },
    'adepta-sororitas': {
        'Faction': 'Adepta Sororitas',
        'Snapshot': 'wahapedia/11e/71adb2497baea773a27cc22ee103635714ae38643a6aeeac9c9968694cf2cd0b/7aaa3e988f3ef81c26d462d21daae8b56c6998adfc49ece9dede51192c76a03e-e9722d3646867c1d',
        'Hash': '7aaa3e988f3ef81c26d462d21daae8b56c6998adfc49ece9dede51192c76a03e',
        'Counts': {'Hallowed Martyrs': 6, 'Penitent Host': 6, 'Bringers of Flame': 6, 'Army of Faith': 6,
                   'Champions of Faith': 6, 'Chorus of Condemnation': 3, 'Sacred Champions': 3},
        'WithoutSection': ['Sanctified Orators'],
    },
    'world-eaters': {
        'Faction': 'World Eaters',
        'Snapshot': 'wahapedia/11e/8bbcb96670417d69647048ae82a04aae682ca736ebf9288ea6adfa33fe35011b/20224f1ab27121e1f213995791868211d83e377fa0d9dfbb13f6b7e82f8d9d4e-0c800ce084ee5d2a',
        'Hash': '20224f1ab27121e1f213995791868211d83e377fa0d9dfbb13f6b7e82f8d9d4e',
        'Counts': {'Berzerker Warband': 6, 'Cult of Blood': 6, 'Khorne Daemonkin': 6, 'Possessed Slaughterband': 6,
                   'Goretrack Onslaught': 6, 'Brazen Engines': 3, 'Butchers of Khorne': 3, 'Vessels of Wrath': 3},
    },
}


def load_case(slug):
    snapshot = SnapshotStore(ROOT / 'tests/fixtures/wahapedia/snapshots').load(CASES[slug]['Snapshot'])
    if digest(snapshot['Raw']) != CASES[slug]['Hash']:
        raise AssertionError('Real HTML fixture hash changed')
    return snapshot


def semantic_text(value):
    # The old rendered-text export has one extra space before ')' in DRAWN TO
    # DESPAIR. Normalize typography/whitespace only, never words or rule values.
    return re.sub(r'\s+([)\]])', r'\1', clean(value).replace('\u2019', "'"))


class RealHtmlTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.adapter = WahapediaReferenceAdapter()
        with patch('urllib.request.OpenerDirector.open', side_effect=AssertionError('Network access forbidden')):
            cls.parsed = {slug: cls.adapter.parse(load_case(slug)) for slug in CASES}

    def assert_page(self, slug):
        expected, parsed = CASES[slug], self.parsed[slug]
        self.assertEqual([], parsed.errors)
        self.assertEqual(expected['Faction'], parsed.faction)
        self.assertEqual(expected['Counts'], dict(Counter(r.detachment for r in parsed.records)))
        self.assertEqual(sum(expected['Counts'].values()), len(parsed.records))
        self.assertEqual(len(parsed.records), len({r.source_id for r in parsed.records}))
        self.assertEqual(set(expected['Counts']), {d['Name'] for d in parsed.detachments if d['Complete']})
        self.assertEqual(set(expected.get('WithoutSection', [])), {d['Name'] for d in parsed.detachments if not d['Complete']})
        for card in parsed.records:
            with self.subTest(card=card.name):
                self.assertTrue(card.timing and card.target and card.effect)
                self.assertRegex(card.cost, r'^\d+ CP$')
                self.assertTrue(card.location.startswith('#'))
                self.assertNotIn('Change to:', card.text)
                self.assertNotIn('Show\n', card.text)

    def test_death_guard_real_html_reproduces_all_45_control_cards(self):
        self.assert_page('death-guard')
        actual = {(key(r.detachment), key(r.name)): r for r in self.parsed['death-guard'].records}
        cards = [c for c in read(ROOT / 'tests/fixtures/references/death-guard-rules-11.json')['ReferenceCards'] if c['Section'] == 'Stratagems']
        self.assertEqual(45, len(cards))
        self.assertEqual({(key(c['Detachment']), key(c['Name'])) for c in cards}, set(actual))
        for card in cards:
            with self.subTest(card=card['Name']):
                result = actual[(key(card['Detachment']), key(card['Name']))]
                self.assertEqual(semantic_text(card['Name']), semantic_text(result.name))
                self.assertEqual(semantic_text(card['Detachment']), semantic_text(result.detachment))
                self.assertEqual(card['Cost'], result.cost)
                fields, text = labels(card['Text'])
                for attr, label in [('timing', 'WHEN'), ('target', 'TARGET'), ('effect', 'EFFECT'), ('restrictions', 'RESTRICTIONS')]:
                    self.assertEqual(semantic_text(fields[label]), semantic_text(getattr(result, attr)), label)
                self.assertEqual(semantic_text(text), semantic_text(result.text))

    def test_sisters_real_html_sections_and_absent_section(self):
        self.assert_page('adepta-sororitas')
        cards = {r.name: r for r in self.parsed['adepta-sororitas'].records}
        card = cards['DIVINE INTERVENTION']
        self.assertEqual('Hallowed Martyrs', card.detachment)
        self.assertIn('discard 1-3 Miracle dice', card.effect)
        self.assertIn('You cannot select SAINT CELESTINE', card.restrictions)
        self.assertEqual('Until the end of the phase, each time an enemy model within Engagement Range of your unit selects its targets, it must select your unit as the target of its attacks.', cards['SUFFERING AND SACRIFICE'].effect)
        self.assertEqual(1, len(self.parsed['adepta-sororitas'].warnings))
        self.assertIn('Sanctified Orators', self.parsed['adepta-sororitas'].warnings[0])

    def test_world_eaters_real_html_sections_and_fields(self):
        self.assert_page('world-eaters')
        cards = {r.name: r for r in self.parsed['world-eaters'].records}
        card = cards['HACK AND SLASH']
        self.assertEqual(('Berzerker Warband', '1 CP', 'Fight phase.'), (card.detachment, card.cost, card.timing))
        self.assertEqual('Until the end of the phase, improve the Armour Penetration characteristic of melee weapons equipped by models in your unit by 1.', card.effect)
        self.assertIn('once per battle', cards['DRAWN TO THE SLAUGHTER'].restrictions)
        self.assertEqual([], self.parsed['world-eaters'].warnings)

    def test_real_pages_do_not_depend_on_metadata_faction_names(self):
        for slug in CASES:
            snapshot = load_case(slug)
            snapshot['Metadata']['SourceFaction'] = 'Not a parser selector'
            parsed = self.adapter.parse(snapshot)
            self.assertEqual(self.parsed[slug].json(), parsed.json())


def modern_card(scope='Mobile Reserve', name='TEST CARD', body=None):
    body = body or '<b>WHEN:</b> Any phase.<br><b>TARGET:</b> One unit.<br><b>EFFECT:</b> Synthetic effect.'
    return f'''<div class="str11Wrap"><div class="str11BodyWrap">
      <div class="str11HeadBlock str11Name" id="{name}">{name}</div><div class="str11CP">1CP</div></div>
      <div class="str11TextWrap"><div class="str11Type">{scope} \u2013 Battle Tactic Stratagem</div>
      <div class="str11Legend ShowFluff">EFFECT: Flavour text must not become rules.</div>
      <div class="str11Text">{body}</div></div></div>'''


def modern_page(cards=None, end='<h2 class="outline_header" id="Crusade-Rules">Crusade Rules</h2>'):
    return ('<html><body><h1>Jade Owls</h1><h2 class="outline_header" id="Mobile-Reserve">'
            'Mobile Reserve<span class="dpPts">2DP</span></h2><h2 id="Stratagems">Stratagems</h2>'
            + (modern_card() if cards is None else cards) + end + '</body></html>')


class HtmlBoundaryTests(unittest.TestCase):
    def parse(self, document):
        return WahapediaReferenceAdapter().parse({'Raw': document.encode(), 'Metadata': {
            'Edition': '11', 'MediaType': 'text/html', 'SourceUri': 'https://example.org/wh40k11ed/factions/jade-owls/'}})

    def test_arbitrary_faction_scope_and_nonfixed_card_count(self):
        parsed = self.parse(modern_page(modern_card() + modern_card(name='SECOND CARD')))
        self.assertEqual([], parsed.errors)
        self.assertEqual(2, len(parsed.records))
        self.assertTrue(parsed.detachments[0]['Complete'])
        self.assertEqual({'Mobile Reserve'}, {r.detachment for r in parsed.records})

    def test_scope_conflict_does_not_leak_card_to_enclosing_detachment(self):
        parsed = self.parse(modern_page(modern_card(scope='Other Formation')))
        self.assertEqual([], parsed.records)
        self.assertIn('conflicts', parsed.errors[0])
        self.assertFalse(parsed.detachments[0]['Complete'])

    def test_card_wrapper_and_each_required_component_must_close(self):
        for class_name in ('str11Name', 'str11CP', 'str11Type', 'str11Text'):
            with self.subTest(component=class_name):
                card = modern_card()
                pattern = r'(<div[^>]*class="[^"]*\b' + class_name + r'\b[^"]*"[^>]*>.*?)</div>'
                card = re.sub(pattern, r'\1', card, count=1, flags=re.S)
                parsed = self.parse(modern_page(card))
                self.assertTrue(parsed.errors or not parsed.detachments[0]['Complete'])

    def test_complete_document_and_next_heading_required(self):
        for text in (modern_page().replace('</html>', ''), modern_page(end='')):
            parsed = self.parse(text)
            self.assertEqual(1, len(parsed.records))
            self.assertFalse(parsed.detachments[0]['Complete'])

    def test_missing_cost_or_body_is_reported_and_not_complete(self):
        for card in (modern_card().replace('str11CP', 'unknown-cost'), modern_card().replace('class="str11Text"', 'class="unknown-body"')):
            parsed = self.parse(modern_page(card))
            self.assertEqual([], parsed.records)
            self.assertTrue(parsed.errors)
            self.assertFalse(parsed.detachments[0]['Complete'])

    def test_faq_and_tooltip_payloads_excluded_but_inline_keywords_preserved(self):
        body = ('<b>WHEN:</b> Any phase.<b>EFFECT:</b> Select '
                '<span class="tooltipUnit"><span>JADE</span> <span>OWLS</span></span>.'
                '<div class="faqErrataSpoiler"><b>EFFECT:</b> Errata disclosure.</div>'
                '<div id="tooltip_contentUnit">Tooltip definition.</div>')
        parsed = self.parse(modern_page(modern_card(body=body)))
        self.assertEqual([], parsed.errors)
        self.assertEqual('Select JADE OWLS.', parsed.records[0].effect)

    def test_lists_and_repeated_field_order_preserved(self):
        body = ('<b>WHEN:</b> First phase.<b>EFFECT:</b> Choose:<ul><li>First.</li><li>Second.</li></ul>'
                '<b>WHEN:</b> Second phase.<b>EFFECT:</b> In order:<ol start="2"><li>Step two.</li><li value="5">Step five.</li></ol>')
        parsed = self.parse(modern_page(modern_card(body=body)))
        self.assertEqual('WHEN: First phase.\nEFFECT: Choose: * First. * Second.\nWHEN: Second phase.\nEFFECT: In order: 2. Step two. 5. Step five.', parsed.records[0].text)

    def test_other_game_modes_and_sibling_errata_are_excluded(self):
        for mode in ('Crusade Rules', 'Boarding Actions', 'Combat Patrol'):
            document = modern_page(modern_card() + '<div class="faqErrataStrat"><h3>Errata</h3>EFFECT: Old rule.</div>',
                end='<h2 class="outline_header">' + mode + '</h2>' + modern_card(name='OTHER MODE'))
            parsed = self.parse(document)
            self.assertEqual([], parsed.errors)
            self.assertEqual(['TEST CARD'], [r.name for r in parsed.records])

    def test_orphan_title_does_not_authorize_complete_section(self):
        parsed = self.parse(modern_page(modern_card() + '<div class="str11Name">LOST CARD</div>'))
        self.assertTrue(parsed.errors)
        self.assertFalse(parsed.detachments[0]['Complete'])

    def test_duplicate_stratagem_sections_are_not_complete(self):
        parsed = self.parse(modern_page(modern_card() + '<h2>Stratagems</h2>' + modern_card(name='SECOND CARD')))
        self.assertEqual(2, len(parsed.records))
        self.assertFalse(parsed.detachments[0]['Complete'])


if __name__ == '__main__':
    unittest.main()
