using System.Text.Json;
using GrimList.Application;
using GrimList.Core;

internal static class BattleDiscardChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid discard change accepted"); }
        BattleRecord New(string mode = "Tactical") => BattleRecord.Create(ArmyList.New("Fixture") with { Disposition = "Take and Hold" }, [],
            new("Purge the Foe", "Immovable Object", "Unstoppable Force", 12), "Opponent", true, mode, "Tactical",
            BattleMissions.Secondaries("Fixed").Take(2).Select(c => c.Id), []);
        string Id(string shortName) => "ca2026-secondary-" + shortName;
        BattleSecondary Add(BattleRecord b, string name, int player = 0)
        {
            b.AddSecondary(player, Id(name)); return b.Players[player].Secondaries.Single(c => c.CardId == Id(name));
        }
        void Score(BattleRecord b, BattleSecondary s, int amount)
        {
            var m = BattleMissions.Secondary(s.CardId, "Tactical"); b.ChangeCondition(0, m, m.Groups[0], m.Groups[0].Conditions[0], 0, amount);
        }

        test("End-turn discards grant one CP total and do not replenish ordinary draws", () =>
        {
            var b = New(); var a = Add(b, "no-prisoners"); var c = Add(b, "centre-ground");
            b.DiscardSecondary(0, a); b.DiscardSecondary(0, c);
            Check(b.Cp(0, 1) == 1 && b.DiscardCpGained(0, 1) == 1 && b.Cp(1, 1) == 0, "Discard reward duplicated or affected opponent");
            Reject(() => b.DiscardSecondary(0, a)); Reject(() => b.AddSecondary(0, Id("plunder")));
            b.UndoDiscard(0, a); Check(b.Cp(0, 1) == 1, "Undo removed the other card's reward");
            b.UndoDiscard(0, c); Check(b.Cp(0, 1) == 0, "Undo left free CP");
        });
        test("Discard rewards share the extra CP cap and preserve unrelated CP corrections", () =>
        {
            var b = New(); b.ChangeCp(0, "Core", 2); b.ChangeCp(0, "Extra", 1); var c = Add(b, "no-prisoners");
            b.DiscardSecondary(0, c); Check(b.Cp(0, 1) == 3 && b.DiscardCpGained(0, 1) == 0, "Other extra CP cap bypassed");
            b.UndoDiscard(0, c); Check(b.Cp(0, 1) == 3 && b.Players[0].Rounds[0].ExtraCp == 1, "Undo erased unrelated CP");
            b.DiscardSecondary(0, c); b.ChangeCp(0, "Extra", -1);
            Check(b.Cp(0, 1) == 3 && b.DiscardCpGained(0, 1) == 1, "Derived reward failed to recalculate");
            b.Navigate(1); b.Navigate(-1); Check(b.Cp(0, 1) == 3, "Navigation granted CP again");
        });
        test("Undoing a spent discard reward is rejected atomically, including later rounds", () =>
        {
            var b = New(); var c = Add(b, "no-prisoners"); b.DiscardSecondary(0, c); b.Round = 2; b.ChangeCp(0, "Spent", 1); b.Round = 1;
            Reject(() => b.UndoDiscard(0, c)); Check(c.DiscardedRound == 1 && b.Cp(0, 2) == 0, "Failed undo partially applied");
            b.Round = 2; b.ChangeCp(0, "Spent", -1); b.Round = 1; b.UndoDiscard(0, c); Check(b.Cp(0, 2) == 0, "Corrected undo failed");
        });
        test("Command redraw charges one CP, keeps history and uses its separate once-per-battle allowance", () =>
        {
            var b = New(); b.ChangeCp(0, "Core", 2); var c = Add(b, "no-prisoners"); Add(b, "centre-ground");
            b.DiscardSecondary(0, c, SecondaryDiscard.CommandRedraw, Id("plunder"));
            var replacement = b.Players[0].Secondaries.Single(s => s.CardId == Id("plunder"));
            Check(b.Cp(0, 1) == 1 && b.CpSpent(0, 1) == 1 && b.DiscardCpGained(0, 1) == 0, "Wrong redraw CP");
            Check(b.NormalDrawCount(0, 1) == 2 && replacement.ReplacesCardId == c.CardId && b.DiscardHistory(0, 1).Single().Contains("Plunder"), "Replacement consumed normal draw or lost history");
            Reject(() => b.DiscardSecondary(0, replacement, SecondaryDiscard.CommandRedraw, Id("outflank")));
            Reject(() => b.RemoveSecondary(0, replacement));
            b.UndoDiscard(0, c); Check(!b.CommandRedrawUsed(0) && b.Cp(0, 1) == 2 && b.Players[0].Secondaries.Count == 2, "Redraw undo didn't refund/restore");
        });
        test("Command redraw cannot spend future discard rewards or silently create invalid later balances", () =>
        {
            var b = New(); var c = Add(b, "no-prisoners"); var other = Add(b, "centre-ground"); b.DiscardSecondary(0, other);
            Reject(() => b.DiscardSecondary(0, c, SecondaryDiscard.CommandRedraw, Id("plunder")));
            Check(c.DiscardedRound == null && b.Players[0].Secondaries.Count == 2, "Rejected redraw mutated cards");
            b.ChangeCp(0, "Core", 1); b.DiscardSecondary(0, c, SecondaryDiscard.CommandRedraw, Id("plunder"));
            Reject(() => b.ChangeCp(0, "Core", -1)); // End-turn gain cannot pay the earlier redraw.
            var x = New(); x.ChangeCp(0, "Core", 1); var target = Add(x, "no-prisoners"); x.Round = 2; x.ChangeCp(0, "Spent", 1); x.Round = 1;
            Reject(() => x.DiscardSecondary(0, target, SecondaryDiscard.CommandRedraw, Id("plunder")));
            Check(target.DiscardedRound == null && !x.CommandRedrawUsed(0) && x.Cp(0, 2) == 0, "Failed redraw didn't roll back");
        });
        test("Redraw undo protects replacement scores and chained discard history", () =>
        {
            var b = New(); b.ChangeCp(0, "Core", 1); var c = Add(b, "centre-ground"); b.DiscardSecondary(0, c, SecondaryDiscard.CommandRedraw, Id("no-prisoners"));
            var replacement = b.Players[0].Secondaries.Last(); Score(b, replacement, 1); Reject(() => b.UndoDiscard(0, c));
            Score(b, replacement, -1); b.DiscardSecondary(0, replacement); Reject(() => b.UndoDiscard(0, c));
            b.UndoDiscard(0, replacement); b.UndoDiscard(0, c);
            Check(b.Players[0].Secondaries.Count == 1 && b.Cp(0, 1) == 1, "Undo chain left cards or CP behind");
        });
        test("Achieved cards cannot earn discard CP, and discarded cards cannot score", () =>
        {
            var b = New(); var c = Add(b, "no-prisoners"); Score(b, c, 1);
            Check(b.SecondaryStatus(0, c).StartsWith("Achieved"), "Achieved status not shown"); Reject(() => b.DiscardSecondary(0, c));
            Score(b, c, -1); b.DiscardSecondary(0, c); Reject(() => Score(b, c, 1));
            b.Round = 2; Reject(() => Score(b, c, 1)); b.Round = 1; b.UndoDiscard(0, c); Score(b, c, 1);
            Check(b.Cp(0, 1) == 0 && b.Total(0) == 12, "Correction kept discard reward and score");
        });
        test("Card-rule discards require explicit conditions, cost no CP and never stand in for shuffle-back", () =>
        {
            var b = New(); var c = Add(b, "a-grievous-blow");
            Reject(() => b.DiscardSecondary(0, c, SecondaryDiscard.CardRule, Id("no-prisoners")));
            b.Round = 2; Reject(() => b.DiscardSecondary(0, c, SecondaryDiscard.CardRule, Id("no-prisoners"), true)); b.Round = 1;
            b.DiscardSecondary(0, c, SecondaryDiscard.CardRule, Id("no-prisoners"), true);
            Check(b.Cp(0, 1) == 0 && !b.CommandRedrawUsed(0) && b.NormalDrawCount(0, 1) == 1, "Free discard consumed CP or draw allowance");
            b.UndoDiscard(0, c); var other = Add(b, "behind-enemy-lines");
            Reject(() => b.DiscardSecondary(0, other, SecondaryDiscard.CardRule, Id("plunder"), true));
        });
        test("Fixed, foreign, duplicate and incomplete discard requests leave state unchanged", () =>
        {
            var b = New("Fixed"); Reject(() => b.DiscardSecondary(0, b.Players[0].Secondaries[0]));
            b = New(); var c = Add(b, "no-prisoners"); b.ChangeCp(0, "Core", 2);
            Reject(() => b.DiscardSecondary(1, c)); Reject(() => b.DiscardSecondary(0, c, SecondaryDiscard.CommandRedraw));
            Reject(() => b.DiscardSecondary(0, c, SecondaryDiscard.CommandRedraw, c.CardId));
            Reject(() => b.DiscardSecondary(0, c, SecondaryDiscard.EndTurn, Id("plunder")));
            Check(c.DiscardedRound == null && b.Cp(0, 1) == 2 && b.Players[0].Secondaries.Count == 1, "Invalid request partially mutated state");
        });
        test("Discard persistence retains reasons, replacements and CP; legacy manual discards grant nothing", () =>
        {
            async Task Run()
            {
                var store = new BattleStore(Path.Combine(root, "discard-persistence")); var b = New(); b.ChangeCp(0, "Core", 2);
                var c = Add(b, "no-prisoners"); b.DiscardSecondary(0, c, SecondaryDiscard.CommandRedraw, Id("plunder"));
                b.DiscardSecondary(0, b.Players[0].Secondaries.Last()); await store.SaveAsync(b);
                var loaded = (await store.LoadAsync(b.ArmySnapshot.Id)).Single();
                Check(loaded.SchemaVersion == 5 && loaded.Cp(0, 1) == 2 && loaded.CpSpent(0, 1) == 1 && loaded.DiscardHistory(0, 1).Count() == 2, "Discard state didn't round-trip");
                var legacy = New(); var old = Add(legacy, "centre-ground"); old.DiscardedRound = 1; legacy.SchemaVersion = 1;
                var path = Path.Combine(root, "discard-persistence", "battles", legacy.Id + ".json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy, BattleStore.Json));
                var reopened = (await store.LoadAsync(legacy.ArmySnapshot.Id)).Single();
                Check(reopened.Cp(0, 1) == 0 && reopened.SecondaryStatus(0, reopened.Players[0].Secondaries[0]).Contains("older version"), "Legacy discard received retroactive CP");
                await store.SaveAsync(reopened); Check(reopened.SchemaVersion == 5, "Legacy save not upgraded");
            }
            Run().GetAwaiter().GetResult();
        });
    }
}
