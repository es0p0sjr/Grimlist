using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GrimList.Core;

public record ReferenceTarget(string Name, string Id = "", string Match = "Unresolved");
public record ReferenceDetails(string Id = "", string Edition = "", string SourceId = "", string SourceCardId = "",
    string SourceType = "StructuredJson", string Scope = "Faction", string FactionId = "",
    List<ReferenceTarget>? Detachments = null, string Timing = "", string Target = "", string Effect = "", string Restrictions = "",
    string SemanticHash = "", string Status = "Active", string Replaces = "", string Match = "Unresolved");

// A completeness declaration is scoped to one owner, edition, category and optional
// faction/detachment. Partial declarations never authorize removal through absence.
public record ReferenceCoverage(string SourceId, string Edition, string Category = "Stratagems", string Faction = "", string Detachment = "",
    string State = "Unknown", string Mode = "Partial", bool Authoritative = false, SourceInfo? Source = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CatalogueFingerprint = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ReviewReason = null);
public record ReferenceChange(string Status, string Id, string Faction, string Detachment, string Name, string SourceId,
    string Message = "", List<string>? DifferingFields = null);

public static class ReferenceModel
{
    // Merge-routing boundary, not a test of whether a card has gameplay effects.
    // Stratagems use the display-reference pipeline; Enhancements, Upgrades and
    // Dispositions retain their selection semantics. Preserve this classification
    // for attachment work: routing selectable cards as display-only loses the
    // dedicated merge/validation behavior that their metadata requires.
    public static bool DisplayOnly(ReferenceCard c) => !c.IsUpgrade && Key(c.Section) is not ("ENHANCEMENT" or "ENHANCEMENTS" or "DISPOSITION" or "DISPOSITIONS");
    public static string Category(string section) => Key(section) switch
    {
        "STRATAGEM" or "STRATAGEMS" => "Stratagems",
        "FACTIONRULE" or "FACTIONRULES" or "ARMYRULE" or "ARMYRULES" => "Army Rules",
        "DETACHMENTRULE" or "DETACHMENTRULES" or "DETACHMENTS" => "Detachments",
        _ => section
    };
    public static string Key(string value) => UnitIdentity.Normalize(value);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Owner(SourceInfo source) => source.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase)
        ? "bsdata:" + source.Edition : "source-" + Hash(source.Url.Length > 0 ? source.Url.Trim().TrimEnd('/') : source.Name.Trim());
    public static string Clean(string value) => Regex.Replace(value.Trim().Replace("\r\n", "\n"), @"\s+", " ");
    public static string LogicalKey(ReferenceCard c) => DataJson.Write(new[] { c.Details?.Edition ?? c.Source?.Edition ?? "", Key(c.Faction),
        Key(c.Section), c.Details?.Scope ?? (c.Detachment.Length > 0 ? "Detachment" : ArmyReferences.IsCore(c) ? "Global" : "Faction"),
        string.Join("|", (c.Details?.Detachments ?? (c.Detachment.Length > 0 ? [new(c.Detachment)] : [])).Select(t => Key(t.Name)).Order()), Key(c.Name) });
    public static ReferenceCard Canonical(ReferenceCard card, SourceInfo fallback)
    {
        var source = card.Source ?? fallback;
        var details = card.Details ?? new ReferenceDetails(SourceType: source.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase) ? "BSData" : "StructuredJson",
            Scope: card.Detachment.Length > 0 ? "Detachment" : Key(card.Faction) == "CORE" ? "Global" : "Faction");
        // Once canonicalized, an unknown edition remains unknown. A subsequent
        // catalogue update must not silently adopt legacy cards into its edition.
        details = details with { Edition = details.Id.Length > 0 || details.Edition.Length > 0 ? details.Edition : source.Edition.Length > 0 ? source.Edition : fallback.Edition,
            SourceId = details.SourceId.Length > 0 ? details.SourceId : Owner(source),
            Detachments = details.Detachments ?? (card.Detachment.Length > 0 ? [new(card.Detachment)] : []) };
        var next = card with { Source = source, Details = details, Section = Category(card.Section), Faction = details.Scope == "Global" ? "Core" : card.Faction };
        if (details.Id.Length == 0)
            details = details with { Id = "ref-" + Hash(DataJson.Write(new[] { details.Edition, details.SourceId, details.FactionId.Length > 0 ? details.FactionId : Key(card.Faction),
                details.SourceCardId.Length > 0 ? details.SourceCardId : LogicalKey(next) })) };
        next = next with { Details = details };
        return next with { Details = details with { SemanticHash = ContentHash(next) } };
    }
    public static string ContentHash(ReferenceCard c) => Hash(DataJson.Write(new { Name = Clean(c.Name), Cost = Clean(c.Cost),
        Text = Clean(c.Text), Timing = Clean(c.Details?.Timing ?? ""), Target = Clean(c.Details?.Target ?? ""),
        Effect = Clean(c.Details?.Effect ?? ""), Restrictions = Clean(c.Details?.Restrictions ?? ""),
        Faction = Key(c.Faction), c.Details?.Edition, c.Details?.Scope, Category = Key(c.Section),
        Detachments = (c.Details?.Detachments ?? []).Select(t => t.Id.Length > 0 ? t.Id : Key(t.Name)).Order().ToArray() })
        + (c.Applicability != null ? "\nApplicability:" + DataJson.Write(c.Applicability) : "")
        + (c.WarlordRestrictions is { Count: > 0 } ? "\nWarlordRestrictions:" + DataJson.Write(c.WarlordRestrictions) : "")
        + (c.Modifiers is { Count: > 0 } ? "\nModifiers:" + ModifierContent(c) : "")
        + (c.Weapons is { Count: > 0 } ? "\nWeapons:" + DataJson.Write(c.Weapons) : ""));
    internal static string ModifierContent(ReferenceCard card) => DataJson.Write((card.Modifiers ?? []).Select(m => new {
        m.Target, m.Characteristic, m.Operation, m.Value, m.Profile, m.WeaponType, m.Condition, m.WeaponPredicate, m.Recipient
    }).Select(m => DataJson.Write(m)).Order(StringComparer.Ordinal));
    public static string Description(ReferenceCard c)
    {
        if (c.Weapons == null && EnhancementWeapons.Profiles(c) is { Count: > 0 } legacyWeapons)
            return EnhancementWeapons.GrantText(legacyWeapons);
        var d = c.Details;
        if (d == null || new[] { d.Timing, d.Target, d.Effect, d.Restrictions }.All(string.IsNullOrWhiteSpace)) return c.Text;
        if (d.SourceType == "BSData" && c.Text.Length > 0) return c.Text;
        return string.Join("\n\n", new[] { ("WHEN", d.Timing), ("TARGET", d.Target), ("EFFECT", d.Effect), ("RESTRICTIONS", d.Restrictions) }
            .Where(p => p.Item2.Length > 0).Select(p => p.Item1 + ": " + p.Item2).Concat(c.Text.Length > 0 ? [c.Text] : []));
    }
    public static Dataset ExportDataset(Dataset data) => data.IsScoped ? throw new InvalidDataException("Load the complete library before exporting it.") : data with { SchemaVersion = Math.Max(2, data.SchemaVersion),
        ReferenceCards = DatasetSnapshot.For(data).References.All.ToList(), ReferenceCoverage = data.ReferenceCoverage ?? [] };
}
