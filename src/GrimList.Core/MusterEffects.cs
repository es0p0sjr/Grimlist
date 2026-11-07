using System.Text.RegularExpressions;

namespace GrimList.Core;

public record MusterNominationRule(string Id, string Name, int Minimum, int Maximum, List<string> Targets,
    List<string> GrantedKeywords, bool SelectModels = false);
public record MusterSelection(string Detachment, string RuleId, string EntryId, int? ModelIndex = null);
public record ActiveMusterRule(string Detachment, MusterNominationRule Rule);

/// <summary>Roster selections grant derived state; the imported datasheet is never modified.</summary>
public static class MusterEffects
{
    internal static List<MusterNominationRule> Parse(string text)
    {
        text = Regex.Replace(text.Replace("*", "").Replace("^^", "").Replace("__", ""), @"\s+", " ");
        const string count = @"(?<count>\d+|one|two|three|four|five|six)";
        var pattern = @"(?:When mustering your army|In the Muster Armies step),? (?:you (?:can|must) )?select (?<optional>up to )?" + count
            + @" (?<target>[\p{L}\p{N} '\-’/]+?) (?<kind>units|models) from your army\. (?:Until the end of the battle, )?(?:those|the selected) (?:units|models) (?:have|gain) the (?<keyword>[\p{L}\p{N} '\-]+?) keyword\b";
        return Regex.Matches(text, pattern, RegexOptions.IgnoreCase).Select(m =>
        {
            var number = m.Groups["count"].Value.ToLowerInvariant();
            var maximum = int.TryParse(number, out var n) ? n : Array.IndexOf(new[] { "zero", "one", "two", "three", "four", "five", "six" }, number);
            var targets = Regex.Split(m.Groups["target"].Value, @"\s+or\s+|\s*/\s*", RegexOptions.IgnoreCase).ToList();
            var grant = m.Groups["keyword"].Value.Trim();
            var models = m.Groups["kind"].Value.Equals("models", StringComparison.OrdinalIgnoreCase);
            return new MusterNominationRule("muster-" + ReferenceModel.Hash(UnitIdentity.Normalize(m.Value)),
                string.Join(" / ", targets) + " → " + grant, m.Groups["optional"].Success ? 0 : maximum, maximum, targets, [grant], models);
        }).DistinctBy(r => r.Id).ToList();
    }

