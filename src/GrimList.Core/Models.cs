using System.Text;
using System.Text.Json;

namespace GrimList.Core;

public record SourceInfo(string Name, string Version, string Date, string Url, string ImportedAt = "", string Edition = "", string ContentHash = "");
public record PointOption(int Models, int Points, int FromCopy = 1)
{
    public string Label => $"{Models} model{(Models == 1 ? "" : "s")} · {Points} pts";
    public override string ToString() => Label;
}
public record WeaponProfile(string Name, string Type, string Range, string Attacks, string Skill, string Strength, string Ap, string Damage, string Keywords = "",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SourceId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<string>? SourceCategories = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? GrantedBy = null);
public record ModelProfile(string Name, Dictionary<string, string> Characteristics);
public record AbilityProfile(string Name, string Text, string Section = "Abilities");
public record UnitProfile(Dictionary<string, string>? Characteristics = null, List<WeaponProfile>? Weapons = null, string Notes = "",
    List<string>? Keywords = null, List<AbilityProfile>? Abilities = null, List<ModelProfile>? Models = null, List<string>? Warnings = null, bool? MustBeWarlord = null,
    LoadoutDefinition? Loadout = null, AttachmentDefinition? AttachmentRules = null, AssociationDefinition? Associations = null,
    List<DetachmentKeywordRule>? DetachmentKeywords = null, DatasheetSemantics? Semantics = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<WarlordRestriction>? WarlordRestrictions = null);
