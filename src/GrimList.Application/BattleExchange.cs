using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrimList.Core;

namespace GrimList.Application;

public sealed record BattleArmyShare(string ParticipantId, string Name, string Faction, string Disposition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detachments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Points = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Limit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Roster = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Fixed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PointsComplete = null);
// HostFirst is retained only to read and fingerprint version 1 exchanges.
public sealed record BattleExchangePacket(int Version, string Kind, string BattleId, string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HostFirst,
    BattleArmyShare Host, BattleArmyShare? Guest = null, string InvitationHash = "");
public sealed record BattleExchangeDraft(BattleExchangePacket Packet, ArmyList LocalArmy, List<ReferenceCard> LocalRules, bool IsHost);

// Self-contained, versioned messages. No networking or library updates occur here.
public static class BattleExchange
{
    public const string Prefix = "GRIMBATTLE1:";
    public const int MaxText = 400_000, MaxJson = 256_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public static BattleArmyShare Snapshot(ArmyList army, Dataset? data, string playerName)
    {
        var view = DispositionPrimaries.ForArmy(army, data);
        if (view.Matchups.Count == 0) throw new InvalidDataException(view.Hint);
        var share = new BattleArmyShare(Guid.NewGuid().ToString("N"), playerName.Trim(), army.Faction, view.Disposition, PointsComplete: null);
        ValidateArmy(share, 3); return share;
    }
    public static BattleExchangeDraft Invite(ArmyList army, Dataset? data, string playerName)
    {
        var packet = new BattleExchangePacket(3, "invitation", Guid.NewGuid().ToString("N"),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(5)), null, Snapshot(army, data, playerName));
        return new(packet, DataJson.Read<ArmyList>(DataJson.Write(army)), ArmyReferences.ForArmy(army, data).ToList(), true);
    }
    public static BattleExchangeDraft Join(BattleExchangePacket invitation, ArmyList army, Dataset? data, string playerName)
    {
        Validate(invitation);
        if (invitation.Version != 3) throw new InvalidDataException("Ask your opponent to update GrimList and create a new invitation. Older invitations contain full army lists.");
        if (invitation.Kind != "invitation") throw new InvalidDataException("Choose a battle invitation to join.");
        var response = invitation with { Kind = "response", Guest = Snapshot(army, data, playerName), InvitationHash = Hash(invitation) };
        return new(response, DataJson.Read<ArmyList>(DataJson.Write(army)), ArmyReferences.ForArmy(army, data).ToList(), false);
    }
    public static BattleExchangeDraft Accept(BattleExchangeDraft invitation, BattleExchangePacket response)
    {
        Validate(response);
        var original = invitation.Packet with { Kind = "invitation", Guest = null, InvitationHash = "" };
        if (!invitation.IsHost || response.Kind != "response"
            || response.InvitationHash != Hash(original) || response.BattleId != invitation.Packet.BattleId)
            throw new InvalidDataException("This response does not match your saved invitation.");
        if (invitation.Packet.Kind == "response" && Hash(invitation.Packet) != Hash(response))
            throw new InvalidDataException("A different opponent response was already accepted for this battle.");
        return invitation with { Packet = response };
    }
    public static BattleRecord CreateBattle(BattleExchangeDraft draft, bool hostFirst,
        string yourMode = "Tactical", string opponentMode = "Tactical", IEnumerable<string>? yourFixed = null, IEnumerable<string>? opponentFixed = null)
    {
        var packet = draft.Packet; Validate(packet);
        if (packet.Guest == null) throw new InvalidDataException("Import the opponent's response before starting this battle.");
        var yours = draft.IsHost ? packet.Host : packet.Guest;
        var theirs = draft.IsHost ? packet.Guest : packet.Host;
        var b = BattleRecord.Create(draft.LocalArmy, draft.LocalRules, DispositionPrimaries.Matchup(yours.Disposition, theirs.Disposition),
            theirs.Name, draft.IsHost == hostFirst, yourMode, opponentMode, yourFixed ?? [], opponentFixed ?? []);
        b.Players[draft.IsHost == hostFirst ? 0 : 1].Name = yours.Name;
        b.Id = packet.BattleId; b.Code = packet.Code; b.Exchange = packet; b.LocalParticipantId = yours.ParticipantId;
        return b;
    }
    public static string Encode(BattleExchangePacket packet)
    {
        if (packet.Version != 3) throw new InvalidDataException("Create a new invitation to share only your name, faction and disposition.");
        Validate(packet); var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        if (bytes.Length > MaxJson) throw new InvalidDataException("This battle exchange is too large.");
        using var output = new MemoryStream();
        using (var zip = new BrotliStream(output, CompressionLevel.SmallestSize, true)) zip.Write(bytes);
        return Prefix + Convert.ToBase64String(output.ToArray());
    }
    public static BattleExchangePacket Decode(string text)
    {
        if (text.Length > MaxText) throw new InvalidDataException("This exchange exceeds the size limit.");
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Open a GrimList battle invitation or response. The short battle code alone does not contain opponent details.");
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(text[Prefix.Length..]));
            using var zip = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = zip.Read(buffer)) > 0)
            {
                if (output.Length + count > MaxJson) throw new InvalidDataException("This exchange exceeds the size limit.");
                output.Write(buffer, 0, count);
            }
            var packet = JsonSerializer.Deserialize<BattleExchangePacket>(output.ToArray(), Json)
                ?? throw new InvalidDataException("Empty battle exchange.");
            Validate(packet); return packet;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        { throw new InvalidDataException("This battle exchange is damaged or unsupported.", ex); }
    }
    public static async Task<string> ReadAsync(Stream input)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, true, 4096, true);
        var text = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            if (text.Length + count > MaxText) throw new InvalidDataException("This exchange exceeds the size limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
    public static string Hash(BattleExchangePacket packet) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(packet, Json)));
    public static void Validate(BattleExchangePacket packet)
    {
        if (packet.Version is not (1 or 2 or 3) || (packet.Version == 1 ? packet.HostFirst == null : packet.HostFirst != null)
            || packet.Kind is not ("invitation" or "response") || !Guid.TryParseExact(packet.BattleId, "N", out _)
            || packet.Code is not { Length: 10 } || packet.Code.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Unsupported battle invitation.");
        ValidateArmy(packet.Host, packet.Version);
        if (packet.Kind == "invitation")
        {
            if (packet.Guest != null || packet.InvitationHash != "") throw new InvalidDataException("Invalid invitation contents.");
        }
        else
        {
            ValidateArmy(packet.Guest!, packet.Version);
            var original = packet with { Kind = "invitation", Guest = null, InvitationHash = "" };
            if (packet.InvitationHash != Hash(original) || packet.Guest!.ParticipantId == packet.Host.ParticipantId)
                throw new InvalidDataException("The response has changed invitation details.");
        }
    }
    private static void ValidateArmy(BattleArmyShare a, int version)
    {
        if (a == null || !Guid.TryParseExact(a.ParticipantId, "N", out _) || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 200
            || string.IsNullOrWhiteSpace(a.Faction) || a.Faction.Length > 200)
            throw new InvalidDataException("Enter a player name and select an army with a faction and disposition.");
        _ = DispositionPrimaries.Matchup(a.Disposition, a.Disposition);
        if (version == 3)
        {
            if (Encoding.UTF8.GetByteCount(a.Name) > 160 || Encoding.UTF8.GetByteCount(a.Faction) > 160)
                throw new InvalidDataException("Keep player and faction names within 160 UTF-8 bytes for QR sharing.");
            if (a.Detachments != null || a.Points != null || a.Limit != null || a.Roster != null || a.Mode != null || a.Fixed != null || a.PointsComplete != null)
                throw new InvalidDataException("Only player name, faction and disposition may be shared.");
            return;
        }
        if (a.Detachments == null || a.Detachments.Length > 2000 || string.IsNullOrWhiteSpace(a.Roster) || a.Roster.Length > 100_000
            || a.Points is null or < 0 || a.Limit is null or < 1 or > 100_000 || a.Mode is not ("Tactical" or "Fixed") || a.Fixed == null)
            throw new InvalidDataException("The saved legacy army exchange is incomplete or unsupported.");
        if (a.Mode == "Fixed" && (a.Fixed.Length != 2 || a.Fixed.Distinct().Count() != 2
            || a.Fixed.Any(id => !BattleMissions.Secondaries("Fixed").Any(c => c.Id == id)))
            || a.Mode == "Tactical" && a.Fixed.Length != 0) throw new InvalidDataException("Invalid secondary mission choices.");
    }
}
