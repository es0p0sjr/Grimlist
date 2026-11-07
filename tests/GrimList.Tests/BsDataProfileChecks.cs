using System.Text.Json.Nodes;
using GrimList.Core;

static class BsDataProfileChecks
{
    // Synthetic rule/weapon text on a reduced BSData schema; not a playable faction pack.
    public static void AddProfiles(List<BsDataDocument> documents)
    {
        var units = documents[0].Root["sharedSelectionEntries"]!.AsArray();
        units[0]!["categoryLinks"] = JsonNode.Parse("""[{"name":"Character"},{"name":"Epic Hero"},{"name":"Infantry"},{"name":"Faction: Death Guard"}]""");
        units[1]!["categoryLinks"] = JsonNode.Parse("""[{"name":"Infantry"}]""");
        units[2]!["categoryLinks"] = JsonNode.Parse("""[{"name":"Battleline"},{"name":"Infantry"}]""");
        units[3]!["categoryLinks"] = JsonNode.Parse("""[{"targetId":"vehicle-category"},{"name":"Transport"}]""");
        foreach (var unit in units)
            unit!["profiles"] = JsonNode.Parse("""[{"id":"stat-fixture","name":"Fixture model","typeName":"Unit","characteristics":[{"name":"M","$text":"5\"","typeId":"movement"},{"name":"T","$text":"7","typeId":"toughness"},{"name":"Sv","$text":"2+"},{"name":"W","$text":"6"},{"name":"LD","$text":"6+"},{"name":"OC","$text":"1"},{"name":"InSv","$text":"4+"}]}]""");
        units[0]!["infoLinks"] = JsonNode.Parse("""[{"type":"rule","name":"Shared fixture rule","targetId":"shared-rule"}]""");
        units[0]!["selectionEntries"] = JsonNode.Parse("""[{"id":"weapon-entry","name":"Test weapon","type":"upgrade","infoLinks":[{"type":"profile","name":"Test scythe","targetId":"shared-weapon"},{"type":"profile","name":"Test scythe","targetId":"shared-weapon"}]}]""");
        units[0]!["profiles"]!.AsArray().Add(JsonNode.Parse("""{"id":"ability","name":"Fixture ability","typeName":"Abilities","characteristics":[{"name":"Description","$text":"**Synthetic ability**\nFor importer tests only."}]}"""));
        documents[1].Root["categoryEntries"] = JsonNode.Parse("""[{"id":"vehicle-category","name":"Vehicle"}]""");
        documents[1].Root["sharedProfiles"] = JsonNode.Parse("""[{"id":"shared-weapon","name":"Test scythe","typeName":"Melee Weapons","characteristics":[{"name":"Range","$text":"Melee"},{"name":"A","$text":"6"},{"name":"WS","$text":"2+"},{"name":"S","$text":"9"},{"name":"AP","$text":"-2"},{"name":"D","$text":"3"},{"name":"Keywords","$text":"Lethal Hits"}]}]""");
        documents[1].Root["sharedRules"] = JsonNode.Parse("""[{"id":"shared-rule","name":"Shared fixture rule","description":"<p>Synthetic shared rule.</p>Not game rules."}]""");
    }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    public static void Run(Action<string, Action> test, string root, Func<List<BsDataDocument>> fixture)
    {
        var source = new SourceInfo("BSData datasheet fixture", "1", "", "", Edition: "11");
        test("Category precedence and display order include Monsters, Dedicated Transports and Allies", () =>
        {
            Equal("Epic Heroes", UnitCategories.FromKeywords(["Vehicle", "Character", "Epic Hero"]));
            Equal("Characters", UnitCategories.FromKeywords(["Character", "Battleline"]));
            Equal("Battleline", UnitCategories.FromKeywords(["Infantry", "Battleline"]));
            Equal("Dedicated Transports", UnitCategories.FromKeywords(["Vehicle", "Dedicated Transport"]));
            Equal("Monsters", UnitCategories.FromKeywords(["Fly", "Monster"]));
            Equal("Monsters", UnitCategories.FromKeywords(["Monsters"]));
            Equal("Characters", UnitCategories.FromKeywords(["Monster", "Character"]));
            Equal("Epic Heroes", UnitCategories.FromKeywords(["Monster", "Character", "Epic Hero"]));
            Equal("Battleline", UnitCategories.FromKeywords(["Monster", "Battleline"]));
            Equal("Others", UnitCategories.FromKeywords(["Infantry", "Fly"]));
            Equal("Epic Heroes,Characters,Battleline,Dedicated Transports,Vehicles,Monsters,Others,Allies", string.Join(',', new[] { "Allies", "Others", "Monsters", "Dedicated Transports", "Vehicles", "Battleline", "Characters", "Epic Heroes" }.OrderBy(UnitCategories.Order)));
            var data = BsDataImporter.Import(fixture(), source).Dataset;
            Equal("Epic Heroes", data.Units[0].Category); Equal("Others", data.Units[1].Category);
            Equal("Battleline", data.Units[2].Category); Equal("Vehicles", data.Units[3].Category);
            var docs = fixture();
            docs[0].Root["sharedSelectionEntries"]![1]!["categoryLinks"] = JsonNode.Parse("""[{"name":"Monster"}]""");
            var monster = BsDataImporter.Import(docs, source).Dataset.Units[1];
            Equal("Monsters", monster.Category);
            Equal("Monsters", (monster with { Category = "Others" }).DisplayCategory);
        });
        test("BSData extracts stats, shared weapons, rules, keywords and deduplicates profiles", () =>
        {
            var profile = BsDataImporter.Import(fixture(), source).Dataset.Units[0].Profile!;
            Equal("7", profile.Characteristics!["T"]); Equal("4+", profile.Characteristics["InSv"]);
            Equal(1, profile.Weapons!.Count); Equal("2+", profile.Weapons[0].Skill); Equal("Lethal Hits", profile.Weapons[0].Keywords);
            Equal(2, profile.Abilities!.Count); Equal(true, profile.Abilities.Any(a => a.Text == "Synthetic ability\nFor importer tests only."));
            Equal(true, profile.Abilities.Any(a => a.Text == "Synthetic shared rule.\nNot game rules."));
            Equal(true, profile.Keywords!.Contains("Epic Hero")); Equal(0, profile.Warnings!.Count);
        });
        test("Datasheet modifiers, variants, missing links and excluded extras stay explicit", () =>
        {
            var docs = fixture(); var hero = docs[0].Root["sharedSelectionEntries"]![0]!;
            var stats = hero["profiles"]![0]!;
            stats["modifiers"] = JsonNode.Parse("""[{"field":"movement","type":"set","value":"6\""},{"field":"toughness","type":"set","value":"99","conditions":[{"type":"atLeast","value":1}]}]""");
            var alternate = stats.DeepClone(); alternate["name"] = "Alternate model"; alternate["id"] = "alternate";
            alternate["characteristics"]![1]!["$text"] = "8"; hero["profiles"]!.AsArray().Add(alternate);
            hero["infoLinks"]!.AsArray().Add(JsonNode.Parse("""{"name":"Missing rule","targetId":"missing","type":"rule"}"""));
            hero["selectionEntries"]!.AsArray().Add(JsonNode.Parse("""{"id":"extra","name":"Enhancements","profiles":[{"name":"Excluded","typeName":"Abilities","characteristics":[{"name":"Description","$text":"Should not appear"}]}]}"""));
            var profile = BsDataImporter.Import(docs, source).Dataset.Units[0].Profile!;
            Equal(2, profile.Models!.Count); Equal(null, profile.Characteristics);
            Equal("6\"", profile.Models[0].Characteristics["M"]); Equal("7", profile.Models[0].Characteristics["T"]);
            Equal(true, profile.Warnings!.Any(w => w.Contains("conditional"))); Equal(true, profile.Warnings!.Any(w => w.Contains("Missing rule")));
            Equal(false, profile.Abilities!.Any(a => a.Name == "Excluded"));
        });
        test("XML preserves datasheet characteristic text and linked rule descriptions", () =>
        {
            const string xml = """
            <catalogue id="dg" name="Chaos - Death Guard"><selectionEntries><selectionEntry id="model" name="XML fixture" type="model">
              <categoryLinks><categoryLink name="Character"/></categoryLinks><costs><cost name="pts" typeId="points" value="50"/></costs>
              <profiles><profile name="XML model" typeName="Unit"><characteristics><characteristic name="M" typeId="move">6&quot;</characteristic><characteristic name="T" typeId="t">4</characteristic></characteristics></profile></profiles>
              <infoLinks><infoLink name="Shared XML rule" type="rule" targetId="rule"/></infoLinks>
              <selectionEntries><selectionEntry id="weapon" name="XML weapon" type="upgrade"><profiles><profile name="Test gun" typeName="Ranged Weapons"><characteristics><characteristic name="Range">24&quot;</characteristic><characteristic name="A">D6</characteristic><characteristic name="BS">3+</characteristic><characteristic name="S">4</characteristic><characteristic name="AP">-1</characteristic><characteristic name="D">1</characteristic></characteristics></profile></profiles></selectionEntry></selectionEntries>
            </selectionEntry></selectionEntries><sharedRules><rule id="rule" name="Shared XML rule"><description>Synthetic XML rule &amp; fixture.</description></rule></sharedRules></catalogue>
            """;
            var unit = BsDataImporter.Import([BsDataDocument.Parse("fixture.cat", xml)], source with { Edition = "10" }).Dataset.Units[0];
            Equal("Characters", unit.Category); Equal("6\"", unit.Profile!.Characteristics!["M"]);
            Equal("D6", unit.Profile.Weapons![0].Attacks); Equal("3+", unit.Profile.Weapons[0].Skill);
            Equal("Synthetic XML rule & fixture.", unit.Profile.Abilities![0].Text);
        });
        test("Combined datasheets persist; profiles-only preserves points; points-only preserves categories", () =>
        {
            var data = BsDataImporter.Import(fixture(), source).Dataset;
            var directory = Path.Combine(root, "bsdata-profiles");
            using (var store = WindowsServices.OpenStore(directory))
            {
                store.ApplyImport(store.PreviewImport(data, ImportLayer.Combined).Dataset);
                var first = store.LoadDataset()!.Units.Single(u => u.Name == "Typhus");
                store.Save(ArmyList.New("Death Guard") with { Entries = [new("hero", first.Id, first.Name, 1)] });
                var renamed = data.Units[0] with { Name = "Renamed fixture hero", Options = [] };
                store.ApplyImport(store.PreviewImport(data with { Units = [renamed] }, ImportLayer.Profiles).Dataset);
                Equal(100, store.LoadDataset()!.FindUnit(first.Id)!.Options[0].Points);
                Equal("Renamed fixture hero", store.LoadArmies()[0].Entries[0].Name);
                var points = renamed with { Options = [new(1, 110)], Profile = null, Category = "Units" };
                store.ApplyImport(store.PreviewImport(data with { Units = [points] }, ImportLayer.Points).Dataset);
            }
            using (var store = WindowsServices.OpenStore(directory))
            {
                var unit = store.LoadDataset()!.Units.Single(u => u.Name == "Renamed fixture hero");
                Equal("Epic Heroes", unit.Category); Equal(1, unit.Profile!.Weapons!.Count); Equal(2, unit.Profile.Abilities!.Count);
                Equal("11", unit.ProfileSource!.Edition); Equal(110, ArmyCalculator.Calculate(store.LoadArmies()[0], store.LoadDataset()).Total);
            }
        });
        test("Datasheets can import even when all unit prices are unavailable", () =>
        {
            var docs = fixture();
            foreach (var node in docs[0].Root["sharedSelectionEntries"]!.AsArray()) node!["costs"] = new JsonArray();
            var data = BsDataImporter.Import(docs, source).Dataset;
            Equal(true, data.Units.All(u => u.Options.Count == 0 && u.Profile != null));
            Equal(4, DatasetMerge.Prepare(null, data, ImportLayer.Profiles).Dataset.Units.Count);
        });
    }
}
