using System.Text.RegularExpressions;

namespace GrimList.Core;

public static class EnhancementWeapons
{
    public static List<WeaponProfile> Profiles(ReferenceCard card)
    {
        if (card.Weapons != null) return card.Weapons;
        // Older imports flattened this weapon-only profile in characteristic order.
        // Read its saved values, never substitute stats from a different revision or
        // confuse it with The Imperium's Sword (1st Company Task Force).
        if (card.Section != "Enhancements" || card.Name != "Imperium's Sword" || card.Detachment != "Assault Brethren"
            || card.Source?.Edition != "11" || !card.Source.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase)) return [];
        var lines = card.Text.Replace("\r\n", "\n").Trim().Split('\n').Select(s => s.Trim()).ToArray();
        if (lines.Length is not (6 or 7) || lines[0] != "Melee"
            || !Regex.IsMatch(lines[1], @"^\d+(?:D\d+(?:[+-]\d+)?)?$") || !Regex.IsMatch(lines[2], @"^[2-6]\+$")
            || !Regex.IsMatch(lines[3], @"^\d+$") || !Regex.IsMatch(lines[4], @"^-?\d+$")
            || !Regex.IsMatch(lines[5], @"^\d*D?\d+(?:[+-]\d+)?$")) return [];
        return [new(card.Name, "Melee", lines[0], lines[1], lines[2], lines[3], lines[4], lines[5], lines.Length == 7 ? lines[6] : "")];
    }

    public static string GrantText(IEnumerable<WeaponProfile> weapons)
        => "The bearer is equipped with " + string.Join(", ", weapons.Select(w => w.Name).Distinct()) + " in addition to its other weapons. See its weapon profiles.";
}
