using GrimList.Application;
using GrimList.Core;

internal static class BattleTrackerChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid change accepted"); }
        BattleRecord New(string primary = "Battlefield Dominance", string mode = "Tactical", bool first = true) => BattleRecord.Create(
            ArmyList.New("Fixture") with { Name = "Your army", Disposition = "Take and Hold" }, [],
            new("Purge the Foe", primary, "Unstoppable Force", 12), "Opponent", first, mode, "Tactical",
            BattleMissions.Secondaries("Fixed").Take(2).Select(c => c.Id), []);
        void Score(BattleRecord b, int player, BattleMission card, int group, int row, int turn, int value)
            => b.ChangeCondition(player, card, card.Groups[group], card.Groups[group].Conditions[row], turn, value);
        BattleMission Secondary(string name, string mode = "Tactical") => BattleMissions.Secondaries(mode).Single(c => c.Name == name);

        test("Battle pack contains all 25 primaries, 18 Tactical and four Fixed variants offline", () =>
        {
            Check(BattleMissions.All.Count == 47, "Incomplete catalogue");
            Check(BattleMissions.All.Sum(c => c.Groups.Sum(g => g.Conditions.Count)) == 131, "Missing mode-specific conditions");
            Check(BattleMissions.Primary("Purge and Secure").Groups.Count == 3, "Different timings merged");
            Check(Secondary("Plunder").Instructions.Contains("terrain area, not the acting unit"), "FAQ lost");
        });
        test("Battle setup orders players by first turn and snapshots army without mutation", () =>
        {
            var b = New(first: false);
            Check(b.Players[0].Name == "Opponent" && b.Players[1].Name == "Your army", "Wrong player order");
            Check(b.Players[1].Primary == "Battlefield Dominance" && b.Total(0) == 10 && b.Total(1) == 10, "Primary/BR missing");
            Check(b.Code.Length == 10 && b.Code != New().Code, "Battle code not generated");
            var army = b.ArmySnapshot; var copy = BattleRecord.Create(army, [], new("Purge the Foe", "Immovable Object", "Unstoppable Force", 12), "Other", true, "Tactical", "Tactical", [], []);
            army.Entries.Add(new("x", "u", "Test", 1)); Check(copy.ArmySnapshot.Entries.Count == 0, "Snapshot shares mutable list");
        });
        test("Primary counts, cumulative subsets and 15 VP cap preserve corrections", () =>
        {
            var b = New(); b.Round = 2; var c = BattleMissions.Primary(b.Players[0].Primary);
            Score(b, 0, c, 1, 0, 0, 5); Score(b, 0, c, 1, 1, 0, 4);
            Check(b.Scores(0).Where(l => l.Round == 2).Sum(l => l.Vp) == 15, "Round cap ignored");
            Check(b.Scores(0).Sum(l => l.RawVp) == 23, "Underlying score lost");
            Score(b, 0, c, 1, 0, 0, -3);
            Check(b.Scores(0).Sum(l => l.Vp) == 10, "Bonus subset didn't recalculate");
            Check(b.Count(0, c.Groups[1].Conditions[1], 0, 2) == 4, "Correction erased bonus evidence");
            Score(b, 0, c, 1, 0, 0, -2); Check(b.Total(0) == 10, "Bonus scored without base");
        });
        test("OR choices replace each other while independent conditions add", () =>
        {
            var b = New("Triangulation"); b.Round = 2; var c = BattleMissions.Primary("Triangulation");
            Score(b, 0, c, 1, 0, 0, 1); Score(b, 0, c, 1, 1, 0, 1); Score(b, 0, c, 1, 2, 0, 1);
            Check(b.Scores(0).Sum(l => l.Vp) == 10 && b.Scores(0).Count(l => l.Count > 0) == 1, "OR options stacked");
            Score(b, 0, c, 0, 0, 0, 1); Check(b.Scores(0).Sum(l => l.Vp) == 14, "Independent score lost");
        });
        test("Round restrictions and fifth-round timing are enforced", () =>
        {
            var b = New(); var c = BattleMissions.Primary(b.Players[0].Primary);
            Reject(() => Score(b, 0, c, 1, 0, 0, 1));
            Check(c.Groups[1].Label(0, 0, 5) == "End of your turn", "Round-five timing wrong");
            Check(Secondary("Beacon").Groups[0].Turns(1, 5).Single() == 1, "Fifth-round fallback wrong");
            Check(Secondary("Beacon").Groups[0].Turns(0, 5).Single() == 1, "Opponent turn wrong");
        });
        test("Both turns share the primary round cap; other player and secondary caps stay separate", () =>
        {
            var b = New("Punishment"); b.Round = 2; var c = BattleMissions.Primary("Punishment");
            Score(b, 0, c, 0, 0, 0, 1); Score(b, 0, c, 0, 0, 1, 1); Score(b, 0, c, 1, 0, 0, 1); Score(b, 0, c, 1, 1, 0, 1);
            var secondary = Secondary("No Prisoners"); b.AddSecondary(0, secondary.Id); Score(b, 0, secondary, 0, 0, 0, 3);
            Check(b.Scores(0).Where(l => l.Primary).Sum(l => l.Vp) == 15, "Turn caps counted separately");
            Check(b.Total(0) == 30 && b.Total(1) == 10, "Caps leak between categories or players");
        });
        test("Primary battle cap is 45 and end-of-battle scoring bypasses only the round cap", () =>
        {
            var b = New("Punishment"); var c = BattleMissions.Primary("Punishment");
            b.Round = 5; Score(b, 0, c, 0, 0, 0, 1); Score(b, 0, c, 0, 0, 1, 1); Score(b, 0, c, 1, 0, 0, 1); Score(b, 0, c, 1, 1, 0, 1);
            b.Navigate(1); Score(b, 0, c, 2, 0, 2, 1); Check(b.Total(0) == 33, "End-battle bonus treated as round-five score");
            b.Scoreboard = false;
            for (var r = 2; r <= 3; r++) { b.Round = r; Score(b, 0, c, 0, 0, 0, 1); Score(b, 0, c, 0, 0, 1, 1); Score(b, 0, c, 1, 0, 0, 1); Score(b, 0, c, 1, 1, 0, 1); }
            Check(b.Total(0) == 55, "Battle limit ignored");
        });
        test("Secondary round cap covers multiple cards and both turns, with chronological recalculation", () =>
        {
            var b = New(mode: "Fixed"); var blow = Secondary("A Grievous Blow", "Fixed"); var assassination = Secondary("Assassination", "Fixed");
            Score(b, 0, blow, 0, 0, 0, 2); Score(b, 0, blow, 0, 0, 1, 2); Score(b, 0, assassination, 0, 0, 1, 2);
            Check(b.Scores(0).Where(l => !l.Primary).Sum(l => l.Vp) == 15 && b.Scores(0).Sum(l => l.RawVp) == 22, "Secondary round cap not shared");
            Score(b, 0, blow, 0, 0, 0, -2);
            Check(b.Scores(0).Where(l => !l.Primary).Sum(l => l.Vp) == 14, "Secondary correction didn't recalculate");
            Check(b.Total(1) == 10, "Opponent affected");
        });
        test("Command-phase awards consume round allowance before end-of-turn conditions", () =>
        {
            var b = New(); b.Round = 2; var c = BattleMissions.Primary(b.Players[0].Primary);
            Score(b, 0, c, 0, 0, 0, 1); Score(b, 0, c, 1, 0, 0, 5);
            Check(b.Scores(0).Single(l => l.Round == 2 && l.Timing.StartsWith("End of your turn")).Vp == 0, "Caps allocated out of phase order");
        });
        test("Fixed cards have correct mode-specific VP, 20 VP cap and cumulative bonuses", () =>
        {
            var b = New(mode: "Fixed"); var c = Secondary("A Grievous Blow", "Fixed");
            for (var r = 1; r <= 5; r++) { b.Round = r; Score(b, 0, c, 0, 0, 0, 2); }
            Check(b.Scores(0).Sum(l => l.Vp) == 20, "Fixed card cap missing");
            b.Round = 1; Score(b, 0, c, 0, 0, 0, -1);
            Check(b.Scores(0).Where(l => l.Round == 3).Sum(l => l.Vp) == 8, "Earlier correction failed to reallocate Fixed allowance");
            var a = Secondary("Assassination", "Fixed"); Score(b, 0, a, 0, 0, 0, 2); Score(b, 0, a, 0, 1, 0, 1);
            Check(b.Scores(0).Where(l => l.Mission == a.Name).Sum(l => l.Vp) == 7, "Wrong Fixed assassination values");
        });
        test("Tactical scoring is once per card, caps counted units and preserves both timing choices", () =>
        {
            var b = New(); var c = Secondary("No Prisoners"); b.AddSecondary(0, c.Id); Score(b, 0, c, 0, 0, 0, 3);
            Check(b.Scores(0).Sum(l => l.Vp) == 5 && b.Scores(0).Single(l => l.RawVp > 0).Count == 3, "Local cap lost count");
            Reject(() => Score(b, 0, c, 0, 0, 1, 1)); b.Round = 2; Reject(() => Score(b, 0, c, 0, 0, 0, 1));
            Check(!b.VisibleSecondaries(0).Any(), "Achieved card carried forward");
            Check(Secondary("Display Of Might").Groups.Count == 2, "Lost alternative timing");
        });
        test("Draw timing, carry-over, discard and card-selection correction respect history", () =>
        {
            var b = New(); var c = Secondary("No Prisoners"); b.AddSecondary(1, c.Id);
            Reject(() => Score(b, 1, c, 0, 0, 0, 1)); b.Round = 2; Check(b.VisibleSecondaries(1).Count() == 1, "Unscored card didn't carry");
            Score(b, 1, c, 0, 0, 0, 1); b.Round = 1; Reject(() => b.DiscardSecondary(1, b.Players[1].Secondaries[0]));
            b.Round = 2; Score(b, 1, c, 0, 0, 0, -1); b.Round = 1; b.DiscardSecondary(1, b.Players[1].Secondaries[0]);
            b.Round = 2; Reject(() => Score(b, 1, c, 0, 0, 0, 1));
            b.Round = 1; b.UndoDiscard(1, b.Players[1].Secondaries[0]);
            b.RemoveSecondary(1, b.Players[1].Secondaries[0]); Check(b.Players[1].Secondaries.Count == 0, "Selection correction failed");
        });
        test("CP navigation never grants resources and corrections protect later spending", () =>
        {
            var b = New(); Reject(() => b.ChangeCp(0, "Spent", 1)); b.ChangeCp(0, "Core", 2); b.ChangeCp(0, "Extra", 2);
            Check(b.Cp(0, 1) == 3, "Extra CP limit not applied"); b.Navigate(1); b.ChangeCp(0, "Spent", 3); b.Navigate(-1);
            Reject(() => b.ChangeCp(0, "Core", -1)); Check(b.Cp(0, 2) == 0 && b.Cp(0, 1) == 3, "CP correction corrupts later balance");
            for (var i = 0; i < 8; i++) b.Navigate(1);
            Check(b.Round == 5 && b.Scoreboard && b.Cp(0, 5) == 0, "Round navigation reset or duplicated resources");
            b.Navigate(-1); Check(!b.Scoreboard && b.Round == 5, "Scoreboard back navigation failed");
        });
        test("Battle save/resume retains scores, snapshots, codes and independent histories", () =>
        {
            async Task Run()
            {
                var store = new BattleStore(Path.Combine(root, "battles-fixture")); var b = New();
                b.ChangeCp(0, "Core", 2); b.Navigate(1); var c = BattleMissions.Primary(b.Players[0].Primary); Score(b, 0, c, 1, 0, 0, 3);
                await store.SaveAsync(b); var loaded = (await store.LoadAsync(b.ArmySnapshot.Id)).Single();
                Check(loaded.Total(0) == 19 && loaded.Round == 2 && loaded.Cp(0, 2) == 2 && loaded.Code == b.Code, "Round trip lost state");
                var second = New(); second.ArmySnapshot = b.ArmySnapshot; await store.SaveAsync(second);
                Check((await store.LoadAsync(b.ArmySnapshot.Id)).Count == 2, "New battle overwrote previous battle");
                Check((await store.LoadAsync("Other army")).Count == 0, "Wrong army history loaded");
            }
            Run().GetAwaiter().GetResult();
        });
    }
}
