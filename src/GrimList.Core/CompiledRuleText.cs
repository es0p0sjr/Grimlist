using System.Runtime.CompilerServices;

namespace GrimList.Core;

// Compile static interpretation once per immutable source object. Dynamic
// conditions, recipients and eligibility remain in the complete roster evaluator.
// Weak identity keys release old revisions and never reuse a record copy's rules.
// This is an in-memory representation, not a persisted schema or freshness stamp.
internal static class CompiledRuleText
{
    internal const int Version = 1;
    private static long compilations;
    internal static long CompilationCount => Interlocked.Read(ref compilations);
    private static readonly ConditionalWeakTable<ReferenceCard, CardRules> cards = new();
    private static readonly ConditionalWeakTable<AbilityProfile, AbilityRules> abilities = new();

    private static Lazy<IReadOnlyList<DatasheetModifier>> Compile(Func<IEnumerable<DatasheetModifier>> parse) => new(() =>
    {
        var result = Array.AsReadOnly(parse().ToArray());
        Interlocked.Increment(ref compilations);
        return result;
    });

    private sealed class CardRules(ReferenceCard card)
    {
        public readonly Lazy<IReadOnlyList<DatasheetModifier>> Enhancement = Compile(() => WeaponRules.WithTextFallback(card.Modifiers, card.Name, card.Text));
        public readonly Lazy<IReadOnlyList<DatasheetModifier>> Roster = Compile(() => WeaponRules.WithTextFallback(card.Modifiers, card.Name, card.Text, rosterRule: true));
    }
    private sealed class AbilityRules(AbilityProfile ability)
    {
        public readonly Lazy<IReadOnlyList<DatasheetModifier>> Attached = Compile(() => DatasheetRuleText.Attached(ability));
        public readonly Lazy<IReadOnlyList<DatasheetModifier>> Weapons = Compile(() => DatasheetRuleText.WeaponAbilities(ability.Name, ability.Text));
    }
    internal static IReadOnlyList<DatasheetModifier> ForCard(ReferenceCard card, bool rosterRule = false)
    {
#if PERFORMANCE_CHECKS
        if (!ControlledPerformance.CacheRuleText)
            return WeaponRules.WithTextFallback(card.Modifiers, card.Name, card.Text, rosterRule: rosterRule).ToArray();
#endif
        var rules = cards.GetValue(card, static c => new(c));
        return (rosterRule ? rules.Roster : rules.Enhancement).Value;
    }
    internal static IReadOnlyList<DatasheetModifier> Attached(AbilityProfile ability)
    {
#if PERFORMANCE_CHECKS
        if (!ControlledPerformance.CacheRuleText) return DatasheetRuleText.Attached(ability).ToArray();
#endif
        return abilities.GetValue(ability, static a => new(a)).Attached.Value;
    }
    internal static IReadOnlyList<DatasheetModifier> Weapons(AbilityProfile ability)
    {
#if PERFORMANCE_CHECKS
        if (!ControlledPerformance.CacheRuleText) return DatasheetRuleText.WeaponAbilities(ability.Name, ability.Text).ToArray();
#endif
        return abilities.GetValue(ability, static a => new(a)).Weapons.Value;
    }
#if PERFORMANCE_CHECKS
    internal static void ResetPerformanceCaches() { cards.Clear(); abilities.Clear(); }
#endif
}
