namespace GrimList.Core;

public record AssociationCondition(string Kind, string Scope = "", string ChildId = "", string ChildName = "",
    string Comparison = "", int Value = 0, List<AssociationCondition>? Children = null, string Field = "associations");
public record AssociationModifier(string Operation, int Value, AssociationCondition Condition, List<NumericRepeat>? Repeats = null, int Order = 0, string SourceId = "");
public record AssociationConstraint(string Id, string Scope, string ChildId, string ChildName, string Type, int Value,
    List<AssociationModifier> Modifiers);
public record AssociationDefinition(string SourceEntryId, List<string> IdentityIds, List<AssociationConstraint> Constraints, List<string> Warnings);
public record AssociationLimitResult(string ConstraintId, string Target, string Type, int Base, int Effective, int Actual,
    List<string> AppliedModifiers, bool Valid);
public record AssociationEvaluation(List<AssociationLimitResult> Constraints, bool CoversProposedSet, string? Problem);

public static class AssociationRules
{
    private static bool Matches(UnitDefinition unit, string id, string name = "") => id is "any" or "unit"
        || unit.Profile?.Associations?.SourceEntryId == id
        || (unit.Aliases ?? []).Any(a => a.EndsWith(":" + id, StringComparison.Ordinal))
        || name.Length == 0 && unit.Profile?.Associations?.IdentityIds.Contains(id) == true
        || name.Length > 0 && unit.Profile?.Keywords?.Any(k => UnitIdentity.Normalize(k) == UnitIdentity.Normalize(name)) == true;
    public static AssociationEvaluation Evaluate(ArmyList army, Dataset data, ArmyEntry bodyguard, IReadOnlyCollection<string> proposedIds)
    {
        var definition = data.FindUnit(bodyguard.UnitKey)?.Profile?.Associations;
        if (definition == null) return new([], false, null);
        if (definition.Warnings.Count > 0) return new([], false, "Association constraints need source review: " + string.Join("; ", definition.Warnings));
        var members = proposedIds.Select(id => army.Entries.Single(e => e.Id == id)).ToList();
        var proposed = army.Entries.Where(e => proposedIds.Contains(e.Id) || e.AttachedToId.Length > 0 && e.AttachedToId != bodyguard.Id).ToList();
        int Count(string scope, string id, string name = "") => (scope is "roster" or "force" ? proposed : members).Count(e => MusterEffects.EffectiveUnit(army, e, data) is { } unit && Matches(unit, id, name));
        int ConditionCount(AssociationCondition c) => c.Field == "selections" ? bodyguard.Models : Count(c.Scope, c.ChildId, c.ChildName);
        bool Test(AssociationCondition condition) => RuleEvaluation.Test(condition, c => c.Kind, c => c.Children ?? [],
            c => RuleEvaluation.Compare(ConditionCount(c), c.Comparison, c.Value));
        string Reason(AssociationCondition c) => c.Kind is "and" or "or"
            ? "(" + string.Join(c.Kind == "and" ? " AND " : " OR ", (c.Children ?? []).Where(Test).Select(Reason)) + ")"
            : $"{c.ChildName} {c.Field} {c.Comparison} {c.Value} (actual {ConditionCount(c)}, scope {c.Scope})";
        List<AssociationLimitResult> limits = [];
        foreach (var constraint in definition.Constraints)
        {
            var value = constraint.Value; List<string> applied = [];
            foreach (var modifier in constraint.Modifiers.Where(m => Test(m.Condition)))
            {
                value = RuleEvaluation.Effective(value,
                    [new(modifier.Operation, modifier.Value, new(), modifier.Repeats ?? [], modifier.Order, modifier.SourceId)],
                    q => q.Field == "selections" ? bodyguard.Models : Count(q.Scope, q.ChildId));
                applied.Add($"{modifier.Operation.ToUpperInvariant()} {constraint.ChildName} {constraint.Type} {modifier.Value}: {Reason(modifier.Condition)}");
            }
            value = Math.Max(0, value);
            var count = Count(constraint.Scope, constraint.ChildId, constraint.ChildName);
            limits.Add(new(constraint.Id, constraint.ChildName, constraint.Type, constraint.Value, value, count, applied,
                constraint.Type == "max" ? count <= value : count >= value));
        }
        var covered = members.Count > 0 && members.All(e => MusterEffects.EffectiveUnit(army, e, data) is { } unit
            && definition.Constraints.Any(c => c.Type == "max" && Matches(unit, c.ChildId, c.ChildName)));
        var failed = limits.FirstOrDefault(l => !l.Valid);
        return new(limits, covered, failed == null ? null : $"{bodyguard.Name}: {failed.Target} associations {failed.Actual}; effective {failed.Type} = {failed.Effective} (base {failed.Base}).");
    }
    public static string Describe(ArmyList army, Dataset data, ArmyEntry bodyguard, IReadOnlyCollection<string> proposedIds)
    {
        var result = Evaluate(army, data, bodyguard, proposedIds);
        return $"Bodyguard: {bodyguard.Name}\nProposed associations: {string.Join(", ", proposedIds.Select(id => army.Entries.Single(e => e.Id == id).Name))}\n"
            + string.Join("\n", result.Constraints.Select(c => $"Base: {c.Target} {c.Type} = {c.Base}\nApplied modifiers: {(c.AppliedModifiers.Count == 0 ? "none" : string.Join("; ", c.AppliedModifiers))}\nEffective: {c.Target} {c.Type} = {c.Effective}\nActual: {c.Target} count = {c.Actual}"))
            + "\nAssociation result: " + (result.Problem ?? (result.CoversProposedSet ? "VALID" : "No complete structural permission; check Leader/Support abilities."));
    }
}
