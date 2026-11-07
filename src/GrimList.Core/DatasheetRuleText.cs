using System.Text.RegularExpressions;

namespace GrimList.Core;

// Conservative grammar for source rules which are prose-only. Unrecognized clauses
// remain readable rules; they never become unconditional numeric changes.
public static partial class DatasheetRuleText
{
    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static string Clean(string text) => Regex.Replace(text.Replace("**", "").Replace("^^", "").Replace('’', '\''), @"\s+", " ").Trim();
    private static List<DatasheetModifier> Numeric(string source, string text, string prefix, EffectCondition? condition = null)
    {
        List<DatasheetModifier> result = [];
        var pattern = @"^(?:Add (?<value>\d+)[""″]? to|Subtract (?<subtract>\d+) from|Improve) (?:(?:the|this model's|the bearer's) )?(?<fields>Move|Toughness|Save|Wounds|Leadership|Objective Control|Attacks|Strength|Armour Penetration|Damage|Ballistic Skill|Weapon Skill|Range)(?: and (?<second>\w+))? characteristics?(?<target>[^.]*?)(?: by (?<improve>\d+))?\.$";
        var match = Regex.Match(text, pattern, Rx);
        if (!match.Success) return result;
        var target = match.Groups["target"].Value;
        var weapon = Regex.IsMatch(target, @"\bweapons?\b", Rx);
        // Named weapons require imported profile-target semantics, not fuzzy name matching.
        if (weapon && !Regex.IsMatch(target, @"^ of (?:(?:the bearer's|this model's|models in that unit's) (?:melee |ranged )?weapons?|(?:melee |ranged )?weapons? equipped by (?:the bearer|this model))(?: instead)?$", Rx)) return result;
        if (!weapon && target.Length > 0 && !Regex.IsMatch(target, @"^(?: of (?:the bearer|this model|models in (?:the bearer's|this model's|this|that) unit|that unit))?(?: instead)?$", Rx)) return result;
        var value = match.Groups["value"].Success ? match.Groups["value"].Value : match.Groups["subtract"].Success ? match.Groups["subtract"].Value : match.Groups["improve"].Value;
        if (value.Length == 0) return result;
        foreach (var field in new[] { match.Groups["fields"].Value, match.Groups["second"].Value }.Where(f => f.Length > 0))
        {
            var stat = EffectiveDatasheets.Stat(field);
            var operation = match.Groups["subtract"].Success || match.Groups["improve"].Success && stat is "Sv" or "BS" or "WS" or "AP" or "LD" ? "decrement" : "increment";
            result.Add(new(prefix + "/" + stat, source, weapon ? "weapon" : "model", stat, operation, value,
                WeaponType: target.Contains("melee", StringComparison.OrdinalIgnoreCase) ? "Melee" : target.Contains("ranged", StringComparison.OrdinalIgnoreCase) ? "Ranged" : "", Condition: condition,
                Recipient: !weapon && Regex.IsMatch(target, @"models in (?:the bearer's|this model's|this) unit", Rx) ? new("attached-unit") : null));
        }
        return result;
    }
    public static List<DatasheetModifier> Enhancement(string name, string text)
    {
        var clean = Clean(text);
        var sentences = Regex.Split(clean, @"(?<=\.)\s+"); List<DatasheetModifier> result = [];
        for (var i = 0; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            var conditional = Regex.Match(sentence, @"^(While .+?), (add .+)$", Rx);
            var effects = conditional.Success ? NumericClauses(name, conditional.Groups[2].Value, name + "/conditional/" + i,
                new("battlefield", Label: conditional.Groups[1].Value)) : NumericClauses(name, sentence, name + "/text/" + i);
            var unitStat = Regex.Match(sentence, @"^This unit has \+(?<value>\d+) (?<stat>OC|M)\.$", Rx);
            if (unitStat.Success) effects.Add(new(name + "/unit/" + i, name, "model", unitStat.Groups["stat"].Value.ToUpperInvariant(),
                "increment", unitStat.Groups["value"].Value, Recipient: new("attached-unit")));
            if (conditional.Success && sentence.EndsWith(" instead.", StringComparison.OrdinalIgnoreCase))
                effects = effects.Select(e => result.LastOrDefault(b => b.Condition == null && b.Target == e.Target && b.Characteristic == e.Characteristic
                    && b.WeaponType == e.WeaponType && b.Operation == e.Operation) is { } baseline
                    ? e with { Value = (int.Parse(e.Value) - int.Parse(baseline.Value)).ToString(), Replaces = baseline.Id } : e).ToList();
            result.AddRange(effects);
            result.AddRange(Grants(name, sentence, name + "/grant/" + i));
        }
        return result;
    }
    private static List<DatasheetModifier> NumericClauses(string source, string text, string prefix, EffectCondition? condition = null)
    {
        // Only split a completely supported conjunction. Never turn the tail of
        // an unrecognized conditional sentence into an unconditional effect.
        if (!Regex.IsMatch(text, @"^(?:Add |Subtract |Improve )", Rx)) return [];
        var clauses = Regex.Split(text, @"\s+and (?=(?:add |subtract |improve ))", Rx);
        if (clauses.Length == 1) return Numeric(source, text, prefix, condition);
        var parsed = clauses.Select((c, i) => Numeric(source, c.TrimEnd('.') + ".", prefix + "/clause/" + i, condition)).ToList();
        return parsed.Any(p => p.Count == 0) ? [] : parsed.SelectMany(p => p).ToList();
    }
    private static List<DatasheetModifier> Grants(string source, string clause, string id)
    {
        List<DatasheetModifier> result = [];
        var grant = Regex.Match(clause, @"^(?:The bearer|This model|Models in (?:this|that) unit|(?:This|That) unit) (?:has|have|gains?|gain) (?:the )?(?<value>[\w +'-]+) (?<kind>ability|keyword)\.$", Rx);
        if (grant.Success) result.Add(new(id, source, grant.Groups["kind"].Value.ToLowerInvariant(), "", "add", grant.Groups["value"].Value));
        var inv = Regex.Match(clause, @"^(?:The bearer|This model|Models in (?:this|that) unit|(?:This|That) unit) (?:has|have) (?:a|an) (?<value>[2-6]\+) invulnerable save\.$", Rx);
        if (inv.Success) result.Add(new(id, source, "model", "InSv", "set", inv.Groups["value"].Value));
        result.AddRange(WeaponAbilities(source, clause).Select(m => m with { Id = id + "/" + m.Id }));
        return result;
    }
    public static List<DatasheetModifier> Attached(AbilityProfile ability)
    {
        var match = Regex.Match(Clean(ability.Text), @"^While this model is leading a unit, (?<effect>.+)$", Rx);
        if (!match.Success) return RecipientGrants(ability);
        var clause = match.Groups["effect"].Value;
        var id = "attached/" + EffectiveDatasheets.Key(ability.Name);
        return Numeric(ability.Name, clause, id).Concat(Grants(ability.Name, clause, id))
            .Select(m => m with { Recipient = new("led-unit") }).ToList();
    }
    public static List<DatasheetModifier> SelfAura(AbilityProfile ability, IEnumerable<string> keywords)
    {
        if (!ability.Name.Contains("Aura", StringComparison.OrdinalIgnoreCase)) return [];
        var text = Clean(ability.Text);
        var match = Regex.Match(text, @"^While (?:a|an) friendly (?<keys>.+?) unit is within \d+[""″] of this model, (?<effect>.+)$", Rx);
        if (!match.Success || Regex.IsMatch(text, @"\bother\b|excluding (?:this model|the bearer)|except (?:this model|the bearer)", Rx)
            || !EffectiveDatasheets.MatchesKeywords(keywords, match.Groups["keys"].Value)) return [];
        var effect = match.Groups["effect"].Value;
        var condition = new EffectCondition("constant", "1", Label: "Self Aura — while on the battlefield");
        var modifiers = Numeric(ability.Name, effect, "aura/" + EffectiveDatasheets.Key(ability.Name), condition);
        var grant = Regex.Match(effect, @"^(?:models in that unit|that unit) (?:have|has) (?:the )?(?<ability>[\w +'-]+) ability\.$", Rx);
        if (grant.Success) modifiers.Add(new("aura/" + EffectiveDatasheets.Key(ability.Name) + "/ability", ability.Name, "ability", "", "add", grant.Groups["ability"].Value, Condition: condition));
        var keyword = Regex.Match(effect, @"^(?:models in that unit|that unit) (?:have|has|gain|gains) (?:the )?(?<keyword>[\w '-]+) keyword\.$", Rx);
        if (keyword.Success) modifiers.Add(new("aura/" + EffectiveDatasheets.Key(ability.Name) + "/keyword", ability.Name, "keyword", "", "add", keyword.Groups["keyword"].Value, Condition: condition));
        return modifiers;
    }
}
