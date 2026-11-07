using GrimList.Core;

internal static class ReferenceChecks
{
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid rules import accepted"); }
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        test("Stratagem colours use activation timing, including mixed phases, and ignore effect duration", () =>
        {
            Equal(StratagemTurn.Either, StratagemTiming.FromText("WHEN: Your opponent’s Shooting phase or the Fight phase. TARGET: A unit."));
            Equal(StratagemTurn.Either, StratagemTiming.FromText("WHEN: Your Shooting phase or the Fight phase. TARGET: A unit."));
            Equal(StratagemTurn.Either, StratagemTiming.FromText("WHEN: Start of any phase. TARGET: A unit."));
            Equal(StratagemTurn.Either, StratagemTiming.FromText("WHEN: Start of the Command phase. TARGET: A unit."));
            Equal(StratagemTurn.Yours, StratagemTiming.FromText("WHEN: Your Shooting phase. TARGET: A unit. EFFECT: Until your opponent's next Movement phase."));
            Equal(StratagemTurn.Yours, StratagemTiming.FromText("WHEN: Your Movement phase or your Charge phase. TARGET: A unit."));
            Equal(StratagemTurn.Opponents, StratagemTiming.FromText("WHEN: Your opponent’s Movement phase. TARGET: A unit. EFFECT: Until the start of your turn."));
            Equal(StratagemTurn.Opponents, StratagemTiming.FromText("WHEN: Your opponent’s Movement or Charge phase. TARGET: A unit."));
            Equal(StratagemTurn.Yours, StratagemTiming.FromText("WHEN: Your Movement /Charge phase. TARGET: A unit."));
            Equal(StratagemTurn.Opponents, StratagemTiming.FromText("WHEN: Your opponent’s Movement/Charge phase. TARGET: A unit."));
            Equal(StratagemTurn.Opponents, StratagemTiming.FromText("WHEN: The Fight phase, during your opponent's turn. TARGET: A unit."));
            Equal(StratagemTurn.Unknown, StratagemTiming.FromText("Timing not supplied. EFFECT: Your Shooting phase."));
        });
        var source = new SourceInfo("Rules fixtures", "1", "", "", Edition: "11");
        var unit = new UnitDefinition("Death Guard", "Test unit", [new(1, 50)], Profile: new(Notes: "Keep me"), PointsSource: source, ProfileSource: source);
        var current = new Dataset(source, [unit], Detachments: [new("Death Guard", "Test detachment"), new("Orks", "Other")],
            ReferenceCards: [new("Orks", "Army Rules", "Other", "Keep this rule")]);
        var incoming = new Dataset(source, [], Detachments: [new("Death Guard", "Test detachment", "Disruption", "2 DP")],
            ReferenceCards: [new("Death Guard", "Stratagems", "Example", "WHEN: Now. TARGET: Friendly unit. EFFECT: Fixture.", "Test detachment", "1 CP", source)], ReferenceOnly: true);
        test("Rules-only import preserves units and other factions, updates metadata, and checks editions", () =>
        {
            var merged = DatasetMerge.Prepare(current, incoming, ImportLayer.Profiles).Dataset;
            Equal(DataJson.Write(current.Units), DataJson.Write(merged.Units));
            Equal(2, merged.Detachments!.Count); Equal("Disruption", merged.Detachments.Single(d => d.Faction == "Death Guard").Disposition);
            Equal(2, merged.ReferenceCards!.Count);
            Equal(2, DatasetMerge.Prepare(merged, incoming, ImportLayer.Profiles).Dataset.ReferenceCards!.Count);
            Reject(() => DatasetMerge.Prepare(current, incoming with { Source = source with { Edition = "10" } }, ImportLayer.Profiles));
            Reject(() => DatasetMerge.Prepare(current, incoming, ImportLayer.Points));
            Reject(() => DatasetMerge.Prepare(null, incoming, ImportLayer.Profiles));
            Reject(() => (incoming with { ReferenceCards = [] }).Validate());
        });
        test("Reference cards and dispositions survive SQLite without changing rosters", () =>
        {
            var path = Path.Combine(root, "references");
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(current); var library = store.LoadDataset()!;
                var army = ArmyList.New("Death Guard") with { Entries = [new("entry", library.Units.Single().Key, unit.Name, 1, "Existing notes")], Detachment = "Test detachment" };
                store.Save(army); var before = DataJson.Write(store.LoadArmies());
                store.ApplyImport(store.PreviewImport(incoming, ImportLayer.Profiles).Dataset);
                Equal(before, DataJson.Write(store.LoadArmies())); Equal(50, ArmyCalculator.Calculate(army, store.LoadDataset()).Total);
            }
            using (var store = WindowsServices.OpenStore(path))
            {
                Equal("2 DP", store.LoadDataset()!.Detachments!.Single(d => d.Faction == "Death Guard").Cost);
                Equal("1 CP", store.LoadDataset()!.ReferenceCards!.Single(c => c.Section == "Stratagems").Cost);
            }
        });
    }
}
