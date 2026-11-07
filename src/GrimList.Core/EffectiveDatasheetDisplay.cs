using System.Text.RegularExpressions;

namespace GrimList.Core;

public record EffectiveCharacteristic(string Key, string Value, bool Modified);
public record DatasheetStatline(string Name, Dictionary<string, string>? Source, Dictionary<string, string> Effective);

// Both native UIs compare the final projection to the immutable source, not to an
// intermediate modifier. A cancelled-out or unresolved effect gets no highlight.
public static class EffectiveDatasheetDisplay
{
    public const string ModifiedColour = "#F4D58D";
    // Presentation only: use model stats when the common row is absent, and group
    // identical statlines without changing the unit/model data used by rules.
    public static List<DatasheetStatline> Statlines(UnitProfile? source, UnitProfile? effective)
    {
        List<DatasheetStatline> result = [];
        string Signature(Dictionary<string, string> stats) => DataJson.Write(stats
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => new KeyValuePair<string, string>(DatasheetStats.IsInvulnerable(p.Key) ? "InSv"
                : DatasheetStats.IsFeelNoPain(p.Key) ? "FNP" : EffectiveDatasheets.Stat(p.Key), p.Value.Trim()))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToList());
        void Add(string name, Dictionary<string, string>? baseline, Dictionary<string, string>? stats)
        {
            if (stats == null || stats.Count == 0) return;
            var index = result.FindIndex(r => Signature(r.Effective) == Signature(stats));
            if (index < 0) result.Add(new(name, baseline, stats));
            else if (name.Length > 0) result[index] = result[index] with {
                Name = string.Join(" / ", new[] { result[index].Name, name }.Where(n => n.Length > 0).Distinct()) };
        }
        // A projected mixed unit may acquire only a shared defensive save. Its
        // complete model rows already include that save; avoid a blank primary row.
        if (DatasheetStats.Standard(effective?.Characteristics).Count > 0 || effective?.Models is not { Count: > 0 })
            Add("", source?.Characteristics, effective?.Characteristics);
        foreach (var model in effective?.Models ?? [])
            Add(model.Name, source?.Models?.FirstOrDefault(m => m.Name == model.Name)?.Characteristics ?? source?.Characteristics, model.Characteristics);
        return result;
    }
    public static bool Changed(string? original, string effective) => (original ?? "—").Trim() != effective.Trim();
    public static List<EffectiveCharacteristic> Characteristics(Dictionary<string, string>? source, Dictionary<string, string>? effective)
        => DatasheetStats.Standard(effective).Select(p => new EffectiveCharacteristic(p.Key, p.Value,
            Changed(source?.FirstOrDefault(b => EffectiveDatasheets.Stat(b.Key) == EffectiveDatasheets.Stat(p.Key)).Value, p.Value))).ToList();
    public static WeaponProfile? BaseWeapon(UnitProfile? source, WeaponProfile effective)
    {
        var name = Regex.Replace(effective.Name, @"^\d+\s*×\s*", "");
        var matches = (source?.Weapons ?? []).Where(w => effective.SourceId != null && w.SourceId != null
            ? w.SourceId == effective.SourceId : w.Name == name && w.Type == effective.Type).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
    public static string WeaponValue(WeaponProfile weapon, string field) => EffectiveDatasheets.Stat(field) switch
    {
        "Range" => weapon.Range, "A" => weapon.Attacks, "BS" or "WS" => weapon.Skill, "S" => weapon.Strength,
        "AP" => weapon.Ap, "D" => weapon.Damage, "Keywords" => weapon.Keywords, _ => ""
    };
    public static bool WeaponChanged(UnitProfile? source, WeaponProfile effective, string field)
        => BaseWeapon(source, effective) is { } original && Changed(WeaponValue(original, field), WeaponValue(effective, field));
}
