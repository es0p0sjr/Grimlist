using GrimList.Application;
using GrimList.Core;

internal static class DispositionPrimaryChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        string[] dispositions = ["Take and Hold", "Purge the Foe", "Disruption", "Reconnaissance", "Priority Assets"];
        var source = new SourceInfo("Primary fixture", "1", "2026-10-14", "", Edition: "11");
        var data = new Dataset(source, [new("Fixture", "Unit", [new(1, 50)])], Detachments:
            [new("Fixture", "Orders", Cost: "1 DP", Dispositions: [.. dispositions, "Custom orders"])]);
        var army = ArmyList.New("Fixture") with { Detachments = ["Orders"], Disposition = dispositions[0] };
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
        test("All 25 primary assignments match GW Event Companion v1.2, including reverse matchups", () =>
        {
            // Independent transcription of the five disposition rows; pages 9-53.
            string[][] expected =
            [
                ["Battlefield Dominance", "Immovable Object", "Determined Acquisition", "Purge and Secure", "Inescapable Dominion"],
                ["Unstoppable Force", "Meatgrinder", "Punishment", "Consecrate", "Destroyer's Wrath"],
                ["Death Trap", "Delaying Action", "Outmanoeuvre", "Smoke and Mirrors", "Locate and Deny"],
                ["Reconnaissance Sweep", "Triangulation", "Surveil the Foe", "Gather Intel", "Search and Scour"],
                ["Secure Asset", "Vital Link", "Extract Relic", "Vanguard Operation", "Sabotage"]
            ];
            for (var i = 0; i < 5; i++)
            {
                var rows = DispositionPrimaries.ForArmy(army with { Disposition = dispositions[i] }, data).Matchups;
                Check(rows.Count == 5 && rows.Select(r => r.OpponentDisposition).SequenceEqual(dispositions), "Missing or duplicated opponents");
                for (var j = 0; j < 5; j++)
                    Check(rows[j].YourPrimary == expected[i][j] && rows[j].OpponentPrimary == expected[j][i], $"Wrong direction: {dispositions[i]} vs {dispositions[j]}");
            }
        });
        test("Primary matchups require a known selected disposition, matching game and 11th edition", () =>
        {
            Check(DispositionPrimaries.ForArmy(army with { Disposition = " take AND hold " }, data).Matchups.Count == 5, "Case normalization failed");
            foreach (var choice in new[] { "", "Custom orders", "Invalid" })
                Check(DispositionPrimaries.ForArmy(army with { Disposition = choice }, data).Matchups.Count == 0, "Invented primary for " + choice);
            Check(DispositionPrimaries.ForArmy(army, null).Matchups.Count == 0, "Missing data accepted");
            Check(DispositionPrimaries.ForArmy(army with { Detachments = [] }, data).Matchups.Count == 0, "Removed detachment retained primaries");
            foreach (var edition in new[] { "10", "", "12" })
                Check(DispositionPrimaries.ForArmy(army, data with { Source = source with { Edition = edition } }).Matchups.Count == 0, "Wrong edition accepted");
            Check(DispositionPrimaries.ForArmy(army, data with { Game = "Other" }).Matchups.Count == 0, "Mismatched game accepted");
            Check(DispositionPrimaries.ForArmy(army with { Game = "Other" }, data with { Game = "Other" }).Matchups.Count == 0, "Other game accepted");
        });
        test("Primary matchups follow saved selection, undo, redo, removal and reopening", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "primary-session"));
            store.Import(data); store.Save(army);
            var session = new ArmySession(store); session.Open(army.Id);
            string First() => DispositionPrimaries.ForArmy(session.Army, session.Dataset).Matchups[0].YourPrimary;
            session.SelectDisposition("Purge the Foe"); Check(First() == "Unstoppable Force", "Selection did not update");
            session.Undo(); Check(First() == "Battlefield Dominance", "Undo did not update");
            session.Redo(); Check(First() == "Unstoppable Force", "Redo did not update");
            session.RemoveDetachment("Orders");
            Check(DispositionPrimaries.ForArmy(session.Army, session.Dataset).Matchups.Count == 0, "Removal left stale rows");
            session.Undo(); Check(First() == "Unstoppable Force", "Undo removal lost rows");
            session = new ArmySession(store); session.Open(army.Id);
            Check(First() == "Unstoppable Force", "Reopen lost rows");
        });
    }
}
