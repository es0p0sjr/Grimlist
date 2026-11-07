using System.Text.Json.Nodes;
using GrimList.Core;

internal static class AssociationChecks
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static Dataset Import(string modifiers, bool allAssociations = false)
    {
        var document = BsDataDocument.Parse("fixture.json", """
        {"catalogue":{"id":"fixture","name":"Chaos - Death Guard","gameSystemId":"game","revision":1,
         "categoryEntries":[{"id":"leader","name":"Leader"},{"id":"permit","name":"Permission marker"}],
         "selectionEntries":[
          {"id":"body","name":"Fixture Bodyguard","type":"model","costs":[{"name":"pts","typeId":"points","value":50}],
           "profiles":[{"name":"Fixture","typeName":"Unit","characteristics":[{"name":"M","$text":"6"}]}],
           "constraints":[{"id":"limit","field":"associations","childId":"leader","scope":"self","type":"max","value":1}]},
          {"id":"first","name":"Fixture first","type":"model","costs":[{"name":"pts","typeId":"points","value":50}],
           "categoryLinks":[{"name":"Leader","targetId":"leader"},{"name":"Permission marker","targetId":"permit"}],
           "profiles":[{"name":"Leader","typeName":"Abilities","characteristics":[{"name":"Description","$text":"This model can be attached to the following unit: Fixture Bodyguard."}]}]},
          {"id":"second","name":"Fixture second","type":"model","costs":[{"name":"pts","typeId":"points","value":50}],
           "categoryLinks":[{"name":"Leader","targetId":"leader"}],
           "profiles":[{"name":"Leader","typeName":"Abilities","characteristics":[{"name":"Description","$text":"This model can be attached to the following unit: Fixture Bodyguard."}]}]}
         ]}}
        """);
        document.Root["selectionEntries"]![0]!["modifiers"] = JsonNode.Parse(modifiers);
        if (allAssociations) document.Root["selectionEntries"]![0]!["constraints"]![0]!.AsObject().Remove("childId");
        return BsDataImporter.Import([document], new("Association fixtures", "1", "", "", Edition: "11")).Dataset;
    }
    private static ArmyList Army(Dataset data) => ArmyList.New("Death Guard") with
    { Entries = new[] { "Fixture Bodyguard", "Fixture first", "Fixture second" }.Select((name, i) =>
        { var u = data.Units.Single(u => u.Name == name); return new ArmyEntry(i.ToString(), u.Key, u.Name, 1); }).ToList() };
    public static void Run(Action<string, Action> test)
    {
        test("Association limits reuse numeric multiplication/repeats against the proposed attachment set", () =>
        {
            var data = Import("""[{"field":"limit","type":"multiply","value":2,"repeats":[{"field":"associations","scope":"self","childId":"permit","value":1,"repeats":1}]}]""");
            var army = Army(data);
            Check(AssociationRules.Evaluate(army, data, army.Entries[0], ["1", "2"]).Constraints.Single().Effective == 2, "Proposed permission selection not counted");
            Check(AssociationRules.Evaluate(army, data, army.Entries[0], ["2"]).Constraints.Single().Effective == 1, "Absent permission repeated modifier");
            Check(army.Entries.All(e => e.AttachedToId.Length == 0), "Query committed the proposed attachments");
        });
        test("Association constraints without a child ID count all attachments; explicit model conditions use bodyguard size", () =>
        {
            var data = Import("""[{"field":"limit","type":"set","value":2,"conditions":[{"field":"selections","scope":"self","childId":"model","type":"atLeast","value":20}]}]""", allAssociations: true);
            var army = Army(data);
            Check(LeaderRules.CombinationProblem(army, data, "0", new[] { "1", "2" }) != null, "Base all-associations limit was ignored");
            army = army with { Entries = army.Entries.Select(e => e.Id == "0" ? e with { Models = 20 } : e).ToList() };
            Check(LeaderRules.CombinationProblem(army, data, "0", new[] { "1", "2" }) == null, "Explicit bodyguard model condition did not raise the limit");
            Check(data.Units.Single(u => u.Name == "Fixture Bodyguard").Profile!.Associations!.Warnings.Count == 0, "Supported implicit all-associations constraint was flagged unsupported");
        });
        test("Association importer supports all numeric comparisons against proposed category-ID counts", () =>
        {
            foreach (var (comparison, expected) in new[] { ("atLeast", true), ("atMost", true), ("greaterThan", false), ("lessThan", false), ("equalTo", true), ("notEqualTo", false) })
            {
                var data = Import($$"""[{"field":"limit","type":"set","value":2,"conditions":[{"field":"associations","scope":"self","childId":"permit","childName":"Incorrect display label","type":"{{comparison}}","value":1}]}]""");
                var army = Army(data);
                Check((LeaderRules.CombinationProblem(army, data, "0", new[] { "1", "2" }) == null) == expected, comparison + " evaluated incorrectly");
            }
        });
        test("Association modifiers preserve set/increment/decrement order and nested AND/OR conditions", () =>
        {
            var data = Import("""
            [{"field":"limit","type":"set","value":1},
             {"field":"limit","type":"increment","value":2,"conditionGroups":[{"type":"and","conditions":[{"field":"associations","scope":"self","childId":"leader","type":"equalTo","value":2}],
              "conditionGroups":[{"type":"or","conditions":[{"field":"associations","scope":"self","childId":"permit","type":"atLeast","value":1},{"field":"associations","scope":"self","childId":"leader","type":"greaterThan","value":10}]}]}]},
             {"field":"limit","type":"decrement","value":1}]
            """);
            var army = Army(data); var limits = AssociationRules.Evaluate(army, data, army.Entries[0], ["1", "2"]);
            Check(limits.Constraints.Single().Effective == 2 && limits.Constraints.Single().AppliedModifiers.Count == 3, "Modifier sequence was flattened incorrectly");
            Check(LeaderRules.CombinationProblem(army, data, "0", new[] { "1", "2" }) == null, "Nested condition failed");
            var withoutPermit = army with { Entries = [army.Entries[0], army.Entries[2], army.Entries[2] with { Id = "3" }] };
            Check(LeaderRules.CombinationProblem(withoutPermit, data, "0", new[] { "2", "3" }) != null, "Failed OR branch granted permission");
        });
        test("Association scope separates bodyguard-local counts from force counts and rejects unsupported grammar", () =>
        {
            foreach (var scope in new[] { "self", "root-entry", "force", "roster", "unknown-scope" })
            {
                var data = Import($$"""[{"field":"limit","type":"set","value":2,"conditions":[{"field":"associations","scope":"{{scope}}","childId":"permit","type":"atLeast","value":1}]}]""");
                var army = Army(data);
                army = army with { Entries = [army.Entries[0], army.Entries[1] with { AttachedToId = "other-body" }, army.Entries[2],
                    army.Entries[2] with { Id = "3" }, army.Entries[0] with { Id = "other-body" }] };
                var valid = LeaderRules.CombinationProblem(army, data, "0", new[] { "2", "3" }) == null;
                Check(valid == (scope is "force" or "roster"), "Wrong scope semantics for " + scope);
            }
            var unsupported = Import("""[{"field":"limit","type":"set","value":2,"conditions":[{"field":"selections","scope":"self","childId":"permit","type":"atLeast","value":1}]}]""");
            Check(LeaderRules.CombinationProblem(Army(unsupported), unsupported, "0", new[] { "1", "2" }) != null, "Unsupported selection field became an unconditional permission");
        });
    }
}
