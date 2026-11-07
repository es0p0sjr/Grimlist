using System.Diagnostics;
using GrimList.Core;

// Identical dependency/oracle checks on the host and isolated Android app.
public static class IncrementalEvaluationWorkload
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static UnitDefinition Unit(string name, params DatasheetModifier[] modifiers) => new("Dependency test", name, [new(1, 50)],
        Profile: new(Keywords: [name], Characteristics: new() { ["OC"] = "1" }, Semantics: new([], modifiers.ToList(), 3)));
    private static ArmyEntry Entry(UnitDefinition unit, string? id = null) => new(id ?? unit.Name, unit.Key, unit.Name, 1);
    private static void Compare(ArmyList army, Dataset data)
    {
        // Exercise the optimized path first; the oracle cannot seed recent state.
        var actual = army.Entries.ToDictionary(e => e.Id, e => DataJson.Write(EffectiveDatasheets.Project(army, e, data)));
        var full = EffectiveDatasheets.EvaluateFull(army, data);
        foreach (var entry in army.Entries) Check(actual[entry.Id] == DataJson.Write(full[entry.Id]), "Oracle mismatch: " + entry.Id);
    }
    public static void Run(Action<string, Action> test, Action<string>? measure = null)
    {
        test("Compiled roster dependencies invalidate readers, including newly matching contributors and transitive grants", () =>
        {
            var source = Unit("Source", new DatasheetModifier("keyword", "Choice", "keyword", "", "add", "Signal", Condition: new("models", Count: 2)));
            var middle = Unit("Middle", new DatasheetModifier("relay", "Roster relay", "keyword", "", "add", "Relay", Condition: new("roster-keyword", Label: "Signal")));
            var reader = Unit("Reader", new DatasheetModifier("benefit", "Roster benefit", "model", "OC", "increment", "1", Condition: new("roster-keyword", Label: "Relay")));
            var spare = Unit("Spare");
            var data = new Dataset(new("Synthetic", "1", "", ""), [source, middle, reader, spare]);
            var army = ArmyList.New(source.Faction) with { Entries = [Entry(source), Entry(middle), Entry(reader), Entry(spare)] };
            Compare(army, data);
            foreach (var models in new[] { 2, 1, 3, 1 })
            {
                army = army with { Entries = army.Entries.Select(e => e.Id == "Source" ? e with { Models = models } : e).ToList() };
                Compare(army, data);
                Check(EffectiveDatasheets.Work(army, data) is { Incremental: true, Reused: 1 }, "Unrelated entry was recalculated.");
                Check(EffectiveDatasheets.Project(army, army.Entries[2], data)!.EffectiveCharacteristics["OC"] == (models > 1 ? "2" : "1"),
                    "Transitive count did not settle.");
            }
        });
        test("Roster add/remove, ordering, replacement and duplicate source labels preserve full-evaluator results", () =>
        {
            var guard = Unit("Guard"); var leader = Unit("Leader") with { Profile = new(Keywords: ["Character"],
                Abilities: [new("Leader", "This model can be attached to the following unit: Guard.")],
                Semantics: new([], [new("grant", "Leader", "model", "OC", "increment", "1", Recipient: new("attached-unit"))], 3)) };
            var watcher = Unit("Watcher", new DatasheetModifier("count", "Count", "model", "OC", "increment", "1", Condition: new("roster-unit", "any", Count: 5)));
            var spare = Unit("Spare");
            var data = new Dataset(new("Synthetic", "1", "", ""), [guard, leader, watcher, spare]);
            var army = ArmyList.New(guard.Faction) with { Entries = [Entry(guard), Entry(leader) with { AttachedToId = "Guard" }, Entry(watcher), Entry(spare)] };
            Compare(army, data);
            army = army with { Entries = army.Entries.Append(Entry(leader, "copy")).ToList() }; Compare(army, data);
            Check(EffectiveDatasheets.Work(army, data).Reused == 1, "Add invalidated unrelated spare or missed labels/counts.");
            army = army with { Entries = army.Entries.AsEnumerable().Reverse().ToList() }; Compare(army, data);
            army = army with { Entries = army.Entries.Where(e => e.Id != "copy").ToList() }; Compare(army, data);
            Check(EffectiveDatasheets.Work(army, data).Reused == 1, "Removal invalidated unrelated spare.");
            army = army with { Entries = army.Entries.Where(e => e.Id != "Leader").ToList() }; Compare(army, data);
            army = army with { Entries = army.Entries.Select(e => e.Id == "Guard" ? e with { UnitKey = spare.Key } : e).ToList() }; Compare(army, data);
        });
        foreach (var scope in new[] { "self", "roster", "force" })
            test("Association dependency plans cover constraints, conditions and repeats: " + scope, () =>
            {
                var guard = Unit("Guard") with { Profile = new(Keywords: ["Guard"], Characteristics: new() { ["OC"] = "1" },
                    Associations: new("guard-source", ["guard-source"], [new("limit", "self", "any", "All", "max", 0,
                        [new("increment", 1, new("count", scope, "any", "All", "atLeast", 1),
                            [new(new(scope, "any", "associations"), 1)])])], [])) };
                var leader = Unit("Leader") with { Profile = new(Keywords: ["Character"],
                    Abilities: [new("Leader", "This model can be attached to the following unit: Guard.")],
                    Semantics: new([], [new("grant", "Leader", "model", "OC", "increment", "1", Recipient: new("bodyguard"))], 3)) };
                var otherGuard = Unit("Other guard");
                var otherLeader = Unit("Other leader") with { Profile = new(Abilities: [new("Leader", "This model can be attached to the following unit: Other guard.")]) };
                var spare = Unit("Spare");
                var data = new Dataset(new("Synthetic", "1", "", ""), [guard, leader, otherGuard, otherLeader, spare]);
                var army = ArmyList.New(guard.Faction) with { Entries = [Entry(guard), Entry(leader) with { AttachedToId = "Guard" }, Entry(otherGuard), Entry(otherLeader), Entry(spare)] };
                Compare(army, data);
                army = army with { Entries = army.Entries.Select(e => e.Id == "Spare" ? e with { Models = 2 } : e).ToList() }; Compare(army, data);
                Check(EffectiveDatasheets.Work(army, data).Reused == (scope == "self" ? 4 : 2), "Association plan recalculated the wrong components.");
                army = army with { Entries = army.Entries.Select(e => e.Id == "Other leader" ? e with { AttachedToId = "Other guard" } : e).ToList() }; Compare(army, data);
                Check(EffectiveDatasheets.Work(army, data).Reused >= 1, "Association edit did not preserve spare.");
                army = army with { Entries = army.Entries.Where(e => e.Id != "Other leader").ToList() }; Compare(army, data);
            });
        test("A force association limit changes an untouched group's legality when another group attaches", () =>
        {
            var guard = Unit("Guard") with { Profile = new(Characteristics: new() { ["OC"] = "1" },
                Associations: new("guard", [], [new("force-limit", "force", "any", "All", "max", 1, [])], [])) };
            var leader = Unit("Leader") with { Profile = new(Abilities: [new("Leader", "This model can be attached to the following unit: Guard.")],
                Semantics: new([], [new("grant", "Grant", "model", "OC", "increment", "1", Recipient: new("bodyguard"))], 3)) };
            var spare = Unit("Spare"); var data = new Dataset(new("Synthetic", "1", "", ""), [guard, leader, spare]);
            var army = ArmyList.New(guard.Faction) with { Entries = [Entry(guard), Entry(leader) with { AttachedToId = "Guard" },
                Entry(guard, "second-guard"), Entry(leader, "second-leader"), Entry(spare)] };
            Compare(army, data);
            foreach (var attach in new[] { true, false, true, false })
            {
                army = army with { Entries = army.Entries.Select(e => e.Id == "second-leader" ? e with { AttachedToId = attach ? "second-guard" : "" } : e).ToList() };
                Compare(army, data);
                Check(EffectiveDatasheets.Project(army, army.Entries[0], data)!.EffectiveCharacteristics["OC"] == (attach ? "1" : "2"), "Force limit did not change the other group's grant.");
                Check(EffectiveDatasheets.Work(army, data).Reused == 1, "Spare was not reused.");
            }
        });
        test("Seeded mixed roster edits match full evaluation and measure incremental work", () =>
        {
            var units = Enumerable.Range(0, 20).Select(i => Unit("Unit " + i)).ToList();
            units[0] = units[0] with { Profile = units[0].Profile! with { Semantics = new([], [new("count", "Roster", "model", "OC", "increment", "1",
                Condition: new("roster-unit", "any", Count: 20))], 3) } };
            var data = new Dataset(new("Synthetic", "1", "", ""), units);
            var army = ArmyList.New(units[0].Faction) with { Entries = units.Select(u => Entry(u)).ToList() }; Compare(army, data);
            var random = new Random(60128); List<ArmyList> history = [army]; long reused = 0, projected = 0, incrementalTicks = 0, fullTicks = 0, incrementalBytes = 0, fullBytes = 0;
            for (var step = 0; step < 80; step++)
            {
                var index = random.Next(army.Entries.Count);
                army = (step % 8) switch
                {
                    0 => army with { Entries = army.Entries.Append(Entry(units[random.Next(units.Count)], "added-" + step)).ToList() },
                    1 when army.Entries.Count > 2 => army with { Entries = army.Entries.Where((_, i) => i != index).ToList() },
                    2 => army with { Entries = army.Entries.AsEnumerable().Reverse().ToList() },
                    3 => army with { Name = "Army " + step, Disposition = "Disposition " + step },
                    4 => history[random.Next(history.Count)],
                    _ => army with { Entries = army.Entries.Select((e, i) => i == index ? e with { Models = e.Models == 1 ? 2 : 1 } : e).ToList() }
                };
                var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                var actual = army.Entries.ToDictionary(e => e.Id, e => EffectiveDatasheets.Project(army, e, data));
                incrementalTicks += Stopwatch.GetTimestamp() - start; incrementalBytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
                var work = EffectiveDatasheets.Work(army, data); reused += work.Reused; projected += work.Projected;
                bytes = GC.GetAllocatedBytesForCurrentThread(); start = Stopwatch.GetTimestamp();
                var full = EffectiveDatasheets.EvaluateFull(army, data);
                fullTicks += Stopwatch.GetTimestamp() - start; fullBytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
                foreach (var entry in army.Entries) Check(DataJson.Write(actual[entry.Id]) == DataJson.Write(full[entry.Id]), "Mixed edit " + step + ": " + entry.Id);
                history.Add(army);
            }
            Check(reused > 500, "Mixed edits stopped reusing unaffected entries.");
            (measure ?? Console.WriteLine)($"MEASURE mixed-dependencies 80 edits incl. cached undo: incremental {incrementalTicks * 1000d / Stopwatch.Frequency:F1} ms/{incrementalBytes} bytes, full {fullTicks * 1000d / Stopwatch.Frequency:F1} ms/{fullBytes} bytes; {reused} reused, {projected} projections");
        });
        test("Per-entry muster nominations and presentation context reuse unaffected rules", () =>
        {
            var unit = Unit("Guard"); var other = Unit("Spare");
            var nomination = new MusterNominationRule("chosen", "Chosen", 0, 1, ["Guard"], ["Chosen"]);
            var data = new Dataset(new("Synthetic", "1", "", ""), [unit, other], Detachments: [new(unit.Faction, "Host", Nominations: [nomination])]);
            var army = ArmyList.New(unit.Faction) with { Detachment = "Host", Entries = [Entry(unit), Entry(other)] };
            Compare(army, data);
            army = army with { MusterSelections = [new("Host", "chosen", "Guard")] }; Compare(army, data);
            Check(EffectiveDatasheets.Work(army, data).Reused == 1 && EffectiveDatasheets.Project(army, army.Entries[0], data)!.EffectiveKeywords.Contains("Chosen"), "Muster context not localized.");
            army = army with { Name = "Renamed", Disposition = "New disposition" }; Compare(army, data);
            Check(EffectiveDatasheets.Work(army, data) is { Reused: 2, Projected: 0 }, "Presentation context recalculated rules.");
            army = army with { Limit = 1000 }; Compare(army, data);
            Check(!EffectiveDatasheets.Work(army, data).Incremental, "Uncompiled global context was reused.");
        });
        test("Unknown dependency branches and association scopes retain full fallback", () =>
        {
            var unknown = Unit("Unknown", new DatasheetModifier("future", "Future", "model", "OC", "increment", "1",
                Condition: new("and", Children: [new("constant", "0"), new("future-roster-condition")])));
            var spare = Unit("Spare");
            foreach (var unit in new[] { unknown, Unit("Future association") with { Profile = new(Associations:
                new("id", [], [new("future", "future-scope", "any", "", "max", 1, [])], [])) } })
            {
                var data = new Dataset(new("Synthetic", "1", "", ""), [unit, spare]);
                var army = ArmyList.New(unit.Faction) with { Entries = [Entry(unit), Entry(spare)] }; Compare(army, data);
                army = army with { Entries = army.Entries.Select(e => e.Id == "Spare" ? e with { Models = 2 } : e).ToList() }; Compare(army, data);
                Check(!EffectiveDatasheets.Work(army, data).Incremental, "Unknown dependency reused a baseline.");
            }
        });
    }
}
