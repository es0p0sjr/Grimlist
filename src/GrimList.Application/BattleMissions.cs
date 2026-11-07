using System.Globalization;
using System.Text.Json;

namespace GrimList.Application;

public sealed record BattleCondition(string Id, string Text, int Vp, int? Cap, string Relation, bool Count);
public sealed record BattleTiming(string Id, string Rounds, string Timing, IReadOnlyList<BattleCondition> Conditions)
{
    public bool EndBattle => Rounds == "END OF THE BATTLE";
    public bool Applies(int round) => Rounds switch
    {
        "ANY BATTLE ROUND" => round is >= 1 and <= 5,
        "FIRST BATTLE ROUND" => round == 1,
        "FIRST AND SECOND BATTLE ROUND" => round <= 2,
        "SECOND BATTLE ROUND ONWARDS" => round >= 2,
        "SECOND TO FOURTH BATTLE ROUND" => round is >= 2 and <= 4,
        "SECOND AND THIRD BATTLE ROUND" => round is 2 or 3,
        "FOURTH BATTLE ROUND ONWARDS" => round >= 4,
        "FIFTH BATTLE ROUND" => round == 5,
        "END OF THE BATTLE" => false,
        _ => throw new InvalidDataException("Unknown mission round restriction: " + Rounds)
    };
    // Turn is absolute (first/second player), independent of Attacker/Defender.
    public IEnumerable<int> Turns(int owner, int round)
    {
        if (EndBattle) { yield return 2; yield break; }
        if (Timing == "WHEN: End of a turn.") { yield return 0; yield return 1; yield break; }
        if (Timing.Contains("opponent", StringComparison.Ordinal))
            yield return round == 5 && Timing.Contains("whichever comes first", StringComparison.Ordinal) ? 1 : 1 - owner;
        else yield return owner;
    }
    public string Label(int owner, int turn, int round)
    {
        if (EndBattle) return "End of the battle";
        if (Timing == "WHEN: End of a turn.") return turn == owner ? "End of your turn" : "End of opponent's turn";
        if (round == 5 && Timing.Contains("Command phase (or", StringComparison.Ordinal)) return "End of your turn";
        if (round == 5 && owner == 1 && Timing.Contains("whichever comes first", StringComparison.Ordinal)) return "End of the fifth battle round";
        return Timing.Replace("WHEN: ", "");
    }
}
public sealed record BattleMission(string Id, string Name, string Mode, string Instructions, IReadOnlyList<BattleTiming> Groups);

public static class BattleMissions
{
    private static readonly Lazy<IReadOnlyList<BattleMission>> Cards = new(Load);
    public static IReadOnlyList<BattleMission> All => Cards.Value;
    public static BattleMission Primary(string name) => All.Single(c => c.Mode == "Primary" && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public static BattleMission Secondary(string id, string mode) => All.Single(c => c.Id == id && c.Mode == mode);
    public static IEnumerable<BattleMission> Secondaries(string mode) => All.Where(c => c.Mode == mode).OrderBy(c => c.Name);
    private static IReadOnlyList<BattleMission> Load()
    {
        var result = new List<BattleMission>();
        foreach (var type in new[] { "primary", "secondary" })
        {
            using var stream = typeof(BattleMissions).Assembly.GetManifestResourceStream($"GrimList.Missions.{type}-missions-2026-27.json")
                ?? throw new InvalidDataException("Mission cards are missing from this installation.");
            using var document = JsonDocument.Parse(stream);
            foreach (var card in document.RootElement.GetProperty("cards").EnumerateArray())
            foreach (var modeValue in card.GetProperty("modes").EnumerateArray())
            {
                var mode = modeValue.GetString()!;
                var groups = new List<BattleTiming>(); var instructions = new List<string>();
                foreach (var section in card.GetProperty("sections").EnumerateArray())
                {
                    switch (section.GetProperty("kind").GetString())
                    {
                        case "scoring":
                            var conditions = new List<BattleCondition>();
                            foreach (var condition in section.GetProperty("conditions").EnumerateArray())
                            foreach (var points in condition.GetProperty("points").EnumerateArray().Where(p => p.GetProperty("modes").EnumerateArray().Any(m => m.GetString() == mode)))
                            {
                                var text = condition.GetProperty("condition").GetProperty("text").GetString()!;
                                var cap = points.GetProperty("capVp");
                                conditions.Add(new(condition.GetProperty("id").GetString()!, text, points.GetProperty("vp").GetInt32(),
                                    cap.ValueKind == JsonValueKind.Null ? null : cap.GetInt32(), condition.GetProperty("relationToPrevious").GetString()!,
                                    text.StartsWith("For each", StringComparison.Ordinal)));
                            }
                            if (conditions.Count > 0)
                            {
                                var group = new BattleTiming(conditions[0].Id + "-group", section.GetProperty("roundsText").GetString()!, section.GetProperty("timingText").GetString()!, conditions);
                                _ = group.Applies(1); groups.Add(group);
                            }
                            break;
                        case "action":
                            instructions.Add(section.GetProperty("name").GetString() + " · " + section.GetProperty("actionType").GetString());
                            instructions.AddRange(section.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("label").GetString() + ": " + f.GetProperty("text").GetString()));
                            break;
                        default:
                            if (mode != "Fixed" || !section.GetProperty("text").GetString()!.StartsWith("WHEN DRAWN:"))
                                instructions.Add(section.GetProperty("text").GetString()!);
                            break;
                    }
                }
                var id = card.GetProperty("id").GetString()!;
                if (document.RootElement.TryGetProperty("clarifications", out var clarifications))
                    instructions.AddRange(clarifications.EnumerateArray().Where(c => c.GetProperty("cardId").GetString() == id).Select(c => "FAQ: " + c.GetProperty("summary").GetString()));
                var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(card.GetProperty("name").GetString()!.ToLowerInvariant());
                result.Add(new(id, name, mode, string.Join("\n\n", instructions), groups));
            }
        }
        if (result.Count(c => c.Mode == "Primary") != 25 || result.Count(c => c.Mode == "Tactical") != 18 || result.Count(c => c.Mode == "Fixed") != 4)
            throw new InvalidDataException("The mission pack is incomplete.");
        return result;
    }
}
