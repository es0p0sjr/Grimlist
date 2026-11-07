using System.Text.Json.Nodes;
using GrimList.Core;

internal static class LoadoutChecks
{
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid loadout accepted"); }
    public static Dataset Fixture()
    {
        var doc = BsDataDocument.Parse("fixture.json", """
        {"catalogue":{"id":"dg-loadout","name":"Chaos - Death Guard","selectionEntries":[
          {"id":"squad","name":"Loadout fixture squad","type":"unit","costs":[{"name":"pts","typeId":"points","value":100}],
           "categoryLinks":[{"name":"Battleline"}],
           "profiles":[{"id":"stats","name":"Fixture model","typeName":"Unit","characteristics":[{"name":"M","$text":"6\""},{"name":"T","$text":"4"},{"name":"Sv","$text":"3+"},{"name":"W","$text":"2"},{"name":"LD","$text":"6+"},{"name":"OC","$text":"2"}]}],
           "selectionEntries":[
             {"id":"champion","name":"Champion","type":"model","constraints":[{"type":"min","field":"selections","scope":"parent","value":1},{"type":"max","field":"selections","scope":"parent","value":1}],
              "entryLinks":[{"type":"selectionEntryGroup","targetId":"ranged"},{"type":"selectionEntry","targetId":"blade","constraints":[{"type":"min","field":"selections","scope":"parent","value":1}]}]},
             {"id":"marine","name":"Trooper","type":"model","constraints":[{"type":"min","field":"selections","scope":"parent","value":4},{"type":"max","field":"selections","scope":"parent","value":9}],
              "entryLinks":[{"type":"selectionEntryGroup","targetId":"ranged"},{"type":"selectionEntry","targetId":"blade","constraints":[{"type":"min","field":"selections","scope":"parent","value":1}]}]}
           ]}],
           "sharedSelectionEntryGroups":[{"id":"ranged","name":"Ranged weapon","defaultSelectionEntryId":"rifle-link","constraints":[{"type":"min","field":"selections","scope":"parent","value":1},{"type":"max","field":"selections","scope":"parent","value":1}],
              "entryLinks":[{"type":"selectionEntry","targetId":"plasma"},{"id":"rifle-link","type":"selectionEntry","targetId":"rifle"},{"type":"selectionEntry","targetId":"heavy"}]}],
           "sharedSelectionEntries":[
             {"id":"rifle","name":"Rifle","type":"upgrade","profiles":[{"id":"rifle-p","name":"Rifle","typeName":"Ranged Weapons","characteristics":[{"name":"Range","$text":"24\""},{"name":"A","$text":"2"}]}]},
             {"id":"plasma","name":"Plasma gun","type":"upgrade","profiles":[{"id":"plasma-p1","name":"Plasma gun - standard","typeName":"Ranged Weapons","characteristics":[{"name":"Range","$text":"24\""},{"name":"Keywords","$text":"Rapid Fire 1"}]},{"id":"plasma-p2","name":"Plasma gun - supercharge","typeName":"Ranged Weapons","characteristics":[{"name":"Range","$text":"24\""},{"name":"Keywords","$text":"Hazardous"}]}],"rules":[{"id":"hazardous","name":"Hazardous","description":"Synthetic equipment rule."}]},
             {"id":"heavy","name":"Heavy gun","type":"upgrade","constraints":[{"id":"heavy-max","type":"max","field":"selections","scope":"squad","value":0}],"modifiers":[{"type":"increment","field":"heavy-max","value":1,"repeats":[{"field":"selections","scope":"squad","childId":"model","value":5,"repeats":1,"roundUp":false}]}],"profiles":[{"id":"heavy-p","name":"Heavy gun","typeName":"Ranged Weapons","characteristics":[{"name":"Range","$text":"36\""}]}]},
             {"id":"blade","name":"Blade","type":"upgrade","profiles":[{"id":"blade-p","name":"Blade","typeName":"Melee Weapons","characteristics":[{"name":"Range","$text":"Melee"}]}]}
           ]}}
        """);
        var dataset = BsDataImporter.Import([doc], new("Synthetic loadout fixture", "1", "", "", Edition: "11")).Dataset;
        return dataset with { Units = dataset.Units.Select(u => u with { Options = [new(5, 100), new(10, 200)] }).ToList() };
    }
    public static void Run(Action<string, Action> test, string root)
    {
        var data = Fixture(); var unit = data.Units[0];
        File.WriteAllText(Path.Combine(root, "loadout-fixture.json"), DataJson.Write(data));
        ArmyEntry Entry(int models = 5) => new("entry", unit.Key, unit.Name, models, Loadout: Loadouts.Create(unit, models));
        test("Loadout importer resolves shared groups, defaults, fixed gear and per-five limits", () =>
        {
            var entry = Entry();
            Equal(2, unit.Profile!.Loadout!.Roles.Count); Equal(5, entry.Loadout!.Models.Count);
            Equal(1, entry.Loadout.Models.Count(m => m.RoleId == "champion")); Equal(0, Loadouts.Issues(unit, entry).Count);
            Equal(5, Loadouts.Weapons(unit, entry.Loadout).Single(w => w.Profile.Name == "Rifle").Count);
            Equal(5, Loadouts.Weapons(unit, entry.Loadout).Single(w => w.Profile.Name == "Blade").Count);
            entry = entry with { Loadout = Loadouts.Choose(unit, entry, 1, "ranged", "heavy", 1) };
            Equal(4, Loadouts.Weapons(unit, entry.Loadout).Single(w => w.Profile.Name == "Rifle").Count);
            Reject(() => Loadouts.Choose(unit, entry, 2, "ranged", "heavy", 1));
            Reject(() => Loadouts.Choose(unit, entry, 1, "ranged", "heavy", 0));
            entry = entry with { Models = 10, Loadout = Loadouts.Resize(unit, entry.Loadout!, 10) };
            entry = entry with { Loadout = Loadouts.Choose(unit, entry, 2, "ranged", "heavy", 1) };
            Equal(2, Loadouts.Weapons(unit, entry.Loadout).Single(w => w.Profile.Name == "Heavy gun").Count);
            Equal(true, Loadouts.Issues(unit, entry with { Models = 5, Loadout = Loadouts.Resize(unit, entry.Loadout!, 5) }).Count > 0);
        });
        test("Play equipment includes both firing modes and only selected equipment rules", () =>
        {
            var entry = Entry();
            Equal(false, Loadouts.Abilities(unit, entry.Loadout).Any(a => a.Name == "Hazardous"));
            entry = entry with { Loadout = Loadouts.Choose(unit, entry, 0, "ranged", "plasma", 1) };
            Equal(2, Loadouts.Weapons(unit, entry.Loadout).Count(w => w.Profile.Name.StartsWith("Plasma")));
            Equal(true, Loadouts.Abilities(unit, entry.Loadout).Any(a => a.Name == "Hazardous"));
            Equal(false, Loadouts.Weapons(unit, entry.Loadout).Any(w => w.Profile.Name == "Heavy gun"));
        });
        test("Loadout persistence, duplication, export and datasheet updates retain choices", () =>
        {
            var directory = Path.Combine(root, "loadouts");
            var entry = Entry(); entry = entry with { Loadout = Loadouts.Choose(unit, entry, 0, "ranged", "plasma", 1) };
            var army = ArmyList.New(unit.Faction) with { Entries = [entry] };
            using (var store = WindowsServices.OpenStore(directory)) { store.Import(data); store.Save(army); }
            using (var store = WindowsServices.OpenStore(directory))
            {
                var saved = store.LoadArmies().Single();
                Equal(DataJson.Write(entry.Loadout), DataJson.Write(saved.Entries[0].Loadout));
                var copy = saved.Duplicate(); store.Save(copy);
                Equal(DataJson.Write(entry.Loadout), DataJson.Write(copy.Entries[0].Loadout));
                var changed = Loadouts.Choose(unit, copy.Entries[0], 0, "ranged", "rifle", 1);
                Equal(false, DataJson.Write(changed) == DataJson.Write(entry.Loadout));
                Equal(true, ArmyCalculator.Export(saved, store.LoadDataset()).Contains("1 × Plasma gun"));
                store.ApplyImport(store.PreviewImport(data, ImportLayer.Profiles).Dataset);
                Equal(DataJson.Write(entry.Loadout), DataJson.Write(store.LoadArmies().First(a => a.Id == saved.Id).Entries[0].Loadout));
            }
        });
        test("Legacy weapon choices stay explicit and group alternate firing profiles", () =>
        {
            var legacy = unit with { Profile = unit.Profile! with { Loadout = null } };
            var entry = new ArmyEntry("manual", legacy.Key, legacy.Name, 5);
            Equal(0, Loadouts.Weapons(legacy, entry.Loadout).Count);
            Equal(1, Loadouts.Issues(legacy, entry).Count);
            entry = entry with { Loadout = Loadouts.Choose(legacy, entry, -1, "manual", "manual:Plasma gun", 2) };
            Equal(2, Loadouts.Weapons(legacy, entry.Loadout).Count);
            Equal(true, Loadouts.Weapons(legacy, entry.Loadout).All(w => w.Count == 2));
            Equal(true, Loadouts.Issues(unit, entry).Count > 0);
        });
        test("Model pool limits and size-dependent caps are enforced", () =>
        {
            var definition = unit.Profile!.Loadout!;
            var restricted = unit with { Profile = unit.Profile with { Loadout = definition with
            {
                Roles = definition.Roles.Select(r => r with { Minimum = new(0), Maximum = new(10) }).ToList(),
                ModelGroups = [new("Champion pool", ["champion"], new(1), new(1)), new("Troopers", ["marine"], new(4), new(9))]
            } } };
            var entry = new ArmyEntry("pool", restricted.Key, restricted.Name, 5, Loadout: Loadouts.Create(restricted, 5));
            Equal(1, entry.Loadout!.Models.Count(m => m.RoleId == "champion"));
            Equal(0, Loadouts.Issues(restricted, entry).Count);
            var changed = entry.Loadout with { Models = entry.Loadout.Models.Select((m, i) => i == 1 ? m with { RoleId = "champion" } : m).ToList() };
            Equal(true, Loadouts.Issues(restricted, entry with { Loadout = changed }).Any(i => i.Contains("Champion pool")));
            Equal(2, new LoadoutBound(1, [new("set", 2, "atLeast", 10)]).At(10));
            Equal(1, new LoadoutBound(1, [new("set", 2, "atLeast", 10)]).At(5));
        });
        test("BattleScribe XML equipment defaults and invalid loadout data fail safely", () =>
        {
            const string xml = """
            <catalogue id="dg" name="Chaos - Death Guard"><selectionEntries><selectionEntry id="model" name="XML equipped model" type="model">
            <costs><cost name="pts" typeId="pts" value="10"/></costs>
            <selectionEntryGroups><selectionEntryGroup id="gun" name="Weapon" defaultSelectionEntryId="rifle">
              <constraints><constraint type="min" field="selections" scope="parent" value="1"/><constraint type="max" field="selections" scope="parent" value="1"/></constraints>
              <selectionEntries><selectionEntry id="rifle" name="Test rifle" type="upgrade"><profiles><profile id="rifle-p" name="Test rifle" typeName="Ranged Weapons"><characteristics><characteristic name="Range">24&quot;</characteristic></characteristics></profile></profiles></selectionEntry></selectionEntries>
            </selectionEntryGroup></selectionEntryGroups></selectionEntry></selectionEntries></catalogue>
            """;
            var source = new SourceInfo("XML fixture", "1", "", "", Edition: "10");
            var loaded = BsDataImporter.Import([BsDataDocument.Parse("fixture.cat", xml)], source).Dataset.Units[0];
            Equal("Test rifle", Loadouts.Weapons(loaded, Loadouts.Create(loaded, 1)).Single().Profile.Name);
            var malformed = BsDataImporter.Import([BsDataDocument.Parse("fixture.cat", xml.Replace("value=\"1\"", "value=\"bad\""))], source).Dataset.Units[0];
            Equal(true, malformed.Profile!.Loadout == null);
            Equal(true, malformed.Profile.Warnings!.Any(w => w.Contains("Automatic loadout unavailable")));
        });
    }
}
