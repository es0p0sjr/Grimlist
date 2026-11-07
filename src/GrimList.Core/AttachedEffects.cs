using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace GrimList.Core;

public record WeaponAdjustment(string Scope, List<string> Keywords, int Strength = 0, int Ap = 0, bool RequiresAfflicted = false);
public record AttachedEffect(string Source, AbilityProfile Ability, WeaponAdjustment? Adjustment);
public record EffectiveWeapon(WeaponProfile Original, WeaponProfile Profile, List<string> Changes);

public static class AttachedEffects
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static string Clean(string text) => Regex.Replace(text.Replace("**", ""), @"\s+", " ").Trim();
    internal sealed record SourceRule(bool Leading, int? MinimumSize, bool Led, WeaponAdjustment? LeadingAdjustment, WeaponAdjustment? RecipientAdjustment);
    private static readonly ConditionalWeakTable<AbilityProfile, Lazy<SourceRule>> sourceRules = new();
    internal static SourceRule CompileSource(AbilityProfile ability) => sourceRules.GetValue(ability, static a => new(() => ParseSource(a))).Value;
    internal static SourceRule ParseSource(AbilityProfile ability)
    {
        var text = Clean(ability.Text);
        var leading = Regex.Match(text, @"^While this model is leading a unit, (.+)$", Options);
        var size = Regex.Match(text, @"^If this unit has a Starting Strength of (\d+) or more, or if a Character is leading this unit, then (.+)$", Options);
        var led = Regex.Match(text, @"^While (?:this unit is being led|a Character(?: model)? is leading this unit), (.+)$", Options);
        return new(leading.Success, size.Success ? int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) : null, led.Success,
            leading.Success ? ParseAdjustment(leading.Groups[1].Value) : null,
            size.Success ? ParseAdjustment(size.Groups[2].Value) : led.Success ? ParseAdjustment(led.Groups[1].Value) : null);
    }
    // Public effects own their mutable keyword lists; callers cannot alter compiled evidence.
    private static WeaponAdjustment? Copy(WeaponAdjustment? value) => value == null ? null : value with { Keywords = value.Keywords.ToList() };

    public static List<AttachedEffect> ForUnit(ArmyList army, Dataset? data, ArmyEntry? entry)
    {
        if (entry == null || data == null || army.Game != data.Game) return [];
        var bodyguard = entry.AttachedToId.Length > 0 && LeaderRules.Problem(army, data, entry.Id, entry.AttachedToId) == null
            ? army.Entries.Single(e => e.Id == entry.AttachedToId) : entry;
        var leaders = army.Entries.Where(e => e.AttachedToId == bodyguard.Id && LeaderRules.Problem(army, data, e.Id, bodyguard.Id) == null).ToList();
        List<AttachedEffect> effects = [];
        foreach (var leader in leaders)
            foreach (var ability in MusterEffects.EffectiveUnit(army, leader, data)?.Profile?.Abilities ?? [])
            {
                var rule = CompileSource(ability);
                if (rule.Leading) effects.Add(new(LeaderRules.EntryLabel(army, leader.Id), ability, Copy(rule.LeadingAdjustment)));
            }
        var characterLeading = leaders.Any(e => (MusterEffects.EffectiveUnit(army, e, data)?.Profile?.Keywords ?? []).Any(k => k.Equals("Character", StringComparison.OrdinalIgnoreCase)));
        foreach (var ability in MusterEffects.EffectiveUnit(army, bodyguard, data)?.Profile?.Abilities ?? [])
        {
            var rule = CompileSource(ability);
            if (rule.MinimumSize is { } size && (bodyguard.Models >= size || characterLeading) || rule.Led && characterLeading)
                effects.Add(new(LeaderRules.EntryLabel(army, bodyguard.Id), ability, Copy(rule.RecipientAdjustment)));
        }
        // Identical imported abilities do not stack when supplied by multiple attached models.
        return effects.DistinctBy(e => (e.Ability.Name, e.Ability.Text)).ToList();
    }

    // Recognise complete, supported clauses. Extra conditions stay in the ability text, not unconditional stat changes.
    private static WeaponAdjustment? ParseAdjustment(string clause)
    {
        // A separate additive sentence (e.g. Foul Infusion's critical-hit rule) does not restrict the first grant.
        clause = Regex.Split(clause, @"(?<=\.) In addition, ", Options)[0];
        var stats = Regex.Match(clause, @"^each time a model in (?:this|that) unit makes a (?<scope>ranged|melee) attack that targets an Afflicted unit, improve the Strength and Armou?r Penetration characteristics of that attack by (?<n>\d+)\.$", Options);
        if (stats.Success) return new(stats.Groups["scope"].Value, [], int.Parse(stats.Groups["n"].Value, CultureInfo.InvariantCulture), -int.Parse(stats.Groups["n"].Value, CultureInfo.InvariantCulture), true);
        var keywords = Regex.Match(clause, @"^(?:(?<scope>ranged|melee) )?weapons equipped by models in (?:this|that) unit have the (?<keywords>\[[^\]]+\](?: (?:and )?\[[^\]]+\])*) (?:abilities|abilties|ability)\.$", Options);
        if (keywords.Success) return new(keywords.Groups["scope"].Value, KeywordList(keywords.Groups["keywords"].Value));
        var attacks = Regex.Match(clause, @"^each time a model in (?:this|that) unit makes an? (?:(?<scope>ranged|melee) )?attack which targets a unit which is Afflicted, that attack has the (?<keywords>\[[^\]]+\]) ability\.$", Options);
        return attacks.Success ? new(attacks.Groups["scope"].Value, KeywordList(attacks.Groups["keywords"].Value), RequiresAfflicted: true) : null;
    }
    private static List<string> KeywordList(string text) => Regex.Matches(text, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value).ToList();

    public static List<EffectiveWeapon> Weapons(IEnumerable<WeaponProfile> weapons, IEnumerable<AttachedEffect> effects, bool targetAfflicted)
    {
        var active = effects.Where(e => e.Adjustment != null && (!e.Adjustment.RequiresAfflicted || targetAfflicted)).ToList();
        return weapons.Select(original =>
        {
            var profile = original; List<string> changes = [];
            var melee = original.Range.Trim().Equals("Melee", StringComparison.OrdinalIgnoreCase);
            foreach (var effect in active)
            {
                var modifier = effect.Adjustment!;
                if (modifier.Scope.Equals("melee", StringComparison.OrdinalIgnoreCase) && !melee || modifier.Scope.Equals("ranged", StringComparison.OrdinalIgnoreCase) && melee) continue;
                List<string> applied = [];
                var strength = Add(profile.Strength, modifier.Strength); var ap = Add(profile.Ap, modifier.Ap);
                if (strength != profile.Strength) applied.Add($"S {profile.Strength} → {strength}");
                if (ap != profile.Ap) applied.Add($"AP {profile.Ap} → {ap}");
                var keywords = profile.Keywords;
                foreach (var keyword in modifier.Keywords)
                {
                    var next = AddKeyword(keywords, keyword);
                    if (next != keywords) { keywords = next; applied.Add(keyword); }
                }
                profile = profile with { Strength = strength, Ap = ap, Keywords = keywords };
                if (applied.Count > 0) changes.Add($"{effect.Source} · {effect.Ability.Name}: {string.Join("; ", applied)}");
            }
            return new EffectiveWeapon(original, profile, changes);
        }).ToList();
    }
    private static string Add(string value, int delta) => delta != 0 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? (number + delta).ToString(CultureInfo.InvariantCulture) : value;
    private static string AddKeyword(string existing, string addition)
    {
        var keywords = existing.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => k is not "-" and not "—").ToList();
        if (keywords.Any(k => UnitIdentity.Normalize(k) == UnitIdentity.Normalize(addition))) return existing;
        var sustained = Regex.Match(addition, @"^Sustained Hits (\d+)$", Options);
        if (sustained.Success)
        {
            var prior = keywords.Select(k => Regex.Match(k.Trim('[', ']'), @"^Sustained Hits (\d+)$", Options)).Where(m => m.Success).ToList();
            if (prior.Any(m => int.Parse(m.Groups[1].Value) >= int.Parse(sustained.Groups[1].Value))) return existing;
            keywords.RemoveAll(k => Regex.IsMatch(k.Trim('[', ']'), @"^Sustained Hits \d+$", Options));
        }
        keywords.Add(addition); return string.Join(", ", keywords);
    }
}
