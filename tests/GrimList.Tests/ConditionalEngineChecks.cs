using GrimList.Core;

internal static class ConditionalEngineChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("Conditional importer: an inactive duplicate never hides an innate grant", () =>
        {
            var doc = BsDataDocument.Parse("Rules.json", """
                {"catalogue":{"name":"Example","id":"catalogue","gameSystemId":"test-system","selectionEntries":[
                  {"name":"Bearer","id":"bearer","type":"model","costs":[{"name":"pts","typeId":"points","value":50}],
                   "profiles":[{"name":"Bearer","id":"profile","typeName":"Unit","characteristics":[{"name":"T","typeId":"t","$text":"4"}]}],
                   "infoLinks":[{"id":"innate","type":"rule","targetId":"fnp"},{"id":"conditional","type":"rule","targetId":"fnp",
                     "modifiers":[{"field":"hidden","type":"set","value":true,"conditions":[{"field":"associations","scope":"self","childId":"any","type":"lessThan","value":1}]}]}]}],
                  "sharedRules":[{"id":"fnp","name":"Feel No Pain 5+","description":"Ignore a lost wound on 5+."}]}}
                """);
            var source = BsDataImporter.ImportAll([doc], new("Synthetic", "1", "", "", Edition: "11")).Dataset;
            var unit = source.Units.Single();
            var army = ArmyList.New(unit.Faction) with { Entries = [new("bearer", unit.Key, unit.Name, 1)] };
            var projected = EffectiveDatasheets.Project(army, army.Entries[0], source)!;
            Check(DatasheetStats.FeelNoPain(projected.Unit.Profile).Single().Name == "5+++ FNP", "Innate ability was lost to conditional duplicate");
        });
        var leader = new UnitDefinition("Test", "Medic", [new(1, 50)], Profile: new(Keywords: ["Character", "Medic"], Abilities: [
            new("Leader", "This model can be attached to the following unit: GUARD"),
            new("Protection", "While this model is leading a unit, models in that unit have the Feel No Pain 5+ ability.")]));
        var condition = new EffectCondition("association", "source-category", Label: "Medic");
        var guard = new UnitDefinition("Test", "Guard", [new(5, 100)], Profile: new(new() { ["T"] = "4", ["InSv"] = "" },
            [new("Blade", "Melee", "Melee", "2", "3+", "4", "0", "1")], Keywords: ["Guard"],
            Semantics: new([], [new("t", "Attachment", "model", "T", "increment", "1", Condition: condition),
                new("a", "Attachment", "weapon", "A", "increment", "2", Condition: condition),
                new("k", "Attachment", "keyword", "", "add", "Protected", Condition: condition),
                new("i", "Attachment", "model", "InSv", "set", "4+", Condition: condition)], 2)));
        var data = new Dataset(new("Synthetic", "1", "", ""), [leader, guard]);
        var army = new ArmyList("test", "test", "Test", 2000, [new("leader", leader.Key, leader.Name, 1), new("guard", guard.Key, guard.Name, 5)]);
        EffectiveUnitState State(ArmyList roster, Dataset? source = null) => EffectiveDatasheets.Project(roster, roster.Entries.Single(e => e.Id == "guard"), source ?? data)!;
        test("Conditional engine: legal graph activates every effect kind and reverses without base mutation", () =>
        {
            var before = DataJson.Write(data);
            var linked = LeaderRules.Attach(army, data, "leader", "guard");
            var s = State(linked);
            Check(s.EffectiveCharacteristics["T"] == "5" && s.EffectiveCharacteristics["InSv"] == "4+" && s.EffectiveWeaponProfiles[0].Attacks == "4"
                && s.EffectiveKeywords.Contains("Protected") && DatasheetStats.FeelNoPain(s.Unit.Profile).Any(r => r.Name == "5+++ FNP"), "Conditional effects absent");
            s = State(LeaderRules.Attach(linked, data, "leader", ""));
            Check(s.EffectiveCharacteristics["T"] == "4" && s.EffectiveCharacteristics["InSv"] == "" && s.EffectiveWeaponProfiles[0].Attacks == "2"
                && !s.EffectiveKeywords.Contains("Protected") && DatasheetStats.FeelNoPain(s.Unit.Profile).Count == 0, "Conditional effects stale");
            var withoutGrant = data with { Units = [leader with { Profile = leader.Profile! with { Abilities = leader.Profile!.Abilities!.Take(1).ToList() } }, guard] };
            Check(DatasheetStats.FeelNoPain(State(linked, withoutGrant).Unit.Profile).Count == 0, "Removed source ability still grants FNP");
            Check(DataJson.Write(data) == before, "Base data mutated");
        });
        test("Conditional engine: dependency chain is independent of modifier order", () =>
        {
            var modifiers = new List<DatasheetModifier> {
                new("second", "Dependent", "model", "T", "increment", "2", Condition: new("keyword", "Activated")),
                new("first", "Choice", "keyword", "", "add", "Activated", Condition: new("selection", "switch")) };
            var source = data with { Units = [guard with { Profile = guard.Profile! with { Semantics = new([], modifiers, 2) } }] };
            var proposed = army with { Entries = [army.Entries[1] with { Loadout = new([], [new("toggle", "switch")]) }] };
            Check(State(proposed, source).EffectiveCharacteristics["T"] == "6", "Dependency order affected result");
            var reversed = source with { Units = [source.Units[0] with { Profile = source.Units[0].Profile! with { Semantics = new([], modifiers.AsEnumerable().Reverse().ToList(), 2) } }] };
            Check(State(proposed, reversed).EffectiveCharacteristics["T"] == "6", "Reversing definitions changed result");
            Check(State(proposed with { Entries = [proposed.Entries[0] with { Loadout = null }] }, source).EffectiveCharacteristics["T"] == "4", "Removing dependency left modifier");
        });
        test("Conditional engine: unknown and cyclic conditions cannot become permanent grants", () =>
        {
            var rule = new AbilityProfile("Conditional protection", "Unresolved grant");
            var source = data with { Units = [guard with { Profile = guard.Profile! with { Semantics = new([], [
                new("cycle", "Cycle", "keyword", "", "add", "Loop", Condition: new("not", Children: [new("keyword", "Loop")]))], 2,
                [new(rule, new("unknown"))]) } }] };
            var state = State(army with { Entries = [army.Entries[1]] }, source);
            Check(!state.EffectiveKeywords.Contains("Loop") && !state.EffectiveAbilities.Contains(rule), "Unresolved effect became permanent");
            Check(state.Unit.Profile!.Warnings!.Any(w => w.Contains("could not converge")), "Cycle has no diagnostic");
        });
    }
}
