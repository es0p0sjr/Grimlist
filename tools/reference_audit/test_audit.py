import unittest
from audit import Graph, canonical_rows, candidate_type, coverage, hidden_state, applicable


class SourceAuditTests(unittest.TestCase):
    def fixture(self):
        det = dict(id='det-one', name='A shared detachment', type='upgrade', hidden=False)
        group = dict(id='group', name='Detachments', selectionEntries=[det])
        lib = dict(id='lib', name='Shared', library=True, sharedSelectionEntryGroups=[group])
        army = dict(id='army', name='Faction', catalogueLinks=[dict(targetId='lib')], entryLinks=[
            dict(id='alias-one', name='Detachment', type='selectionEntryGroup', targetId='group'),
            dict(id='alias-two', name='Detachment', type='selectionEntryGroup', targetId='group')])
        return army, lib, det

    def graph(self, army, lib):
        graph = Graph([army, lib], {'army': {'Faction': 'Faction'}}, 'ordinary-force')
        graph.audit_faction('army')
        return graph

    def test_raw_links_collapse_to_one_stable_identity(self):
        army, lib, _ = self.fixture()
        graph = self.graph(army, lib)
        self.assertEqual(2, len(graph.rows))
        canonical = canonical_rows(graph.rows, '11')
        self.assertEqual(1, len(canonical))
        self.assertEqual(1, canonical[0]['DuplicateAliasCount'])
        self.assertEqual('det-one', canonical[0]['DetachmentSourceId'])

    def test_same_name_distinct_source_ids_do_not_collapse(self):
        army, lib, det = self.fixture()
        lib['sharedSelectionEntryGroups'][0]['selectionEntries'].append(dict(det, id='det-two'))
        self.assertEqual(2, len(canonical_rows(self.graph(army, lib).rows, '11')))

    def test_primary_catalogue_visibility_is_not_faction_name_logic(self):
        army, lib, det = self.fixture()
        det['modifiers'] = [dict(field='hidden', type='set', value=True, conditions=[
            dict(scope='primary-catalogue', type='notInstanceOf', childId='other-army')])]
        self.assertTrue(all(r['Status']=='Excluded' for r in self.graph(army, lib).rows))
        det['modifiers'][0]['conditions'][0]['childId']='army'
        self.assertTrue(all(r['Status']=='Selectable' for r in self.graph(army, lib).rows))

    def test_parent_visibility_and_unknown_conditions_remain_conservative(self):
        army, lib, _ = self.fixture()
        lib['sharedSelectionEntryGroups'][0]['modifiers'] = [dict(field='hidden', type='set', value=True,
            conditions=[dict(scope='roster', type='atLeast', childId='some-unit', value=1)])]
        self.assertTrue(all(r['Status']=='Unresolved' for r in self.graph(army, lib).rows))

    def test_force_context_is_explicit_and_separate_from_catalogue(self):
        n=dict(modifiers=[dict(field='hidden', type='set', value=True, conditions=[
            dict(scope='force', type='instanceOf', childId='crusade')])])
        self.assertEqual(False, hidden_state(n, 'army', 'ordinary-force')[0])
        self.assertEqual(True, hidden_state(n, 'army', 'crusade')[0])

    def test_modifier_group_conditions_are_not_discarded(self):
        n=dict(modifierGroups=[dict(conditions=[dict(scope='primary-catalogue',type='instanceOf',childId='other')],
            modifiers=[dict(field='hidden',type='set',value=True)])])
        self.assertEqual(False,hidden_state(n,'army','force')[0])
        self.assertEqual(True,hidden_state(n,'other','force')[0])

    def test_allied_imported_root_does_not_become_native_detachment(self):
        army, lib, _ = self.fixture()
        army['entryLinks']=[]
        army['catalogueLinks'][0]['importRootEntries']=True
        lib['selectionEntryGroups']=lib.pop('sharedSelectionEntryGroups')
        lib['selectionEntries']=lib['selectionEntryGroups']
        graph=self.graph(army,lib)
        self.assertTrue(graph.rows)
        self.assertTrue(all(r['Status']=='Excluded' and 'Allied' in r['Reason'] for r in graph.rows))

    def test_broken_link_reported_not_invented(self):
        army, lib, _ = self.fixture()
        army['entryLinks'][0]['targetId']='missing'
        graph=self.graph(army, lib)
        self.assertEqual(1,len(graph.rows))
        self.assertIn('missing', graph.issues[0]['Error'])

    def test_candidate_audit_distinguishes_mentions_from_stratagem_profiles(self):
        mention=dict(name='Ability', typeName='Abilities', characteristics=[{'$text':'Reduce a stratagem cost.'}])
        card=dict(mention, typeName='Stratagem')
        self.assertEqual('StratagemMention',candidate_type(mention,'/profiles/0',())[0])
        self.assertEqual('TypedStratagem',candidate_type(card,'/sharedProfiles/0',())[0])
        self.assertEqual('publications',candidate_type({'name':'Book'},'/publications/0',())[0])

    def test_partial_source_card_count_cannot_claim_complete(self):
        row=dict(Faction='Faction',Detachment='One',DetachmentSourceId='one')
        card=dict(Faction='Faction',Section='Stratagems',Detachment='One')
        partial=dict(Edition='11',Category='Stratagems',Faction='Faction',Detachment='One',State='Complete',Mode='Partial',Authoritative=True)
        self.assertEqual('Partial',coverage([card]*6,[partial],row,'11')[1])
        complete=dict(partial,Mode='CompleteForDetachment')
        self.assertEqual('Complete',coverage([card],[complete],row,'11')[1])
        self.assertEqual('Unknown',coverage([],[],row,'11')[1])

    def test_filtering_enforces_edition_and_identity(self):
        row=dict(Faction='Faction',Detachment='One',DetachmentSourceId='one')
        card=dict(Faction='Faction',Section='Stratagems',Details=dict(Edition='10',Detachments=[dict(Name='One',Id='one')]))
        self.assertFalse(applicable(card,row,'11'))
        card['Details']['Edition']='11'
        self.assertTrue(applicable(card,row,'11'))
        card['Details']['Detachments'][0]['Id']='different'
        self.assertFalse(applicable(card,row,'11'))


if __name__=='__main__':
    unittest.main()