public record ReferenceCard(string Faction, string Section, string Name, string Text, string Detachment = "", string Cost = "", SourceInfo? Source = null, bool IsUpgrade = false,
    ReferenceDetails? Details = null, EnhancementCondition? Eligibility = null, List<DatasheetModifier>? Modifiers = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<WarlordRestriction>? WarlordRestrictions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] EffectCondition? Applicability = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<WeaponProfile>? Weapons = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayText => ReferenceModel.Description(this);
}
public record SourceCost(string SelectionId, string TargetId, string Name, int Quantity, int Points, List<SourceCost>? Children = null);
public record EquipmentCost(string OptionId, int? Points, string Warning = "", SourceCost? Source = null);
public record DetachmentUnitCost(string Detachment, string Upgrade, int Points, string SourceId);
public record UnitDefinition(string Faction, string Name, List<PointOption> Options, int? MaxCopies = null, string Category = "Units", UnitProfile? Profile = null,
    string Id = "", List<string>? Aliases = null, SourceInfo? PointsSource = null, SourceInfo? ProfileSource = null, string PointsWarning = "", List<EquipmentCost>? WargearCosts = null,
    List<string>? RequiredDetachments = null, List<PointOption>? AlliedOptions = null, string AlliedPointsWarning = "",
    List<DetachmentUnitCost>? DetachmentCosts = null, string CopyCategory = "", List<EquipmentCost>? ModelCosts = null, UnitPriceFormula? PriceFormula = null,
    List<ImportDiagnostic>? Diagnostics = null)
{
    public string NameKey => ImportedUnitFacts.For(this).NameKey;
    public string Key => string.IsNullOrWhiteSpace(Id) ? NameKey : Id;
    [System.Text.Json.Serialization.JsonIgnore]
    public ImportStatus ImportStatus => Diagnostics?.Any(d => d.Kind == "SourceDataError") == true ? ImportStatus.SourceDataError
        : PricingRepair.NeedsRepair(this) ? ImportStatus.Unavailable
        : Profile?.Warnings?.Count > 0 || Profile?.Loadout?.Warnings.Count > 0 ? ImportStatus.AvailableWithWarnings : ImportStatus.Available;
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayCategory => UnitCategories.FromKeywords(new[] { Category }.Concat(Profile?.Keywords ?? []));
    public string Summary => Options.Count == 0 ? "Points unavailable" : string.Join(" / ", Options.Where(o => o.FromCopy == 1).Select(o => o.Label));
    public List<PointOption> PricesFor(ArmyList army, Dataset? data = null)
    {
        var options = AlliedRules.IsAlly(army, this) ? AlliedOptions ?? Options : Options;
        var addition = (DetachmentCosts ?? []).Where(c => DetachmentRules.Contains(army, c.Detachment, data)).Sum(c => c.Points);
        return addition == 0 ? options : options.Select(o => o with { Points = checked(o.Points + addition) }).ToList();
    }
    public string PriceWarningFor(ArmyList army) => AlliedRules.IsAlly(army, this) && AlliedOptions != null ? AlliedPointsWarning : PointsWarning;
}
public static class UnitCategories
{
    public static string FromKeywords(IEnumerable<string> keywords)
    {
        var keys = keywords.Select(UnitIdentity.Normalize).ToHashSet();
        if (keys.Contains("ALLIES")) return "Allies";
        if (keys.Overlaps(["DEDICATEDTRANSPORT", "DEDICATEDTRANSPORTS"])) return "Dedicated Transports";
        if (keys.Contains("EPICHERO") || keys.Contains("EPICHEROES")) return "Epic Heroes";
        if (keys.Contains("CHARACTER") || keys.Contains("CHARACTERS")) return "Characters";
        if (keys.Contains("BATTLELINE")) return "Battleline";
        if (keys.Contains("VEHICLE") || keys.Contains("VEHICLES")) return "Vehicles";
        if (keys.Contains("MONSTER") || keys.Contains("MONSTERS")) return "Monsters";
        return "Others";
    }
    public static int Order(string category) => FromKeywords([category]) switch
    { "Epic Heroes" => 0, "Characters" => 1, "Battleline" => 2, "Dedicated Transports" => 3, "Vehicles" => 4, "Monsters" => 5, "Allies" => 7, _ => 6 };
}
public record DetachmentGroupLimit(string Id, string Name, int Maximum);
public record DetachmentDefinition(string Faction, string Name, string Disposition = "", string Cost = "", List<string>? Dispositions = null, SourceInfo? Source = null,
    string Id = "", string FactionId = "", List<string>? Tags = null, List<string>? CannotCombineWithTags = null,
    List<MusterNominationRule>? Nominations = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<WarlordRestriction>? WarlordRestrictions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] EffectCondition? Applicability = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<DetachmentGroupLimit>? GroupLimits = null)
{
    // Keep the singular field readable for existing libraries and exported JSON.
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> DispositionOptions => (Dispositions ?? [Disposition]).Where(d => !string.IsNullOrWhiteSpace(d))
        .Select(d => d.Trim()).DistinctBy(UnitIdentity.Normalize).ToList();
    [System.Text.Json.Serialization.JsonIgnore]
    public string DispositionLabel => string.Join(" / ", DispositionOptions);
}
public record FactionContext(string Faction, string CatalogueId, List<string> Keywords);
public record Dataset(SourceInfo Source, List<UnitDefinition> Units, string Game = "Warhammer 40,000", List<DetachmentDefinition>? Detachments = null, List<ReferenceCard>? ReferenceCards = null, bool ReferenceOnly = false,
    int SchemaVersion = 1, List<ReferenceCoverage>? ReferenceCoverage = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<FactionContext>? FactionContexts = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<RulesPack>? RulesPacks = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<DetachmentSourceScope>? DetachmentSources = null)
{
    // Only a successfully parsed, commit-pinned online download can advance an
    // installed authority. Serialized/cached/partial imports cannot impersonate it.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBsDataDownload { get; init; }
    // A scoped editing view retains the full library's shared ability definitions
    // without retaining unrelated unit profiles. Never part of an exported dataset.
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, List<AbilityProfile>>? SharedAbilities { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsScoped { get; init; }
    public UnitDefinition? FindUnit(string key) => DatasetSnapshot.For(this).UnitsByIdentity.GetValueOrDefault(key);
    public void Validate()
    {
        if (IsScoped) throw new InvalidDataException("A faction editing view cannot replace the full library.");
        if (SchemaVersion is < 1 or > 4) throw new InvalidDataException("Unsupported dataset schema version: " + SchemaVersion);
        DetachmentLifecycle.Validate(this);
        if (RulesPacks is { Count: > 0 })
        {
            if (SchemaVersion < 3) throw new InvalidDataException("Rules packs require dataset schema 3.");
            if (RulesPacks.Select(p => p.Id).Distinct().Count() != RulesPacks.Count) throw new InvalidDataException("Duplicate rules pack ID.");
            foreach (var pack in RulesPacks)
            {
                GrimList.Core.RulesPacks.Validate(pack);
                if (pack.Source.Edition != Source.Edition) throw new InvalidDataException("Rules pack edition does not match dataset.");
            }
        }
        if (string.IsNullOrWhiteSpace(Source.Name) || string.IsNullOrWhiteSpace(Source.Version))
            throw new InvalidDataException("Give this points source a name and version.");
        if (Source.Date.Length > 0 && !DateOnly.TryParseExact(Source.Date, "yyyy-MM-dd", out _))
            throw new InvalidDataException("Use YYYY-MM-DD for the source date, or leave it blank if unknown.");
        if (ReferenceOnly && Units.Count != 0) throw new InvalidDataException("A reference-only package cannot contain units.");
        if (Units.Count == 0 && (RulesPacks?.Count ?? 0) == 0 && (!ReferenceOnly || (ReferenceCards?.Count ?? 0) == 0 && !(ReferenceCoverage ?? []).Any(c => c.Authoritative && c.Mode != "Partial"))) throw new InvalidDataException("No unit points or reference rules were found. Nothing was imported.");
        if (ReferenceCards?.Any(c => string.IsNullOrWhiteSpace(c.Faction) || string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Section) || string.IsNullOrWhiteSpace(ReferenceModel.Description(c))) == true)
            throw new InvalidDataException("Each reference card needs a faction, section, name and rule text.");
        foreach (var card in ReferenceCards ?? [])
            if (card.Details is { } detail && (detail.Scope is not ("Global" or "Faction" or "Detachment")
                || detail.Status is not ("Active" or "Stale" or "Obsolete" or "Removed" or "Unresolved")
                || detail.SourceId.Length == 0))
                throw new InvalidDataException("Invalid canonical reference metadata: " + card.Name);
        foreach (var coverage in ReferenceCoverage ?? []) ReferenceMerge.ValidateCoverage(coverage, coverage.Edition);
        if (Units.Select(u => u.Key).Distinct().Count() != Units.Count)
            throw new InvalidDataException("Duplicate unit names within a faction. Review the source before importing.");
        if (Detachments?.Any(d => string.IsNullOrWhiteSpace(d.Faction) || string.IsNullOrWhiteSpace(d.Name)) == true)
            throw new InvalidDataException("Each detachment needs a faction and name.");
        if ((Detachments ?? []).SelectMany(d => d.GroupLimits ?? []).Any(g => string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Name) || g.Maximum < 0))
            throw new InvalidDataException("Invalid detachment group limit.");
        foreach (var detachment in Detachments ?? [])
            if (detachment.Nominations is { } nominations && (nominations.Select(r => r.Id).Distinct().Count() != nominations.Count
                || nominations.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Minimum < 0 || r.Maximum < 1 || r.Maximum < r.Minimum
                    || r.Targets.Count == 0 || r.GrantedKeywords.Count == 0 || r.Targets.Concat(r.GrantedKeywords).Any(string.IsNullOrWhiteSpace))))
                throw new InvalidDataException($"Invalid muster nomination metadata for {detachment.Name}.");
        foreach (var u in Units)
        {
            if (string.IsNullOrWhiteSpace(u.Name) || string.IsNullOrWhiteSpace(u.Faction))
                throw new InvalidDataException("Every unit needs a faction and name.");
            if (u.PointsSource != null) ValidateSource(u.PointsSource);
            if (u.ProfileSource != null) ValidateSource(u.ProfileSource);
            if (u.Profile?.Loadout is { } definition)
                foreach (var adjustment in definition.Roles.SelectMany(r => (r.Minimum.Adjustments ?? []).Concat(r.Maximum.Adjustments ?? [])).Where(a => a.Query != null))
                {
                    var query = adjustment.Query!;
                    if (adjustment.Operation is not ("set" or "increment" or "decrement" or "multiply") || adjustment.Value < 0 || adjustment.Threshold < 0 || adjustment.PerModels != 0
                        || adjustment.Comparison is not ("atLeast" or "atMost" or "greaterThan" or "lessThan" or "equalTo" or "notEqualTo")
                        || query.Scope is not ("unit" or "unit-self") && u.Aliases?.Any(a => a.EndsWith(":" + query.Scope, StringComparison.Ordinal)) != true
                        || query.RoleIds.Count == 0 || query.RoleIds.Distinct().Count() != query.RoleIds.Count || query.RoleIds.Any(id => !definition.Roles.Any(r => r.Id == id)))
                        throw new InvalidDataException($"Invalid selection-dependent model limit for {u.Name}.");
                }
            if (u.ModelCosts?.Any(c => c.Points is null or < 0 || !((u.Profile?.Loadout?.Roles ?? []).Any(r => r.Id == c.OptionId))) == true
                || u.ModelCosts?.GroupBy(c => c.OptionId).Any(g => g.Count() > 1) == true)
                throw new InvalidDataException($"Invalid model prices for {u.Name}.");
            if (u.PriceFormula is { } formula)
            {
                if (formula.SelectionRules != null)
                {
                    if (u.Profile?.Loadout?.Composition == null) throw new InvalidDataException("Recursive pricing needs its imported selection definition.");
                    RuleEvaluation.Validate(formula.SelectionRules);
                }
                bool Valid(PriceTest t, int depth = 0) => depth < 20 && (t.Kind is "and" or "or"
                    ? t.Children != null && t.Children.All(c => Valid(c, depth + 1))
                    : t.Comparison is "atLeast" or "greaterThan" or "atMost" or "lessThan" or "equalTo" or "notEqualTo"
                        && t.Value >= 0 && (t.Kind is "models" or "copy" or "native" or "constant"
                            || t.Kind == "roles" && t.RoleIds is { Count: > 0 } && t.RoleIds.All(id => u.Profile?.Loadout?.Roles.Any(r => r.Id == id) == true)));
                if (formula.Base < 0 || formula.Rules.Any(r => r.Value < 0 || r.Operation is not ("set" or "increment" or "decrement" or "multiply") || !Valid(r.Test)))
                    throw new InvalidDataException($"Invalid composition pricing for {u.Name}.");
            }
            if (u.Profile?.Loadout?.Composition is { } composition)
                SelectionCompositions.Validate(composition, u.Profile.Loadout.Roles.Select(r => r.Id));
            if (u.DetachmentCosts?.Any(c => c.Points < 0 || string.IsNullOrWhiteSpace(c.Detachment) || string.IsNullOrWhiteSpace(c.SourceId)) == true
                || u.DetachmentCosts?.GroupBy(c => c.SourceId).Any(g => g.Count() > 1) == true)
                throw new InvalidDataException($"Invalid detachment prices for {u.Name}.");
            if (u.MaxCopies is <= 0 || u.Options.Any(o => o.Models <= 0 || o.Points < 0 || o.FromCopy <= 0))
                throw new InvalidDataException($"Invalid size or points for {u.Name}.");
            if (u.WargearCosts?.Any(c => string.IsNullOrWhiteSpace(c.OptionId) || c.Points < 0) == true
                || u.WargearCosts?.GroupBy(c => c.OptionId).Any(g => g.Count() > 1) == true)
                throw new InvalidDataException($"Invalid equipment costs for {u.Name}.");
            if (u.Options.Select(o => (o.Models, o.FromCopy)).Distinct().Count() != u.Options.Count)
                throw new InvalidDataException($"Duplicate size/price tiers for {u.Name}.");
            if (u.Options.Any(o => !u.Options.Any(b => b.Models == o.Models && b.FromCopy == 1)))
                throw new InvalidDataException($"A base cost is missing for {u.Name}.");
            if (u.AlliedOptions is { } allied && (allied.Any(o => o.Models <= 0 || o.Points < 0 || o.FromCopy <= 0)
                || allied.Select(o => (o.Models, o.FromCopy)).Distinct().Count() != allied.Count
                || allied.Any(o => !allied.Any(b => b.Models == o.Models && b.FromCopy == 1))))
                throw new InvalidDataException($"Invalid allied price tiers for {u.Name}.");
        }
        var keys = new Dictionary<string, string>();
        foreach (var u in Units)
            foreach (var alias in new[] { u.Key, u.NameKey }.Concat(u.Aliases ?? []))
            {
                var normalized = UnitIdentity.Normalize(alias);
                if (keys.TryGetValue(normalized, out var owner) && owner != u.Key)
                    throw new InvalidDataException($"Ambiguous unit name or alias: {alias}");
                keys[normalized] = u.Key;
            }
    }
    public static void ValidateSource(SourceInfo source)
    {
        if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Version) ||
            (source.Date.Length > 0 && !DateOnly.TryParseExact(source.Date, "yyyy-MM-dd", out _)))
            throw new InvalidDataException("Each source needs a name, version and a valid YYYY-MM-DD date (or blank).");
    }
}
public record ArmyEntry(string Id, string UnitKey, string Name, int Models, string Notes = "", UnitLoadout? Loadout = null, string AttachedToId = "", EnhancementSelection? Enhancement = null, List<RosterChoice>? Choices = null);
public record ArmyList(string Id, string Name, string Faction, int Limit, List<ArmyEntry> Entries, string Game = "Warhammer 40,000", string Detachment = "", string WarlordEntryId = "", List<string>? Detachments = null, string Disposition = "", List<MusterSelection>? MusterSelections = null)
{
    public static ArmyList New(string faction) => new(Guid.NewGuid().ToString(), "Untitled army", faction, 2000, []);
    public ArmyList Duplicate()
    {
        var entries = Entries.Select(e => e with { Id = Guid.NewGuid().ToString() }).ToList();
        var ids = Entries.Select((e, i) => (e.Id, NewId: entries[i].Id)).ToDictionary(e => e.Id, e => e.NewId);
        entries = entries.Select(e => e with { AttachedToId = ids.GetValueOrDefault(e.AttachedToId, "") }).ToList();
        var warlord = Entries.FindIndex(e => e.Id == WarlordEntryId);
        return this with { Id = Guid.NewGuid().ToString(), Name = Name + " (copy)", Entries = entries, WarlordEntryId = warlord < 0 ? "" : entries[warlord].Id,
            MusterSelections = MusterSelections?.Where(s => ids.ContainsKey(s.EntryId)).Select(s => s with { EntryId = ids[s.EntryId] }).ToList() };
    }
}
public record PricedEntry(ArmyEntry Entry, int? Points, int Copy, int? BasePoints = null, int CopyTax = 0, int? WargearPoints = 0, int? EnhancementPoints = 0)
{
    public string Breakdown => $"Base {BasePoints?.ToString() ?? "?"} pts"
        + (CopyTax != 0 ? $" · Copy {Copy}: +{CopyTax} pts" : "")
        + (WargearPoints != 0 ? $" · Wargear +{WargearPoints?.ToString() ?? "?"} pts" : "")
        + (EnhancementPoints != 0 ? $" · Enhancement/upgrade +{EnhancementPoints?.ToString() ?? "?"} pts" : "");
}
public record ArmyResult(List<PricedEntry> Entries, int Total, List<string> Issues)
{
    public bool Complete => Entries.All(e => e.Points.HasValue);
    public bool Validated => Complete && Issues.Count == 0;
    public string ValidationStatus => Validated ? "Army validated." : "Army not validated.";
}
public static class ArmyCalculator
{
    public static int NextCopy(ArmyList army, Dataset? dataset, UnitDefinition unit) => 1 + army.Entries.Count(e =>
        unit.CopyCategory.Length == 0 ? (dataset?.FindUnit(e.UnitKey)?.Key ?? e.UnitKey) == unit.Key
            : MusterEffects.EffectiveUnit(army, e, dataset)?.Profile?.Keywords?.Any(k => UnitIdentity.Normalize(k) == UnitIdentity.Normalize(unit.CopyCategory)) == true);

