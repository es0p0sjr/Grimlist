namespace GrimList.Core;

public record AlliedPermission(string Name, string SourceUrl, int? PointsLimit = null,
    bool Enhancements = false, bool Warlord = false);

// 11th-edition muster permissions, checked 2026-10-02. See README for scope and sources.
// These rules never rewrite a unit's stored faction, prices, keywords or permanent identity.
public static class AlliedRules
{
    private const string RulesRoot = "https://wahapedia.ru/wh40k11ed/factions/";
    private static string N(string value) => UnitIdentity.Normalize(value);
    private static readonly HashSet<string> Marines = new(new[] { "Space Marines", "Black Templars", "Blood Angels", "Dark Angels", "Deathwatch", "Space Wolves", "Imperial Fists", "Iron Hands", "Raven Guard", "Salamanders", "Ultramarines", "White Scars" }.Select(N));
    private static readonly HashSet<string> Imperial = new(new[] { "Adepta Sororitas", "Adeptus Custodes", "Adeptus Mechanicus", "Astra Militarum", "Grey Knights", "Imperial Agents", "Imperial Knights", "Titan Legions" }.Select(N).Concat(Marines));
    private static readonly HashSet<string> Chaos = new(new[] { "Chaos Space Marines", "Chaos Daemons", "Chaos Knights", "Chaos Titan Legions", "Death Guard", "World Eaters", "Thousand Sons", "Emperor's Children", "Plague Legions", "Blood Legions", "Scintillating Legions", "Legions of Excess" }.Select(N));
    private static readonly (string Host, string Legion, string Detachment, string Page)[] Legions =
    [
        ("Death Guard", "Plague Legions", "Tallyband Summoners", "death-guard"),
        ("World Eaters", "Blood Legions", "Khorne Daemonkin", "world-eaters"),
        ("Thousand Sons", "Scintillating Legions", "Changehost of Deceit", "thousand-sons"),
        ("Emperor's Children", "Legions of Excess", "Carnival of Excess", "emperor-s-children")
    ];
    public static bool Has(UnitDefinition unit, string keyword) => ImportedUnitFacts.For(unit).Membership.Contains(N(keyword));
    private static bool Named(UnitDefinition unit, params string[] names) => names.Any(name => N(unit.Name) == N(name) || Has(unit, name));
    public static string OriginFaction(UnitDefinition unit) => ImportedUnitFacts.For(unit).OriginFaction;
    internal static string ResolveOriginFaction(UnitDefinition unit)
    {
        var explicitFactions = (unit.Profile?.Keywords ?? []).Where(k => k.StartsWith("Faction:", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[(k.IndexOf(':') + 1)..].Trim()).ToList();
        foreach (var legion in Legions)
            if (Has(unit, legion.Legion)) return legion.Legion;
        foreach (var faction in explicitFactions)
        {
            var mapped = N(faction) switch
            {
                "ADEPTUSASTARTES" => Marines.Contains(N(unit.Faction)) ? unit.Faction : "Space Marines",
                "ASURYANI" or "AELDARI" or "HARLEQUINS" or "ANHRATHE" => "Aeldari",
                "AGENTSOFTHEIMPERIUM" => "Imperial Agents",
                "LEGIONESDAEMONICA" => "Chaos Daemons",
                "HERETICASTARTES" => "Chaos Space Marines",
                _ => MfmImporter.Factions.FirstOrDefault(f => N(f) == N(faction))
            };
            if (mapped != null) return mapped;
        }
        return unit.Faction;
    }
    public static bool IsAlly(ArmyList army, UnitDefinition unit) => N(OriginFaction(unit)) != N(army.Faction);
    internal static bool IsMarineFaction(string faction) => Marines.Contains(N(faction));
    public static string Category(ArmyList army, UnitDefinition unit, Dataset? data = null) => MusterRules.Category(army, unit, data);
    private static bool Edition11(Dataset? data, UnitDefinition unit) => data?.Source.Edition == "11"
        && (unit.PointsSource?.Edition is null or "" or "11") && (unit.ProfileSource?.Edition is null or "" or "11");
    private static int? Scale(int limit, int incursion) => limit switch
    { > 0 and <= 1000 => incursion, > 1000 and <= 2000 => incursion * 2, > 2000 and <= 3000 => incursion * 3, _ => null };
    private static AlliedPermission Rule(string name, string page, int? limit = null, bool enhancements = false, bool warlord = false)
        => new(name, RulesRoot + page + "/", limit, enhancements, warlord);

    public static AlliedPermission? Permission(ArmyList army, UnitDefinition unit, Dataset? data)
    {
        if (!IsAlly(army, unit) || !Edition11(data, unit) || data?.Game != army.Game) return null;
        var host = N(army.Faction); var origin = N(OriginFaction(unit));
        if (Imperial.Contains(host) && origin == "IMPERIALKNIGHTS" && Has(unit, "Imperium") && (Has(unit, "Titanic") || Has(unit, "Armiger")))
            return Rule("Freeblades", "imperial-knights");
        if (Chaos.Contains(host) && origin == "CHAOSKNIGHTS" && Has(unit, "Chaos") && (Has(unit, "Titanic") || Has(unit, "War Dog")))
            return Rule("Dreadblades", "chaos-knights");
        if (Imperial.Contains(host) && origin == "IMPERIALAGENTS" && Has(unit, "Imperium")
            && new[] { "Retinue", "Character", "Requisitioned", "Dedicated Transport" }.Any(k => Has(unit, k)))
            return Rule("Assigned Agents", "imperial-agents", warlord: !Has(unit, "Officio Assassinorum"));
        if (host is "CHAOSSPACEMARINES" or "CHAOSKNIGHTS" && origin == "CHAOSDAEMONS" && Has(unit, "Legiones Daemonica"))
            return Rule("Daemonic Pact", "chaos-daemons", Scale(army.Limit, 250));
        foreach (var legion in Legions)
            if (host == N(legion.Host) && origin == N(legion.Legion)
                && (unit.RequiredDetachments != null ? unit.RequiredDetachments.Count == 0 || unit.RequiredDetachments.Any(d => DetachmentRules.Contains(army, d, data)) : DetachmentRules.Contains(army, legion.Detachment, data)))
                return Rule(legion.Detachment, legion.Page, Scale(army.Limit, 500), enhancements: true);
        if (host == "DRUKHARI" && origin == "AELDARI")
        {
            if (DetachmentRules.Contains(army, "Reaper's Wager", data))
                return Has(unit, "Harlequins") ? Rule("Reaper's Wager", "drukhari", Scale(army.Limit, 500), enhancements: true) : null;
            if (Has(unit, "Harlequins") || Has(unit, "Anhrathe"))
                return Rule("Corsairs and Travelling Players", "drukhari", Scale(army.Limit, 250));
        }
        if (host == "GENESTEALERCULTS")
        {
            if (origin == "ASTRAMILITARUM" && DetachmentRules.Contains(army, "Brood Brother Auxilia", data)
                && !new[] { "Aircraft", "Commissar", "Epic Hero", "Militarum Tempestus", "Ogryn", "Ratling", "Tech-priest Enginseer", "Ministorum Priest" }.Any(k => Has(unit, k)))
                return Rule("Brood Brother Auxilia", "genestealer-cults", Scale(army.Limit, 500));
            if (origin == "TYRANIDS" && DetachmentRules.Contains(army, "Final Day", data) && Has(unit, "Vanguard Invader")
                && !new[] { "Aircraft", "Broodlord", "Genestealers" }.Any(k => Has(unit, k)))
                return Rule("Final Day", "genestealer-cults", Scale(army.Limit, 500), enhancements: true);
        }
        if (host == "IMPERIALKNIGHTS" && origin == "ADEPTUSMECHANICUS" && DetachmentRules.Contains(army, "Questor Forgepact", data)
            && Named(unit, "Tech-priest Dominus", "Tech-priest Manipulus", "Skitarii Marshal", "Skitarii Rangers", "Skitarii Vanguard"))
            return Rule("Questor Forgepact", "imperial-knights", 500, enhancements: true);
        if (host == "CHAOSKNIGHTS" && origin == "CHAOSSPACEMARINES" && DetachmentRules.Contains(army, "Iconoclast Fiefdom", data) && Has(unit, "Damned"))
            return Rule("Iconoclast Fiefdom", "chaos-knights", 500, enhancements: true);
        if (host == "CHAOSDAEMONS" && origin == "CHAOSSPACEMARINES" && DetachmentRules.Contains(army, "Shadow Legion", data)
            && (Has(unit, "Damned") || Named(unit, "Chaos Lord", "Chaos Lord in Terminator Armour", "Chaos Lord with Jump Pack", "Chaos Terminator Squad",
                "Chosen", "Dark Apostle", "Havocs", "Legionaries", "Master of Possession", "Possessed", "Raptors", "Sorcerer", "Sorcerer in Terminator Armour", "Warp Talons")))
            return Rule("Shadow Legion", "chaos-daemons", Scale(army.Limit, 500), enhancements: true);
        return null;
    }

    public static string? Problem(ArmyList army, UnitDefinition unit, Dataset? data)
    {
        if (!IsAlly(army, unit)) return null;
        if (data?.Source.Edition != "11" && N(unit.Faction) == N(army.Faction)) return null;
        if (!Edition11(data, unit)) return $"{unit.Name}: different faction; allied selection requires matching 11th-edition data.";
        if (Permission(army, unit, data) == null)
        {
            var legion = Legions.FirstOrDefault(l => N(l.Host) == N(army.Faction) && N(l.Legion) == N(OriginFaction(unit)));
            return legion.Detachment != null ? $"{unit.Name}: requires {legion.Detachment}."
                : $"{unit.Name}: different faction; no supported ally rule permits this unit in the selected army and detachments.";
        }
        return null;
    }
    public static bool CanBeWarlord(ArmyList army, UnitDefinition unit, Dataset? data) => data?.Source.Edition != "11" ? N(unit.Faction) == N(army.Faction) : !IsAlly(army, unit)
        || Permission(army, unit, data)?.Warlord == true;
    public static bool CanTakeEnhancement(ArmyList army, UnitDefinition unit, ReferenceCard card, Dataset? data)
        => data?.Source.Edition != "11" ? N(unit.Faction) == N(army.Faction)
            : !IsAlly(army, unit) || Permission(army, unit, data) is { Enhancements: true } rule && N(rule.Name) == N(card.Detachment);

    public static List<UnitDefinition> Library(ArmyList army, Dataset? data) => (data?.Units ?? [])
        .Where(u => UnitAvailability.Problem(army, u, data) == null)
        // Prefer the host catalogue's contextual version, then the original faction's version.
        .OrderBy(u => N(u.Faction) == N(army.Faction) ? 0 : N(u.Faction) == N(OriginFaction(u)) ? 1 : 2)
        .ThenBy(u => u.Key, StringComparer.Ordinal)
        .GroupBy(u => N(OriginFaction(u)) + "|" + N(u.Name))
        // Keep distinct same-name variants in the preferred catalogue (e.g. Space Wolves).
        .SelectMany(group => group.Where(u => N(u.Faction) == N(group.First().Faction))).ToList();

    private sealed record LimitIssue(string Key, int Excess, string Message);
    private static List<LimitIssue> Limits(ArmyList army, Dataset? data, List<PricedEntry> priced)
    {
        List<LimitIssue> issues = [];
        if (data == null) return issues;
        var allies = priced.Select(e => (Entry: e, Unit: MusterEffects.EffectiveUnit(army, e.Entry, data)))
            .Where(e => e.Unit != null && IsAlly(army, e.Unit)).Select(e => (e.Entry, Unit: e.Unit!, Rule: Permission(army, e.Unit!, data))).ToList();
        foreach (var group in allies.Where(e => e.Rule != null).GroupBy(e => e.Rule!.Name))
        {
            var rule = group.First().Rule!;
            if (rule.PointsLimit is int cap)
            {
                var total = group.Sum(e => e.Entry.Points ?? 0);
                if (total > cap) issues.Add(new(rule.Name, total - cap, $"{rule.Name}: allied points {total}/{cap}; {total - cap} over the allowance."));
            }
            if (rule.Name is "Freeblades" or "Dreadblades")
            {
                var titanic = group.Where(e => Has(e.Unit, "Titanic")).Sum(e => e.Entry.Entry.Models);
                var small = group.Where(e => !Has(e.Unit, "Titanic")).Sum(e => e.Entry.Entry.Models);
                if (titanic > 1 || small > 3 || titanic > 0 && small > 0)
                    issues.Add(new(rule.Name, Math.Max(0, titanic - 1) + Math.Max(0, small - 3) + (titanic > 0 && small > 0 ? titanic + small : 0),
                        $"{rule.Name}: take either 1 Titanic model or up to 3 {(rule.Name == "Freeblades" ? "Armiger" : "War Dog")} models, not both."));
            }
            if (rule.Name == "Assigned Agents" && Scale(army.Limit, 1) is int scale)
                foreach (var category in new[] { "Retinue", "Character", "Requisitioned" })
                {
                    var count = group.Count(e => Has(e.Unit, category)); var allowed = category == "Requisitioned" ? (scale == 3 ? 2 : 1) : scale;
                    if (count > allowed) issues.Add(new("Agents/" + category, count - allowed, $"Assigned Agents: {category} units {count}/{allowed}."));
                }
            if (rule.Name == "Daemonic Pact")
                foreach (var god in new[] { "Khorne", "Tzeentch", "Nurgle", "Slaanesh" })
                {
                    var battleline = group.Count(e => Has(e.Unit, god) && Has(e.Unit, "Battleline"));
                    var other = group.Count(e => Has(e.Unit, god) && !Has(e.Unit, "Battleline"));
                    if (other > battleline) issues.Add(new("Pact/" + god, other - battleline, $"Daemonic Pact: {god} non-Battleline units {other} exceed {battleline} {god} Battleline units. Add Battleline allies of the same god."));
                }
        }
        return issues;
    }
    public static List<string> Issues(ArmyList army, Dataset? data, List<PricedEntry> priced)
    {
        var issues = Limits(army, data, priced).Select(i => i.Message).ToList();
        if (data == null) return issues;
        foreach (var entry in priced)
        {
            var unit = MusterEffects.EffectiveUnit(army, entry.Entry, data);
            if (unit == null || !IsAlly(army, unit) || Permission(army, unit, data) is not { } rule) continue;
            if (entry.Points == null) issues.Add($"{rule.Name}: allied points are incomplete; the allowance cannot be fully checked.");
            if (Scale(army.Limit, 1) == null && rule.Name is not ("Freeblades" or "Dreadblades" or "Questor Forgepact" or "Iconoclast Fiefdom"))
                issues.Add($"{rule.Name}: no published ally allowance is configured for this game size.");
            var keyword = rule.Name is "Freeblades" or "Assigned Agents" ? "Imperium" : rule.Name == "Dreadblades" ? "Chaos" : null;
            if (keyword != null && army.Entries.Any(e => MusterEffects.EffectiveUnit(army, e, data) is { } u && !Has(u, keyword)))
                issues.Add($"{rule.Name}: every model in the army must have the {keyword} keyword; check imported keywords for all units.");
            if (rule.Name == "Daemonic Pact" && army.Entries.Any(e => MusterEffects.EffectiveUnit(army, e, data) is { } u
                && N(OriginFaction(u)) != "CHAOSDAEMONS" && !Has(u, "Chaos Knights") && !Has(u, "Heretic Astartes")))
                issues.Add("Daemonic Pact: non-pact units must have Chaos Knights or Heretic Astartes keywords.");
            if (Has(unit, "Dedicated Transport") && rule.Name == "Assigned Agents")
                issues.Add("Assigned Agents transports must start with passengers embarked. Passenger assignments are not checked.");
        }
        if (N(army.Faction) == "CHAOSDAEMONS" && DetachmentRules.Contains(army, "Shadow Legion", data)
            && army.Entries.Any(e => MusterEffects.EffectiveUnit(army, e, data) is { } u && (Has(u, "Daemon Prince") || Has(u, "Daemon Prince with Wings") || Has(u, "Epic Hero") && !Named(u, "Be'lakor"))))
            issues.Add("Shadow Legion excludes Daemon Princes and Epic Heroes other than Be'lakor.");
        return issues.Distinct().ToList();
    }
    public static void ValidateIncrease(ArmyList before, ArmyList after, Dataset? data)
    {
        var oldResult = ArmyCalculator.Calculate(before, data); var nextResult = ArmyCalculator.Calculate(after, data);
        ValidateIncrease(before, after, data, oldResult, nextResult);
    }
    public static void ValidateIncrease(ArmyList before, ArmyList after, Dataset? data, ArmyResult oldResult, ArmyResult nextResult)
    {
        // Reductions and detachment/limit changes remain editable, with visible validation.
        if (after.Entries.Count <= before.Entries.Count && after.Entries.Sum(e => e.Models) <= before.Entries.Sum(e => e.Models) && nextResult.Total <= oldResult.Total) return;
        var previous = Limits(before, data, oldResult.Entries).ToDictionary(i => i.Key, i => i.Excess);
        var increased = Limits(after, data, nextResult.Entries).Where(i => i.Excess > previous.GetValueOrDefault(i.Key)).ToList();
        if (increased.Count > 0) throw new InvalidDataException(string.Join("\n", increased.Select(i => i.Message)));
    }
    public static string Summary(ArmyList army, Dataset? data, List<PricedEntry> priced)
    {
        var allies = priced.Where(e => MusterEffects.EffectiveUnit(army, e.Entry, data) is { } u && IsAlly(army, u)).ToList();
        return allies.Count == 0 ? "Allies: none selected" : $"Allies: {allies.Count} units · {allies.Sum(e => e.Points ?? 0)} pts{(allies.Any(e => e.Points == null) ? " + unknown" : "")}";
    }
    public static string Allowance(AlliedPermission rule, int armyLimit) => rule.Name switch
    {
        "Freeblades" => "1 Titanic model OR up to 3 Armiger models",
        "Dreadblades" => "1 Titanic model OR up to 3 War Dog models",
        "Assigned Agents" when Scale(armyLimit, 1) is int scale => $"{scale} Retinue, {scale} Character, {(scale == 3 ? 2 : 1)} Requisitioned units; dedicated transports separately",
        "Daemonic Pact" => $"Up to {rule.PointsLimit?.ToString() ?? "?"} pts; each god needs at least as many Battleline as non-Battleline units",
        _ => rule.PointsLimit is int cap ? $"Up to {cap} pts" : "Allowance unspecified for this game size"
    };
}
