namespace GrimList.Application;

public sealed record BattleCardScore(string Name, int Vp);
public sealed record BattleRoundScoreboard(int PrimaryVp, int SecondaryVp,
    IReadOnlyList<BattleCardScore> Primary, IReadOnlyList<BattleCardScore> Secondary,
    IReadOnlyList<string> Discarded, IReadOnlyList<string> ShuffledBack, IReadOnlyList<string> Recovered);

public sealed partial class BattleRecord
{
    public BattleRoundScoreboard RoundScoreboard(int player, int round)
    {
        var lines = Scores(player).Where(l => l.Round == round).ToList();
        List<BattleCardScore> Cards(bool primary) => lines.Where(l => l.Primary == primary && l.RawVp > 0)
            .GroupBy(l => l.Mission).Select(g => new BattleCardScore(g.Key, g.Sum(l => l.Vp))).ToList();
        string Name(string id) => BattleMissions.Secondary(id, "Tactical").Name;
        return new(lines.Where(l => l.Primary).Sum(l => l.Vp), lines.Where(l => !l.Primary).Sum(l => l.Vp),
            Cards(true), Cards(false),
            RecordedDiscards(player).Where(c => c.Round == round).Select(c => Name(c.CardId)).ToList(),
            Players[player].ShuffledCards.Where(c => c.Round == round).Select(c => Name(c.CardId)).ToList(),
            Players[player].DiscardRecoveries.Where(c => c.RecoveredRound == round).Select(c => Name(c.CardId)).ToList());
    }
    public string ScoreboardCards(int player, int round, bool primary)
    {
        var score = RoundScoreboard(player, round);
        var cards = primary ? score.Primary : score.Secondary;
        var text = new List<string> { $"{Players[player].Name} · {(primary ? score.PrimaryVp : score.SecondaryVp)} VP" };
        text.AddRange(cards.Count == 0 ? ["Scored: —"] : cards.Select(c => $"Scored: {c.Name} · {c.Vp} VP"));
        if (!primary)
        {
            text.Add("Discarded: " + (score.Discarded.Count == 0 ? "—" : string.Join(", ", score.Discarded)));
            if (score.ShuffledBack.Count > 0) text.Add("Shuffled back: " + string.Join(", ", score.ShuffledBack));
            if (score.Recovered.Count > 0) text.Add("Recovered: " + string.Join(", ", score.Recovered));
        }
        return string.Join("\n", text);
    }
}