    public static ArmyResult Calculate(ArmyList army, Dataset? dataset)
    {
        var copies = new Dictionary<string, int>();
        var categoryCopies = new Dictionary<string, int>();
        var entries = new List<PricedEntry>();
        var issues = new List<string>();
        if (dataset == null) issues.Add("Import a points source to calculate this army.");
        if (dataset != null && dataset.Game != army.Game) issues.Add("The points source belongs to a different game.");
        if (army.Entries.Count == 0) issues.Add("Add units to start your army.");
        if (army.Limit <= 0) issues.Add("Set a points limit greater than zero.");
        issues.AddRange(WarlordRules.Issues(army, dataset));
        issues.AddRange(LeaderRules.Issues(army, dataset));
        issues.AddRange(DetachmentRules.Issues(army, dataset));
        issues.AddRange(Enhancements.ArmyIssues(army, dataset));
        issues.AddRange(MusterRules.Issues(army, dataset));
        issues.InsertRange(0, MusterEffects.Issues(army, dataset));
        foreach (var entry in army.Entries)
        {
            issues.AddRange(EffectiveDatasheets.ChoiceIssues(army, entry, dataset));
            var unit = MusterEffects.EffectiveUnit(army, entry, dataset);
            issues.AddRange((unit?.Profile?.Warnings ?? []).Where(w => w.StartsWith("Conditional rules could not converge", StringComparison.Ordinal)).Select(w => entry.Name + ": " + w));
            var key = unit?.Key ?? entry.UnitKey;
            var nativeCopy = copies.GetValueOrDefault(key) + 1;
            var copy = unit?.CopyCategory is { Length: > 0 } category ? categoryCopies.GetValueOrDefault(UnitIdentity.Normalize(category)) + 1 : nativeCopy;
            copies[key] = nativeCopy;
            foreach (var keyword in (unit?.Profile?.Keywords ?? []).Select(UnitIdentity.Normalize).Distinct())
                categoryCopies[keyword] = categoryCopies.GetValueOrDefault(keyword) + 1;
            int? points = null;
            int? basePoints = null, wargearPoints = 0, enhancementPoints = 0; var copyTax = 0;
            if (unit != null && dataset?.Game == army.Game)
            {
                if (UnitAvailability.Problem(army, unit, dataset) is { } availability) issues.Add(availability);
                if (entry.Loadout != null) issues.AddRange(Loadouts.Issues(unit, entry).Select(i => $"{entry.Name}: {i}"));
                if (unit.PriceWarningFor(army).Length > 0) issues.Add($"{entry.Name}: {unit.PriceWarningFor(army)}");
                var options = unit.PricesFor(army, dataset);
                points = options.Where(o => o.Models == entry.Models && o.FromCopy <= copy)
                    .OrderByDescending(o => o.FromCopy).FirstOrDefault()?.Points;
                if (points == null) issues.Add($"{entry.Name}: size {entry.Models} is unavailable in this source.");
                basePoints = options.FirstOrDefault(o => o.Models == entry.Models && o.FromCopy == 1)?.Points;
                if (unit.PriceFormula != null && points != null)
                {
                    var addition = (unit.DetachmentCosts ?? []).Where(c => DetachmentRules.Contains(army, c.Detachment, dataset)).Sum(c => c.Points);
                    points = FormulaPricing.Cost(unit, entry, copy, AlliedRules.IsAlly(army, unit)) + addition;
                    basePoints = FormulaPricing.Cost(unit, entry, 1, AlliedRules.IsAlly(army, unit)) + addition;
                    if (points == null) issues.Add($"{entry.Name}: composition pricing needs a valid model loadout.");
                }
                copyTax = (points ?? 0) - (basePoints ?? 0);
                wargearPoints = WargearPricing.Cost(unit, entry);
                if (wargearPoints == null) issues.Add($"{entry.Name}: selected wargear points are unavailable; review its loadout.");
                points += wargearPoints;
            }
            else issues.Add($"{entry.Name}: missing from this points source.");
            if (entry.Enhancement != null)
            {
                issues.AddRange(Enhancements.Issues(army, dataset, entry).Select(i => $"{entry.Name}: {i}"));
                enhancementPoints = Enhancements.Cost(Enhancements.Find(army, dataset, entry.Enhancement));
                points += enhancementPoints;
            }
            entries.Add(new(entry, points, copy, basePoints, copyTax, wargearPoints, enhancementPoints));
        }
        var total = entries.Sum(e => e.Points ?? 0);
        issues.AddRange(AlliedRules.Issues(army, dataset, entries));
        if (dataset == null || dataset.Game != army.Game || !army.Entries.Any(e => WarlordRules.IsCharacter(MusterEffects.EffectiveUnit(army, e, dataset))))
            issues.Add("Add at least one Character unit to the army.");
        if (total > army.Limit) issues.Add($"{total - army.Limit:N0} points over the army limit.");
        return new(entries, total, issues.Distinct().ToList());
    }

    public static string Export(ArmyList army, Dataset? dataset) => RosterExport.Text(army, dataset);

}
public static class DataJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string Write<T>(T data) => JsonSerializer.Serialize(data, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("The file is empty.");
}
