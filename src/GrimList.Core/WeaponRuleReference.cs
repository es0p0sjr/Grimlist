using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace GrimList.Core;

public record RuleDefinition(string Name, string Text);

public static class WeaponRuleReference
{
    // Published ability lists/datasets are immutable. Weak identity indexes release
    // old revisions and separate same-named rules from different imports/editions.
    private sealed class Definitions(IEnumerable<AbilityProfile> abilities)
    {
        public readonly Dictionary<string, string> Text = abilities.GroupBy(a => Key(a.Name)).ToDictionary(g => g.Key,
            g => string.Join("\n\n", g.Select(a => a.Text.Trim()).Where(t => t.Length > 0).Distinct()));
    }
    private static readonly ConditionalWeakTable<List<AbilityProfile>, Lazy<Definitions>> definitions = new();
    private static readonly ConditionalWeakTable<Dataset, ConcurrentDictionary<string, Lazy<Definitions>>> fallbackDefinitions = new();
    private static Definitions Index(List<AbilityProfile> abilities) => definitions.GetValue(abilities,
        static a => new(() => new Definitions(a))).Value;
    public static List<string> Keywords(string? text) => Regex.Replace(text ?? "", @"\]\s*\[", ",")
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(k => k.Trim('[', ']').Trim()).Where(k => k is not ("" or "-" or "—"))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string Key(string name)
    {
        name = name.Trim().Trim('[', ']').Trim();
        if (Regex.IsMatch(name, @"^Anti(?:\b|[- ])", RegexOptions.IgnoreCase)) return "ANTI";
        name = Regex.Replace(name, @"[\s(]*(?:\d+\+?|D[36]|X)[\s)]*$", "", RegexOptions.IgnoreCase);
        return UnitIdentity.Normalize(name);
    }

    public static bool IsWeaponDefinition(AbilityProfile ability, IEnumerable<WeaponProfile> weapons)
        => weapons.SelectMany(w => Keywords(w.Keywords)).Any(k => Key(k) == Key(ability.Name));

    public static List<RuleDefinition> ForWeapon(WeaponProfile weapon, UnitDefinition? unit, Dataset? data)
    {
        const string unavailable = "Definition not available in the imported datasheets.";
        var edition = unit?.ProfileSource?.Edition ?? data?.Source.Edition;
        var local = unit?.Profile?.Abilities is { } abilities ? Index(abilities) : null;
        Definitions? imported = null;
        Definitions? Imported()
        {
            if (imported != null || data == null) return imported;
            if (data.SharedAbilities?.GetValueOrDefault(edition ?? "") is { } shared) return imported = Index(shared);
            return imported = fallbackDefinitions.GetValue(data, static _ => new())
                .GetOrAdd(edition == null ? "null" : "value/" + edition, _ => new(() => new Definitions(data.Units
                    .Where(u => (u.ProfileSource?.Edition ?? data.Source.Edition) == edition)
                    .SelectMany(u => u.Profile?.Abilities ?? [])))).Value;
        }
        return Keywords(weapon.Keywords).Select(keyword =>
        {
            var key = Key(keyword);
            // An explicitly present but empty local definition still takes priority.
            var text = local?.Text.GetValueOrDefault(key) ?? Imported()?.Text.GetValueOrDefault(key);
            return new RuleDefinition(keyword, string.IsNullOrEmpty(text) ? unavailable : text);
        }).ToList();
    }

    internal static List<RuleDefinition> ForWeaponUncached(WeaponProfile weapon, UnitDefinition? unit, Dataset? data)
    {
        var edition = unit?.ProfileSource?.Edition ?? data?.Source.Edition;
        var local = unit?.Profile?.Abilities ?? [];
        // Extra keywords supplied by attached units may have their definitions on another datasheet.
        var imported = data?.SharedAbilities?.GetValueOrDefault(edition ?? "") ?? (data?.Units ?? []).Where(u => (u.ProfileSource?.Edition ?? data?.Source.Edition) == edition)
            .SelectMany(u => u.Profile?.Abilities ?? []).ToList();
        return Keywords(weapon.Keywords).Select(keyword =>
        {
            var matches = local.Where(a => Key(a.Name) == Key(keyword)).ToList();
            if (matches.Count == 0) matches = imported.Where(a => Key(a.Name) == Key(keyword)).ToList();
            var texts = matches.Select(a => a.Text.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            return new RuleDefinition(keyword, texts.Count > 0 ? string.Join("\n\n", texts) : "Definition not available in the imported datasheets.");
        }).ToList();
    }
}
