using System.Text.Json.Nodes;
using GrimList.Core;

internal static class CerastusChecks
{
    // Reduced 11e Chaos Knights source structure checked 2026-10-02:
    // https://github.com/BSData/wh40k-11e/blob/main/Chaos%20-%20Chaos%20Knights%20Library.json
    // Both links expand the same shared Warlord target, but only the outer link
    // supplies a child. That is a finite tree, not a recursive definition.
    private static BsDataDocument Fixture(string name, string id, int points, int surcharge)
        => BsDataDocument.Parse("cerastus-fixture.json", $$$"""
        {"catalogue":{"name":"Chaos - Chaos Knights","id":"46d8-abc8-ef3a-9f85","gameSystemId":"fixture-game",
          "selectionEntries":[{"name":"{{{name}}}","id":"{{{id}}}","type":"model",
            "categoryLinks":[{"name":"Faction: Chaos Knights"},{"name":"Chaos"},{"name":"Titanic"},{"name":"Vehicle"}],
            "profiles":[{"name":"Fixture stats","typeName":"Unit","characteristics":[{"name":"T","$text":"11"}]}],
            "costs":[{"name":"pts","typeId":"points","value":{{{points}}}}],
            "modifiers":[{"field":"points","type":"increment","value":{{{surcharge}}},"conditionGroups":[{"type":"and",
              "localConditionGroups":[{"field":"selections","scope":"parent","type":"atLeast","value":1,"repeats":1,"conditions":[
                {"childId":"any","scope":"self","type":"before","value":1},
                {"childId":"{{{id}}}","scope":"self","type":"instanceOf","value":1}]}]}]}],
            "entryLinks":[{"name":"Warlord","id":"outer-link","targetId":"warlord","type":"selectionEntry",
              "entryLinks":[{"name":"Warlord","id":"inner-link","targetId":"warlord","type":"selectionEntry"}]}]}],
          "sharedSelectionEntries":[{"name":"Warlord","id":"warlord","type":"upgrade","costs":[{"name":"pts","typeId":"points","value":0}]}]}}
        """);
    public static void Run(Action<string, Action> test)
    {
        var source = new SourceInfo("Reduced Cerastus regression fixture", "1", "", "", Edition: "11");
        test("Cerastus nested Warlord links retain points, copy surcharges and allied selection", () =>
        {
            foreach (var (name, id, price, surcharge) in new[] {
                ("Chaos Cerastus Knight Castigator", "b6e5-911b-93f-476", 370, 15),
                ("Chaos Cerastus Knight Atrapos", "8b1d-9029-f9e1-42d9", 385, 30) })
            {
                var imported = BsDataImporter.ImportAll([Fixture(name, id, price, surcharge)], source);
                var unit = imported.Dataset.Units.Single();
                if (!unit.Options.SequenceEqual(new[] { new PointOption(1, price), new PointOption(1, price + surcharge, 2) })
                    || unit.PointsWarning.Length > 0 || imported.Warnings.Any(w => w.Contains("cyclic", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("Finite shared links lost pricing or were reported as cycles: " + name);
                var army = ArmyList.New("Death Guard") with { Entries = [new("ally", unit.Key, unit.Name, 1)] };
                var result = ArmyCalculator.Calculate(army, imported.Dataset);
                if (!AlliedRules.Library(army, imported.Dataset).Contains(unit) || !result.Complete || result.Total != price)
                    throw new Exception("Repaired knight cannot be selected and priced as an ally");
                var native = ArmyList.New("Chaos Knights") with { Entries = [new("first", unit.Key, name, 1), new("second", unit.Key, name, 1)] };
                if (ArmyCalculator.Calculate(native, imported.Dataset).Total != price * 2 + surcharge)
                    throw new Exception("Copy surcharge was not preserved");
            }
        });
        test("Real shared-reference cycles and nested paid upgrades still reject incomplete pricing", () =>
        {
            foreach (var cycle in new[] { false, true })
            {
                var document = Fixture("Fixture knight", "knight", 100, 10);
                var shared = document.Root["sharedSelectionEntries"]![0]!.AsObject();
                if (cycle) shared["entryLinks"] = JsonNode.Parse("""[{"id":"recursive-link","targetId":"warlord","type":"selectionEntry"}]""");
                else shared["costs"]![0]!["value"] = 15;
                var unit = BsDataImporter.ImportAll([document], source).Dataset.Units.Single();
                if (unit.Options.Count != 0 || !unit.PointsWarning.Contains(cycle ? "Cyclic selection" : "Paid models or equipment"))
                    throw new Exception("Unsafe pricing accepted: " + unit.PointsWarning);
            }
        });
    }
}
