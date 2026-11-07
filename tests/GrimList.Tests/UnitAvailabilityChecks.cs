using GrimList.Core;

internal static class UnitAvailabilityChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        var ally = new UnitDefinition("Death Guard", "Daemon ally", [new(1, 70)], Profile: new(Keywords: ["Daemon", "Faction: Plague Legions"]));
        var native = new UnitDefinition("Death Guard", "Native daemon", [new(1, 100)], Profile: new(Keywords: ["Daemon", "Faction: Death Guard"]));
        var data = new Dataset(new("Availability fixture", "1", "", "", Edition: "11"), [ally, native],
            Detachments: [new("Death Guard", "Test host", Cost: "1 DP"), new("Death Guard", "Tallyband Summoners", Cost: "1 DP")]);
        var army = ArmyList.New("Death Guard") with { Detachments = ["Test host"], Entries = [new("ally", ally.Key, ally.Name, 1)] };
        test("Allied faction availability follows detachments without restricting native daemons", () =>
        {
            Check(UnitAvailability.Problem(army, ally, data) != null, "Allied unit allowed without its detachment");
            Check(UnitAvailability.Problem(army, native, data) == null, "Native daemon was hidden");
            var allowed = DetachmentRules.WithSelection(army, ["Test host", "tallyband summoners"]);
            Check(UnitAvailability.Problem(allowed, ally, data) == null, "Selecting required detachment did not unlock ally");
            Check(!ArmyCalculator.Calculate(allowed, data).Issues.Any(i => i.Contains("requires Tallyband")), "Allowed ally flagged");
            var removed = DetachmentRules.WithSelection(allowed, ["Test host"]);
            Check(removed.Entries.Count == 1 && ArmyCalculator.Calculate(removed, data).Issues.Any(i => i.Contains("requires Tallyband")), "Detachment removal must retain and flag existing ally");
            Check(UnitAvailability.Problem(army, ally, data with { Source = data.Source with { Edition = "10" } }) == null, "Eleventh edition restriction leaked into tenth edition");
        });
        test("Unit availability metadata supports alternative detachments and survives import layers and persistence", () =>
        {
            var restricted = native with { RequiredDetachments = ["Alternative", "Tallyband Summoners"] };
            Check(UnitAvailability.Problem(army, restricted, data) != null, "Explicit restriction ignored");
            Check(UnitAvailability.Problem(DetachmentRules.WithSelection(army, ["Alternative"]), restricted, data) == null, "Alternative unlock ignored");
            Check(UnitAvailability.Problem(army, ally with { RequiredDetachments = [] }, data) == null, "Explicit unrestricted override ignored");
            var original = data with { Units = [restricted] };
            var points = original with { Units = [native with { Profile = null, Options = [new(1, 90)] }] };
            var merged = DatasetMerge.Prepare(original, points, ImportLayer.Points).Dataset;
            Check(merged.Units.Single().RequiredDetachments!.SequenceEqual(restricted.RequiredDetachments!), "Points update lost availability metadata");
            using var store = WindowsServices.OpenStore(Path.Combine(root, "availability-persistence"));
            store.Import(merged);
            Check(store.LoadDataset()!.Units.Single().RequiredDetachments!.Count == 2, "Availability metadata lost on save");
        });
        File.WriteAllText(Path.Combine(root, "availability-fixture.json"), DataJson.Write(data));
    }
}
