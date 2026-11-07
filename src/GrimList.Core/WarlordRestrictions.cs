using System.Net;
using System.Text.RegularExpressions;

namespace GrimList.Core;

// A prohibition with its source and applicability, never a mutation of the base unit.
public record WarlordRestriction(string Source, string Text, EffectCondition? Condition = null);
public record WarlordEligibility(bool Eligible, bool MustBeWarlord, bool CannotBeWarlord, List<string> Reasons);

public static partial class WarlordRules
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AbilityProfile, List<WarlordRestriction>> abilityRestrictions = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ReferenceCard, List<WarlordRestriction>> referenceRestrictions = new();
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Prohibition = @"(?:cannot|can not|can't|can never|must not|may not) be (?:(?:selected|chosen|nominated) (?:(?:as|to be) )?)?(?:(?:your|the army's) )?warlord\b";

    public static List<WarlordRestriction> ParseRestrictions(string source, string text, bool self = true)
    {
        text = WebUtility.HtmlDecode(Regex.Replace(text, @"<br\s*/?>|</p>", "\n", IgnoreCase));
        text = Regex.Replace(text, "<[^>]*>", "").Replace("*", "").Replace("^", "").Replace("_", "").Replace('’', '\'');
        List<WarlordRestriction> result = [];
        foreach (var part in Regex.Split(text, @"(?<=[.!?])\s+|[\r\n]+"))
        {
            var sentence = Regex.Replace(part, @"\s+", " ").Trim().TrimStart('•', '-', ' ');
            var body = sentence;
            EffectCondition? condition = null;
            var prefix = Regex.Match(body, @"^If (?:this (?:model|unit)|the bearer) has the (?<key>.+?) keyword,? (?:then )?", IgnoreCase);
            if (prefix.Success) { condition = new("keyword", prefix.Groups["key"].Value); body = body[prefix.Length..]; }
            else
            {
                prefix = Regex.Match(body, @"^If your Army Faction is (?<faction>[^,]+), (?:then )?", IgnoreCase);
                if (prefix.Success) { condition = new("armyFaction", prefix.Groups["faction"].Value); body = body[prefix.Length..]; }
            }
            EffectCondition? target = null;
            var match = self && Regex.IsMatch(body, @"^(?:This (?:model|unit)|The bearer) " + Prohibition, IgnoreCase);
            if (!match)
            {
                var targeted = Regex.Match(body, @"^(?<target>.+?) (?:models?|units?)(?: (?:from|in) your army)? " + Prohibition, IgnoreCase);
                if (!targeted.Success)
                    targeted = Regex.Match(body, @"^You (?:cannot|can not|must not|may not) (?:select|choose|nominate) (?<target>.+?) (?:models?|units?)(?: (?:from|in) your army)? (?:as|to be) your warlord\b", IgnoreCase);
                if (targeted.Success)
                {
                    var predicate = Regex.Replace(targeted.Groups["target"].Value, @"^(?:(?:all|any|an?|the|friendly)\s+)+", "", IgnoreCase);
                    // Unresolved pronouns and battlefield clauses must never become army-wide prohibitions.
                    if (!Regex.IsMatch(predicate, @"\b(?:this|that|these|those|other|enemy|if|while|unless|selected|such)\b", IgnoreCase))
                    { target = new("warlordTarget", predicate); match = true; }
                }
            }
            if (match)
            {
                if (Regex.IsMatch(body, @"\bwarlord\b.*\b(?:if|unless|while|when)\b", IgnoreCase))
                    condition = new("unknown", Label: sentence);
                var tests = new[] { condition, target }.OfType<EffectCondition>().ToList();
                result.Add(new(source, sentence, tests.Count == 0 ? null : tests.Count == 1 ? tests[0] : new("and", Children: tests)));
            }
        }
        return result;
    }

    private static IEnumerable<WarlordRestriction> UnitRestrictions(UnitDefinition unit) => (unit.Profile?.WarlordRestrictions ?? [])
        .Concat((unit.Profile?.Abilities ?? []).SelectMany(a => abilityRestrictions.GetValue(a, key => ParseRestrictions(key.Name, key.Text))));

    private static IEnumerable<WarlordRestriction> Restrictions(ArmyList army, UnitDefinition unit, Dataset data)
    {
        foreach (var rule in UnitRestrictions(unit)) yield return rule;
        foreach (var detachment in DetachmentRules.Selected(army).Select(n => DetachmentRules.Find(army, n, data)).OfType<DetachmentDefinition>())
            foreach (var rule in detachment.WarlordRestrictions ?? []) yield return rule;
        var cards = ArmyReferences.ForArmy(army, data);
        if (AlliedRules.IsAlly(army, unit) && AlliedRules.Permission(army, unit, data) is { } permission)
            cards.AddRange(ArmyReferences.ForArmy(ArmyList.New(AlliedRules.OriginFaction(unit)) with { Game = army.Game }, data)
                .Where(c => UnitIdentity.Normalize(c.Name) == UnitIdentity.Normalize(permission.Name)));
        foreach (var card in cards.Distinct().Where(c => ReferenceModel.Key(c.Section) is
            "ARMYRULES" or "DETACHMENTS" or "ALLIEDRULES" or "ALLIES"))
        {
            foreach (var rule in card.WarlordRestrictions ?? []) yield return rule;
            foreach (var rule in referenceRestrictions.GetValue(card, key => ParseRestrictions(key.Name,
                string.Join("\n", new[] { key.Text, key.Details?.Effect, key.Details?.Restrictions }), self: false))) yield return rule;
        }
    }

    private static bool? RestrictionApplies(EffectCondition? test, ArmyList army, ArmyEntry entry, UnitDefinition unit)
    {
        if (test == null) return true;
        if (test.Kind == "not") return test.Children?.Count == 1 ? !RestrictionApplies(test.Children[0], army, entry, unit) : null;
        if (test.Kind is "and" or "or")
        {
            var values = (test.Children ?? []).Select(c => RestrictionApplies(c, army, entry, unit)).ToList();
            return test.Kind == "and" ? values.Contains(false) ? false : values.Contains(null) ? null : true
                : values.Contains(true) ? true : values.Contains(null) ? null : false;
        }
        int? value = test.Kind switch
        {
            "warlordTarget" => EffectiveDatasheets.MatchesKeywords((unit.Profile?.Keywords ?? []).Append(unit.Category).Append(unit.Name), test.Value) ? 1 : 0,
            "armyFaction" => UnitIdentity.Normalize(army.Faction) == UnitIdentity.Normalize(test.Value) ? 1 : 0,
            "ally" => AlliedRules.IsAlly(army, unit) ? 1 : 0,
            _ => null
        };
        return value == null ? EffectiveDatasheets.Test(test, army, entry, unit)
            : EffectiveDatasheets.Test(test with { Kind = "constant", Value = value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }, army, entry, unit);
    }

    public static WarlordEligibility Evaluate(ArmyList army, ArmyEntry entry, Dataset? data)
    {
        if (data == null || data.Game != army.Game || !army.Entries.Any(e => e.Id == entry.Id)
            || MusterEffects.EffectiveUnit(army, entry, data) is not { } unit)
            return new(false, false, false, ["Warlord eligibility is unknown; import the unit's datasheet."]);
        var restrictions = Restrictions(army, unit, data).Select(r => (Rule: r, Active: RestrictionApplies(r.Condition, army, entry, unit))).ToList();
        var forbidden = restrictions.Where(r => r.Active == true).Select(r => $"Cannot be your Warlord — {r.Rule.Source}: {r.Rule.Text}").Distinct().ToList();
        if (!AlliedRules.CanBeWarlord(army, unit, data))
            forbidden.Add($"Cannot be your Warlord — allied restriction ({AlliedRules.Permission(army, unit, data)?.Name ?? "no allied Warlord permission"}).");
        // A prohibition always wins, including over mandatory source constraints/Supreme Commander.
        if (forbidden.Count > 0) return new(false, false, true, forbidden);
        var unknown = restrictions.Where(r => r.Active == null).Select(r => $"Warlord eligibility needs review — {r.Rule.Source}: {r.Rule.Text}").Distinct().ToList();
        if (unknown.Count > 0) return new(false, false, false, unknown);
        if (UnitAvailability.Problem(army, unit, data) is { } problem) return new(false, false, false, [problem]);
        var required = Mandatory(unit);
        return required || IsCharacter(unit) ? new(true, required, false, []) : new(false, false, false, ["Only an eligible CHARACTER or a mandatory Warlord can be selected."]);
    }

    public static string RestrictionReason(ArmyList army, ArmyEntry entry, Dataset? data)
    {
        var result = Evaluate(army, entry, data);
        return result.CannotBeWarlord || result.Reasons.Any(r => r.StartsWith("Warlord eligibility needs review")) ? string.Join("\n", result.Reasons) : "";
    }
    public static bool Required(ArmyList army, ArmyEntry entry, Dataset? data) => Evaluate(army, entry, data).MustBeWarlord;
}
