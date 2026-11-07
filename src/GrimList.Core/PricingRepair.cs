namespace GrimList.Core;

public record PricingRepairRow(string Key, string Faction, string Unit, string Status, string Reason, string Prices = "", string AlliedPrices = "");
public record PricingRepairPreview(Dataset Dataset, string BaseSnapshot, List<PricingRepairRow> Rows, int SkippedCrucible = 0, int SkippedReferences = 0)
{
    public int Recovered => Rows.Count(r => r.Status == "Recovered");
    public int Unresolved => Rows.Count - Recovered;
}

public static class PricingRepair
{
    public static bool IsCrucible(UnitDefinition unit) => new[] { unit.Name, unit.Category }.Concat(unit.Profile?.Keywords ?? [])
        .Any(value => System.Text.RegularExpressions.Regex.IsMatch(value, @"\bCrucible\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
    public static bool IsReference(UnitDefinition unit) => unit.Profile?.Keywords?.Any(k => UnitIdentity.Normalize(k) == "REFERENCE") == true;
    public static bool NeedsRepair(UnitDefinition unit) => unit.Options.Count == 0 || unit.PointsWarning.Length > 0
        || unit.AlliedOptions is { Count: 0 } || unit.AlliedPointsWarning.Length > 0 || WargearPricing.MissingPrices(unit)
        || unit.WargearCosts?.Any(c => c.Points == null && (!c.OptionId.StartsWith("manual:") || WargearPricing.Options(unit).Any(o => o.Id == c.OptionId))) == true
        || unit.WargearCosts == null && unit.Profile?.Loadout?.Warnings.Any(w => w.Contains("additional equipment points")) == true;
    private static string Reason(UnitDefinition unit) => string.Join(" ", new[] { unit.PointsWarning, unit.AlliedPointsWarning }
        .Concat(unit.WargearCosts?.Where(c => c.Points == null).Select(c => c.Warning) ?? []).Where(s => s.Length > 0).Distinct()) is { Length: > 0 } reason
        ? reason : "No supported prices in the current source.";
    public static List<PricingRepairRow> Scan(Dataset data) => data.Units.Where(u => !IsCrucible(u) && !IsReference(u) && NeedsRepair(u))
        .OrderBy(u => u.Faction).ThenBy(u => u.Name).Select(u => new PricingRepairRow(u.Key, u.Faction, u.Name, "Unavailable", Reason(u))).ToList();
    private static string Prices(List<PointOption> options) => string.Join(" / ", options.Select(o => o.Label + (o.FromCopy > 1 ? $" (copy {o.FromCopy}+)" : "")));

    public static PricingRepairPreview Preview(Dataset current, Dataset incoming, CancellationToken cancellation = default)
    {
        current.Validate(); incoming.Validate();
        if (current.Game != incoming.Game || current.Source.Edition is not ("10" or "11") || current.Source.Edition != incoming.Source.Edition)
            throw new InvalidDataException("Repair requires source data for the same game and known edition as this library.");
        var index = incoming.Units.SelectMany(u => new[] { u.Key, u.NameKey }.Concat(u.Aliases ?? []).Select(k => (Key: UnitIdentity.Normalize(k), Unit: u)))
            .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Select(p => p.Unit).DistinctBy(u => u.Key).ToList());
        List<PricingRepairRow> rows = []; var replacements = new Dictionary<string, UnitDefinition>();
        foreach (var unit in current.Units.Where(u => !IsCrucible(u) && !IsReference(u) && NeedsRepair(u)))
        {
            cancellation.ThrowIfCancellationRequested();
            var matches = new[] { unit.Key }.Concat(unit.Aliases ?? []).Append(unit.NameKey).SelectMany(k => index.GetValueOrDefault(UnitIdentity.Normalize(k)) ?? [])
                .Where(u => UnitIdentity.Normalize(u.Faction) == UnitIdentity.Normalize(unit.Faction)).DistinctBy(u => u.Key).ToList();
            string? problem = matches.Count == 0 ? "Not found in this source. Import its original catalogue or use a newer source."
                : matches.Count > 1 ? "Several source entries match; automatic repair needs an unambiguous identity." : null;
            var candidate = matches.Count == 1 ? matches[0] : null;
            if (candidate != null)
            {
                if (new[] { candidate.PointsSource, candidate.ProfileSource, unit.PointsSource, unit.ProfileSource }.Any(s => s?.Edition is { Length: > 0 } e && e != current.Source.Edition))
                    problem = "Unit source edition does not match the library.";
                else if (NeedsRepair(candidate)) problem = Reason(candidate);
                else if (unit.AlliedOptions != null && candidate.AlliedOptions == null && !AlliedRules.IsMarineFaction(AlliedRules.OriginFaction(candidate)))
                    problem = "This source does not provide the unit's separate allied prices.";
                else if (unit.WargearCosts is { Count: > 0 } && candidate.WargearCosts == null)
                    problem = "This source does not provide the unit's equipment pricing.";
                else if (candidate.WargearCosts != null && candidate.Profile?.Loadout is { } loadout
                    && loadout.UnitGroups.Concat(loadout.Roles.SelectMany(r => r.Groups)).SelectMany(g => g.Options)
                        .Any(o => !candidate.WargearCosts.Any(c => c.OptionId == o.Id && c.Points != null)))
                    problem = "Equipment identities changed; a full datasheet refresh is needed before pricing can be repaired.";
            }
            if (problem == null && candidate != null)
            {
                var repaired = unit with { Options = candidate.Options, AlliedOptions = candidate.AlliedOptions,
                    DetachmentCosts = candidate.DetachmentCosts, CopyCategory = candidate.CopyCategory,
                    ModelCosts = candidate.ModelCosts, PriceFormula = candidate.PriceFormula,
                    Profile = unit.Profile == null ? candidate.Profile : candidate.Profile?.Loadout == null ? unit.Profile : unit.Profile with { Loadout = candidate.Profile.Loadout },
                    WargearCosts = candidate.WargearCosts, PointsWarning = "", AlliedPointsWarning = "", PointsSource = candidate.PointsSource ?? incoming.Source, Diagnostics = candidate.Diagnostics };
                var invalidDefault = repaired.Options.Where(o => o.FromCopy == 1).Any(o =>
                {
                    var entry = new ArmyEntry("check", repaired.Key, repaired.Name, o.Models);
                    return WargearPricing.Cost(repaired, entry) == null
                        || repaired.PriceFormula != null && FormulaPricing.Cost(repaired, entry, 1, false) == null;
                });
                if (invalidDefault)
                {
                    rows.Add(new(unit.Key, unit.Faction, unit.Name, "Needs source support", "Default equipment or model selections cannot be priced; loadout support is needed."));
                    continue;
                }
                replacements.Add(unit.Key, repaired);
                rows.Add(new(unit.Key, unit.Faction, unit.Name, "Recovered", "Prices recovered from " + repaired.PointsSource!.Name,
                    Prices(repaired.Options), repaired.AlliedOptions == null ? "Same as native" : Prices(repaired.AlliedOptions)));
            }
            else rows.Add(new(unit.Key, unit.Faction, unit.Name, "Needs source support", problem ?? "No usable prices returned."));
        }
        var result = current with { Units = current.Units.Select(u => replacements.GetValueOrDefault(u.Key, u)).ToList() };
        result.Validate(); return new(result, DataJson.Write(current), rows.OrderBy(r => r.Faction).ThenBy(r => r.Unit).ToList(), current.Units.Count(u => IsCrucible(u) && NeedsRepair(u)), current.Units.Count(u => IsReference(u) && NeedsRepair(u)));
    }

}
