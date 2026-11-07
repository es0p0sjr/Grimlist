namespace GrimList.Application;

public enum SecondaryDiscard { EndTurn, CommandRedraw, CardRule }

public sealed partial class BattleRecord
{
    // GW Event Companion v1.2, Select Secondary Missions / Generating CP.
    // Automatic CP is derived from events, separately from manually entered CP.
    public int NormalDrawCount(int player, int round) => Players[player].Secondaries.Count(c => c.DrawnRound == round && c.ReplacesCardId.Length == 0);
    private IEnumerable<(string CardId, int Round, string Reason)> RecordedDiscards(int player) =>
        Players[player].Secondaries.Where(c => c.DiscardedRound.HasValue).Select(c => (c.CardId, c.DiscardedRound!.Value, c.DiscardReason))
        .Concat(Players[player].DiscardRecoveries.Select(c => (c.CardId, c.DiscardedRound, c.Reason)));
    public bool CommandRedrawUsed(int player) => RecordedDiscards(player).Any(c => c.Reason == nameof(SecondaryDiscard.CommandRedraw));
    public int RedrawCpSpent(int player, int round) => RecordedDiscards(player).Count(c => c.Round == round && c.Reason == nameof(SecondaryDiscard.CommandRedraw));
    private bool HasEndTurnDiscard(int player, int round) => RecordedDiscards(player).Any(c => c.Round == round && c.Reason == nameof(SecondaryDiscard.EndTurn));
    public bool HasRecordedDiscardCp(int player, int round) => RecordedDiscards(player).Any(c => c.Round == round
        && c.Reason is nameof(SecondaryDiscard.EndTurn) or nameof(SecondaryDiscard.CommandRedraw));
    public int DiscardCpGained(int player, int round) => HasEndTurnDiscard(player, round) && Players[player].Rounds[round - 1].ExtraCp == 0 ? 1 : 0;
    public int CpGained(int player, int round)
    {
        var r = Players[player].Rounds[round - 1];
        return r.CoreCp + Math.Min(1, r.ExtraCp + (HasEndTurnDiscard(player, round) ? 1 : 0));
    }
    public int CpSpent(int player, int round) => Players[player].Rounds[round - 1].SpentCp + RedrawCpSpent(player, round);
    private bool CpHistoryIsValid(int player) => Enumerable.Range(1, 5).All(r => Cp(player, r) >= 0
        // A later end-of-turn reward cannot fund an earlier Command-phase redraw.
        && (RedrawCpSpent(player, r) == 0 || Cp(player, r - 1) + Players[player].Rounds[r - 1].CoreCp
            + Players[player].Rounds[r - 1].ExtraCp >= RedrawCpSpent(player, r)));

