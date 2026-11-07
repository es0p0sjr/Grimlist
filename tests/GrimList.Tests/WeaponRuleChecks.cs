using GrimList.Core;

internal static class WeaponRuleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        var unit = new UnitDefinition("Test", "Bearer", [new(1, 100)], Profile: new(
            new() { ["M"] = "6\"", ["T"] = "10", ["Sv"] = "3+", ["W"] = "5", ["LD"] = "6+", ["OC"] = "1" },
            [new("Gun", "Ranged", "24\"", "2", "3+", "5", "-1", "1", "HEAVY", "gun"),
             new("Blade", "Melee", "Melee", "3", "3+", "6", "-2", "2", "", "blade")], Keywords: ["Test", "Character", "Infantry"]));
        var enhancement = new ReferenceCard("Test", "Enhancements", "Mobile bearer", "CHARACTER model only. The bearer's weapons have the [ASSAULT] ability.", "Host", "10 pts");
        var detachment = new ReferenceCard("Test", "Detachments", "Weapon training", "Melee weapons equipped by TEST models from your army have the [SUSTAINED HITS 1] ability.", "Host");
        var data = new Dataset(new("Synthetic", "1", "", "", Edition: "11"), [unit], Detachments: [new("Test", "Host"), new("Test", "Other")], ReferenceCards: [enhancement, detachment]);
        var army = ArmyList.New("Test") with { Detachment = "Host", Entries = [new("bearer", unit.Key, unit.Name, 1)] };
        EffectiveUnitState State(ArmyList a, Dataset? d = null) => EffectiveDatasheets.Project(a, a.Entries[0], d ?? data)!;
        bool Has(EffectiveUnitState state, int weapon, string key) => WeaponRules.Tokens(state.EffectiveWeaponProfiles[weapon].Keywords).Contains(key);
        test("Weapon keywords: enhancement, detachment, ranged/melee targeting, removal and immutable base", () =>
        {
            var before = DataJson.Write(data);
            var selected = Enhancements.Select(army, data, "bearer", new("Host", enhancement.Name));
            Check(Has(State(selected), 0, "ASSAULT") && Has(State(selected), 1, "ASSAULT"), "Bearer grant missing");
            Check(!Has(State(selected), 0, "SUSTAINED HITS 1") && Has(State(selected), 1, "SUSTAINED HITS 1"), "Detachment scope incorrect");
            var removed = Enhancements.Select(selected, data, "bearer", null);
            Check(!Has(State(removed), 0, "ASSAULT") && Has(State(removed), 1, "SUSTAINED HITS 1"), "Enhancement removal affected other source");
            var changed = selected with { Detachment = "Other", Detachments = ["Other"] };
            Check(State(changed).EffectiveWeaponProfiles.SequenceEqual(unit.Profile!.Weapons!), "Detachment removal left stale grants");
            Check(DataJson.Write(data) == before, "Projection mutated source");
        });
        test("Weapon keywords: simultaneous sources deduplicate, preserve provenance, and ignore enumeration order", () =>
        {
            List<DatasheetModifier> modifiers = [new("a", "One", "weapon", "Keywords", "add", "ASSAULT, LETHAL HITS"),
                new("a", "Two", "weapon", "Keywords", "append", "[ASSAULT], HEAVY"),
                new("c", "Three", "weapon", "Keywords", "remove", "HEAVY"),
                new("d", "Four", "weapon", "Keywords", "add", "SUSTAINED HITS 2, SUSTAINED HITS 1")];
            Dataset Source(IEnumerable<DatasheetModifier> mods) => data with { ReferenceCards = [], Units = [unit with { Profile = unit.Profile! with { Semantics = new([], mods.ToList()) } }] };
            var forward = State(army, Source(modifiers)); var reverse = State(army, Source(modifiers.AsEnumerable().Reverse()));
            Check(DataJson.Write(forward.EffectiveWeaponProfiles) == DataJson.Write(reverse.EffectiveWeaponProfiles), "Source order changed result");
            Check(forward.EffectiveWeaponProfiles.All(w => WeaponRules.Tokens(w.Keywords).Count(k => k == "ASSAULT") == 1 && !w.Keywords.Contains("HEAVY")
                && w.Keywords.Contains("SUSTAINED HITS 2") && !w.Keywords.Contains("SUSTAINED HITS 1")), "Duplicate/removal/numbered ability merge incorrect");
            Check(forward.AppliedModifierSources.Any(m => m.Source == "One") && forward.AppliedModifierSources.Any(m => m.Source == "Two"), "Duplicate grant lost source provenance");
            var remaining = State(army, Source(modifiers.Skip(1)));
            Check(Has(remaining, 0, "ASSAULT"), "Removing one source removed another source's grant");
        });
        test("Weapon keywords: source predicates, exact profiles and unresolved conditions", () =>
        {
            List<DatasheetModifier> mods = [
                new("range", "Ranged effect", "weapon", "Keywords", "add", "LETHAL HITS", WeaponType: "Ranged"),
                new("exact", "Named effect", "weapon", "Keywords", "add", "DEVASTATING WOUNDS", Profile: "Blade"),
                new("remove", "Remove Heavy", "weapon", "Keywords", "remove", "HEAVY"),
                new("predicate", "Source Heavy", "weapon", "Keywords", "add", "ASSAULT", WeaponPredicate: new("keyword", "HEAVY")),
                new("id", "ID effect", "weapon", "Keywords", "add", "PRECISION", WeaponPredicate: new("source-id", "blade")),
                new("conditional", "Battlefield", "weapon", "Keywords", "add", "TORRENT", Condition: new("battlefield", Label: "Only after advancing")),
                new("unknown", "Unknown selector", "weapon", "Keywords", "add", "INDIRECT FIRE", WeaponPredicate: new("unsupported", "original source selector"))];
            var source = data with { ReferenceCards = [], Units = [unit with { Profile = unit.Profile! with { Semantics = new([], mods) } }] };
            var state = State(army, source);
            Check(Has(state, 0, "LETHAL HITS") && !Has(state, 1, "LETHAL HITS"), "Ranged scope incorrect");
            Check(Has(state, 1, "DEVASTATING WOUNDS") && !Has(state, 0, "DEVASTATING WOUNDS") && Has(state, 1, "PRECISION"), "Exact profile/ID incorrect");
            Check(Has(state, 0, "ASSAULT") && !Has(state, 1, "ASSAULT"), "Predicate used partially modified keywords");
            Check(state.EffectiveWeaponProfiles.All(w => !w.Keywords.Contains("TORRENT") && !w.Keywords.Contains("INDIRECT FIRE")), "Conditional effect became permanent");
            Check(state.AppliedModifierSources.Count(m => m.Conditional) == 4, "Conditional details lost");
            var roundtrip = DataJson.Read<Dataset>(DataJson.Write(source));
            Check(State(army, roundtrip).EffectiveWeaponProfiles.SequenceEqual(state.EffectiveWeaponProfiles), "Persistence lost predicates");
        });
        test("Weapon prose: multiple abilities, removal, battlefield conditions and source keyword filters", () =>
        {
            var parsed = DatasheetRuleText.WeaponAbilities("Rule", "While the bearer has advanced, the bearer's ranged weapons have the [ASSAULT] and [HEAVY] abilities.");
            Check(parsed.Count == 1 && parsed[0].Condition?.Kind == "battlefield" && parsed[0].WeaponType == "Ranged", "Conditional bearer grant not retained");
            parsed = DatasheetRuleText.WeaponAbilities("Rule", "Ranged weapons equipped by TEST models from your army that have the [HEAVY] ability lose the [HEAVY] ability.", true);
            Check(parsed.Count == 1 && parsed[0].Operation == "remove" && parsed[0].WeaponPredicate?.Value == "HEAVY" && parsed[0].Condition?.Kind == "keyword", "Predicate/remove grammar failed");
            Check(DatasheetRuleText.WeaponAbilities("Aura", "Weapons equipped by TEST models from your army have the [ASSAULT] ability.").Count == 0, "Bearer effect leaked to army");
            Check(DatasheetRuleText.WeaponAbilities("Rule", "The bearer's weapons have the [ASSAULT] ability while within 6 inches of an enemy.").Count == 0, "Unknown trailing restriction ignored");
        });
        test("Weapon keywords: parameterized removal, inactive detachment and unknown source metadata", () =>
        {
            var rule = detachment with { Modifiers = [new("grant", "Training", "weapon", "Keywords", "add", "SUSTAINED HITS 2")] };
            var changed = unit with { Profile = unit.Profile! with { Semantics = new([], [new("remove", "Suppression", "weapon", "Keywords", "remove", "SUSTAINED HITS"),
                new("unknown", "Old source", "weapon", "Keywords", "add", "PRECISION", WeaponPredicate: new("source-category", "aaaa-bbbb"))]) } };
            var source = data with { Units = [changed], ReferenceCards = [rule] };
            var state = State(army, source);
            Check(state.EffectiveWeaponProfiles.All(w => !w.Keywords.Contains("SUSTAINED") && !w.Keywords.Contains("PRECISION")), "Removal or unknown metadata became active");
            Check(state.AppliedModifierSources.Any(m => m.Conditional && m.Source == "Old source"), "Unknown category metadata was hidden");
            var unavailable = data with { ReferenceCards = [rule], Detachments = [new("Test", "Host", Applicability: new("constant", "0"))] };
            Check(State(army, unavailable).EffectiveWeaponProfiles.SequenceEqual(unit.Profile!.Weapons!), "Unavailable detachment still grants effects");
        });
        test("Stored BSData group traversals recover ranged targeting without dropping conditions or resolving unknown filters", () =>
        {
            var grant = new DatasheetModifier("grant", "Arbitrary rule", "weapon", "Keywords", "append", "ASSAULT",
                Condition: new("selection", "toggle"), WeaponPredicate: new("unknown", "self.entries.group.recursive.profiles.Ranged Weapons"));
            var source = data with { ReferenceCards = [], Units = [unit with { Profile = unit.Profile! with { Semantics = new([], [grant]) } }] };
            Check(!Has(State(army, source), 0, "ASSAULT"), "Recovered selector dropped source condition");
            var selected = army with { Entries = [army.Entries[0] with { Loadout = new([], [new("equipment", "toggle")]) }] };
            Check(Has(State(selected, source), 0, "ASSAULT") && !Has(State(selected, source), 1, "ASSAULT"), "Recovered selector broadened its weapon target");
            Check(WeaponRules.Test(new("unknown", "self.entries.group.recursive.unresolved.profiles.Ranged Weapons"), unit.Profile!.Weapons![0]) == null, "Unknown filter was discarded");
        });
        test("Weapon rule source identity includes semantics and ignores modifier enumeration order", () =>
        {
            var card = ReferenceModel.Canonical(detachment with { Modifiers = [new("a", "Rule", "weapon", "Keywords", "add", "ASSAULT"),
                new("b", "Rule", "weapon", "Keywords", "add", "HEAVY")] }, data.Source);
            Check(ReferenceModel.ContentHash(card) == ReferenceModel.ContentHash(card with { Modifiers = card.Modifiers!.AsEnumerable().Reverse().ToList() }), "Reference hash depends on effect order");
            Check(ReferenceModel.ContentHash(card) != ReferenceModel.ContentHash(card with { Modifiers = [card.Modifiers![0] with { Condition = new("unknown") }, card.Modifiers[1]] }), "Conditional change invisible to reference conflict detection");
        });
        test("Effective values: final values only, cancelled effects unhighlighted and play-mode profile matching", () =>
        {
            var modified = unit with { Profile = unit.Profile! with { Semantics = new([], [new("t", "Rule", "model", "T", "increment", "1"),
                new("s", "Rule", "weapon", "S", "increment", "2"), new("a", "Rule", "weapon", "A", "increment", "1"), new("b", "Other", "weapon", "A", "decrement", "1")]) } };
            var state = State(army, data with { ReferenceCards = [], Units = [modified] });
            var stats = EffectiveDatasheetDisplay.Characteristics(state.BaseUnit.Profile!.Characteristics, state.EffectiveCharacteristics);
            Check(stats.Single(s => s.Key == "T") is { Value: "11", Modified: true } && !stats.Single(s => s.Key == "M").Modified, "Characteristic highlighting incorrect");
            var weapon = state.EffectiveWeaponProfiles[0] with { Name = "2 × Gun" };
            Check(EffectiveDatasheetDisplay.WeaponChanged(state.BaseUnit.Profile, weapon, "S") && !EffectiveDatasheetDisplay.WeaponChanged(state.BaseUnit.Profile, weapon, "A"), "Weapon highlight compares intermediate value");
            Check(stats.All(s => !s.Value.Contains('→')), "History leaked into value");
        });
        test("Weapon imports: source category selectors, selection conditions and detachment grants survive JSON", () =>
        {
            var document = BsDataDocument.Parse("test.json", """
                {"catalogue":{"id":"test","name":"Test","categoryEntries":[{"id":"aaaa-bbbb","name":"Source weapon category"}],
                "sharedProfiles":[{"id":"schema","name":"Weapon schema","typeName":"Ranged Weapons","characteristics":[{"typeId":"keywords-field","name":"Keywords","$text":""}]}],
                "selectionEntryGroups":[
                  {"id":"detachments","name":"Detachments","selectionEntries":[{"id":"host","name":"Host","type":"upgrade","rules":[{"id":"rule","name":"Training","description":"Melee weapons equipped by TEST models from your army have the [LETHAL HITS] ability."}]}]},
                  {"id":"enhancements","name":"Enhancements","selectionEntries":[{"id":"gift","name":"Arsenal","type":"upgrade","costs":[{"name":"pts","value":10}],
                    "profiles":[{"id":"description","name":"Arsenal","typeName":"Abilities","characteristics":[{"name":"Description","$text":"CHARACTER model only."}]}],
                    "modifiers":[
                      {"type":"set","field":"hidden","value":false,"conditions":[{"field":"selections","scope":"force","childId":"host","type":"atLeast","value":1}]},
                      {"type":"append","field":"keywords-field","value":"ASSAULT","affects":"self.entries.recursive.aaaa-bbbb.profiles.Ranged Weapons","conditions":[{"field":"selections","scope":"root-entry","childId":"toggle","type":"atLeast","value":1}]}
                    ]}]}]}}
                """);
            var references = BsDataImporter.ImportReferences(document, data.Source);
            var card = references.ReferenceCards!.Single(c => c.Section == "Enhancements");
            var mod = card.Modifiers!.Single(m => m.Characteristic == "Keywords");
            Check(mod.WeaponType == "Ranged" && mod.WeaponPredicate is { Kind: "source-category", Value: "aaaa-bbbb" } && mod.Condition?.Kind == "selection", "Source selector/condition not retained");
            var bearer = unit with { Profile = unit.Profile! with { Weapons = unit.Profile.Weapons!.Select(w => w with { SourceCategories = w.Type == "Ranged" ? ["aaaa-bbbb"] : [] }).ToList() } };
            var source = DataJson.Read<Dataset>(DataJson.Write(data with { Units = [bearer], ReferenceCards = references.ReferenceCards,
                Detachments = references.Detachments, FactionContexts = references.FactionContexts }));
            var selected = army with { Entries = [army.Entries[0] with { Enhancement = new("Host", "Arsenal"), Loadout = new([], [new("equipment", "toggle")]) }] };
            Check(Has(State(selected, source), 0, "ASSAULT") && !Has(State(selected, source), 1, "ASSAULT") && Has(State(selected, source), 1, "LETHAL HITS"),
                "Imported effects not applied: " + DataJson.Write(State(selected, source).EffectiveWeaponProfiles));
            selected = selected with { Entries = [selected.Entries[0] with { Loadout = new([], []) }] };
            Check(!Has(State(selected, source), 0, "ASSAULT"), "Source selection removal left grant");
        });
        test("Weapon exports use effective profiles, retain base library and support older enhancement metadata", () =>
        {
            var legacy = data with { ReferenceCards = [enhancement with { Modifiers = [] }, detachment] };
            var selected = Enhancements.Select(army, legacy, "bearer", new("Host", enhancement.Name));
            selected = selected with { Entries = [selected.Entries[0] with { Loadout = new([], [new("manual", "manual:Gun"), new("manual", "manual:Blade")], true) }] };
            var xml = System.Xml.Linq.XDocument.Parse(YellowscribeExport.Roster(selected, legacy));
            var profiles = xml.Descendants().Where(e => e.Name.LocalName == "profile" && e.Attribute("typeName")?.Value is "Ranged Weapons" or "Melee Weapons").ToList();
            Check(profiles.Count == 2 && profiles.All(p => p.Descendants().Any(c => c.Attribute("name")?.Value == "Keywords" && c.Value.Contains("ASSAULT"))), "Export dropped effective grants");
            Check(unit.Profile!.Weapons![0].Keywords == "HEAVY", "Export mutated source");
        });
        // Shared fixture for native renderer regression tests.
        var rendered = unit with { Profile = unit.Profile! with { Semantics = new([], [new("t", "Rule", "model", "T", "increment", "1"), new("s", "Rule", "weapon", "S", "increment", "1")]) } };
        File.WriteAllText(Path.Combine(root, "weapon-rules-ui.json"), DataJson.Write(data with { Units = [rendered] }));
    }
}
