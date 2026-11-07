using System.Text.RegularExpressions;

namespace GrimList.Core;

// Predicates inspect the source weapon, never a partially modified keyword list.
// Unknown selectors remain conditional instead of broadening their target.
public record WeaponPredicate(string Kind, string Value = "", List<WeaponPredicate>? Children = null);

public static class WeaponRules
{
    public static IEnumerable<DatasheetModifier> WithTextFallback(List<DatasheetModifier>? stored, string source, string text, bool rosterRule = false)
    {
        var parsed = rosterRule ? DatasheetRuleText.WeaponAbilities(source, text, true) : DatasheetRuleText.Enhancement(source, text);
        if (stored == null) return parsed;
        stored = stored.Select(ModelStatModifiers.Normalize).ToList();
        var represented = stored.Where(m => m.Target == "weapon" && EffectiveDatasheets.Stat(m.Characteristic) == "Keywords")
            .SelectMany(m => Tokens(m.Value)).Select(EffectiveDatasheets.Key).ToHashSet();
        // Fill missing model-stat prose only when no structural interpretation of
        // that stat exists, including unresolved old selectors. Never widen those.
        var modelFallback = parsed.Where(p => p.Target == "model" && !stored.Any(m =>
            EffectiveDatasheets.Stat(m.Characteristic) == EffectiveDatasheets.Stat(p.Characteristic) && m.Target is "model" or "weapon"));
        return stored.Concat(modelFallback).Concat(parsed.Where(m => m.Target == "weapon" && EffectiveDatasheets.Stat(m.Characteristic) == "Keywords")
            .Select(m => m with { Value = string.Join(", ", Tokens(m.Value).Where(k => !represented.Contains(EffectiveDatasheets.Key(k)))) })
            .Where(m => m.Value.Length > 0));
    }
    public static bool? Matches(DatasheetModifier modifier, WeaponProfile weapon)
    {
        if (modifier.WeaponType.Length > 0 && !modifier.WeaponType.Equals(weapon.Type, StringComparison.OrdinalIgnoreCase)
            || modifier.Profile.Length > 0 && modifier.Profile != weapon.Name) return false;
        return Test(modifier.WeaponPredicate, weapon);
    }
    public static bool? Test(WeaponPredicate? predicate, WeaponProfile weapon)
    {
        if (predicate == null) return true;
        var children = (predicate.Children ?? []).Select(p => Test(p, weapon)).ToList();
        return predicate.Kind switch
        {
            "all" => true,
            "type" => predicate.Value.Equals(weapon.Type, StringComparison.OrdinalIgnoreCase),
            "profile" => predicate.Value == weapon.Name,
            "source-id" => weapon.SourceId == null ? null : predicate.Value == weapon.SourceId,
            "source-category" => weapon.SourceCategories?.Contains(predicate.Value),
            "name-contains" => weapon.Name.Contains(predicate.Value, StringComparison.OrdinalIgnoreCase),
            "keyword" => Tokens(weapon.Keywords).Any(k => KeywordMatches(k, predicate.Value)),
            "and" => children.Contains(false) ? false : children.Contains(null) ? null : true,
            "or" => children.Contains(true) ? true : children.Contains(null) ? null : false,
            "not" => children.Count == 1 ? !children[0] : null,
            // Libraries imported before this traversal was supported retain the raw
            // selector. Recognize the same exact grammar as new imports, preserving
            // their source conditions; never treat all unknown selectors as a match.
            "unknown" => BsDataWeaponSelectors.Type(predicate.Value) switch
            {
                "All" => true,
                "Ranged" => weapon.Type.Equals("Ranged", StringComparison.OrdinalIgnoreCase),
                "Melee" => weapon.Type.Equals("Melee", StringComparison.OrdinalIgnoreCase),
                _ => null
            },
            _ => null
        };
    }
    public static List<string> Tokens(string text) => Regex.Replace(text, @"\]\s*(?:and\s*)?\[", ", ")
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(k => k.Trim('[', ']', ' ')).Where(k => k is not ("" or "-" or "—" or "N/A")).ToList();

