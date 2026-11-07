using System.Text.RegularExpressions;

namespace GrimList.Core;

public static class DetachmentRules
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dataset, System.Collections.Concurrent.ConcurrentDictionary<DetachmentDefinition, DetachmentDefinition>> semanticCache = new();
    public static int? Budget(int points) => points switch { 1000 => 2, 2000 => 3, 3000 => 4, _ => null };
    public static string BudgetLabel(int points) => points == 1000
        ? "2 DP total or one detachment up to 3 DP" : $"{Budget(points)?.ToString() ?? "?"} DP";
    public static bool UsesSingleDetachmentAllowance(ArmyList army, Dataset? data) =>
        army.Limit == 1000 && Selected(army).Count == 1 && Spent(army, data) == 3;
    public static bool WithinBudget(ArmyList army, Dataset? data) =>
        Budget(army.Limit) is int budget && Spent(army, data) is int spent &&
        (spent <= budget || UsesSingleDetachmentAllowance(army, data));
    public static string SelectionSummary(ArmyList army, Dataset? data) => army.Limit == 1000
        ? $"{Spent(army, data)?.ToString() ?? "?"} DP selected · {BudgetLabel(army.Limit)}"
        : $"{Spent(army, data)?.ToString() ?? "?"} / {Budget(army.Limit)?.ToString() ?? "?"} DP selected";
    public static int? Cost(DetachmentDefinition? definition)
    {
        var match = Regex.Match(definition?.Cost ?? "", @"^\s*(\d+)\s*DP\s*$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var cost) ? cost : null;
    }
    public static List<DetachmentDefinition> Options(string faction, Dataset? data) => Options(ArmyList.New(faction), data);
    public static List<DetachmentDefinition> Options(ArmyList army, Dataset? data) => (data?.Detachments ?? [])
        .Where(d => d.Faction == army.Faction && Applicable(army, d, data)).OrderBy(d => d.Name).ToList();
    public static bool Applicable(ArmyList army, DetachmentDefinition definition, Dataset? data)
    {
        if (definition.Faction != army.Faction || definition.Name.Length > 0 && data != null && !DetachmentLifecycle.Allows(data, definition)) return false;
        var context = data?.FactionContexts?.FirstOrDefault(c => c.Faction == army.Faction);
        bool? Test(EffectCondition? condition)
        {
            if (condition == null) return true;
            if (condition.Kind == "not") return condition.Children?.Count == 1 ? !Test(condition.Children[0]) : null;
            if (condition.Kind is "and" or "or")
            {
                var values = (condition.Children ?? []).Select(Test).ToList();
                return condition.Kind == "and" ? values.Contains(false) ? false : values.Contains(null) ? null : true
                    : values.Contains(true) ? true : values.Contains(null) ? null : false;
            }
            int? value = condition.Kind switch
            {
                "catalogue" => context == null ? null : context.CatalogueId == condition.Value ? 1 : 0,
                "factionKeyword" => context == null ? null : EffectiveDatasheets.MatchesKeywords(context.Keywords, condition.Value) ? 1 : 0,
                "detachment" => Contains(army, condition.Value, data) ? 1 : 0,
                "constant" => int.TryParse(condition.Value, out var n) ? n : null,
                _ => null
            };
            if (value == null) return null;
            return condition.Comparison switch { "instanceOf" or "atLeast" => value >= condition.Count, "notInstanceOf" or "notEqualTo" => value != condition.Count,
                "equalTo" => value == condition.Count, "atMost" => value <= condition.Count, "greaterThan" => value > condition.Count, "lessThan" => value < condition.Count, _ => null };
        }
        return Test(definition.Applicability) == true;
    }
    public static bool UsesPoints(string faction, Dataset? data) => Options(faction, data).Any(d => Cost(d).HasValue);
    public static List<string> Selected(ArmyList army) => army.Detachments is { } names ? [.. names]
        : string.IsNullOrWhiteSpace(army.Detachment) ? [] : [army.Detachment];
    public static bool Contains(ArmyList army, string name, Dataset? data = null) => !DetachmentLifecycle.IsObsolete(data, army.Faction, name)
        && Selected(army).Any(n => !DetachmentLifecycle.IsObsolete(data, army.Faction, n)
            && UnitIdentity.Normalize(DetachmentLifecycle.ResolveName(data, army.Faction, n)) == UnitIdentity.Normalize(DetachmentLifecycle.ResolveName(data, army.Faction, name)));
    public static DetachmentDefinition? Find(string faction, string name, Dataset? data) => Options(faction, data)
        .FirstOrDefault(d => UnitIdentity.Normalize(d.Name) == UnitIdentity.Normalize(DetachmentLifecycle.ResolveName(data, faction, name)));
    public static DetachmentDefinition? Find(ArmyList army, string name, Dataset? data) => Options(army, data)
        .FirstOrDefault(d => UnitIdentity.Normalize(d.Name) == UnitIdentity.Normalize(DetachmentLifecycle.ResolveName(data, army.Faction, name)));
    public static int? Spent(ArmyList army, Dataset? data)
    {
        var costs = Selected(army).Select(n => Cost(Find(army, n, data))).ToList();
        return costs.Any(c => !c.HasValue) ? null : costs.Sum(c => c!.Value);
    }
    public static ArmyList WithSelection(ArmyList army, IEnumerable<string> names)
    {
        var selected = names.Where(n => !string.IsNullOrWhiteSpace(n)).DistinctBy(UnitIdentity.Normalize).ToList();
        return army with { Detachments = selected, Detachment = selected.FirstOrDefault() ?? "" };
    }
    public static bool CanAdd(ArmyList army, string name, Dataset? data) => !Contains(army, name, data) &&
        Cost(Find(army, name, data)).HasValue && WithinBudget(WithSelection(army, [.. Selected(army), name]), data)
        && CombinationIssues(WithSelection(army, [.. Selected(army), name]), data).Count == 0;
    public static DetachmentDefinition Semantics(DetachmentDefinition definition, Dataset? data)
    {
        if (data == null) return definition;
        return semanticCache.GetOrCreateValue(data).GetOrAdd(definition, d => Compile(d, data));
    }
    private static DetachmentDefinition Compile(DetachmentDefinition definition, Dataset? data)
    {
        var text = string.Join("\n", (data?.ReferenceCards ?? []).Where(c => c.Faction == definition.Faction
            && UnitIdentity.Normalize(c.Detachment) == UnitIdentity.Normalize(definition.Name) && c.Section is "Detachments" or "Detachment Rules").Select(c => c.Text));
        return Interpret(definition, text);
    }
    internal static DetachmentDefinition Interpret(DetachmentDefinition definition, string text)
    {
        text = text.Replace("*", "").Replace("^^", "").Replace("__", "");
        var tags = (definition.Tags ?? []).ToList(); var bans = (definition.CannotCombineWithTags ?? []).ToList();
        foreach (Match m in Regex.Matches(text, @"\bThis Detachment (?:has|gains) the (?<tag>[\p{L}\p{N} -]+?) (?:tag|keyword)\b", RegexOptions.IgnoreCase)) tags.Add(m.Groups["tag"].Value);
        foreach (Match m in Regex.Matches(text, @"\bThis is (?:a|an) (?<tag>[\p{L}\p{N} -]+?) Detachment\b", RegexOptions.IgnoreCase)) tags.Add(m.Groups["tag"].Value);
        foreach (Match m in Regex.Matches(text, @"\b(?:cannot|can not|can't) (?:be (?:taken|selected|included) (?:with|alongside)|(?:select|include|take)) (?:any |an? )?(?:other |another )?(?:Detachment (?:with|that (?:has|have)) (?:the )?(?<tag>[\p{L}\p{N} -]+?) (?:tag|keyword)|(?<tag>[\p{L}\p{N} -]+?) Detachment)\b", RegexOptions.IgnoreCase)) bans.Add(m.Groups["tag"].Value);
        return definition with { Tags = tags.DistinctBy(UnitIdentity.Normalize).ToList(), CannotCombineWithTags = bans.DistinctBy(UnitIdentity.Normalize).ToList(),
            Nominations = definition.Nominations ?? MusterEffects.Parse(text) };
    }
    public static string SelectionMetadata(DetachmentDefinition definition, Dataset? data)
    {
        var semantic = Semantics(definition, data);
        return string.Join(" · ", new[] { definition.Cost, definition.DispositionLabel,
            semantic.Tags?.Count > 0 ? "Tags: " + string.Join(", ", semantic.Tags) : "",
            semantic.CannotCombineWithTags?.Count > 0 ? "Cannot combine with: " + string.Join(", ", semantic.CannotCombineWithTags) : "",
            string.Join("; ", (semantic.GroupLimits ?? []).Select(g => $"{g.Name}: maximum {g.Maximum}")) }.Where(s => s.Length > 0));
    }
    public static List<string> CombinationIssues(ArmyList army, Dataset? data)
    {
        var selected = Selected(army).Select(n => Find(army, n, data)).OfType<DetachmentDefinition>()
            .Select(d => Semantics(d, data)).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        List<string> issues = [];
        foreach (var group in selected.SelectMany(d => (d.GroupLimits ?? []).Select(g => (Definition: d, Limit: g))).GroupBy(p => p.Limit.Id))
        {
            var limit = group.Min(p => p.Limit.Maximum);
            var members = group.Select(p => p.Definition.Name).DistinctBy(UnitIdentity.Normalize).ToList();
            if (members.Count > limit) issues.Add($"{group.First().Limit.Name}: select at most {limit} detachment(s) from this group. Selected: {string.Join(", ", members)}.");
        }
        foreach (var source in selected)
        foreach (var other in selected.Where(d => UnitIdentity.Normalize(d.Name) != UnitIdentity.Normalize(source.Name)))
            foreach (var tag in source.CannotCombineWithTags ?? [])
                if ((other.Tags ?? []).Any(t => UnitIdentity.Normalize(t) == UnitIdentity.Normalize(tag)))
                    issues.Add($"{source.Name} cannot be combined with {other.Name}: excludes Detachments with the {tag} tag.");
        return issues.Distinct().ToList();
    }
    public static List<string> Dispositions(ArmyList army, Dataset? data) => Selected(army)
        .SelectMany(n => Find(army, n, data)?.DispositionOptions ?? []).DistinctBy(UnitIdentity.Normalize).Order().ToList();
    public static ArmyList NormalizeDisposition(ArmyList army, Dataset? data)
    {
        var options = Dispositions(army, data);
        var current = options.FirstOrDefault(d => UnitIdentity.Normalize(d) == UnitIdentity.Normalize(army.Disposition));
        return army with { Disposition = current ?? (options.Count == 1 ? options[0] : "") };
    }
    public static List<string> Issues(ArmyList army, Dataset? data)
    {
        var issues = new List<string>(); var selected = Selected(army).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (selected.Count == 0) issues.Add("Select at least one detachment.");
        if (selected.DistinctBy(n => UnitIdentity.Normalize(DetachmentLifecycle.ResolveName(data, army.Faction, n))).Count() != selected.Count) issues.Add("A detachment can only be selected once.");
        foreach (var name in selected)
            if (DetachmentLifecycle.IsObsolete(data, army.Faction, name))
                issues.Add($"{name}: obsolete in the updated BSData catalogue. Select a current detachment; your saved army has been retained.");
            else if (DetachmentCatalogue.IsRetired(data, army.Faction, name))
                issues.Add($"{name}: retired after the Space Marines Codex update. Select a current detachment; your saved army has been retained.");
        foreach (var name in selected)
            if (data?.Detachments?.FirstOrDefault(d => d.Faction == army.Faction && UnitIdentity.Normalize(d.Name) == UnitIdentity.Normalize(name)) is { } definition
                && !Applicable(army, definition, data)) issues.Add($"{name}: not available for the selected faction/catalogue context.");
        issues.AddRange(CombinationIssues(army, data));
        if (!UsesPoints(army.Faction, data)) return issues;
        var budget = Budget(army.Limit);
        if (!budget.HasValue) issues.Add("Choose 1,000, 2,000 or 3,000 points to set the detachment budget.");
        foreach (var name in selected)
            if (Cost(Find(army, name, data)) == null) issues.Add($"{name}: detachment DP cost unavailable.");
        if (Spent(army, data) is int spent && budget is int cap && !WithinBudget(army, data))
            issues.Add($"{spent - cap} DP over the detachment budget ({spent}/{cap} DP)." +
                (army.Limit == 1000 ? " At 1,000 points, a single detachment may cost up to 3 DP." : ""));
        return issues;
    }
}
