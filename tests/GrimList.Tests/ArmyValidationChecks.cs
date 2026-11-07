using GrimList.Core;

internal static class ArmyValidationChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        var source = new SourceInfo("Validation fixtures", "1", "", "", Edition: "11");
        var character = new UnitDefinition("Death Guard", "Commander", [new(1, 100)], Category: "Characters", Profile: new(Keywords: ["Character"]));
        var defiler = new UnitDefinition("Death Guard", "Defiler", [new(1, 450)], Profile: new(Keywords: ["Vehicle"]));
        var data = new Dataset(source, [character, defiler], Detachments: [new("Death Guard", "Selected", Cost: "1 DP")]);
        var army = ArmyList.New("Death Guard") with { Detachment = "Selected", WarlordEntryId = "commander",
            Entries = [new("commander", character.Key, character.Name, 1)] };
        ArmyList Copies(UnitDefinition unit, int count) => army with { Entries = [.. army.Entries,
            .. Enumerable.Range(0, count).Select(i => new ArmyEntry("copy-" + i, unit.Key, unit.Name, 1))] };
        test("Army validation accepts complete rosters at or below the points limit", () =>
        {
            Check(ArmyCalculator.Calculate(army, data).Validated, "Under-limit army rejected");
            var exact = data with { Units = [character with { Options = [new(1, 2000)] }] };
            Check(ArmyCalculator.Calculate(army, exact).Validated, "Exactly-at-limit army rejected");
            var over = exact with { Units = [character with { Options = [new(1, 2001)] }] };
            var result = ArmyCalculator.Calculate(army, over);
            Check(!result.Validated && result.Issues.Any(i => i.Contains("points over")), "Over-limit army validated");
            Check(!ArmyCalculator.Calculate(army with { Limit = 0 }, data).Validated, "Zero points limit accepted");
        });
        test("At least one detachment is required even without imported DP costs", () =>
        {
            foreach (var edition in new[] { "10", "11" })
            {
                var noDp = data with { Source = source with { Edition = edition }, Detachments = [new("Death Guard", "Selected")] };
                foreach (var missing in new[] { army with { Detachment = "" }, army with { Detachments = [] }, army with { Detachments = [" "] } })
                    Check(ArmyCalculator.Calculate(missing, noDp).Issues.Any(i => i.Contains("at least one detachment")), "Missing detachment accepted");
                Check(ArmyCalculator.Calculate(army, noDp).Validated, "Legacy detachment selection rejected");
            }
        });
        test("A Character and exactly one eligible selected warlord are required", () =>
        {
            var noCharacter = army with { Entries = [new("vehicle", defiler.Key, defiler.Name, 1)], WarlordEntryId = "vehicle" };
            var result = ArmyCalculator.Calculate(noCharacter, data);
            Check(!result.Validated, "Non-character army validated");
            Check(result.Issues.Any(i => i.Contains("Character", StringComparison.OrdinalIgnoreCase)), "Missing Character requirement explanation");
            Check(result.Issues.Any(i => i.Contains("warlord", StringComparison.OrdinalIgnoreCase)), "Missing Warlord eligibility explanation");
            foreach (var id in new[] { "", "removed", "copy-0" })
                Check(!ArmyCalculator.Calculate(Copies(defiler, 1) with { WarlordEntryId = id }, data).Validated, "Missing/ineligible warlord accepted");
            var duplicate = army with { Entries = [army.Entries[0], army.Entries[0]] };
            Check(WarlordRules.Issues(duplicate, data).Any(i => i.Contains("Exactly one")), "Duplicate warlord identities accepted");
            var mandatory = data with { Units = [character with { Profile = character.Profile! with { MustBeWarlord = true } }, defiler] };
            var unset = army with { WarlordEntryId = "" };
            Check(!ArmyCalculator.Calculate(unset, mandatory).Validated, "Validation silently selected a warlord");
            Check(ArmyCalculator.Calculate(WarlordRules.Normalize(unset, mandatory), mandatory).Validated, "Automatic mandatory selection no longer works");
            var nonCharacter = data with { Units = [character with { Category = "Vehicles", Profile = new(MustBeWarlord: true) }] };
            Check(ArmyCalculator.Calculate(army, nonCharacter).Issues.Any(i => i.Contains("Character")), "Mandatory warlord substituted for Character requirement");
        });
        test("Four Defilers fail validation at 2000 points even when their points fit", () =>
        {
            Check(ArmyCalculator.Calculate(Copies(defiler, 3), data).Validated, "Three Defilers rejected");
            var result = ArmyCalculator.Calculate(Copies(defiler, 4), data);
            Check(result.Total == 1900 && !result.Validated && result.Issues.Any(i => i.Contains("Defiler") && i.Contains("limit of 3")), "Fourth Defiler validated");
            foreach (var (keyword, limit) in new[] { ("Battleline", 6), ("Dedicated Transport", 6), ("Epic Hero", 1) })
            {
                var unit = defiler with { Name = keyword + " fixture", Options = [new(1, 50)], Profile = new(Keywords: [keyword]) };
                var fixture = data with { Units = [character, unit] };
                Check(ArmyCalculator.Calculate(Copies(unit, limit), fixture).Validated, "Legal special copy limit rejected");
                Check(!ArmyCalculator.Calculate(Copies(unit, limit + 1), fixture).Validated, "Special copy limit ignored");
            }
        });
        test("Missing prices or units and existing roster issues prevent a validated status", () =>
        {
            var unknown = data with { Units = [character with { Options = [] }] };
            Check(!ArmyCalculator.Calculate(army, unknown).Complete && !ArmyCalculator.Calculate(army, unknown).Validated, "Unknown points validated");
            Check(!ArmyCalculator.Calculate(army, data with { Units = [] }).Validated, "Missing unit validated");
            Check(!ArmyCalculator.Calculate(army with { Entries = [] }, data).Validated, "Empty army validated");
            Check(!ArmyCalculator.Calculate(army with { Entries = [army.Entries[0] with { AttachedToId = "missing" }] }, data).Validated, "Existing attachment issue ignored");
        });
        test("Exports report actual validation and preserve unresolved roster selections", () =>
        {
            var text = RosterExport.Text(army, data);
            Check(text.Contains("Army validated.") && !text.Contains("Full army legality"), "Valid export kept blanket disclaimer");
            var invalid = Copies(defiler, 4) with { WarlordEntryId = "" };
            var before = DataJson.Write(invalid); text = RosterExport.Text(invalid, data);
            Check(text.Contains("Army not validated.") && text.Contains("Defiler: 4 units exceeds the limit of 3") && text.Contains("exactly one eligible warlord"), "Invalid export omitted reasons");
            Check(before == DataJson.Write(invalid), "Export modified saved army");
        });
        File.WriteAllText(Path.Combine(root, "validation-fixture-data.json"), DataJson.Write(data));
        File.WriteAllText(Path.Combine(root, "validation-fixture-army.json"), DataJson.Write(army));
    }
}
