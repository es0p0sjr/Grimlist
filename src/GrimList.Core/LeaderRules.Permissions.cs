namespace GrimList.Core;

public static partial class LeaderRules
{
    private static bool MatchesTerm(UnitDefinition unit, string term)
    {
        var key = UnitIdentity.Normalize(term);
        return new[] { unit.Name }.Concat(unit.Profile?.Keywords ?? []).Any(n =>
            UnitIdentity.Normalize(n) == key || UnitIdentity.Normalize(n) == key + "S");
    }
    private static bool SameLeader(UnitDefinition first, UnitDefinition second) => first.Key == second.Key || first.NameKey == second.NameKey;

    private static bool AllowsPartner(UnitDefinition granting, AttachmentDefinition rules, UnitDefinition other,
        AttachmentDefinition otherRules, UnitDefinition bodyguard, out string? restriction)
    {
        restriction = null;
        var allowed = false;
        foreach (var permission in rules.AdditionalAttachments)
        {
            if (permission.BodyguardTargets.Count > 0 && !permission.BodyguardTargets.Any(t => MatchesTerm(bodyguard, t))) continue;
            var matches = permission.AnyLeader ? otherRules.CanAttach && !otherRules.IsSupport
                : permission.PartnerTargets.Any(t => MatchesTerm(other, t));
            if (!matches) continue;
            if (permission.ForbidDuplicateLeaders && SameLeader(granting, other))
            {
                restriction = "This attachment rule does not permit two of the same Leader on this bodyguard.";
                return false;
            }
            allowed = true;
        }
        return allowed;
    }

    public static string BodyguardRuleText(UnitDefinition? unit) => string.Join("\n\n", Semantics(unit?.Profile).BodyguardRules.Select(r => r.SourceText)
        .Concat((Semantics(unit?.Profile).InheritedLeaders ?? []).Select(r => r.SourceText)).Distinct());

    private static bool BodyguardAllowsPair(ArmyEntry entry, AttachmentDefinition rules, UnitDefinition first, UnitDefinition second, out string? restriction)
    {
        restriction = null;
        var allowed = false;
        foreach (var permission in rules.BodyguardRules)
        {
            if (permission.RequiredModels is { } required && entry.Models != required)
                restriction = $"This bodyguard needs {required} models to take two Leaders.";
            else if (permission.ForbidDuplicateLeaders && SameLeader(first, second))
                restriction = "This bodyguard's two Leaders must be different units.";
            else if (permission.KeywordLimit == 1 && MatchesTerm(first, permission.LimitedKeyword) && MatchesTerm(second, permission.LimitedKeyword))
                restriction = $"This bodyguard can have at most one {permission.LimitedKeyword} unit attached.";
            else if (permission.LeaderLimit >= 2) allowed = true;
            if (restriction != null) return false;
        }
        return allowed;
    }
}
