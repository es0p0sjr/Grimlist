using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GrimList.Core;

namespace GrimList.Application;

// This projection repairs source-owned equipment metadata, never armies or base/MFM prices.
public static class EquipmentPriceRecovery
{
    public record UnitEvidence(string Faction, List<string> Aliases, string WeaponsHash, List<EquipmentCost> Costs, string LegacyWeaponsHash = "");
    public record Pack(SourceInfo Source, List<UnitEvidence> Units);
    private static readonly Lazy<List<Pack>> bundled = new(() =>
    {
        using var input = typeof(EquipmentPriceRecovery).Assembly.GetManifestResourceStream("GrimList.Application.Resources.equipment-prices.json.gz");
        if (input == null) return [];
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<Pack>>(gzip, DataJson.Options) ?? [];
    });
    public static string WeaponsHash(UnitDefinition unit, bool legacy = false) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes((unit.Profile?.Weapons ?? []).Select(w => legacy ? w with { SourceId = null, SourceCategories = null } : w).ToList(),
            new JsonSerializerOptions { WriteIndented = false })));
    public static Pack Create(Dataset data) => new(data.Source, data.Units.Where(u => u.Profile?.Weapons?.Count > 0)
        .Select(u => new UnitEvidence(u.Faction, u.Aliases?.Where(a => a.StartsWith("bsdata:")).ToList() ?? [],
            WeaponsHash(u), u.WargearCosts?.Where(c => c.OptionId.StartsWith("manual:")).ToList() ?? [], WeaponsHash(u, legacy: true))).ToList());
    public static Dataset ApplyBundled(Dataset data) => Apply(data, bundled.Value);
    public static Dataset Apply(Dataset data, IEnumerable<Pack> packs)
    {
        var evidence = packs.Where(p => p.Source.ContentHash.Length > 0 && p.Source.Edition is "10" or "11")
            .SelectMany(p => p.Units.Select(u => (p.Source, Unit: u))).ToLookup(x => (x.Source.ContentHash.ToUpperInvariant(), x.Source.Edition, x.Unit.Faction));
        var changed = false;
        var units = data.Units.Select(unit =>
        {
            var source = unit.ProfileSource;
            if (source == null || unit.WargearCosts == null || unit.Profile?.Weapons?.Count is not > 0) return unit;
            // An existing source-selection price may be a user override. Without
            // its own matching evidence, do not introduce manual aliases that could
            // bypass that price. The legacy empty-table failure has no such rows.
            if (unit.WargearCosts.Any(c => !c.OptionId.StartsWith("manual:"))) return unit;
            var candidates = evidence[(source.ContentHash.ToUpperInvariant(), source.Edition, unit.Faction)]
                .Where(x => x.Unit.Aliases.Intersect(unit.Aliases ?? []).Any()).Select(x => x.Unit).ToList();
            if (candidates.Count != 1) return unit;
            var legacy = unit.Profile.Weapons.All(w => w.SourceId == null && w.SourceCategories == null);
            if ((legacy ? candidates[0].LegacyWeaponsHash : candidates[0].WeaponsHash) != WeaponsHash(unit, legacy)) return unit;
            var known = unit.WargearCosts.Select(c => c.OptionId).ToHashSet();
            var missing = candidates[0].Costs.Where(c => !known.Contains(c.OptionId)).ToList();
            if (missing.Count == 0) return unit;
            changed = true;
            return unit with { WargearCosts = unit.WargearCosts.Concat(missing).ToList() };
        }).ToList();
        return changed ? data with { Units = units } : data;
    }

    public static List<string> ValidateBundled(Dataset data)
    {
        var repaired = ApplyBundled(data);
        return repaired.Units.SelectMany(WargearPricing.CoverageIssues).ToList();
    }
}
