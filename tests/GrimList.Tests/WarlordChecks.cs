using GrimList.Core;
using System.Text.Json.Nodes;

static class WarlordChecks
{
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid warlord accepted"); }
    public static void Run(Action<string, Action> test, string root)
    {
        WarlordRestrictionChecks.Run(test, root);
        var source = new SourceInfo("Warlord fixtures", "1", "", "", Edition: "11");
        var hero = new UnitDefinition("Death Guard", "Test character", [new(1, 50)], Category: "Characters", Profile: new(Keywords: ["Character"], MustBeWarlord: false));
        var commander = new UnitDefinition("Death Guard", "Test commander", [new(1, 100)], Category: "Epic Heroes", Profile: new(Abilities: [new("Supreme Commander", "Synthetic test ability.")]));
        var infantry = new UnitDefinition("Death Guard", "Test infantry", [new(5, 70)]);
        var data = new Dataset(source, [hero, commander, infantry]);
        var a = new ArmyEntry("a", hero.Key, hero.Name, 1); var b = new ArmyEntry("b", commander.Key, commander.Name, 1);
        var army = ArmyList.New("Death Guard") with { Entries = [a] };
        test("Warlord selection is by roster entry and rejects ineligible or absent entries", () =>
        {
            Equal("a", WarlordRules.Select(army, data, "a").WarlordEntryId);
            Reject(() => WarlordRules.Select(army, data, "missing"));
            Reject(() => WarlordRules.Select(army with { Entries = [new("squad", infantry.Key, infantry.Name, 5)] }, data, "squad"));
            Equal(0, WarlordRules.Candidates(army with { Faction = "Orks" }, data).Count);
            Equal(0, WarlordRules.Candidates(army with { Game = "Other" }, data).Count);
        });
        test("Supreme Commander overrides a manual warlord; removal clears the selection", () =>
        {
            var selected = army with { WarlordEntryId = "a", Entries = [a, b] };
            Equal("b", WarlordRules.Normalize(selected, data).WarlordEntryId);
            Reject(() => WarlordRules.Select(selected, data, "a"));
            Reject(() => WarlordRules.Select(selected, data, ""));
            Equal("", WarlordRules.Normalize(selected with { WarlordEntryId = "b", Entries = [a] }, data).WarlordEntryId);
            Equal("a", WarlordRules.Normalize(army with { WarlordEntryId = "a" }, data).WarlordEntryId);
        });
        test("Multiple required warlords require an explicit choice rather than a random winner", () =>
        {
            var multiple = army with { Entries = [a, b, b with { Id = "c" }], WarlordEntryId = "a" };
            Equal("", WarlordRules.Normalize(multiple, data).WarlordEntryId);
            Equal("c", WarlordRules.Select(multiple, data, "c").WarlordEntryId);
            Equal(true, WarlordRules.Issues(multiple, data).Single().Contains("Multiple"));
        });
        test("Warlord survives SQLite, duplication, export and profile-import automatic selection", () =>
        {
            var directory = Path.Combine(root, "warlord");
            using (var store = WindowsServices.OpenStore(directory))
            {
                store.Import(data with { Units = [hero, commander with { Profile = new(MustBeWarlord: false) }, infantry] });
                store.Save(army with { Entries = [a, b], WarlordEntryId = "a" });
                Equal("a", store.LoadArmies()[0].WarlordEntryId);
                store.ApplyImport(store.PreviewImport(data with { Units = [commander] }, ImportLayer.Profiles).Dataset);
                Equal("b", store.LoadArmies()[0].WarlordEntryId);
            }
            using (var store = WindowsServices.OpenStore(directory))
            {
                var saved = store.LoadArmies()[0]; Equal("b", saved.WarlordEntryId);
                var clone = saved.Duplicate(); Equal(clone.Entries[1].Id, clone.WarlordEntryId); Equal(false, clone.WarlordEntryId == saved.WarlordEntryId);
                Equal(true, ArmyCalculator.Export(saved, store.LoadDataset()).Contains("Warlord: Test commander"));
                store.Save(saved with { Entries = [a] }); Equal("", store.LoadArmies()[0].WarlordEntryId);
            }
        });
        test("BSData Mortarion-style Warlord minimum imports without a named ability", () =>
        {
            const string json = """{"catalogue":{"id":"dg","name":"Chaos - Death Guard","selectionEntries":[{"id":"mort","name":"Mortarion fixture","type":"model","costs":[{"name":"pts","typeId":"points","value":100}],"profiles":[{"name":"Unit","typeName":"Unit","characteristics":[{"name":"T","$text":"12"}]}],"entryLinks":[{"name":"Warlord","targetId":"warlord","constraints":[{"id":"required","field":"selections","scope":"parent","type":"min","value":1}]}]}],"sharedSelectionEntries":[{"id":"warlord","name":"Warlord","type":"upgrade"}]}}""";
            var document = BsDataDocument.Parse("fixture.json", json);
            var unit = BsDataImporter.Import([document], source).Dataset.Units.Single();
            Equal(true, unit.Profile!.MustBeWarlord); Equal(true, WarlordRules.Required(unit));
            document.Root["selectionEntries"]![0]!["entryLinks"]![0]!["modifiers"] = JsonNode.Parse("""[{"field":"required","type":"set","value":0,"conditions":[{"type":"atLeast","value":1}]}]""");
            Equal(false, BsDataImporter.Import([document], source).Dataset.Units[0].Profile!.MustBeWarlord);
        });
        test("Invulnerable save is separate from SV and absent values never become a guessed save", () =>
        {
            var stats = new Dictionary<string, string> { ["M"] = "10\"", ["T"] = "12", ["Sv"] = "2+", ["InSv"] = "4+" };
            Equal(3, DatasheetStats.Standard(stats).Count); Equal("2+", DatasheetStats.Standard(stats)["Sv"]);
            Equal("4+", DatasheetStats.Invulnerable(stats)); Equal("", DatasheetStats.Invulnerable(null));
            Equal("", DatasheetStats.Invulnerable(new() { ["Invulnerable Save"] = "—" }));
            Equal("5+", DatasheetStats.Invulnerable(new() { ["Invulnerable Save"] = "5+" }));
        });
    }
}
