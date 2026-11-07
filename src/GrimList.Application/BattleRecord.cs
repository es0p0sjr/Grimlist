using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrimList.Core;

namespace GrimList.Application;

public sealed class BattlePlayer
{
    public string Name { get; set; } = "";
    public string Disposition { get; set; } = "";
    public string Primary { get; set; } = "";
    public string Mode { get; set; } = "Tactical";
    public bool BattleReady { get; set; } = true;
    public List<BattleRound> Rounds { get; set; } = Enumerable.Range(1, 5).Select(_ => new BattleRound()).ToList();
    public Dictionary<string, int> EndScores { get; set; } = [];
    public List<BattleSecondary> Secondaries { get; set; } = [];
    public List<BattleCardCorrection> CardCorrections { get; set; } = [];
    public List<BattleCardCorrection> ShuffledCards { get; set; } = [];
    public List<BattleDiscardRecovery> DiscardRecoveries { get; set; } = [];
}
public sealed class BattleRound
{
    public Dictionary<string, int> Counts { get; set; } = [];
    public int CoreCp { get; set; }
    public int ExtraCp { get; set; }
    public int SpentCp { get; set; }
}
public sealed class BattleSecondary
{
    public string CardId { get; set; } = "";
    public int DrawnRound { get; set; } = 1;
    public int? DiscardedRound { get; set; }
    public string DiscardReason { get; set; } = "";
    public string ReplacesCardId { get; set; } = "";
    public bool DrawnRandomly { get; set; }
    public int? RecoveredRound { get; set; }
}
public sealed record BattleCardCorrection(string CardId, int Round);
public sealed record BattleDiscardRecovery(string CardId, int DiscardedRound, string Reason, int RecoveredRound);
public sealed record BattleScoreLine(int Round, int Turn, string Mission, string Timing, string Condition, int Count, int RawVp, int Vp, bool Primary);

