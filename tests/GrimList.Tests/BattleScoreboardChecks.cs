using GrimList.Application;
using GrimList.Core;

internal static class BattleScoreboardChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid card action accepted"); }
        BattleRecord New() => BattleRecord.Create(ArmyList.New("Fixture"), [],
            new("Purge the Foe", "Immovable Object", "Unstoppable Force", 12), "Opponent", true, "Tactical", "Tactical", [], []);
        const string cardId = "ca2026-secondary-no-prisoners";
        test("Recover restores discarded cards without erasing history, consuming draws or changing CP", () =>
        {
            var b = New(); b.AddSecondary(0, cardId); var card = b.Players[0].Secondaries.Single(); b.DiscardCard(0, card);
            b.Round = 2; var slots = b.DrawSlotsRemaining(0); b.RecoverCard(0, card);
            Check(!b.DiscardPool(0).Any() && b.VisibleSecondaries(0).Contains(card) && b.DrawSlotsRemaining(0) == slots,
                "Recovery did not restore active card or consumed a normal draw");
            Check(b.RoundScoreboard(0, 1).Discarded.Single() == "No Prisoners" && b.RoundScoreboard(0, 2).Recovered.Single() == "No Prisoners", "Recovery erased discard history");
            Reject(() => b.RecoverCard(0, card)); b.Round = 1;
            Check(!b.VisibleSecondaries(0).Contains(card), "Recovered card leaked into earlier round");
            b.Round = 2; b.DiscardCard(0, card); b.RecoverCard(0, card);
            Check(b.RoundScoreboard(0, 2).Discarded.Count == 1 && b.RoundScoreboard(0, 2).Recovered.Count == 2 && b.Cp(0, 2) == 0,
                "Repeated recovery lost events or changed CP");
        });
        test("Recovering legacy automatic discards preserves saved CP rewards and costs", () =>
        {
            var b = New(); b.ChangeCp(0, "Core", 1); b.AddSecondary(0, cardId); var card = b.Players[0].Secondaries.Single();
            b.DiscardSecondary(0, card, SecondaryDiscard.CommandRedraw, "ca2026-secondary-centre-ground");
            b.RecoverCard(0, card);
            Check(b.Cp(0, 1) == 0 && b.CpSpent(0, 1) == 1 && b.CommandRedrawUsed(0), "Recovery refunded legacy redraw");
            b.DiscardSecondary(0, card); b.ChangeCp(0, "Spent", 1); b.RecoverCard(0, card);
            Check(b.Cp(0, 1) == 0 && b.CpGained(0, 1) == 2, "Recovery removed a spent legacy reward");
        });
        test("Manual discard excludes the card from all later draws without changing CP", () =>
        {
            var b = New(); b.ChangeCp(0, "Core", 2); b.AddSecondary(0, cardId);
            var card = b.Players[0].Secondaries.Single(); b.DiscardCard(0, card);
            Check(b.Cp(0, 1) == 2 && b.RandomDrawPool(0).All(c => c.Id != cardId), "Discard changed CP or returned card to pool");
            Reject(() => b.AddSecondary(0, cardId)); Reject(() => b.ShuffleBack(0, card));
            var replacement = b.DrawRandomSecondary(0);
            Check(replacement.ReplacesCardId == cardId && replacement.CardId != cardId, "Replacement lost discard linkage");
            b.Round = 2; Reject(() => b.AddSecondary(0, cardId));
            Check(b.RandomDrawPool(1).Any(c => c.Id == cardId), "Opponent deck was affected");
            Check(b.RoundScoreboard(0, 1).Discarded.SequenceEqual(new[] { "No Prisoners" }), "Discard absent from its round");
        });
        test("Shuffle Back permits later redraw while preserving round history and manual CP", () =>
        {
            var b = New(); b.AddSecondary(0, cardId); b.ChangeCp(0, "Core", 1); b.ChangeCp(0, "Spent", 1);
            b.ShuffleBack(0, b.Players[0].Secondaries.Single());
            Check(b.DrawSlotsRemaining(0) == 2 && b.RandomDrawPool(0).Any(c => c.Id == cardId) && b.CpSpent(0, 1) == 1, "Shuffle changed slot, pool or CP");
            b.Round = 2; b.AddSecondary(0, cardId);
            var mission = BattleMissions.Secondary(cardId, "Tactical");
            b.ChangeCondition(0, mission, mission.Groups[0], mission.Groups[0].Conditions[0], 0, 1);
            Check(b.RoundScoreboard(0, 1).ShuffledBack.Single() == "No Prisoners" && b.RoundScoreboard(0, 1).Secondary.Count == 0,
                "Later draw overwrote shuffle history");
            Check(b.RoundScoreboard(0, 2).Secondary.Single().Name == "No Prisoners", "Redrawn card did not score");
            Reject(() => b.ShuffleBack(0, b.Players[0].Secondaries.Single())); Reject(() => b.DiscardCard(0, b.Players[0].Secondaries.Single()));
        });
        test("Carried cards can be removed while scored, foreign and completed selections are protected", () =>
        {
            var b = New(); b.AddSecondary(0, cardId); var card = b.Players[0].Secondaries.Single(); b.Round = 2;
            Reject(() => b.DiscardCard(1, card)); Reject(() => b.ShuffleBack(1, card));
            b.Finish(); Reject(() => b.DiscardCard(0, card)); b.Reopen(); b.DiscardCard(0, card);
            Check(b.RoundScoreboard(0, 2).Discarded.Count == 1 && b.RoundScoreboard(0, 1).Discarded.Count == 0, "Carried discard attributed to draw round");
            b.UndoDiscard(0, card); b.ShuffleBack(0, card);
            Check(b.RoundScoreboard(0, 2).ShuffledBack.Count == 1 && b.Cp(0, 2) == 0, "Carried shuffle lost history or changed CP");
        });
        test("Compact scoreboard aggregates capped mission points without condition explanations", () =>
        {
            var b = New(); b.AddSecondary(0, cardId); var mission = BattleMissions.Secondary(cardId, "Tactical");
            for (var i = 0; i < 10; i++) b.ChangeCondition(0, mission, mission.Groups[0], mission.Groups[0].Conditions[0], 0, 1);
            var summary = b.RoundScoreboard(0, 1); var expected = b.Scores(0).Where(l => !l.Primary && l.Round == 1).Sum(l => l.Vp);
            Check(summary.Secondary.Count == 1 && summary.Secondary.Single().Vp == expected && summary.SecondaryVp == expected,
                "Scoreboard did not aggregate capped awards");
            var text = b.ScoreboardCards(0, 1, false);
            Check(text.Contains("Scored: No Prisoners") && !text.Contains(mission.Groups[0].Conditions[0].Text), "Scoreboard still explains conditions");
            Check(summary.Discarded.Count == 0, "Achieved card incorrectly listed as manual discard");
        });
        test("Discard and shuffle history survive saving with distinct future draw eligibility", () =>
        {
            async Task Run()
            {
                var b = New(); b.AddSecondary(0, cardId); b.ShuffleBack(0, b.Players[0].Secondaries.Single());
                b.AddSecondary(0, "ca2026-secondary-centre-ground"); b.DiscardCard(0, b.Players[0].Secondaries.Single());
                b.RecoverCard(0, b.Players[0].Secondaries.Single());
                var store = new BattleStore(Path.Combine(root, "scoreboard-save")); await store.SaveAsync(b);
                var saved = (await store.LoadAsync(b.ArmySnapshot.Id)).Single();
                Check(saved.SchemaVersion == 5 && saved.RoundScoreboard(0, 1).Discarded.Single() == "Centre Ground"
                    && saved.RoundScoreboard(0, 1).ShuffledBack.Single() == "No Prisoners", "Saved histories merged or lost");
                Check(saved.RandomDrawPool(0).Any(c => c.Id == cardId) && saved.RandomDrawPool(0).All(c => c.Id != "ca2026-secondary-centre-ground"), "Saved deck eligibility changed");
                Check(!saved.DiscardPool(0).Any() && saved.RoundScoreboard(0, 1).Recovered.Single() == "Centre Ground", "Recovery did not persist");
            }
            Run().GetAwaiter().GetResult();
        });
    }
}
