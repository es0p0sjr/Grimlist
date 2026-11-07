using System.IO.Compression;
using System.Text.Json;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

public static partial class EffectiveDatasheetRecovery
{
    private const string ResourcePrefix = "GrimList.Application.Resources.effective-recovery.";
    private record RecordIndex(string Payload, string? AbilitiesHash, int Version);
    private static Stream Resource(string name) => typeof(EffectiveDatasheetRecovery).Assembly.GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidDataException("Missing effective recovery resource: " + name);
    // Only three small source identities are retained. ZIP indexes and historical
    // payloads are scoped to a recovery operation, never a process-wide object graph.
    private static readonly Lazy<Dictionary<string, string>> sources = new(() =>
    {
        using var stream = Resource("index.json");
        return new(JsonSerializer.Deserialize<Dictionary<string, string>>(stream, DataJson.Options)!, StringComparer.OrdinalIgnoreCase);
    });
    public static Dataset ApplyBundled(Dataset data, string? cacheDirectory = null)
        => ApplyBundledCore(data, cacheDirectory, null);

    internal static Dataset ApplyPersisted(LoadedLibrary library, string cacheDirectory)
        => ApplyBundledCore(library.Dataset!, cacheDirectory, library.Revision == null ? null
            : LibraryRecoveryVersion.Effective + "/" + library.Revision);

    private static Dataset ApplyBundledCore(Dataset data, string? cacheDirectory, string? cacheIdentity)
    {
        var required = data.Units.Select(u => u.ProfileSource?.ContentHash).Concat((data.ReferenceCards ?? []).Select(c => c.Source?.ContentHash))
            .Where(h => h != null && sources.Value.ContainsKey(h)).Select(h => h!.ToUpperInvariant()).Distinct().Order().ToArray();
        var version = LibraryRecoveryVersion.Effective + "/indexed-recovery-5/weapons-1/" + string.Join("/", required.Select(h => h + ":" + sources.Value[h]));
        return RecoveryProjectionCache.Apply(data, cacheDirectory, version, () =>
        {
            var semantics = required.Length == 0 ? ModelStatModifiers.Repair(data) : ApplyIndexed(data, required);
            var weapons = WeaponMetadataRecovery.ApplyBundled(semantics);
            // Retain legacy fingerprint recovery first. Adding proven weapon tags can
            // then unlock a current fingerprint; finish it now, not on the next launch.
            return ReferenceEquals(weapons, semantics) || required.Length == 0 ? weapons : ApplyIndexed(weapons, required);
        }, cacheIdentity);
    }
    private static Dataset ApplyIndexed(Dataset data, IEnumerable<string> required)
    {
        List<Pack> selected = [];
        foreach (var source in required)
        {
            using var stream = Resource(source + ".zip");
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            using var indexStream = archive.GetEntry("index.json")!.Open();
            var index = JsonSerializer.Deserialize<Dictionary<string, RecordIndex>>(indexStream, DataJson.Options)!;
            List<UnitMetadata> units = []; List<CardMetadata> cards = [];
            Dictionary<string, DatasheetSemantics> semantics = [];
            foreach (var unit in data.Units.Where(u => source.Equals(u.ProfileSource?.ContentHash, StringComparison.OrdinalIgnoreCase)))
            {
                if (unit.Profile is not { } profile) continue;
                var profileHash = ProfileHash(profile);
                foreach (var alias in unit.Aliases ?? [])
                {
                    if (!index.TryGetValue("unit/" + (alias + "/" + profileHash).ToUpperInvariant(), out var record)) continue;
                    if ((profile.Semantics?.Version ?? 0) >= record.Version) break;
                    if (record.AbilitiesHash != null && record.AbilitiesHash != AbilitiesHash(profile)) break;
                    if (!semantics.TryGetValue(record.Payload, out var value))
                    {
                        using var payload = archive.GetEntry(record.Payload)!.Open();
                        value = JsonSerializer.Deserialize<DatasheetSemantics>(payload, DataJson.Options)!;
                        semantics.Add(record.Payload, value);
                    }
                    units.Add(new(alias, profileHash, value, record.AbilitiesHash)); break;
                }
            }
            foreach (var card in (data.ReferenceCards ?? []).Where(c => source.Equals(c.Source?.ContentHash, StringComparison.OrdinalIgnoreCase)))
                if (index.TryGetValue("card/" + (card.Faction + "/" + Enhancements.Key(card)).ToUpperInvariant(), out var record))
                {
                    using var payload = archive.GetEntry(record.Payload)!.Open();
                    cards.Add(JsonSerializer.Deserialize<CardMetadata>(payload, DataJson.Options)!);
                }
            selected.Add(new(source, units, cards));
        }
        // Keep the existing source/provenance matching implementation as the final
        // authority. The index changes IO and allocation, never eligibility or rules.
        return Apply(data, selected);
    }
}