public sealed partial class BattleRecord
{
    public int SchemaVersion { get; set; } = 5;
    public BattleExchangePacket? Exchange { get; set; }
    public string LocalParticipantId { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Code { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ArmyList ArmySnapshot { get; set; } = ArmyList.New("");
    public List<ReferenceCard> RulesSnapshot { get; set; } = [];
    public List<BattlePlayer> Players { get; set; } = [];
    public int Round { get; set; } = 1;
    public bool Scoreboard { get; set; }
    public bool Completed { get; set; }
    public int? FinishedRound { get; set; }

    public static BattleRecord Create(ArmyList army, IEnumerable<ReferenceCard> rules, PrimaryMatchup matchup, string opponent, bool armyFirst,
        string yourMode, string opponentMode, IEnumerable<string> yourFixed, IEnumerable<string> opponentFixed)
    {
        BattlePlayer Player(string name, string disposition, string primary, string mode, IEnumerable<string> fixedCards)
        {
            var fixedIds = fixedCards.Distinct().ToList();
            if (mode is not ("Fixed" or "Tactical")) throw new InvalidDataException("Choose Fixed or Tactical secondaries.");
            if (mode == "Fixed" && (fixedIds.Count != 2 || fixedIds.Any(id => !BattleMissions.Secondaries("Fixed").Any(c => c.Id == id))))
                throw new InvalidDataException("Select two different Fixed missions for each Fixed player.");
            _ = BattleMissions.Primary(primary);
            return new() { Name = name.Trim(), Disposition = disposition, Primary = primary, Mode = mode,
                Secondaries = mode == "Fixed" ? fixedIds.Select(id => new BattleSecondary { CardId = id }).ToList() : [] };
        }
        var you = Player(army.Name, army.Disposition, matchup.YourPrimary, yourMode, yourFixed);
        var other = Player(string.IsNullOrWhiteSpace(opponent) ? "Opponent" : opponent, matchup.OpponentDisposition, matchup.OpponentPrimary, opponentMode, opponentFixed);
        return new() { ArmySnapshot = DataJson.Read<ArmyList>(DataJson.Write(army)), RulesSnapshot = rules.ToList(), Players = armyFirst ? [you, other] : [other, you] };
    }

    public static string Key(BattleCondition condition, int turn) => condition.Id + "@" + turn;
    public int Count(int player, BattleCondition condition, int turn, int round, bool end = false)
        => (end ? Players[player].EndScores : Players[player].Rounds[round - 1].Counts).GetValueOrDefault(Key(condition, turn));

    public void ChangeCondition(int player, BattleMission mission, BattleTiming group, BattleCondition condition, int turn, int delta)
    {
        if (!group.Conditions.Contains(condition) || !mission.Groups.Contains(group)) throw new InvalidDataException("Unknown scoring condition.");
        if (!(group.EndBattle ? Scoreboard : group.Applies(Round)) || !group.Turns(player, Round).Contains(turn))
            throw new InvalidDataException("This condition does not score at this time.");
        var p = Players[player];
        if (mission.Mode != "Primary")
        {
            var selection = p.Secondaries.SingleOrDefault(c => c.CardId == mission.Id) ?? throw new InvalidDataException("Select this card first.");
            if (selection.DrawnRound > Round || selection.RecoveredRound > Round || selection.DiscardedRound < Round || mission.Mode != p.Mode)
                throw new InvalidDataException("This card is not active in this round.");
            if (p.Mode == "Tactical" && Round == selection.DrawnRound && turn < player)
                throw new InvalidDataException("This card has not been drawn yet in the first player's turn.");
            if (delta > 0 && selection.DiscardedRound.HasValue)
                throw new InvalidDataException("This card has a recorded discard. Undo that discard before scoring it.");
            if (delta > 0 && p.Mode == "Tactical")
            {
                var otherScore = RawLines(player).Any(l => l.Mission == mission.Name && l.RawVp > 0 && (l.Round != Round || l.Turn != turn));
                if (otherScore) throw new InvalidDataException("A Tactical card scores once. Clear its earlier score before moving it to another turn.");
            }
        }
        var counts = group.EndBattle ? p.EndScores : p.Rounds[Round - 1].Counts;
        var value = Math.Clamp(counts.GetValueOrDefault(Key(condition, turn)) + delta, 0, condition.Count ? 99 : 1);
        if (value > 0)
        {
            // An OR chain is one choice, even if both predicates happen to be true.
            var index = group.Conditions.ToList().IndexOf(condition);
            var start = index;
            while (start > 0 && group.Conditions[start].Relation == "alternative") start--;
            var end = index + 1;
            while (end < group.Conditions.Count && group.Conditions[end].Relation == "alternative") end++;
            for (var i = start; i < end; i++)
                if (i != index) counts.Remove(Key(group.Conditions[i], turn));
            if (condition.Relation == "cumulative")
            {
                var previous = group.Conditions[index - 1];
                var previousCount = counts.GetValueOrDefault(Key(previous, turn));
                if (previousCount == 0) throw new InvalidDataException("Record the preceding scoring condition before adding its bonus.");
                if (condition.Count && previous.Count) value = Math.Min(value, previousCount);
            }
        }
        counts[Key(condition, turn)] = value;
        Completed = false;
    }

    public void AddSecondary(int player, string id)
    {
        var p = Players[player];
        if (Scoreboard || Completed) throw new InvalidDataException("Return to a battle round before drawing cards.");
        if (p.Mode != "Tactical") throw new InvalidDataException("Fixed missions are selected before the battle.");
        _ = BattleMissions.Secondary(id, p.Mode);
        if (p.Secondaries.Any(c => c.CardId == id)) throw new InvalidDataException("This card is already recorded in this battle.");
        var pending = PendingReplacements(player).FirstOrDefault();
        if (pending == null && NormalDrawCount(player, Round) >= 2) throw new InvalidDataException("Two drawn cards are already recorded for this round. Use the card's discard options for a replacement.");
        p.Secondaries.Add(new() { CardId = id, DrawnRound = Round, ReplacesCardId = pending?.CardId ?? "" }); Completed = false;
    }
    public void RemoveSecondary(int player, BattleSecondary selection)
    {
        var p = Players[player];
        RequireOwnedSecondary(player, selection);
        var card = BattleMissions.Secondary(selection.CardId, p.Mode);
        if (selection.DiscardedRound.HasValue || selection.ReplacesCardId.Length > 0)
            throw new InvalidDataException("Undo the associated discard/replacement before correcting this selection.");
        if (p.Mode != "Tactical" || RawLines(player).Any(l => l.Mission == card.Name && l.Count > 0))
            throw new InvalidDataException("Clear this card's scoring conditions before correcting its selection.");
        p.Secondaries.Remove(selection); Completed = false;
    }
    public IEnumerable<BattleSecondary> VisibleSecondaries(int player)
    {
        var p = Players[player];
        return p.Secondaries.Where(c => c.DrawnRound <= Round && !(c.RecoveredRound > Round) && (!c.DiscardedRound.HasValue || c.DiscardedRound >= Round)
            && (p.Mode == "Fixed" || !RawLines(player).Any(l => l.Mission == BattleMissions.Secondary(c.CardId, p.Mode).Name && l.Round < Round && l.RawVp > 0)));
    }
    public int Cp(int player, int round) => Enumerable.Range(1, round).Sum(r => CpGained(player, r) - CpSpent(player, r));
    public void ChangeCp(int player, string kind, int delta)
    {
        var r = Players[player].Rounds[Round - 1];
        var old = kind switch { "Core" => r.CoreCp, "Extra" => r.ExtraCp, "Spent" => r.SpentCp, _ => throw new InvalidDataException("Unknown CP counter.") };
        var value = Math.Clamp(old + delta, 0, kind == "Extra" ? 1 : 99);
        void Set(int n) { if (kind == "Core") r.CoreCp = n; else if (kind == "Extra") r.ExtraCp = n; else r.SpentCp = n; }
        Set(value);
        if (!CpHistoryIsValid(player)) { Set(old); throw new InvalidDataException("This would spend CP that have not been gained. Check this and later rounds, including redraw costs."); }
        Completed = false;
    }

    private IEnumerable<BattleScoreLine> RawLines(int player)
    {
        var p = Players[player];
        var missions = new[] { BattleMissions.Primary(p.Primary) }.Concat(p.Secondaries.Select(s => BattleMissions.Secondary(s.CardId, p.Mode)));
        foreach (var mission in missions)
        foreach (var group in mission.Groups)
        foreach (var round in group.EndBattle ? new[] { 6 } : Enumerable.Range(1, 5).Where(group.Applies))
        foreach (var turn in group.Turns(player, Math.Min(round, 5)))
        {
            var previousCount = 0; BattleCondition? previous = null;
            foreach (var condition in group.Conditions)
            {
                var count = Count(player, condition, turn, Math.Min(round, 5), group.EndBattle);
                var effectiveCount = count;
                if (condition.Relation == "cumulative")
                    effectiveCount = previousCount == 0 ? 0 : condition.Count && previous?.Count == true ? Math.Min(count, previousCount) : count;
                var raw = Math.Min(effectiveCount * condition.Vp, condition.Cap ?? int.MaxValue);
                previousCount = effectiveCount; previous = condition;
                yield return new(round, turn, mission.Name, group.Label(player, turn, Math.Min(round, 5)), condition.Text, count, raw, raw, mission.Mode == "Primary");
            }
        }
    }
    public IReadOnlyList<BattleScoreLine> Scores(int player)
    {
        var result = new List<BattleScoreLine>();
        var battle = new Dictionary<bool, int>(); var rounds = new Dictionary<(int, bool), int>(); var fixedCards = new Dictionary<string, int>();
        // Stable chronological allocation: corrections always recalculate all later caps.
        foreach (var line in RawLines(player).OrderBy(l => l.Round).ThenBy(l => l.Turn)
            .ThenBy(l => l.Timing.Contains("Command phase", StringComparison.Ordinal) ? 0 : 1))
        {
            var vp = Math.Min(line.RawVp, Math.Max(0, 45 - battle.GetValueOrDefault(line.Primary)));
            var key = (line.Round, line.Primary);
            if (line.Round <= 5) vp = Math.Min(vp, Math.Max(0, 15 - rounds.GetValueOrDefault(key)));
            if (!line.Primary && Players[player].Mode == "Fixed") vp = Math.Min(vp, Math.Max(0, 20 - fixedCards.GetValueOrDefault(line.Mission)));
            battle[line.Primary] = battle.GetValueOrDefault(line.Primary) + vp;
            rounds[key] = rounds.GetValueOrDefault(key) + vp;
            if (!line.Primary) fixedCards[line.Mission] = fixedCards.GetValueOrDefault(line.Mission) + vp;
            result.Add(line with { Vp = vp });
        }
        return result;
    }
    public int Total(int player) => Scores(player).Sum(l => l.Vp) + (Players[player].BattleReady ? 10 : 0);
    public string Summary(int player, bool primary)
    {
        var lines = Scores(player).Where(l => l.Primary == primary).ToList();
        var round = lines.Where(l => l.Round == Round).Sum(l => l.Vp);
        var raw = lines.Where(l => l.Round == Round).Sum(l => l.RawVp);
        return $"{(primary ? "Primary" : "Secondary")} {round}/15 this round · {lines.Sum(l => l.Vp)}/45 total"
            + (round == 15 ? " · Round cap reached" : "") + (raw > round ? $" · {raw} VP before round/battle caps" : "");
    }
    public void Navigate(int delta)
    {
        if (Scoreboard && delta < 0) { Scoreboard = false; Round = FinishedRound ?? Round; }
        else if (Round == 5 && delta > 0) Scoreboard = true;
        else Round = Math.Clamp(Round + delta, 1, 5);
        if (!Scoreboard && FinishedRound.HasValue && Round > FinishedRound) { Completed = false; FinishedRound = null; }
    }
}

// A separate file per battle preserves previous results. The army is never mutated.
public sealed class BattleStore(string dataDirectory)
{
    private readonly string directory = Path.Combine(dataDirectory, "battles");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> deleted = new(StringComparer.OrdinalIgnoreCase);
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
    public async Task SaveAsync(BattleRecord battle)
    {
        battle.SchemaVersion = 5;
        battle.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(battle, Json);
        if (!Guid.TryParseExact(battle.Id, "N", out _)) throw new InvalidDataException("Invalid battle identifier.");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (deleted.Contains(battle.Id)) throw new InvalidDataException("This battle was deleted and cannot be saved again.");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, battle.Id + ".json");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, json).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<BattleRecord>> LoadAsync(string armyId)
    {
        var battles = new List<BattleRecord>();
        if (!Directory.Exists(directory)) return battles;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var battle = JsonSerializer.Deserialize<BattleRecord>(await File.ReadAllTextAsync(path).ConfigureAwait(false), Json)
                ?? throw new InvalidDataException("A saved battle could not be read: " + Path.GetFileName(path));
            if (battle.SchemaVersion is not (1 or 2 or 3 or 4 or 5) || battle.Players.Count != 2 || battle.Players.Any(p => p.Rounds.Count != 5) || battle.Round is < 1 or > 5
                || battle.FinishedRound is < 1 or > 5)
                throw new InvalidDataException("Unsupported battle file: " + Path.GetFileName(path));
            if (battle.ArmySnapshot.Id == armyId) battles.Add(battle);
        }
        return battles.OrderByDescending(b => b.UpdatedAt).ToList();
    }
    public async Task<BattleRecord> SaveLinkedBattleAsync(BattleRecord candidate)
    {
        if (candidate.Exchange == null || !Guid.TryParseExact(candidate.Id, "N", out _)) throw new InvalidDataException("Invalid linked battle.");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (deleted.Contains(candidate.Id)) throw new InvalidDataException("This battle was deleted. Create a new invitation to play again.");
            Directory.CreateDirectory(directory); var path = Path.Combine(directory, candidate.Id + ".json");
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<BattleRecord>(await File.ReadAllTextAsync(path), Json);
                if (existing?.Exchange == null || existing.ArmySnapshot.Id != candidate.ArmySnapshot.Id
                    || existing.LocalParticipantId != candidate.LocalParticipantId
                    || BattleExchange.Hash(existing.Exchange) != BattleExchange.Hash(candidate.Exchange))
                    throw new InvalidDataException("This battle already has a different army or setup on this device.");
                return existing;
            }
            var temporary = path + ".tmp"; await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(candidate, Json));
            File.Move(temporary, path, false); return candidate;
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid battle identifier.");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Only a validated battle GUID beneath this store may be removed.
            var path = Path.Combine(directory, id + ".json");
            File.Delete(path);
            File.Delete(path + ".tmp");
            deleted.Add(id);
        }
        finally { gate.Release(); }
    }
}
