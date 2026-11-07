using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GrimList.Core;

public static partial class LeaderRules
{
    // Attachment prose compiler: reads unit profiles and caches their permissions.
    // Extend this boundary for new attachment wording; preserve unrelated reference
    // import/merge and enhancement classification (BsDataReferences/DatasetMerge).
    // Preserve version + fingerprint invalidation: existing libraries compile on demand.
    // If compiled semantics change, update the version so saved rules are recompiled.
    // Regression coverage: AttachmentCombinationChecks (persistence/source changes).
    private const int AttachmentVersion = 4;
    // Profiles are immutable snapshots throughout import/merge; copies get a fresh cache entry.
    private static readonly ConditionalWeakTable<UnitProfile, AttachmentDefinition> CompiledRules = new();
    private static string RuleMarkup(string text) => text.Replace("*", "").Replace("^^", "").Replace("__", "");
    private static string CleanRule(string text) => Regex.Replace(RuleMarkup(text), @"\s+", " ").Trim();
    private static IEnumerable<AbilityProfile> Rules(UnitProfile? profile) => (profile?.Abilities ?? [])
        .Where(a => CleanRule(a.Name).Equals("Leader", StringComparison.OrdinalIgnoreCase) || CleanRule(a.Name).Equals("Support", StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> Terms(string text) => Regex.Split(text.Trim().TrimEnd(','), @"\s*,\s*|\s+or\s+", RegexOptions.IgnoreCase)
        .Select(t => Regex.Replace(t.Trim(), @"\Aor\s+", "", RegexOptions.IgnoreCase)).Where(t => t.Length > 0);
    private static string Fingerprint(UnitProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        DataJson.Write(new { profile.Keywords, profile.Abilities, profile.Associations }))));

    // Old libraries are compiled on demand. A changed ability must not retain stale imported permissions.
    public static AttachmentDefinition Semantics(UnitProfile? profile)
    {
        if (profile == null) return new(AttachmentVersion, "", false, false, [], false, [], []);
        return CompiledRules.GetValue(profile, p =>
        {
            var fingerprint = Fingerprint(p);
            return p.AttachmentRules is { } rules && rules.Version == AttachmentVersion && rules.SourceFingerprint == fingerprint
                ? rules : Compile(p, fingerprint);
        });
    }
    public static UnitProfile ImportRules(UnitProfile profile)
    {
        var rules = Semantics(profile);
        return ReferenceEquals(profile.AttachmentRules, rules) ? profile : profile with { AttachmentRules = rules };
    }
    public static Dataset ImportRules(Dataset dataset) => dataset with
    {
        Units = dataset.Units.Select(u => u.Profile == null ? u : u with { Profile = ImportRules(u.Profile) }).ToList()
    };

    private static AttachmentDefinition Compile(UnitProfile profile, string fingerprint)
    {
        var abilities = Rules(profile).ToList();
        var targets = abilities.SelectMany(a => Regex.Matches(RuleMarkup(a.Text), @"can be attached to the following units?\s*:\s*([^\.]+)", RegexOptions.IgnoreCase)
            .SelectMany(m => Regex.Split(Regex.Split(m.Groups[1].Value, @"\b(?:You can attach|If this (?:model|unit)|Before the battle|Unless otherwise stated|Each bodyguard)\b", RegexOptions.IgnoreCase)[0], @"[,;\r\n\u2022\u25cf\u25a0\u25aa]+")))
            .Select(s => s.Trim().Trim('-', '*').Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var keywords = profile.Keywords ?? [];
        // Joining a unit already led by a qualifying model is a retinue permission.
        // It does not make this unit a substitute target for that model's Leader list.
        // Preserve this distinction; AttachmentRecipientChecks covers Cryptothralls.
        var retinues = (profile.Abilities ?? []).Select(a => Regex.Match(CleanRule(a.Text),
            @"^At the start of the Declare Battle Formations step, this unit can join one other unit from your army that is being led by a (?<leader>[\w '-]+) model \(a unit cannot have more than one (?<unique>[\w '-]+) unit joined to it\)\.", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => new RetinueAttachmentRule(m.Groups["leader"].Value, m.Groups["unique"].Value)).ToList();
        var support = keywords.Any(k => k.Equals("Support", StringComparison.OrdinalIgnoreCase))
            || abilities.Any(a => CleanRule(a.Name).Equals("Support", StringComparison.OrdinalIgnoreCase)) || retinues.Count > 0;
        List<AdditionalAttachmentRule> exceptions = [];
        foreach (var ability in abilities)
        foreach (Match match in Regex.Matches(CleanRule(ability.Text),
            @"\byou can attach this (?:model|unit) to (?<bodyguard>[^.]+?),? even if (?:(?:one|a|an) )?(?<partners>[^.]+?) has already been attached to it(?:\s*\((?<distinct>you cannot attach more than one of the same Leader to the same unit)\))?(?:\.|\z)", RegexOptions.IgnoreCase))
        {
            var target = match.Groups["bodyguard"].Value.Trim().TrimEnd(',');
            List<string> bodyguards = [];
            if (!Regex.IsMatch(target, @"\A(?:one of the above units|the above unit|a unit)\z", RegexOptions.IgnoreCase))
            {
                var named = Regex.Match(target, @"\A(?:an?|one) (.+?) unit\z", RegexOptions.IgnoreCase);
                if (!named.Success) continue;
                bodyguards = Terms(named.Groups[1].Value).ToList();
            }
            var partners = Regex.Replace(match.Groups["partners"].Value, @"\s+(?:model|unit)\z", "", RegexOptions.IgnoreCase);
            var anyLeader = partners.Equals("other Leader", StringComparison.OrdinalIgnoreCase);
            exceptions.Add(new(bodyguards, anyLeader, anyLeader ? [] : Terms(partners).ToList(), match.Groups["distinct"].Success, match.Value));
        }
        List<BodyguardAttachmentRule> bodyguardRules = [];
        foreach (var ability in profile.Abilities ?? [])
        {
            var text = CleanRule(ability.Text);
            var ordinary = Regex.Match(text, @"\AThis unit can have up to (?:two|2) Leader units attached to it(?:, provided no more than one of those units is a (?<limited>[^.]+?) unit)?\.", RegexOptions.IgnoreCase);
            if (ordinary.Success)
            {
                var limited = ordinary.Groups["limited"].Value;
                bodyguardRules.Add(new(2, null, false, limited, limited.Length > 0 ? 1 : null, ability.Text));
                continue;
            }
            var sized = Regex.Match(text, @"\AIf this unit has a Starting Strength of (?<size>\d+), you can attach up to (?:two|2) Leader units to it instead of one, provided those Leaders are not duplicates\b", RegexOptions.IgnoreCase);
            if (sized.Success && int.TryParse(sized.Groups["size"].Value, out var size))
                bodyguardRules.Add(new(2, size, true, "", null, ability.Text));
        }
        return new(AttachmentVersion, fingerprint, abilities.Count > 0 || keywords.Any(k => k.Equals("Leader", StringComparison.OrdinalIgnoreCase)) || support,
            support, targets, retinues.Count > 0 || abilities.Any(a => Regex.IsMatch(CleanRule(a.Text), @"\bone leader unit and one support unit\b", RegexOptions.IgnoreCase)), exceptions, bodyguardRules, profile.Associations, retinues,
            InheritedLeaders(profile));
    }
}
