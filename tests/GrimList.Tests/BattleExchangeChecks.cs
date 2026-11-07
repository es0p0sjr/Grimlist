using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GrimList.Application;
using GrimList.Core;

internal static class BattleExchangeChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid exchange accepted"); }
        async Task RejectAsync(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new Exception("Conflicting exchange accepted"); }
        Dataset Data(string faction, string disposition) => new(new("Exchange fixture", "1", "2026-10-15", "", Edition: "11"),
            [new(faction, "Squad", [new(5, 100)], Id: "squad"), new(faction, "Leader", [new(1, 70)], Id: "leader")],
            Detachments: [new(faction, "Orders", Dispositions: [disposition])]);
        ArmyList Army(string faction, string disposition) => ArmyList.New(faction) with { Name = faction + " army", Disposition = disposition, Detachments = ["Orders"],
            WarlordEntryId = "leader", Entries = [new("squad", "squad", "Squad", 5, "Plasma rifle"), new("leader", "leader", "Leader", 1, AttachedToId: "squad")] };
        BattleExchangeDraft Invite() => BattleExchange.Invite(Army("Host", "Take and Hold"), Data("Host", "Take and Hold"), "Host player");
        BattleExchangeDraft Join(BattleExchangePacket packet) => BattleExchange.Join(packet, Army("Guest", "Purge the Foe"), Data("Guest", "Purge the Foe"), "Guest player");

        test("The same army exchange supports either first player chosen separately at battle start", () =>
        {
            var invitation = Invite(); var incoming = BattleExchange.Decode(BattleExchange.Encode(invitation.Packet));
            var guest = Join(incoming); var response = BattleExchange.Decode(BattleExchange.Encode(guest.Packet));
            var host = BattleExchange.Accept(invitation, response); var hash = BattleExchange.Hash(response);
            Check(incoming.Version == 3 && incoming.HostFirst == null && response.HostFirst == null, "Exchange chose a first player");
            foreach (var first in new[] { true, false })
            {
                var fixedIds = BattleMissions.Secondaries("Fixed").Take(2).Select(c => c.Id).ToArray();
                var a = BattleExchange.CreateBattle(host, first, "Tactical", "Fixed", [], fixedIds);
                var b = BattleExchange.CreateBattle(guest, first, "Fixed", "Tactical", fixedIds, []);
                Check(a.Id == b.Id && a.Code == b.Code && a.Code == incoming.Code, "Battle identities differ");
                Check(a.Players.Select(p => p.Name).SequenceEqual(b.Players.Select(p => p.Name))
                    && a.Players[0].Name == (first ? "Host player" : "Guest player"), "Turn order differs");
                Check(a.Players.Select(p => p.Primary).SequenceEqual(b.Players.Select(p => p.Primary)), "Primary assignments differ");
                Check(a.Players.Single(p => p.Name == "Host player").Primary == "Immovable Object"
                    && a.Players.Single(p => p.Name == "Guest player").Primary == "Unstoppable Force", "Wrong disposition matchup");
                Check(a.Players.Single(p => p.Name == "Guest player").Secondaries.Count == 2 && a.LocalParticipantId != b.LocalParticipantId, "Fixed selections or local identity missing");
                Check(BattleExchange.Hash(a.Exchange!) == hash && BattleExchange.Hash(b.Exchange!) == hash, "First turn changed the exchange");
            }
        });
        test("Legacy exchanges retain their fingerprints but first turn is chosen independently for a new battle", () =>
        {
            var draft = Invite(); var old = draft.Packet with { Version = 1, HostFirst = true,
                Host = draft.Packet.Host with { Detachments = "Orders", Points = 170, Limit = 2000, Roster = "Legacy roster", Mode = "Tactical", Fixed = [], PointsComplete = true } };
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            // Serialize the original non-nullable packet shape to verify existing response fingerprints still match.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { old.Version, old.Kind, old.BattleId, old.Code,
                HostFirst = true, old.Host, old.Guest, old.InvitationHash }, json);
            Check(BattleExchange.Hash(old) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), "Legacy fingerprint changed");
            using var memory = new MemoryStream();
            using (var zip = new BrotliStream(memory, CompressionLevel.SmallestSize, true)) zip.Write(bytes);
            var decoded = BattleExchange.Decode(BattleExchange.Prefix + Convert.ToBase64String(memory.ToArray()));
            Reject(() => Join(decoded)); Reject(() => BattleExchange.Encode(decoded));
            var response = old with { Kind = "response", Guest = old.Host with { ParticipantId = Guid.NewGuid().ToString("N"), Name = "Guest player", Disposition = "Purge the Foe" }, InvitationHash = BattleExchange.Hash(old) };
            var host = BattleExchange.Accept(draft with { Packet = old }, response);
            Check(BattleExchange.CreateBattle(host, false).Players[0].Name == "Guest player", "Legacy invitation overrode first-turn choice");
        });
        test("New exchanges contain only player name, faction and disposition plus matching identifiers", () =>
        {
            var army = Army("Host", "Take and Hold"); var draft = BattleExchange.Invite(army, Data("Host", "Take and Hold"), "  Alex  ");
            var shared = BattleExchange.Decode(BattleExchange.Encode(draft.Packet)).Host;
            Check(shared.Name == "Alex" && shared.Faction == "Host" && shared.Disposition == "Take and Hold", "Wrong public details");
            Check(shared.Roster == null && shared.Detachments == null && shared.Points == null && shared.Limit == null
                && shared.Mode == null && shared.Fixed == null && shared.PointsComplete == null, "Private army details leaked");
            var response = Join(draft.Packet).Packet;
            using var input = new MemoryStream(Convert.FromBase64String(BattleExchange.Encode(response)[BattleExchange.Prefix.Length..]));
            using var zip = new BrotliStream(input, CompressionMode.Decompress); using var json = JsonDocument.Parse(zip);
            foreach (var field in new[] { "host", "guest" })
                Check(json.RootElement.GetProperty(field).EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "disposition", "faction", "name", "participantId" }), "Unexpected transmitted fields");
            army.Entries.Clear(); Check(draft.LocalArmy.Entries.Count == 2, "Local snapshot changed with roster");
            Reject(() => BattleExchange.CreateBattle(draft, true));
            Reject(() => BattleExchange.Invite(army, Data("Host", "Take and Hold"), " "));
            Reject(() => BattleExchange.Encode(draft.Packet with { Host = shared with { Roster = "Private roster" } }));
            Reject(() => BattleExchange.Encode(draft.Packet with { Host = shared with { Points = 170 } }));
        });
        test("Responses must match the exact invitation and cannot insert turn order or change the host army", () =>
        {
            var invite = Invite(); var guest = Join(invite.Packet); var other = Invite();
            Reject(() => BattleExchange.Accept(other, guest.Packet));
            Reject(() => BattleExchange.Validate(guest.Packet with { HostFirst = false }));
            Reject(() => BattleExchange.Validate(guest.Packet with { Host = guest.Packet.Host with { Name = "Changed" } }));
            var accepted = BattleExchange.Accept(invite, guest.Packet);
            Reject(() => BattleExchange.Accept(accepted, Join(invite.Packet).Packet));
            Check(BattleExchange.Accept(accepted, guest.Packet).Packet == guest.Packet, "Repeated same response rejected");
        });
        test("Malformed, oversized and unsupported exchanges are rejected before creating a battle", () =>
        {
            var invite = Invite();
            Reject(() => BattleExchange.Decode(invite.Packet.Code)); Reject(() => BattleExchange.Decode(BattleExchange.Prefix + "bad!"));
            Reject(() => BattleExchange.Validate(invite.Packet with { Version = 99 }));
            Reject(() => BattleExchange.Validate(invite.Packet with { BattleId = "../escape" }));
            Reject(() => BattleExchange.Validate(invite.Packet with { Host = null! }));
            Reject(() => BattleExchange.Validate(invite.Packet with { Host = invite.Packet.Host with { Disposition = "Unknown" } }));
            Reject(() => BattleExchange.Validate(invite.Packet with { Host = invite.Packet.Host with { Mode = "Fixed", Fixed = ["fake", "fake"] } }));
            Reject(() => BattleExchange.Decode(new string('X', BattleExchange.MaxText + 1)));
            using var memory = new MemoryStream();
            using (var zip = new BrotliStream(memory, CompressionLevel.SmallestSize, true)) zip.Write(new byte[BattleExchange.MaxJson + 1]);
            Reject(() => BattleExchange.Decode(BattleExchange.Prefix + Convert.ToBase64String(memory.ToArray())));
        });
        test("Saved exchanges resume after restart and repeated response imports preserve independent scores", () =>
        {
            async Task Run()
            {
                var hostPath = Path.Combine(root, "exchange-host"); var guestPath = Path.Combine(root, "exchange-guest");
                var invite = Invite(); var hostStore = new BattleExchangeStore(hostPath); await hostStore.SaveAsync(invite);
                var resumed = (await new BattleExchangeStore(hostPath).LoadAsync(invite.LocalArmy.Id)).Single();
                var guest = Join(BattleExchange.Decode(BattleExchange.Encode(resumed.Packet)));
                var guestStore = new BattleExchangeStore(guestPath); await guestStore.SaveAsync(guest);
                var host = BattleExchange.Accept(resumed, guest.Packet); var battles = new BattleStore(hostPath);
                await hostStore.SaveAsync(host);
                Check((await battles.LoadAsync(invite.LocalArmy.Id)).Count == 0
                    && (await new BattleStore(guestPath).LoadAsync(guest.LocalArmy.Id)).Count == 0, "Exchanging armies started a battle");
                Check(await hostStore.FindBattleAsync(host, battles) == null, "Pending exchange has a scoring record");
                var guestBattle = await new BattleExchangeStore(guestPath).StartAsync(guest, new BattleStore(guestPath), false);
                var hostBattle = await hostStore.StartAsync(host, battles, false); hostBattle.ChangeCp(0, "Core", 2); await battles.SaveAsync(hostBattle);
                var restarted = new BattleExchangeStore(hostPath); var saved = (await restarted.LoadAsync(invite.LocalArmy.Id)).Single();
                var reopened = await restarted.StartAsync(BattleExchange.Accept(saved, guest.Packet), new BattleStore(hostPath), true);
                Check(reopened.Cp(0, 1) == 2 && guestBattle.Cp(0, 1) == 0, "Reimport reset scores or synchronized devices");
                Check(reopened.Players[0].Name == "Guest player", "Reopening changed the first player");
                Check((await restarted.FindBattleAsync(saved, new BattleStore(hostPath)))!.Cp(0, 1) == 2, "Open failed to resume saved scores");
                Check(reopened.Exchange!.Guest!.Name == "Guest player" && reopened.Exchange.Guest.Roster == null && reopened.SchemaVersion == 5, "Opponent snapshot not saved");
                await RejectAsync(() => restarted.SaveAsync(guest));
                await RejectAsync(() => battles.SaveLinkedBattleAsync(guestBattle));
                var conflicting = Join(invite.Packet); await RejectAsync(() => hostStore.StartAsync(BattleExchange.Accept(invite, conflicting.Packet), battles, false));
                await battles.DeleteAsync(hostBattle.Id); restarted.Delete(hostBattle.Id);
                Check((await restarted.LoadAsync(invite.LocalArmy.Id)).Count == 0, "Deleted battle left an exchange entry behind");
            }
            Run().GetAwaiter().GetResult();
        });
        test("QR links round trip compact Unicode details and reject malformed or oversized links", () =>
        {
            var draft = BattleExchange.Invite(Army("Host", "Take and Hold"), Data("Host", "Take and Hold"), "\u039A\u03CE\u03C3\u03C4\u03B1\u03C2 & Alex");
            var response = Join(draft.Packet).Packet;
            foreach (var packet in new[] { draft.Packet, response })
            {
                var link = BattleQr.Link(packet); var decoded = BattleQr.ReadLink(link);
                Check(BattleExchange.Hash(decoded) == BattleExchange.Hash(packet), "QR link changed invitation");
                Check(link.Length <= BattleQr.MaxLinkLength && !link.Contains('+') && !link.Contains('='), "Unbounded or unsafe QR link");
                File.WriteAllText(Path.Combine(root, packet.Kind + "-qr-link.txt"), link);
                var matrix = BattleQr.Matrix(link); var size = matrix.GetLength(0);
                File.WriteAllLines(Path.Combine(root, packet.Kind + "-qr-matrix.txt"), Enumerable.Range(0, size).Select(y => string.Concat(Enumerable.Range(0, size).Select(x => matrix[y, x] ? "1" : "0"))));
            }
            Reject(() => BattleQr.ReadLink("https://example.com/battle/abc"));
            Reject(() => BattleQr.ReadLink(BattleQr.LinkPrefix + "bad?query"));
            Reject(() => BattleQr.ReadLink(BattleQr.LinkPrefix + new string('a', BattleQr.MaxLinkLength)));
            Reject(() => BattleQr.Matrix(new string('x', BattleQr.MaxLinkLength + 1)));
            Reject(() => BattleQr.Matrix("\u03A9")); // QR payloads are ASCII links; UTF-8 is inside the packet.
            var oversized = draft.Packet with { Host = draft.Packet.Host with { Name = new string('x', 161) } };
            Reject(() => BattleExchange.Encode(oversized));
            var bounded = draft.Packet with { Host = draft.Packet.Host with { Name = new string('\u03A9', 80), Faction = new string('\u0416', 80) } };
            Check(BattleQr.Link(Join(bounded).Packet).Length <= BattleQr.MaxLinkLength, "Bounded Unicode response does not fit a QR");
        });
        test("QR modules match independent Nayuki medium-correction byte-mode reference matrices", () =>
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/qr/matrices.json"));
            var fixtures = JsonSerializer.Deserialize<List<QrFixture>>(File.ReadAllText(path))!;
            foreach (var fixture in fixtures)
            {
                var matrix = BattleQr.Matrix(fixture.Text); var size = matrix.GetLength(0);
                var cells = new byte[size * size]; for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) cells[y * size + x] = matrix[y, x] ? (byte)1 : (byte)0;
                Check(size == fixture.Size && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cells)) == fixture.Sha256, "QR differs from reference at length " + fixture.Text.Length);
            }
        });
    }
    private sealed record QrFixture(string Text, int Size, string Sha256);
}
