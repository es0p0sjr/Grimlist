using GrimList.Application;
using GrimList.Core;

internal static class BattleActionChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid battle action accepted"); }
        BattleRecord New(string mode = "Tactical") => BattleRecord.Create(ArmyList.New("Fixture") with { Disposition = "Take and Hold" }, [],
            new("Purge the Foe", "Immovable Object", "Unstoppable Force", 12), "Opponent", true, mode, "Tactical",
            BattleMissions.Secondaries("Fixed").Take(2).Select(c => c.Id), []);
        const string noPrisoners = "ca2026-secondary-no-prisoners", plunder = "ca2026-secondary-plunder";

        test("Wrong-card discard frees a normal draw slot and returns the card without changing CP", () =>
        {
            var b = New(); b.ChangeCp(0, "Core", 2); b.AddSecondary(0, noPrisoners); b.AddSecondary(0, plunder);
            b.DiscardWrongCard(0, b.Players[0].Secondaries[0]);
            Check(b.Cp(0, 1) == 2 && b.DrawSlotsRemaining(0) == 1 && !b.CommandRedrawUsed(0), "Correction changed CP/draw allowance");
            Check(b.RandomDrawPool(0).Any(c => c.Id == noPrisoners) && b.DiscardHistory(0, 1).Single().Contains("Card discarded"), "Correction pool/history missing");
            b.AddSecondary(0, noPrisoners); Check(b.DrawSlotsRemaining(0) == 0, "Replacement slot not filled");
        });
        test("Wrong replacement can be corrected manually or randomly without paying twice", () =>
        {
            var b = New(); b.ChangeCp(0, "Core", 1); b.AddSecondary(0, noPrisoners); b.AddSecondary(0, "ca2026-secondary-centre-ground");
            var original = b.Players[0].Secondaries[0]; b.DiscardSecondary(0, original, SecondaryDiscard.CommandRedraw, plunder);
            b.DiscardWrongCard(0, b.Players[0].Secondaries.Single(s => s.CardId == plunder));
            Check(b.DrawSlotsRemaining(0) == 1 && b.Cp(0, 1) == 0 && b.CommandRedrawUsed(0), "Paid redraw was refunded or lost");
            b.AddSecondary(0, plunder); Check(b.Players[0].Secondaries.Last().ReplacesCardId == noPrisoners, "Manual redraw lost parent");
            b.DiscardWrongCard(0, b.Players[0].Secondaries.Last()); var next = b.DrawRandomSecondary(0);
            Check(next.ReplacesCardId == noPrisoners && b.DrawSlotsRemaining(0) == 0 && b.CpSpent(0, 1) == 1, "Random replacement charged twice or used a normal slot");
            b.UndoDiscard(0, original); Check(b.Cp(0, 1) == 1 && !b.CommandRedrawUsed(0), "Redraw correction broke undo");
        });
        test("Wrong-card correction protects scored, foreign, previously discarded and Fixed cards", () =>
        {
            var b = New(); b.AddSecondary(0, noPrisoners); var c = b.Players[0].Secondaries[0];
            Reject(() => b.DiscardWrongCard(1, c)); b.Round = 2; Reject(() => b.DiscardWrongCard(0, c)); b.Round = 1;
            var mission = BattleMissions.Secondary(noPrisoners, "Tactical"); b.ChangeCondition(0, mission, mission.Groups[0], mission.Groups[0].Conditions[0], 0, 1);
            Reject(() => b.DiscardWrongCard(0, c)); b.ChangeCondition(0, mission, mission.Groups[0], mission.Groups[0].Conditions[0], 0, -1);
            b.DiscardSecondary(0, c); Reject(() => b.DiscardWrongCard(0, c));
            var fixedBattle = New("Fixed"); Reject(() => fixedBattle.DiscardWrongCard(0, fixedBattle.Players[0].Secondaries[0]));
            Check(b.Players[0].CardCorrections.Count == 0, "Rejected actions changed history");
        });
        test("Random draws sample remaining cards without duplicates and preserve per-player pools", () =>
        {
            var b = New(); var first = b.DrawRandomSecondary(0); var second = b.DrawRandomSecondary(0);
            Check(first.CardId != second.CardId && first.DrawnRandomly && second.DrawnRandomly, "Duplicate random draw or missing provenance");
            Reject(() => b.DrawRandomSecondary(0)); b.AddSecondary(1, first.CardId);
            Check(b.RandomDrawPool(0).All(c => c.Id != first.CardId && c.Id != second.CardId), "Already drawn card still in pool");
            Check(b.RandomDrawPool(1).Any(c => c.Id == second.CardId), "Player decks were shared");
            Check(b.RandomDrawPool(0).All(c => c.Id != "ca2026-secondary-defend-stronghold"), "Mandatory round-one redraw ignored");
            b.Round = 2; Check(b.RandomDrawPool(0).Any(c => c.Id == "ca2026-secondary-defend-stronghold"), "Deferred card removed permanently");
            var third = b.DrawRandomSecondary(0); Check(third.CardId != first.CardId && third.CardId != second.CardId, "Later round duplicated a card");
            var fixedBattle = New("Fixed"); Reject(() => fixedBattle.DrawRandomSecondary(0));
        });
        test("Exhausted random deck gives a clear failure without changing CP or state", () =>
        {
            var b = New(); b.Players[0].Secondaries = BattleMissions.Secondaries("Tactical").Select(c => new BattleSecondary { CardId = c.Id, DrawnRound = 1 }).ToList(); b.Round = 2;
            Reject(() => b.DrawRandomSecondary(0)); Check(b.Players[0].Secondaries.Count == 18 && b.Cp(0, 2) == 0, "Exhausted draw changed state");
        });
        test("Finishing early keeps scores and CP, records the played round and can be reopened", () =>
        {
            var b = New(); b.Round = 2; b.ChangeCp(0, "Core", 3); b.DrawRandomSecondary(0); var before = b.Total(0);
            b.Finish(); Check(b.Completed && b.Scoreboard && b.FinishedRound == 2 && b.Round == 2 && b.Total(0) == before && b.Cp(0, 2) == 3, "Finish altered scoring or jumped to five");
            Reject(() => b.DrawRandomSecondary(0)); b.Navigate(-1); Check(b.Round == 2 && !b.Scoreboard && b.Completed, "Review jumped to round five");
            b.Reopen(); Check(!b.Completed && !b.Scoreboard && b.FinishedRound == null && b.Round == 2, "Reopen lost round");
            b.DrawRandomSecondary(0); b.Round = 1; b.Finish(); Check(b.FinishedRound == 2, "Finishing from an earlier view hid later records");
        });
        test("Finished state and random/correction metadata survive save and reopen", () =>
        {
            async Task Run()
            {
                var store = new BattleStore(Path.Combine(root, "action-save")); var b = New(); var c = b.DrawRandomSecondary(0); b.DiscardWrongCard(0, c); b.DrawRandomSecondary(0); b.Finish();
                await store.SaveAsync(b); var loaded = (await store.LoadAsync(b.ArmySnapshot.Id)).Single();
                Check(loaded.SchemaVersion == 5 && loaded.Completed && loaded.FinishedRound == 1 && loaded.Players[0].Secondaries[0].DrawnRandomly
                    && loaded.Players[0].CardCorrections.Count == 1, "Save lost lifecycle metadata");
            }
            Run().GetAwaiter().GetResult();
        });
        test("Deleting removes only the selected battle and cannot be undone by a queued stale save", () =>
        {
            async Task Run()
            {
                var store = new BattleStore(Path.Combine(root, "action-delete")); var b = New(); var keep = New(); keep.ArmySnapshot = b.ArmySnapshot;
                await store.SaveAsync(b); await store.SaveAsync(keep);
                var saving = store.SaveAsync(b); var deleting = store.DeleteAsync(b.Id); await Task.WhenAll(saving, deleting);
                var remaining = await store.LoadAsync(b.ArmySnapshot.Id); Check(remaining.Count == 1 && remaining[0].Id == keep.Id, "Wrong battle deleted");
                try { await store.SaveAsync(b); throw new Exception("Deleted battle resurrected"); } catch (InvalidDataException) { }
                try { await store.DeleteAsync("../" + keep.Id); throw new Exception("Invalid deletion path accepted"); } catch (InvalidDataException) { }
                await store.DeleteAsync(b.Id); Check((await store.LoadAsync(keep.ArmySnapshot.Id)).Count == 1, "Repeated delete damaged remaining log");
            }
            Run().GetAwaiter().GetResult();
        });
    }
}