    public static List<ActiveMusterRule> Rules(ArmyList army, Dataset? data) => DetachmentRules.Selected(army)
        .Select(n => DetachmentRules.Find(army, n, data)).OfType<DetachmentDefinition>()
        .SelectMany(d => (DetachmentRules.Semantics(d, data).Nominations ?? []).Select(r => new ActiveMusterRule(d.Name, r))).ToList();
    public static bool Matches(MusterSelection selection, ActiveMusterRule rule) => selection.RuleId == rule.Rule.Id
        && UnitIdentity.Normalize(selection.Detachment) == UnitIdentity.Normalize(rule.Detachment);
    private static MusterSelection Resolve(ArmyList army, Dataset? data, MusterSelection selection) => selection with {
        Detachment = DetachmentLifecycle.ResolveName(data, army.Faction, selection.Detachment) };
    public static List<MusterSelection> Selected(ArmyList army, ActiveMusterRule rule, Dataset? data = null) => (army.MusterSelections ?? [])
        .Select(s => Resolve(army, data, s)).Where(s => Matches(s, rule)).ToList();
    public static bool Eligible(ArmyList army, Dataset? data, ActiveMusterRule rule, ArmyEntry entry)
    {
        var unit = data?.FindUnit(entry.UnitKey);
        if (unit == null || AlliedRules.IsAlly(army, unit) || UnitAvailability.Problem(army, unit, data) != null) return false;
        var keys = MusterRules.Keywords(army, unit, data).Append(unit.Name).Append(unit.Category).Select(UnitIdentity.Normalize).Distinct().ToList();
        bool Match(string suffix) => suffix.Length == 0 || keys.Any(k => k.Length > 0 && suffix.StartsWith(k, StringComparison.Ordinal) && Match(suffix[k.Length..]));
        return rule.Rule.Targets.Any(t => Match(UnitIdentity.Normalize(t)));
    }
    public static List<MusterSelection> Candidates(ArmyList army, Dataset? data, ActiveMusterRule rule) => army.Entries
        .Where(e => Eligible(army, data, rule, e)).SelectMany(e => rule.Rule.SelectModels
            ? Enumerable.Range(0, e.Models).Select(i => new MusterSelection(rule.Detachment, rule.Rule.Id, e.Id, i))
            : [new MusterSelection(rule.Detachment, rule.Rule.Id, e.Id)]).ToList();
    public static string Label(ArmyList army, MusterSelection selection) => LeaderRules.EntryLabel(army, selection.EntryId)
        + (selection.ModelIndex is int index ? $" · model {index + 1}" : "");
    public static string Summary(ArmyList army, ActiveMusterRule rule, Dataset? data = null) => $"{rule.Rule.Name}: {Selected(army, rule, data).Count} selected · required {rule.Rule.Minimum} · max {rule.Rule.Maximum}";
    public static ArmyList Select(ArmyList army, Dataset? data, MusterSelection selection, bool selected)
    {
        army = army with { MusterSelections = army.MusterSelections?.Select(s => Resolve(army, data, s)).ToList() };
        selection = Resolve(army, data, selection);
        var choices = (army.MusterSelections ?? []).Where(s => s != selection).ToList();
        if (selected)
        {
            var rule = Rules(army, data).SingleOrDefault(r => Matches(selection, r)) ?? throw new InvalidDataException("This muster rule is no longer active.");
            if (!Candidates(army, data, rule).Contains(selection)) throw new InvalidDataException("This unit or model is not eligible for this muster nomination.");
            if (choices.Count(s => Matches(s, rule)) >= rule.Rule.Maximum) throw new InvalidDataException($"{rule.Rule.Name}: at most {rule.Rule.Maximum} nominations.");
            choices.Add(selection);
        }
        return army with { MusterSelections = choices };
    }
    public static ArmyList Normalize(ArmyList army, Dataset? data)
    {
        if (army.MusterSelections == null) return army;
        var rules = Rules(army, data);
        return army with { MusterSelections = army.MusterSelections.Select(s => Resolve(army, data, s)).Where(s => (rules.Any(r => Matches(s, r))
            || (DetachmentLifecycle.IsObsolete(data, army.Faction, s.Detachment) && DetachmentRules.Selected(army).Contains(s.Detachment)))
            && army.Entries.Any(e => e.Id == s.EntryId && (s.ModelIndex == null || s.ModelIndex >= 0 && s.ModelIndex < e.Models))).Distinct().ToList() };
    }
    public static void ValidateIncrease(ArmyList before, ArmyList after, Dataset? data)
    {
        var added = (after.MusterSelections ?? []).Except(before.MusterSelections ?? []).ToList();
        if (added.Count == 0) return;
        foreach (var selection in added)
            Select(after with { MusterSelections = (after.MusterSelections ?? []).Where(s => s != selection).ToList() }, data, selection, true);
        if ((after.MusterSelections ?? []).Distinct().Count() != after.MusterSelections?.Count)
            throw new InvalidDataException("A unit or model cannot be nominated twice for the same muster rule.");
    }
    public static List<string> GrantedKeywords(ArmyList army, ArmyEntry entry, Dataset? data)
    {
        if (!(army.MusterSelections ?? []).Any(s => s.EntryId == entry.Id)) return [];
        return Rules(army, data).Where(r => Eligible(army, data, r, entry) && Selected(army, r, data).Any(s => s.EntryId == entry.Id
            && (r.Rule.SelectModels ? s.ModelIndex is >= 0 && s.ModelIndex < entry.Models : s.ModelIndex == null)))
            .SelectMany(r => r.Rule.GrantedKeywords).DistinctBy(UnitIdentity.Normalize).ToList();
    }
    public static UnitDefinition? EffectiveUnit(ArmyList army, ArmyEntry entry, Dataset? data)
        => EffectiveDatasheets.Project(army, entry, data)?.Unit;
    internal static UnitDefinition? MusterUnit(ArmyList army, ArmyEntry entry, Dataset? data)
    {
        var unit = data?.FindUnit(entry.UnitKey);
        if (unit == null) return null;
        var keywords = MusterRules.Keywords(army, unit, data).Concat(GrantedKeywords(army, entry, data)).DistinctBy(UnitIdentity.Normalize).ToList();
        if (keywords.SequenceEqual(unit.Profile?.Keywords ?? [unit.Category])) return unit;
        return unit with { Category = UnitCategories.FromKeywords(keywords), Profile = (unit.Profile ?? new()) with { Keywords = keywords } };
    }
    public static List<string> Issues(ArmyList army, Dataset? data)
    {
        List<string> issues = [];
        var rules = Rules(army, data);
        foreach (var rule in rules)
        {
            var selected = Selected(army, rule, data); var candidates = Candidates(army, data, rule);
            var count = selected.Distinct().Count(candidates.Contains);
            if (count < rule.Rule.Minimum) issues.Add($"{rule.Detachment}: missing required muster nominations. {Summary(army, rule, data)}. Select eligible units to gain {string.Join(", ", rule.Rule.GrantedKeywords)}.");
            if (selected.Count > rule.Rule.Maximum) issues.Add($"{rule.Detachment}: too many muster nominations. {Summary(army, rule, data)}.");
            if (selected.Any(s => !candidates.Contains(s)) || selected.Distinct().Count() != selected.Count) issues.Add($"{rule.Detachment}: invalid or duplicate muster nomination for {rule.Rule.Name}.");
        }
        if ((army.MusterSelections ?? []).Any(s => !rules.Any(r => Matches(Resolve(army, data, s), r)))) issues.Add("A muster nomination refers to an inactive Detachment rule.");
        return issues;
    }
}
