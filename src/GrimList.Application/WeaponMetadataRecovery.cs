using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GrimList.Core;

namespace GrimList.Application;

// Source evidence only: no rule text, prices, selections or base-stat replacement.
public static class WeaponMetadataRecovery
{
    public record Evidence(string SourceHash, string Edition, string Faction, string Alias, List<WeaponProfile> Weapons);
    private const string ResourceName = "GrimList.Application.Resources.weapon-metadata.zip";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Visible(WeaponProfile weapon) => DataJson.Write(weapon with { SourceId = null, SourceCategories = null }).ReplaceLineEndings("\n");
    private static string Entry(string hash, string edition, string faction, string alias)
        => hash.ToUpperInvariant() + "/" + Hash(DataJson.Write(new[] { edition, faction, alias }).ReplaceLineEndings("\n")) + ".json";
    public static List<Evidence> Create(Dataset data) => data.Units
        .Where(u => u.Profile?.Weapons is { Count: > 0 } && u.ProfileSource?.ContentHash is { Length: > 0 })
        .SelectMany(u => (u.Aliases ?? []).Where(a => a.StartsWith("bsdata:", StringComparison.Ordinal))
            .Select(a => new Evidence(u.ProfileSource!.ContentHash, u.ProfileSource.Edition, u.Faction, a, u.Profile!.Weapons!))).ToList();

    public static void WriteArchive(IEnumerable<Evidence> evidence, Stream output)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var group in evidence.GroupBy(e => Entry(e.SourceHash, e.Edition, e.Faction, e.Alias)).OrderBy(g => g.Key))
        {
            var records = group.Select(DataJson.Write).Distinct().ToList();
            if (records.Count != 1) throw new InvalidDataException("Conflicting weapon metadata: " + group.Key);
            var entry = archive.CreateEntry(group.Key, CompressionLevel.SmallestSize);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open()); writer.Write(records[0]);
        }
    }
    public static Dataset Apply(Dataset data, IEnumerable<Evidence> evidence)
    {
        var records = evidence.ToLookup(e => Entry(e.SourceHash, e.Edition, e.Faction, e.Alias));
        return Apply(data, key => records[key]);
    }
    public static Dataset ApplyBundled(Dataset data)
    {
        using var resource = typeof(WeaponMetadataRecovery).Assembly.GetManifestResourceStream(ResourceName);
        if (resource == null) throw new InvalidDataException("Missing bundled weapon metadata evidence.");
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        return Apply(data, key =>
        {
            if (archive.GetEntry(key) is not { } entry) return [];
            using var input = entry.Open();
            return new[] { JsonSerializer.Deserialize<Evidence>(input, DataJson.Options)! };
        });
    }
    private static Dataset Apply(Dataset data, Func<string, IEnumerable<Evidence>> find)
    {
        var changed = false;
        var units = data.Units.Select(unit =>
        {
            if (unit.Profile?.Weapons is not { Count: > 0 } weapons || unit.ProfileSource is not { ContentHash.Length: > 0 } source
                || weapons.All(w => !string.IsNullOrEmpty(w.SourceId) && w.SourceCategories != null)) return unit;
            var candidates = (unit.Aliases ?? []).SelectMany(a => find(Entry(source.ContentHash, source.Edition, unit.Faction, a)))
                .Where(e => e.SourceHash.Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase) && e.Edition == source.Edition
                    && e.Faction == unit.Faction && (unit.Aliases ?? []).Contains(e.Alias)).ToList();
            if (candidates.Count == 0) return unit;
            List<WeaponProfile>? result = null;
            foreach (var evidence in candidates)
            {
                // Exact visible multiset preserves duplicates/order and rejects edited
                // or partial profiles. Same-looking source profiles must agree on tags.
                if (!weapons.Select(Visible).Order().SequenceEqual(evidence.Weapons.Select(Visible).Order())) return unit;
                var mapped = evidence.Weapons.ToLookup(Visible);
                List<WeaponProfile> proposed = [];
                foreach (var weapon in weapons)
                {
                    var matches = mapped[Visible(weapon)].DistinctBy(w => DataJson.Write(new { w.SourceId, w.SourceCategories })).ToList();
                    if (matches.Count != 1) return unit;
                    var target = matches[0];
                    if (string.IsNullOrEmpty(target.SourceId) || target.SourceCategories == null
                        || !string.IsNullOrEmpty(weapon.SourceId) && weapon.SourceId != target.SourceId
                        || weapon.SourceCategories != null && !weapon.SourceCategories.Order().SequenceEqual(target.SourceCategories.Order())) return unit;
                    proposed.Add(weapon with { SourceId = target.SourceId, SourceCategories = target.SourceCategories.ToList() });
                }
                if (result != null && DataJson.Write(result) != DataJson.Write(proposed)) return unit;
                result = proposed;
            }
            changed = true;
            return unit with { Profile = unit.Profile with { Weapons = result! } };
        }).ToList();
        return changed ? data with { Units = units } : data;
    }
}