    private static bool KeywordMatches(string existing, string query)
        => EffectiveDatasheets.Key(existing) == EffectiveDatasheets.Key(query)
            || EffectiveDatasheets.Key(Regex.Replace(existing, @"\s+(?:\d+\+?|\d*D\d+(?:[+-]\d+)?)$", "", RegexOptions.IgnoreCase)) == EffectiveDatasheets.Key(query);

    public static string Normalize(IEnumerable<string> keywords)
    {
        var distinct = keywords.SelectMany(Tokens).GroupBy(EffectiveDatasheets.Key)
            .Select(g => g.Order(StringComparer.Ordinal).First()).ToList();
        // Numbered instances of the same ability do not stack; use the greatest value.
        return string.Join(", ", distinct.GroupBy(k => Regex.Replace(k.ToUpperInvariant(), @" \d+$", ""))
            .SelectMany(g => g.All(k => Regex.IsMatch(k, @" \d+$"))
                ? g.OrderByDescending(k => int.Parse(Regex.Match(k, @"\d+$").Value)).Take(1) : g)
            .OrderBy(EffectiveDatasheets.Key, StringComparer.Ordinal));
    }
    public static List<WeaponProfile> ApplyKeywords(List<WeaponProfile> weapons,
        List<(DatasheetModifier Modifier, bool Conditional, string Condition)> modifiers, List<AppliedModifierSource> applied)
    {
        if (modifiers.Count == 0) return weapons;
        return weapons.Select(weapon =>
        {
            var matches = modifiers.Select(m => (Effect: m, Match: Matches(m.Modifier, weapon))).Where(m => m.Match != false).ToList();
            var active = matches.Where(m => !m.Effect.Conditional && m.Match == true).Select(m => m.Effect.Modifier).ToList();
            // Set replaces the base list; simultaneous sets contribute a union. Explicit
            // removals win over additions, independently of source/enumeration order.
            var sets = active.Where(m => m.Operation == "set").ToList();
            var values = sets.Count > 0 ? sets.SelectMany(m => Tokens(m.Value)) : Tokens(weapon.Keywords);
            values = values.Concat(active.Where(m => m.Operation is "add" or "append").SelectMany(m => Tokens(m.Value)));
            var removals = active.Where(m => m.Operation == "remove").SelectMany(m => Tokens(m.Value)).ToList();
            var result = Normalize(values.Where(k => !removals.Any(r => KeywordMatches(k, r))));
            foreach (var (effect, match) in matches)
            {
                var m = effect.Modifier;
                if (m.Operation is not ("add" or "append" or "remove" or "set")) continue;
                var conditional = effect.Conditional || match == null;
                var condition = match == null ? string.Join("; ", new[] { effect.Condition, "Unresolved weapon selector: " + DataJson.Write(m.WeaponPredicate) }.Where(s => s.Length > 0)) : effect.Condition;
                applied.Add(new(m.Id, m.Source, weapon.Name, "Keywords", weapon.Keywords,
                    conditional ? m.Operation + " " + m.Value : result, condition, conditional));
            }
            return active.Count == 0 ? weapon : weapon with { Keywords = result };
        }).ToList();
    }
}

public static partial class EffectiveDatasheets
{
    private static IEnumerable<DatasheetModifier> RosterWeaponModifiers(ArmyList army, ArmyEntry entry, Dataset? data, UnitDefinition unit)
    {
        foreach (var card in ArmyReferences.ForArmy(army, data).Where(c => c.Section is "Detachments" or "Army Rules"))
        {
            // A faction's passive rules do not automatically buff allied units.
            if (!ArmyReferences.IsCore(card) && Key(card.Faction) != Key(unit.Faction)) continue;
            if (card.Section == "Detachments" && !(card.Details?.Detachments ?? [new(card.Detachment)])
                .Any(d => DetachmentRules.Contains(army, d.Name, data) && DetachmentRules.Find(army, d.Name, data) != null)) continue;
            var scope = "reference/" + ReferenceModel.LogicalKey(card) + "/";
            foreach (var modifier in CompiledRuleText.ForCard(card, rosterRule: true))
                yield return modifier with { Id = scope + modifier.Id };
        }
    }
}
