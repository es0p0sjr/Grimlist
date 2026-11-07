using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GrimList.Core;

namespace GrimList.Application;

// Derived overlays only. Original profiles/provenance are never written to this
// cache or changed in the repository. Any miss, version change or corruption runs
// the same indexed recovery; cache availability cannot determine rule behavior.
internal static class RecoveryProjectionCache
{
    private record UnitOverlay(int Index, int Semantics, List<WeaponProfile>? Weapons);
    private record CardOverlay(int Index, List<DatasheetModifier>? Modifiers);
    private record Overlay(string Fingerprint, string Version, List<UnitOverlay> Units, List<CardOverlay> Cards, List<DatasheetSemantics> Semantics);
    private static string Fingerprint(Dataset data)
    {
        using var hash = SHA256.Create();
        using (var output = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
        { JsonSerializer.Serialize(output, data, DataJson.Options); output.FlushFinalBlock(); }
        return Convert.ToHexString(hash.Hash!);
    }
    internal static Dataset Apply(Dataset data, string? directory, string version, Func<Dataset> recover, string? cacheIdentity = null)
    {
        if (directory == null) return recover();
        version += "/projection-4";
        var fingerprint = cacheIdentity ?? Fingerprint(data);
        var path = Path.Combine(directory, "derived-cache", "effective-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(version + fingerprint))) + ".gz");
        try
        {
            using var file = File.OpenRead(path);
            var expected = new byte[32]; file.ReadExactly(expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(file))) throw new InvalidDataException("Recovery cache checksum");
            file.Position = 32;
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var overlay = JsonSerializer.Deserialize<Overlay>(gzip, DataJson.Options)!;
            if (overlay.Fingerprint != fingerprint || overlay.Version != version) throw new InvalidDataException("Recovery cache version");
            var units = data.Units.ToList(); var cards = data.ReferenceCards?.ToList();
            foreach (var item in overlay.Units) units[item.Index] = units[item.Index] with { Profile = units[item.Index].Profile! with {
                Semantics = item.Semantics < 0 ? units[item.Index].Profile!.Semantics : overlay.Semantics[item.Semantics],
                Weapons = item.Weapons ?? units[item.Index].Profile!.Weapons } };
            foreach (var item in overlay.Cards) cards![item.Index] = cards[item.Index] with { Modifiers = item.Modifiers };
            return overlay.Units.Count + overlay.Cards.Count == 0 ? data : data with { Units = units, ReferenceCards = cards };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException) { }
        var result = recover();
        List<DatasheetSemantics> shared = [];
        Dictionary<DatasheetSemantics, int> identities = new(ReferenceEqualityComparer.Instance);
        int Intern(DatasheetSemantics value)
        {
            if (!identities.TryGetValue(value, out var id)) { id = shared.Count; identities.Add(value, id); shared.Add(value); }
            return id;
        }
        var projection = new Overlay(fingerprint, version,
            result.Units.Select((u, i) => (u, i)).Where(p => !ReferenceEquals(p.u.Profile?.Semantics, data.Units[p.i].Profile?.Semantics)
                || !ReferenceEquals(p.u.Profile?.Weapons, data.Units[p.i].Profile?.Weapons))
                .Select(p => new UnitOverlay(p.i, p.u.Profile?.Semantics is { } semantics ? Intern(semantics) : -1,
                    ReferenceEquals(p.u.Profile?.Weapons, data.Units[p.i].Profile?.Weapons) ? null : p.u.Profile!.Weapons)).ToList(),
            (result.ReferenceCards ?? []).Select((c, i) => (c, i)).Where(p => !ReferenceEquals(p.c, data.ReferenceCards![p.i]))
                .Select(p => new CardOverlay(p.i, p.c.Modifiers)).ToList(), shared);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                file.Write(new byte[32]);
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true)) JsonSerializer.Serialize(gzip, projection, DataJson.Options);
                file.Position = 32; var checksum = SHA256.HashData(file); file.Position = 0; file.Write(checksum); file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return result;
    }
}
