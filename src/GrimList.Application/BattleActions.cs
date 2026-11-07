using System.Security.Cryptography;

namespace GrimList.Application;

public sealed partial class BattleRecord
{
    private IEnumerable<BattleSecondary> PendingReplacements(int player) => Players[player].Secondaries.Where(c =>
        c.DiscardedRound == Round && c.DiscardReason is nameof(SecondaryDiscard.CommandRedraw) or nameof(SecondaryDiscard.CardRule) or "Manual"
        && Players[player].Secondaries.All(replacement => replacement.ReplacesCardId != c.CardId));
    public int DrawSlotsRemaining(int player) => Players[player].Mode == "Tactical" && !Scoreboard && !Completed
        ? Math.Max(0, 2 - NormalDrawCount(player, Round)) + PendingReplacements(player).Count() : 0;
    public IReadOnlyList<BattleMission> RandomDrawPool(int player) => BattleMissions.Secondaries("Tactical")
        .Where(c => Players[player].Secondaries.All(s => s.CardId != c.Id))
        // Defend Stronghold must be shuffled back when drawn in round one.
        // Leave it in the deck for a later round instead of selecting it here.
        .Where(c => Round != 1 || c.Id != "ca2026-secondary-defend-stronghold").ToList();
    public BattleSecondary DrawRandomSecondary(int player)
    {
        if (DrawSlotsRemaining(player) == 0) throw new InvalidDataException("No secondary draws remain for this round.");
        var pool = RandomDrawPool(player);
        if (pool.Count == 0) throw new InvalidDataException("No eligible secondary cards remain in this player's deck.");
        var card = pool[RandomNumberGenerator.GetInt32(pool.Count)];
        AddSecondary(player, card.Id);
        var selected = Players[player].Secondaries.Single(s => s.CardId == card.Id);
        selected.DrawnRandomly = true;
        return selected;
    }
    public string? WrongCardProblem(int player, BattleSecondary selection)
    {
        if (Players[player].Mode != "Tactical" || !Players[player].Secondaries.Contains(selection)) return "Only this player's Tactical cards can be corrected.";
        if (Scoreboard || Completed || Round != selection.DrawnRound) return "Return to the round where this card was drawn to correct it.";
        if (selection.DiscardedRound.HasValue) return "Undo the recorded discard first.";
        if (HasSecondaryScore(player, selection)) return "Clear this card's scores before discarding a wrong selection.";
        return null;
    }
    public void DiscardWrongCard(int player, BattleSecondary selection)
    {
        if (WrongCardProblem(player, selection) is { } issue) throw new InvalidDataException(issue);
        // Correct a selection, rather than spend the gameplay discard/redraw.
        // For replacements, the original event/cost stays and its slot reopens.
        Players[player].Secondaries.Remove(selection);
        Players[player].CardCorrections.Add(new(selection.CardId, Round));
        Completed = false;
    }
    public string? CardRemovalProblem(int player, BattleSecondary selection)
    {
        if (Completed) return "Reopen this battle before changing cards.";
        return DiscardProblem(player, selection);
    }
    public void DiscardCard(int player, BattleSecondary selection)
    {
        if (CardRemovalProblem(player, selection) is { } issue) throw new InvalidDataException(issue);
        // Keep the card in the recorded deck so it cannot be drawn again.
        // CP is always entered separately by the player.
        selection.DiscardedRound = Round; selection.DiscardReason = "Manual";
        Completed = false;
    }
    public void ShuffleBack(int player, BattleSecondary selection)
    {
        if (CardRemovalProblem(player, selection) is { } issue) throw new InvalidDataException(issue);
        Players[player].Secondaries.Remove(selection);
        Players[player].ShuffledCards.Add(new(selection.CardId, Round));
        Completed = false;
    }
    public IEnumerable<BattleSecondary> DiscardPool(int player) => Players[player].Secondaries
        .Where(c => c.DiscardedRound <= Round).OrderBy(c => BattleMissions.Secondary(c.CardId, "Tactical").Name);
    public string? RecoverCardProblem(int player, BattleSecondary selection)
    {
        if (Players[player].Mode != "Tactical" || !Players[player].Secondaries.Contains(selection)) return "Only this player's Tactical cards can be recovered.";
        if (Completed || Scoreboard) return "Reopen the battle and return to a round to recover cards.";
        if (!selection.DiscardedRound.HasValue || selection.DiscardedRound > Round) return "This card is not in the discard pool for this round.";
        return null;
    }
    public void RecoverCard(int player, BattleSecondary selection)
    {
        if (RecoverCardProblem(player, selection) is { } issue) throw new InvalidDataException(issue);
        // Archive the event before restoring the active card. Legacy automatic CP
        // remains attached to that event; recovery never grants or spends CP.
        Players[player].DiscardRecoveries.Add(new(selection.CardId, selection.DiscardedRound!.Value, selection.DiscardReason, Round));
        selection.DiscardedRound = null; selection.DiscardReason = ""; selection.RecoveredRound = Round;
        Completed = false;
    }
    public void Finish()
    {
        var latest = Math.Max(Round, FinishedRound ?? 1);
        foreach (var p in Players)
        {
            for (var r = 1; r <= 5; r++)
                if (p.Rounds[r - 1].Counts.Values.Any(v => v > 0) || p.Rounds[r - 1].CoreCp > 0 || p.Rounds[r - 1].ExtraCp > 0 || p.Rounds[r - 1].SpentCp > 0)
                    latest = Math.Max(latest, r);
            foreach (var c in p.Secondaries) latest = Math.Max(latest, Math.Max(c.DrawnRound, c.DiscardedRound ?? 1));
            foreach (var c in p.CardCorrections) latest = Math.Max(latest, c.Round);
            foreach (var c in p.ShuffledCards) latest = Math.Max(latest, c.Round);
            foreach (var c in p.DiscardRecoveries) latest = Math.Max(latest, c.RecoveredRound);
        }
        FinishedRound = latest; Round = latest; Completed = true; Scoreboard = true;
    }
    public void Reopen()
    {
        Round = FinishedRound ?? Round; FinishedRound = null; Completed = false; Scoreboard = false;
    }
}