    private void RequireOwnedSecondary(int player, BattleSecondary selection)
    {
        if (Players[player].Mode != "Tactical" || !Players[player].Secondaries.Contains(selection))
            throw new InvalidDataException("Only this player's Tactical cards can be discarded or replaced.");
    }
    public bool HasSecondaryScore(int player, BattleSecondary selection) => RawLines(player).Any(l =>
        l.Mission == BattleMissions.Secondary(selection.CardId, Players[player].Mode).Name && l.Count > 0);
    public string? DiscardProblem(int player, BattleSecondary selection)
    {
        if (Players[player].Mode != "Tactical" || !Players[player].Secondaries.Contains(selection)) return "Fixed missions cannot be discarded.";
        if (Scoreboard || selection.DrawnRound > Round || selection.RecoveredRound > Round) return "Return to a round in which this card is active.";
        if (selection.DiscardedRound.HasValue) return "This card is already discarded. Undo its discard to restore it.";
        if (HasSecondaryScore(player, selection)) return "This card has scoring recorded. Clear its conditions before changing it to an unachieved discard.";
        return null;
    }
    public string? RedrawProblem(int player, BattleSecondary selection)
    {
        if (DiscardProblem(player, selection) is { } problem) return problem;
        if (CommandRedrawUsed(player)) return "The once-per-battle Command-phase redraw has already been used.";
        if (Cp(player, Round) - DiscardCpGained(player, Round) < 1) return "Requires 1 available CP before the end-of-turn discard reward.";
        return null;
    }
    // Only these two cards actually DISCARD on draw. Shuffle-back instructions
    // need a separate deck-instance model and must never be treated as discards.
    public string FreeDiscardRule(BattleSecondary selection) => selection.CardId switch
    {
        "ca2026-secondary-a-grievous-blow" => "No enemy units with starting strength 13+ are on the battlefield.",
        "ca2026-secondary-bring-it-down" => "No enemy models with W 10+ are on the battlefield.",
        _ => ""
    };
    public void DiscardSecondary(int player, BattleSecondary selection, SecondaryDiscard reason = SecondaryDiscard.EndTurn,
        string? replacementId = null, bool cardConditionConfirmed = false)
    {
        RequireOwnedSecondary(player, selection);
        if (DiscardProblem(player, selection) is { } problem) throw new InvalidDataException(problem);
        if (!Enum.IsDefined(reason)) throw new InvalidDataException("Choose a discard option.");
        if (reason == SecondaryDiscard.CommandRedraw && RedrawProblem(player, selection) is { } redrawProblem)
            throw new InvalidDataException(redrawProblem);
        if (reason == SecondaryDiscard.CardRule && (selection.DrawnRound != Round || FreeDiscardRule(selection).Length == 0 || !cardConditionConfirmed))
            throw new InvalidDataException("This free discard is only available when drawn and after confirming the printed card condition.");

        BattleSecondary? replacement = null;
        if (reason != SecondaryDiscard.EndTurn)
        {
            if (string.IsNullOrEmpty(replacementId)) throw new InvalidDataException("Choose the replacement card you drew first.");
            _ = BattleMissions.Secondary(replacementId, "Tactical");
            if (Players[player].Secondaries.Any(c => c.CardId == replacementId)) throw new InvalidDataException("The replacement must be a card not already drawn in this battle.");
            replacement = new() { CardId = replacementId, DrawnRound = Round, ReplacesCardId = selection.CardId };
        }
        else if (replacementId != null) throw new InvalidDataException("An end-of-turn discard does not draw a replacement.");

        selection.DiscardedRound = Round; selection.DiscardReason = reason.ToString();
        if (replacement != null) Players[player].Secondaries.Add(replacement);
        if (!CpHistoryIsValid(player))
        {
            if (replacement != null) Players[player].Secondaries.Remove(replacement);
            selection.DiscardedRound = null; selection.DiscardReason = "";
            throw new InvalidDataException("This redraw would leave a negative CP balance. Correct recorded spending first.");
        }
        Completed = false;
    }
    public void UndoDiscard(int player, BattleSecondary selection)
    {
        RequireOwnedSecondary(player, selection);
        if (selection.DiscardedRound != Round || Scoreboard) throw new InvalidDataException("Return to the round where this card was discarded to undo it.");
        var p = Players[player];
        var replacement = p.Secondaries.SingleOrDefault(c => c.ReplacesCardId == selection.CardId);
        if (replacement != null && (HasSecondaryScore(player, replacement) || replacement.DiscardedRound.HasValue))
            throw new InvalidDataException("Clear the replacement card's scores and undo its discard first.");
        var reason = selection.DiscardReason; var index = replacement == null ? -1 : p.Secondaries.IndexOf(replacement);
        if (replacement != null) p.Secondaries.Remove(replacement);
        selection.DiscardedRound = null; selection.DiscardReason = "";
        if (!CpHistoryIsValid(player))
        {
            selection.DiscardedRound = Round; selection.DiscardReason = reason;
            if (replacement != null) p.Secondaries.Insert(index, replacement);
            throw new InvalidDataException("That discard's CP has been spent. Correct the spending before undoing it.");
        }
        Completed = false;
    }
    public string SecondaryStatus(int player, BattleSecondary selection)
    {
        if (selection.DiscardedRound.HasValue)
        {
            var label = selection.DiscardReason switch
            {
                nameof(SecondaryDiscard.EndTurn) => "Discarded at end of your turn",
                nameof(SecondaryDiscard.CommandRedraw) => "Discarded for Command-phase redraw (1 CP)",
                nameof(SecondaryDiscard.CardRule) => "Discarded when drawn under card rule (0 CP)",
                "Manual" => "Discarded",
                _ => "Discard recorded in an older version (CP entered manually)"
            };
            var replacement = Players[player].Secondaries.SingleOrDefault(c => c.ReplacesCardId == selection.CardId);
            return label + $" · round {selection.DiscardedRound}" + (replacement == null ? "" : "\nReplacement: " + BattleMissions.Secondary(replacement.CardId, "Tactical").Name);
        }
        var score = RawLines(player).FirstOrDefault(l => l.Mission == BattleMissions.Secondary(selection.CardId, Players[player].Mode).Name && l.RawVp > 0 && l.Round <= Round);
        return score == null ? $"Active · drawn in round {selection.DrawnRound}" : $"Achieved · automatically discarded after scoring in round {score.Round} · no CP reward";
    }
    public string DiscardCpSummary(int player, int round) => $"Discard reward: +{DiscardCpGained(player, round)} CP"
        + (HasEndTurnDiscard(player, round) && DiscardCpGained(player, round) == 0 ? " (extra CP cap reached)" : "")
        + $" · Redraw spent: {RedrawCpSpent(player, round)} CP";
    public IEnumerable<string> DiscardHistory(int player, int round) => Players[player].Secondaries.Where(c => c.DiscardedRound == round)
        .Select(c => BattleMissions.Secondary(c.CardId, "Tactical").Name + " · " + SecondaryStatus(player, c))
        .Concat(Players[player].CardCorrections.Where(c => c.Round == round).Select(c => BattleMissions.Secondary(c.CardId, "Tactical").Name + " · Card discarded · no CP change"));
}
