using System.Text.Json.Nodes;
using GrimList.Core;

internal static partial class PricingPatternChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static JsonArray Array(string json) => (JsonArray)JsonNode.Parse(json)!;
    private static JsonObject Role(string id, int min, int max, string type = "model") => new()
    {
        ["id"] = id, ["name"] = id, ["type"] = type,
        ["constraints"] = Array($$"""[{"id":"{{id}}-min","field":"selections","scope":"parent","type":"min","value":{{min}}},{"id":"{{id}}-max","field":"selections","scope":"parent","type":"max","value":{{max}}}]""")
    };
    private static JsonObject Squad(params JsonObject[] children) => new()
    {
        ["id"] = "squad", ["name"] = "Fixture squad", ["type"] = "unit", ["selectionEntries"] = new JsonArray(children.Cast<JsonNode>().ToArray()),
        ["costs"] = Array("""[{"name":"pts","typeId":"points","value":50}]"""),
        ["profiles"] = Array("""[{"name":"Fixture","typeName":"Unit","characteristics":[{"name":"T","$text":"4"}]}]""")
    };
    private static JsonObject Price(string child = "model", string scope = "squad") => (JsonObject)JsonNode.Parse($$"""
        {"field":"points","type":"set","value":100,"conditions":[{"childId":"{{child}}","scope":"{{scope}}","field":"selections","type":"atLeast","value":6,"includeChildSelections":true}]}
        """)!;
    private static UnitDefinition Import(JsonObject squad, string faction = "Chaos - Death Guard", JsonArray? forces = null)
    {
        var root = new JsonObject { ["catalogue"] = new JsonObject { ["id"] = "catalogue", ["gameSystemId"] = "game", ["name"] = faction, ["selectionEntries"] = new JsonArray(squad), ["forceEntries"] = forces } };
        return BsDataImporter.ImportAll([BsDataDocument.Parse("fixture.json", root.ToJsonString())], new("Patterns", "1", "", "", Edition: "11")).Dataset.Units.Single();
    }
    public static void Run(Action<string, Action> test)
    {
        test("Named model groups retain size bands and price selected roles independently", () =>
        {
            var group = Role("group", 5, 10, ""); group["selectionEntries"] = new JsonArray(Role("leader", 1, 1), Role("ordinary", 4, 9));
            var squad = Squad(group); squad["modifiers"] = new JsonArray(Price("group"));
            var unit = Import((JsonObject)squad.DeepClone());
            Check(unit.Options.SequenceEqual(new[] { new PointOption(5, 50), new PointOption(10, 100) }), "Named group bands lost");
            squad["modifiers"] = new JsonArray(Price("leader"));
            var roles = Import((JsonObject)squad.DeepClone());
            Check(roles.PriceFormula != null && roles.Options.All(o => o.Points == 50), "Single leader incorrectly triggered six-model price");
            squad["modifiers"] = new JsonArray(Price("missing-role"));
            Check(Import(squad).Options.Count == 0, "Unknown role treated as a known count");
        });
        test("Variable group caps bound nested specialists without losing the leader", () =>
        {
            var specialist = Role("specialist", 0, 2); specialist["modifiers"] = Array("""[{"field":"specialist-max","type":"set","value":4,"conditions":[{"childId":"model","field":"selections","scope":"squad","type":"atLeast","value":10}]}]""");
            var nested = Role("weapons", 0, 4, ""); nested["selectionEntries"] = new JsonArray(specialist);
            var group = Role("group", 4, 9, ""); group["selectionEntries"] = new JsonArray(Role("ordinary", 2, 9), nested);
            var squad = Squad(Role("leader", 1, 1), group); squad["modifiers"] = new JsonArray(Price());
            Check(Import(squad).Options.SequenceEqual(new[] { new PointOption(5, 50), new PointOption(10, 100) }), "Nested role cap changed total");
        });
        test("A sole model inside a group retains per-model prices", () =>
        {
            var model = Role("turret", 1, 2); model["costs"] = Array("""[{"name":"pts","typeId":"points","value":75}]""");
            var group = new JsonObject { ["id"] = "group", ["selectionEntries"] = new JsonArray(model) };
            var squad = Squad(group); squad.Remove("costs");
            Check(Import(squad).Options.SequenceEqual(new[] { new PointOption(1, 75), new PointOption(2, 150) }), "Nested model price lost");
        });
        test("Exclusive composition bundles select ten or twenty models rather than one", () =>
        {
            var ten = Role("ten", 0, 1, "upgrade"); ten["selectionEntries"] = new JsonArray(Role("a", 10, 10));
            var twenty = Role("twenty", 0, 1, "upgrade"); twenty["selectionEntries"] = new JsonArray(Role("b", 20, 20));
            var group = Role("composition", 1, 1, ""); group["selectionEntries"] = new JsonArray(ten, twenty);
            var squad = Squad(group); var price = Price(); price["conditions"]![0]!["value"] = 16; squad["modifiers"] = new JsonArray(price);
            Check(Import(squad).Options.SequenceEqual(new[] { new PointOption(10, 50), new PointOption(20, 100) }), "Bundle size collapsed");
        });
        test("Specialist substitutions retain exact and asymmetric imported limits", () =>
        {
            var ordinary = Role("ordinary", 4, 4);
            ordinary["modifiers"] = Array("""[{"field":"ordinary-min","type":"decrement","value":1,"repeats":[{"field":"selections","scope":"squad","childId":"specialist","value":1}]},{"field":"ordinary-max","type":"decrement","value":1,"repeats":[{"field":"selections","scope":"squad","childId":"specialist","value":1}]}]""");
            var squad = Squad(Role("leader", 1, 1), ordinary, Role("specialist", 0, 1));
            Check(Import((JsonObject)squad.DeepClone()).Options.Single().Models == 5, "Replacement counted as extra model");
            ordinary["modifiers"]!.AsArray().RemoveAt(1);
            var asymmetric = Import(squad);
            Check(asymmetric.Options.Select(o => o.Models).SequenceEqual(new[] { 5, 6 }), "Asymmetric min/max constraints were flattened into a fixed size");
            Check(asymmetric.Profile!.Loadout!.Composition != null, "Asymmetric constraints were not retained");
        });
        test("Category copy surcharges count mixed units in the selected category", () =>
        {
            var squad = Squad(Role("body", 1, 1));
            squad["categoryLinks"] = Array("""[{"targetId":"category","name":"Fixture squad"}]""");
            squad["modifiers"] = Array("""[{"field":"points","type":"increment","value":10,"localConditionGroups":[{"field":"selections","scope":"parent","type":"atLeast","value":2,"conditions":[{"childId":"any","field":"selections","scope":"self","type":"before","value":1},{"childId":"category","field":"selections","scope":"self","type":"instanceOf","value":1}]}]}]""");
            Check(Import((JsonObject)squad.DeepClone()).Options.SequenceEqual(new[] { new PointOption(1, 50), new PointOption(1, 60, 3) }), "Category surcharge lost");
            squad["categoryLinks"]![0]!["name"] = "Infantry";
            var unit = Import(squad);
            Check(unit.CopyCategory == "Infantry", "Source category lost");
            var other = unit with { Name = "Other infantry", Options = [new(1, 20)], CopyCategory = "", MaxCopies = 1 };
            var vehicle = other with { Name = "Vehicle", Profile = other.Profile! with { Keywords = ["Vehicle"] } };
            var data = new Dataset(new("Fixture", "1", "", "", Edition: "11"), [unit, other, vehicle]);
            var army = ArmyList.New(unit.Faction) with { Entries = [new("a", other.Key, other.Name, 1), new("v", vehicle.Key, vehicle.Name, 1), new("b", unit.Key, unit.Name, 1), new("c", unit.Key, unit.Name, 1)] };
            var result = ArmyCalculator.Calculate(army, data);
            Check(result.Total == 150 && result.Entries.Last().CopyTax == 10, "Mixed category count or unrelated vehicle tax incorrect");
            Check(ArmyCalculator.NextCopy(army, data, unit) == 4, "Library preview ignored mixed category copies");
            Check(ArmyCalculator.Calculate(army with { Entries = army.Entries.Skip(1).ToList() }, data).Total == 120, "Removing category member did not remove surcharge");
        });
        test("Other force modes cannot change Army Roster caps and OR branches remain active", () =>
        {
            var models = Role("body", 5, 10);
            models["modifiers"] = Array("""[{"field":"body-max","type":"set","value":5,"conditions":[{"scope":"force","childId":"boarding","field":"selections","type":"instanceOf","value":1}]}]""");
            var squad = Squad(models); squad["modifiers"] = new JsonArray(Price("model", "unit-self"));
            JsonArray Forces() => Array("""[{"id":"boarding","name":"Boarding Actions"}]""");
            Check(Import((JsonObject)squad.DeepClone(), forces: Forces()).Options.Last().Models == 10, "Boarding cap affected Army Roster");
            squad["modifiers"] = Array("""[{"field":"points","type":"set","value":0,"conditionGroups":[{"type":"or","conditions":[{"scope":"force","childId":"boarding","field":"selections","type":"instanceOf","value":1},{"scope":"unknown","childId":"unknown","type":"equalTo","value":1}]}]}]""");
            Check(Import(squad, forces: Forces()).Options.Count == 0, "OR condition incorrectly discarded");
        });
        test("Chapter-specific native taxes do not create impossible allied price failures", () =>
        {
            var squad = Squad(Role("body", 1, 1));
            squad["modifiers"] = Array("""[{"field":"points","type":"increment","value":5,"conditions":[{"scope":"primary-catalogue","field":"selections","childId":"another-chapter","type":"instanceOf","value":1}]}]""");
            var unit = Import(squad, "Imperium - Adeptus Astartes");
            Check(unit.Options.Single().Points == 50 && !PricingRepair.NeedsRepair(unit) && unit.AlliedOptions == null, "Phantom allied error");
            var source = new SourceInfo("Fixture", "1", "", "", Edition: "11");
            var before = new Dataset(source, [unit with { AlliedOptions = [], AlliedPointsWarning = "Previously unsupported" }]);
            Check(PricingRepair.Preview(before, new(source, [unit])).Recovered == 1, "Old allied error cannot be repaired");
        });
        test("Paid fixed weapon bundles multiply costs and reject optional nested purchases", () =>
        {
            var weapon = Role("gun", 2, 2, "upgrade");
            weapon["costs"] = Array("""[{"name":"pts","typeId":"points","value":5}]""");
            weapon["profiles"] = Array("""[{"name":"Gun","typeName":"Ranged Weapons","characteristics":[{"name":"Range","$text":"24"}]}]""");
            var bundle = Role("pair", 1, 1, "upgrade"); bundle["selectionEntries"] = new JsonArray(weapon);
            var squad = Squad(bundle); squad["type"] = "model";
            var unit = Import((JsonObject)squad.DeepClone());
            Check(unit.Options.Single().Points == 50 && unit.WargearCosts!.Single(c => c.OptionId == "pair").Points == 10, "Bundled guns are free or double-counted");
            weapon["constraints"]![0]!["value"] = 1;
            Check(PricingRepair.NeedsRepair(Import(squad)), "Variable bundle treated as fixed");
        });
    }
}
