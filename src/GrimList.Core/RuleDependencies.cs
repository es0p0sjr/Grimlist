using System.Runtime.CompilerServices;

namespace GrimList.Core;

// Static dependency facts, never evaluated predicates. Inspect every branch,
// including inactive modifiers/repeats, so a later edit cannot expose an unseen
// roster dependency. Immutable source identities bound the lifetime of plans.
internal sealed record RuleDependencies(bool Roster, bool Unknown)
{
    private static readonly ConditionalWeakTable<UnitDefinition, RuleDependencies> units = new();
    private static readonly ConditionalWeakTable<ReferenceCard, RuleDependencies> cards = new();
    internal static RuleDependencies For(UnitDefinition unit) => units.GetValue(unit, Compile);
    internal static RuleDependencies For(ReferenceCard card) => cards.GetValue(card, c =>
        CompileModifiers(CompiledRuleText.ForCard(c, rosterRule: c.Section is "Army Rules" or "Detachments")));
    internal static bool Known(EffectCondition c) => c.Kind is "and" or "or" or "not" or "constant" or "keyword"
        or "selection" or "detachment" or "enhancement" or "models" or "association" or "attached-keyword"
        or "attached-enhancement" or "attached-models" or "roster-keyword" or "roster-unit" or "catalogue" or "unit";
    private static RuleDependencies CompileModifiers(IEnumerable<DatasheetModifier> modifiers)
    {
        var builder = new Builder(); foreach (var modifier in modifiers) builder.Modifier(modifier);
        return builder.Result;
    }
    private static RuleDependencies Compile(UnitDefinition unit)
    {
        var builder = new Builder(); var semantics = unit.Profile?.Semantics;
        if (semantics?.Version is < 1 or > 3) builder.Unknown = true;
        foreach (var modifier in semantics?.Modifiers ?? []) builder.Modifier(modifier);
        foreach (var group in semantics?.Choices ?? [])
        {
            builder.Effect(group.Available);
            foreach (var option in group.Options) builder.Effect(option.Available);
        }
        foreach (var ability in semantics?.Abilities ?? [])
        {
            builder.Effect(ability.Condition);
            foreach (var modifier in ability.Modifiers ?? []) builder.Modifier(modifier);
        }
        foreach (var permission in semantics?.Attachments ?? []) builder.Effect(permission.Condition, owner: true);
        if (unit.Profile?.Associations is { } associations) builder.Associations(associations);
        return builder.Result;
    }
    private sealed class Builder
    {
        public bool Roster, Unknown;
        public RuleDependencies Result => new(Roster, Unknown);
        public void Modifier(DatasheetModifier modifier)
        {
            Effect(modifier.Condition); Effect(modifier.Recipient?.Predicate);
            if (modifier.Recipient is { Scope: not ("attached-unit" or "led-unit" or "leaders" or "other-members" or "bodyguard" or "self") }) Unknown = true;
        }
        public void Effect(EffectCondition? condition, int depth = 0, bool owner = false)
        {
            if (condition == null) return;
            if (depth >= 30) { Unknown = true; return; }
            if (!Known(condition) && !(owner && condition.Kind == "owner")) Unknown = true;
            if (condition.Kind is "roster-keyword" or "roster-unit") Roster = true;
            if (condition.Comparison is not ("atLeast" or "instanceOf" or "atMost" or "equalTo" or "notEqualTo" or "notInstanceOf" or "greaterThan" or "lessThan")) Unknown = true;
            if (condition.Kind is "not" or "owner" && condition.Children?.Count != 1) Unknown = true;
            foreach (var child in condition.Children ?? []) Effect(child, depth + 1, owner);
        }
        public void Associations(AssociationDefinition definition)
        {
            if (definition.Warnings.Count > 0) Unknown = true;
            bool Scope(string scope)
            {
                if (scope is "roster" or "force") { Roster = true; return true; }
                return scope is "self" or "root-entry" or "unit" or "unit-self" || scope.Length > 0 && scope == definition.SourceEntryId;
            }
            bool Query(string scope, string field, string child) => Scope(scope) && child.Length > 0
                && (field == "associations" || field == "selections" && child == "model" && scope is "self" or "root-entry" or "unit" or "unit-self");
            void Condition(AssociationCondition condition, int depth = 0)
            {
                if (depth >= 30) { Unknown = true; return; }
                if (condition.Kind is "and" or "or")
                    foreach (var child in condition.Children ?? []) Condition(child, depth + 1);
                else if (condition.Kind != "count" || !Query(condition.Scope, condition.Field, condition.ChildId)
                    || condition.Comparison is not ("atLeast" or "atMost" or "equalTo" or "notEqualTo" or "greaterThan" or "lessThan")) Unknown = true;
            }
            foreach (var constraint in definition.Constraints)
            {
                if (!Scope(constraint.Scope) || constraint.Type is not ("min" or "max")) Unknown = true;
                foreach (var modifier in constraint.Modifiers)
                {
                    Condition(modifier.Condition);
                    if (modifier.Operation is not ("set" or "increment" or "decrement" or "multiply")) Unknown = true;
                    foreach (var repeat in modifier.Repeats ?? [])
                        if (!Query(repeat.Query.Scope, repeat.Query.Field, repeat.Query.ChildId)
                            || repeat.Query.IncludeChildForces || repeat.Every <= 0 || repeat.Times < 0) Unknown = true;
                }
            }
        }
    }
}
