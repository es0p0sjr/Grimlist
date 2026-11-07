namespace GrimList.Core;

/// <summary>Unit-size prices only. Equipment, composition and reference rules keep their own import paths.</summary>
public static class MfmPoints
{
    public static string Summary(Dataset data)
    {
        var units = data.Units.Where(u => IsMfm(u.PointsSource)).ToList();
        if (units.Count == 0) return "Unit points: catalogue/manual sources; no MFM prices applied.";
        var dates = units.Select(u => u.PointsSource!.Date).Where(d => d.Length > 0).Distinct().Order().ToList();
        var observed = dates.Count == 0 ? "unknown" : dates.Count == 1 ? dates[0] : dates[0] + " to " + dates[^1];
        return $"MFM unit prices: {units.Count}/{data.Units.Count} units · observed {observed}. Other prices retain their existing sources.";
    }

    public static bool IsMfm(SourceInfo? source) => source != null
        && source.Name.StartsWith("Munitorum Field Manual", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
        && (uri.Host == "mfm.warhammer-community.com"
            || uri.Host == "assets.warhammer-community.com"
            || uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/BSData/wh40k-11e-mfm/", StringComparison.OrdinalIgnoreCase));

    public static bool Compatible(UnitDefinition unit, UnitDefinition prices) => unit.PriceFormula == null
        && !(unit.ModelCosts?.Any(c => c.Points != 0) ?? false) && unit.CopyCategory.Length == 0
        && unit.Options.Count > 0 && prices.Options.Count > 0
        && unit.Options.Select(o => o.Models).ToHashSet().SetEquals(prices.Options.Select(o => o.Models));

    // Called after the ordinary merge: only base unit prices/provenance are restored.
    // In particular, never restore an old loadout, enhancement or stratagem here.
    public static UnitDefinition Preserve(UnitDefinition? previous, UnitDefinition next, SourceInfo incomingSource,
        List<string> warnings)
    {
        if (previous == null || !IsMfm(previous.PointsSource)) return next;
        var source = next.PointsSource ?? incomingSource;
        var olderMfm = IsMfm(source) && previous.PointsSource!.Date.Length > 0 && source.Date.Length > 0
            && string.CompareOrdinal(source.Date, previous.PointsSource.Date) < 0;
        var catalogue = !IsMfm(source) && source.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase);
        if (!catalogue && !olderMfm) return next;
        if (!Compatible(next, previous))
        {
            warnings.Add($"{next.Faction} / {next.Name}: saved MFM prices no longer match the catalogue composition. Using catalogue prices; review this unit.");
            return next;
        }
        if (olderMfm) warnings.Add($"{next.Name}: older MFM snapshot ignored; retained {previous.PointsSource!.Version} ({previous.PointsSource.Date}).");
        return next with { Options = previous.Options, PointsSource = previous.PointsSource, PointsWarning = previous.PointsWarning };
    }

    public static ImportPreview Overlay(Dataset catalogue, Dataset prices)
    {
        if (catalogue.Source.Edition != prices.Source.Edition || catalogue.Game != prices.Game)
            throw new InvalidDataException("MFM points and catalogue must belong to the same game and edition.");
        prices.Validate();
        var index = prices.Units.GroupBy(u => UnitIdentity.Normalize(u.NameKey)).ToDictionary(g => g.Key, g => g.ToList());
        var factions = prices.Units.Select(u => UnitIdentity.Normalize(u.Faction)).ToHashSet();
        List<string> warnings = []; var applied = 0;
        var units = catalogue.Units.Select(unit =>
        {
            var faction = unit.Faction;
            // Chapters without a separate MFM page inherit the Space Marines page.
            // Chapters with their own page must match there; do not hide missing entries.
            if (!factions.Contains(UnitIdentity.Normalize(faction)) && AlliedRules.IsMarineFaction(faction)
                && !MfmImporter.Factions.Any(f => UnitIdentity.Normalize(f) == UnitIdentity.Normalize(faction))) faction = "Space Marines";
            var key = UnitIdentity.Normalize(new UnitDefinition(faction, unit.Name, []).NameKey);
            if (!index.TryGetValue(key, out var matches) || matches.Count != 1)
            {
                warnings.Add($"{unit.Faction} / {unit.Name}: no unique MFM match; retained existing unit prices.");
                return unit;
            }
            var match = matches[0];
            if (!Compatible(unit, match))
            {
                warnings.Add($"{unit.Faction} / {unit.Name}: MFM model sizes or composition pricing differ; retained existing unit prices.");
                return unit;
            }
            applied++;
            return Preserve(unit, unit with { Options = match.Options, PointsSource = match.PointsSource ?? prices.Source, PointsWarning = "" }, prices.Source, warnings);
        }).ToList();
        warnings.Insert(0, $"MFM unit points: {applied}/{units.Count} units matched. Snapshot {prices.Source.Version}, observed {prices.Source.Date}. Equipment, enhancement and detachment costs remain supplied by the catalogue.");
        var result = catalogue with { Units = units }; result.Validate();
        return new(result, warnings);
    }
}
