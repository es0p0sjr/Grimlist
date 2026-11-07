using System.Text.Json;

namespace GrimList.Application;

// Pending invitations survive closing either app. Completed exchanges are also
// retained so the guest can show/share the same response again without joining twice.
public sealed class BattleExchangeStore(string directory)
{
    private readonly string folder = Path.Combine(directory, "battle-exchanges");
    public async Task SaveAsync(BattleExchangeDraft draft)
    {
        BattleExchange.Validate(draft.Packet); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, draft.Packet.BattleId + ".json");
        if (File.Exists(path))
        {
            var old = JsonSerializer.Deserialize<BattleExchangeDraft>(await File.ReadAllTextAsync(path), BattleStore.Json)
                ?? throw new InvalidDataException("Saved invitation could not be read.");
            if (old.LocalArmy.Id != draft.LocalArmy.Id || old.IsHost != draft.IsHost
                || old.Packet.Kind == "response" && BattleExchange.Hash(old.Packet) != BattleExchange.Hash(draft.Packet))
                throw new InvalidDataException("This battle was already created or joined with another army or setup.");
        }
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(draft, BattleStore.Json));
        File.Move(temporary, path, true);
    }
    public async Task<IReadOnlyList<BattleExchangeDraft>> LoadAsync(string armyId)
    {
        var result = new List<BattleExchangeDraft>();
        if (!Directory.Exists(folder)) return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var draft = JsonSerializer.Deserialize<BattleExchangeDraft>(await File.ReadAllTextAsync(file), BattleStore.Json)
                ?? throw new InvalidDataException("Saved battle invitation could not be read.");
            BattleExchange.Validate(draft.Packet);
            if (draft.LocalArmy.Id == armyId) result.Add(draft);
        }
        return result;
    }
    public async Task<BattleRecord?> FindBattleAsync(BattleExchangeDraft draft, BattleStore battles)
    {
        BattleExchange.Validate(draft.Packet);
        var local = draft.IsHost ? draft.Packet.Host : draft.Packet.Guest;
        var existing = (await battles.LoadAsync(draft.LocalArmy.Id)).SingleOrDefault(b => b.Id == draft.Packet.BattleId);
        if (existing != null)
        {
            if (existing.Exchange == null || BattleExchange.Hash(existing.Exchange) != BattleExchange.Hash(draft.Packet)
                || existing.LocalParticipantId != local?.ParticipantId)
                throw new InvalidDataException("This battle already has a different opponent or setup.");
        }
        return existing;
    }
    public async Task<BattleRecord> StartAsync(BattleExchangeDraft draft, BattleStore battles, bool hostFirst,
        string yourMode = "Tactical", string opponentMode = "Tactical", IEnumerable<string>? yourFixed = null, IEnumerable<string>? opponentFixed = null)
    {
        var existing = await FindBattleAsync(draft, battles);
        if (existing != null) return existing; // Never reorder players or reset scores when reopening.
        var candidate = BattleExchange.CreateBattle(draft, hostFirst, yourMode, opponentMode, yourFixed, opponentFixed);
        await SaveAsync(draft); return await battles.SaveLinkedBattleAsync(candidate);
    }
    public void Delete(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid battle identifier.");
        if (!Directory.Exists(folder)) return;
        File.Delete(Path.Combine(folder, id + ".json")); File.Delete(Path.Combine(folder, id + ".json.tmp"));
    }
}
