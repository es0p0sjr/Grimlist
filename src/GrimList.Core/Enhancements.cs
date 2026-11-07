using System.Text.RegularExpressions;

namespace GrimList.Core;

public record EnhancementSelection(string Detachment, string Name);

public static class Enhancements
{
    // Owns Enhancement/Upgrade classification, bearer eligibility, shared slots,
    // army allowance and selection validation. LeaderRules calls the slot checks;
    // adding a new attachment permission does not require changing these policies.
    // Preserve for attachment work. Regression coverage: EnhancementChecks,
    // SourceEnhancementChecks and UpgradePricingChecks.
    private const string SelectionError = "GrimList.EnhancementSelection";
    internal static InvalidDataException Rejection(string message)
    {
        var error = new InvalidDataException(message);
        error.Data[SelectionError] = true;
        return error;
    }
    public static bool IsSelectionError(Exception error) => error.Data[SelectionError] is true;
    // The bodyguard and its attached entries share a selection slot; the selection
    // remains stored on its actual bearer. Do not move it when attaching/detaching.
    private static string SlotKey(ArmyList army, ArmyEntry entry) => entry.AttachedToId.Length > 0 && army.Entries.Any(e => e.Id == entry.AttachedToId) ? entry.AttachedToId : entry.Id;
    public static List<ArmyEntry> Slot(ArmyList army, string entryId)
    {
        var entry = army.Entries.Single(e => e.Id == entryId);
        var key = SlotKey(army, entry);
        return army.Entries.Where(e => SlotKey(army, e) == key).ToList();
    }
    public static string? SlotProblem(ArmyList army, string entryId)
    {
        var selected = Slot(army, entryId).Where(e => e.Enhancement != null).ToList();
        return selected.Count <= 1 ? null : "An attached unit can have only one enhancement or upgrade across its bodyguard and leaders. Remove one of these selections: "
            + string.Join("; ", selected.Select(e => $"{LeaderRules.EntryLabel(army, e.Id)} — {e.Enhancement!.Name}")) + ".";
    }
    public static bool Upgrade(ReferenceCard? card) => card != null && (card.IsUpgrade
        || Regex.IsMatch(card.Name, @"\bUPGRADE\b", RegexOptions.IgnoreCase)
        || Regex.IsMatch(card.Text, @"^\s*\[?UPGRADE\]?(?:\s*[:.–-]|\s+)", RegexOptions.IgnoreCase));
    public static string Kind(ReferenceCard card) => Upgrade(card) ? "Upgrade" : "Enhancement";
    // 3,000 points uses a provisional six-choice allowance, continuing the supplied 2/4 progression.
    public static int? Budget(int limit) => limit switch { > 0 and <= 1000 => 2, <= 2000 and > 1000 => 4, <= 3000 and > 2000 => 6, _ => null };
    public static void ValidateIncrease(ArmyList before, ArmyList after, Dataset? data)
    {
        var oldUsage = Usage(before, data); var newUsage = Usage(after, data);
        var previous = UpgradeCopies(before, data);
        foreach (var (key, count) in UpgradeCopies(after, data))
            if (count > 3 && count > previous.GetValueOrDefault(key))
                throw Rejection("Upgrade limit reached: at most 3 instances of the same Upgrade per army.");
        if (Budget(after.Limit) is { } budget && newUsage.Choices > budget && newUsage.Choices > oldUsage.Choices)
            throw Rejection($"Enhancement limit reached: {budget} choices at {after.Limit:N0} points. Up to 3 copies of each Upgrade share 1 choice.");
        foreach (var entry in after.Entries.Where(e => e.Enhancement != null))
            if (SlotProblem(after, entry.Id) is { } problem && (before.Entries.All(e => e.Id != entry.Id)
                || Slot(after, entry.Id).Count(e => e.Enhancement != null) > Slot(before, entry.Id).Count(e => e.Enhancement != null)))
                throw Rejection(problem);
    }
    // Choice allowance is separate from points pricing: copies of one Upgrade share
    // one choice, while each selected instance still has its own points cost.
    public static (int Choices, int Upgrades) Usage(ArmyList army, Dataset? data)
    {
        var selected = army.Entries.Where(e => e.Enhancement != null).ToList();
        var upgrades = selected.Count(e => Upgrade(Find(army, data, e.Enhancement)));
        return (selected.Count - upgrades + UpgradeCopies(army, data).Count, upgrades);
    }
    private static Dictionary<string, int> UpgradeCopies(ArmyList army, Dataset? data) => army.Entries
        .Where(e => e.Enhancement != null && Upgrade(Find(army, data, e.Enhancement)))
        .GroupBy(e => Key(e.Enhancement!)).ToDictionary(g => g.Key, g => g.Count());
    public static List<string> ArmyIssues(ArmyList army, Dataset? data)
    {
        var (choices, upgrades) = Usage(army, data); List<string> issues = [];
        if (UpgradeCopies(army, data).Values.Any(n => n > 3)) issues.Add("An army can take at most 3 instances of each Upgrade; copies of the same Upgrade use 1 enhancement choice.");
        if (Budget(army.Limit) is { } budget && choices > budget) issues.Add($"Enhancement allowance exceeded: {choices}/{budget} choices (copies of each Upgrade share 1 choice).");
        if (choices > 0 && Budget(army.Limit) == null) issues.Add("The enhancement allowance for this game size is unspecified.");
        foreach (var group in army.Entries.GroupBy(e => SlotKey(army, e)))
            if (SlotProblem(army, group.First().Id) is { } problem) issues.Add(problem);
        return issues;
    }
    public static string Key(ReferenceCard card) => Key(new EnhancementSelection(card.Detachment, card.Name));
    public static string Key(EnhancementSelection selection) => UnitIdentity.Normalize(selection.Detachment) + "|"
        + UnitIdentity.Normalize(Regex.Replace(selection.Name, @"\s+UPGRADE\s*$", "", RegexOptions.IgnoreCase));
    public static bool Character(UnitDefinition? unit)
    {
        if (unit == null) return false;
        var keys = (unit.Profile?.Keywords ?? []).Append(unit.Category).Select(UnitIdentity.Normalize).ToHashSet();
        return !keys.Contains("EPICHERO") && !keys.Contains("EPICHEROES") && (keys.Contains("CHARACTER") || keys.Contains("CHARACTERS"));
    }
    public static int? Cost(ReferenceCard? card)
    {
        var match = Regex.Match(card?.Cost ?? "", @"^\s*(\d+)\s*(?:pts?|points?)\s*$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var cost) ? cost : null;
    }
    public static ReferenceCard? Find(ArmyList army, Dataset? data, EnhancementSelection? selection) => selection == null ? null :
        (data?.ReferenceCards ?? []).FirstOrDefault(c => c.Faction == army.Faction && c.Section == "Enhancements"
            && !DetachmentLifecycle.IsObsolete(data, army.Faction, c.Detachment)
            && Key(c) == Key(selection with { Detachment = DetachmentLifecycle.ResolveName(data, army.Faction, selection.Detachment) }));
    private static Match Restriction(ReferenceCard card)
    {
        var text = Regex.Replace(card.Text.Replace("**", ""), @"\s+", " ").Trim();
        text = Regex.Replace(text, @"^\[?UPGRADE\]?(?:\s*[:.–-]\s*|\s+)", "", RegexOptions.IgnoreCase);
        return Regex.Match(text, @"^(?<required>.+?)\s+(?:(?:model|unit)s?\s+)?only(?:\s*\(excluding\s+(?<excluded>[^)]+)\))?\.", RegexOptions.IgnoreCase);
    }
    internal static bool HasKnownRestriction(ReferenceCard card) => card.Eligibility != null || Restriction(card).Success;
    public static bool Eligible(ArmyList army, UnitDefinition? unit, ReferenceCard card, Dataset? data = null)
    {
        // RULE INVARIANT: ordinary Enhancements require a non-Epic-Hero Character;
        // Upgrades may equip non-Characters when their source conditions permit it.
        // Do not apply the Character gate to both just because both share this UI/slot.
        // Missing import metadata must be repaired from source, never guessed here.
        if (unit == null || (!Upgrade(card) && !Character(unit)) || (unit.Profile?.Keywords ?? []).Append(unit.Category)
            .Any(k => UnitIdentity.Normalize(k) is "EPICHERO" or "EPICHEROES")
            || UnitAvailability.Problem(army, unit, data) != null || !AlliedRules.CanTakeEnhancement(army, unit, card, data)
            || card.Faction != army.Faction || card.Section != "Enhancements" || !DetachmentRules.Contains(army, card.Detachment, data)) return false;
        if (card.Applicability != null && !DetachmentRules.Applicable(army, new(card.Faction, "", Applicability: card.Applicability), data)) return false;
        var rule = Restriction(card);
        // Unknown restriction formats cannot establish eligibility.
        if (!rule.Success && card.Eligibility == null) return false;
        var keywords = (unit.Profile?.Keywords ?? []).Select(k => k.StartsWith("Faction:", StringComparison.OrdinalIgnoreCase) ? k[8..].Trim() : k)
            .Concat([AlliedRules.OriginFaction(unit), unit.Name, unit.Category]).Select(UnitIdentity.Normalize).Where(k => k.Length > 0).Distinct().ToList();
        // Apply explicit keyword grants from active detachment rules to the bearer itself.
        foreach (var detachment in data?.ReferenceCards ?? [])
            if (detachment.Section == "Detachments" && detachment.Faction == army.Faction && DetachmentRules.Contains(army, detachment.Detachment, data)
                && (detachment.Applicability == null || DetachmentRules.Applicable(army, new(detachment.Faction, "", Applicability: detachment.Applicability), data)))
                foreach (Match grant in Regex.Matches(detachment.Text.Replace("**", ""), @"(?:^|\n)[\s•*-]*Friendly (?<units>[A-Z][A-Z /'’‐‑–-]+) units have (?<keyword>[A-Z][A-Z ]+)\."))
                    if (grant.Groups["units"].Value.Split('/').Any(n => keywords.Contains(UnitIdentity.Normalize(n))))
                        keywords.Add(UnitIdentity.Normalize(grant.Groups["keyword"].Value));
        bool Match(string clause)
        {
            clause = Regex.Replace(clause, @"\b(?:models?|units?)\b", "", RegexOptions.IgnoreCase).Trim();
            return Regex.Split(clause, @"\s+or\s+|\s*/\s*", RegexOptions.IgnoreCase).Any(branch =>
            {
                var remaining = UnitIdentity.Normalize(Regex.Replace(branch, @"\band\b", "", RegexOptions.IgnoreCase));
                var memo = new Dictionary<string, bool>();
                bool AllKeywords(string suffix)
                {
                    if (suffix.Length == 0) return true;
                    if (memo.TryGetValue(suffix, out var known)) return known;
                    return memo[suffix] = keywords.Any(k => suffix.StartsWith(k, StringComparison.Ordinal) && AllKeywords(suffix[k.Length..]));
                }
                return remaining.Length > 0 && AllKeywords(remaining);
            });
        }
        if (card.Eligibility != null && card.Eligibility.Evaluate(army, unit, keywords, data) != true) return false;
        return !rule.Success || Match(rule.Groups["required"].Value) && (!rule.Groups["excluded"].Success || !Match(rule.Groups["excluded"].Value));
    }
    public static List<ReferenceCard> Options(ArmyList army, Dataset? data, UnitDefinition? unit) => data?.Game != army.Game ? [] :
        (data?.ReferenceCards ?? []).Where(c => Eligible(army, unit, c, data)).DistinctBy(Key).OrderBy(c => c.Detachment).ThenBy(c => c.Name).ToList();
    public static ArmyList Select(ArmyList army, Dataset? data, string entryId, EnhancementSelection? selection)
    {
        var entry = army.Entries.Single(e => e.Id == entryId);
        var next = army with { Entries = army.Entries.Select(e => e.Id == entryId ? e with { Enhancement = selection } : e).ToList() };
        if (selection != null)
        {
            if (entry.Enhancement != null && Key(entry.Enhancement) != Key(selection))
                throw Rejection("A unit can have only one enhancement or upgrade. Remove its current selection before adding another.");
            // Preserve includeEnhancement:false: a selection cannot use its own
            // granted keywords/effects to satisfy its bearer restriction.
            var card = Find(army, data, selection);
            if (card == null || !Options(next, data, EffectiveDatasheets.Project(next, next.Entries.Single(e => e.Id == entryId), data, false)?.Unit).Any(c => Key(c) == Key(card)))
                throw Rejection("This unit cannot take that enhancement or upgrade with the selected detachments.");
        }
        if (selection != null)
        {
            if (SlotProblem(next, entryId) is { } problem) throw Rejection(problem);
            ValidateIncrease(army, next, data);
        }
        return next;
    }
    public static List<string> Issues(ArmyList army, Dataset? data, ArmyEntry entry)
    {
        if (entry.Enhancement == null) return [];
        var card = Find(army, data, entry.Enhancement); var issues = new List<string>();
        if (card == null) issues.Add("Selected enhancement is missing from the imported rules.");
        else if (!Eligible(army, MusterEffects.EffectiveUnit(army, entry, data), card, data)) issues.Add("Selected enhancement or upgrade is no longer eligible for this unit or detachment.");
        if (Cost(card) == null) issues.Add("Enhancement points are unavailable.");
        return issues;
    }
}
