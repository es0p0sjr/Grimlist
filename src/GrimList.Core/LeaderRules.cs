namespace GrimList.Core;

public static partial class LeaderRules
{
    // Shared attachment validation for pickers, commits and saved-roster issues.
    // Preserve for attachment work: validate the complete proposed group, keeping
    // recipient eligibility separate from partner permissions and enhancement slots.
    // Stratagem availability belongs to ArmyReferences, not to this validator.
    // Regression coverage: AttachmentCombinationChecks and AttachmentRecipientChecks.
    public static bool IsLeader(UnitDefinition? unit) => Semantics(unit?.Profile).CanAttach;
    public static bool IsSupport(UnitDefinition? unit) => Semantics(unit?.Profile).IsSupport;
    public static string RoleLabel(UnitDefinition? unit) => IsSupport(unit) ? "Support" : "Leader";
    public static string RuleText(UnitDefinition? unit) => string.Join("\n\n", Rules(unit?.Profile).Select(a => a.Text).Distinct());

    // Only read the explicit bodyguard list, never mentions in later exception text or generic rules.
    public static List<string> BodyguardNames(UnitDefinition? unit) => Semantics(unit?.Profile).BodyguardTargets.ToList();

    public static string EntryLabel(ArmyList army, string id)
    {
        var entry = army.Entries.FirstOrDefault(e => e.Id == id);
        if (entry == null) return "Missing unit";
        var copies = army.Entries.Where(e => e.UnitKey == entry.UnitKey).ToList();
        return entry.Name + (copies.Count > 1 ? $" (unit {copies.FindIndex(e => e.Id == id) + 1})" : "");
    }
    public static string? Problem(ArmyList army, Dataset? data, string leaderId, string bodyguardId, bool checkEnhancements = true)
    {
        var members = army.Entries.Where(e => e.Id != leaderId && e.AttachedToId == bodyguardId).Select(e => e.Id).Append(leaderId).ToList();
        return CombinationProblem(army, data, bodyguardId, members, checkEnhancements);
    }

    // Validate the proposed group as a set. No member gets priority from selection or roster order.
    public static string? CombinationProblem(ArmyList army, Dataset? data, string bodyguardId, IReadOnlyCollection<string> attachmentIds, bool checkEnhancements = true)
    {
        army = army with { Entries = army.Entries.Select(e => attachmentIds.Contains(e.Id) ? e with { AttachedToId = bodyguardId }
            : e.AttachedToId == bodyguardId ? e with { AttachedToId = "" } : e).ToList() };
        var bodyguard = army.Entries.FirstOrDefault(e => e.Id == bodyguardId);
        if (bodyguard == null || attachmentIds.Any(id => army.Entries.All(e => e.Id != id))) return "Select a leader and bodyguard from this roster.";
        if (attachmentIds.Contains(bodyguardId)) return "A unit cannot lead itself.";
        if (attachmentIds.Distinct().Count() != attachmentIds.Count) return "An attachment entry can only appear once in a combination.";
        var bodyguardUnit = MusterEffects.EffectiveUnit(army, bodyguard, data);
        if (data == null || data.Game != army.Game || bodyguardUnit == null) return "Import datasheets to check attachment eligibility.";
        if (UnitAvailability.Problem(army, bodyguardUnit, data) != null) return "Both units must be permitted in this army before attaching them.";
        if (bodyguard.AttachedToId.Length > 0) return "Attached units cannot form attachment chains.";
        var members = new List<(ArmyEntry Entry, UnitDefinition Unit, AttachmentDefinition Rules)>();
        foreach (var id in attachmentIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            var entry = army.Entries.Single(e => e.Id == id);
            var unit = MusterEffects.EffectiveUnit(army, entry, data);
            if (unit == null) return "An attached unit's datasheet is missing; review that attachment first.";
            if (UnitAvailability.Problem(army, unit, data) != null) return "Both units must be permitted in this army before attaching them.";
            var rules = Semantics(unit.Profile);
            if (!rules.CanAttach) return "This unit has no imported Leader or Support ability.";
            // Structured source permissions take precedence over the leader's prose.
            // A recipient's explicit inheritance rule is a separate permission, and
            // checks those same structured restrictions against its referenced unit.
            var permission = EffectiveDatasheets.AttachmentAllowed(army, data, entry, unit, bodyguard, bodyguardUnit);
            var retinueAllowed = rules.Retinues?.Any(r => attachmentIds.Where(other => other != id).Any(other =>
                MusterEffects.EffectiveUnit(army, army.Entries.Single(e => e.Id == other), data) is { } partner
                && IsLeader(partner) && EffectiveDatasheets.MatchesKeywords(partner.Profile?.Keywords ?? [], r.LeaderKeywords))) == true;
            if (!(permission ?? (retinueAllowed || rules.BodyguardTargets.Any(t => MatchesTerm(bodyguardUnit, t))))
                && !InheritsLeader(army, data, entry, unit, bodyguardUnit))
                return "This bodyguard is not permitted by the current Leader/Support rules, keywords and enhancement selections.";
            if ((rules.Retinues ?? []).Any(r => attachmentIds.Where(other => other != id).Any(other =>
                MusterEffects.EffectiveUnit(army, army.Entries.Single(e => e.Id == other), data) is { } partner
                && EffectiveDatasheets.MatchesKeywords(partner.Profile?.Keywords ?? [], r.UniqueKeyword))))
                return "This attached unit already contains the maximum permitted retinue of this kind.";
            if (army.Entries.Any(e => e.AttachedToId == id)) return "Attached units cannot form attachment chains.";
            members.Add((entry, unit, rules));
        }
        // Counts include the candidate. Evaluate source constraint modifiers before applying fallback slots.
        var associations = AssociationRules.Evaluate(army, data, bodyguard, attachmentIds);
        if (associations.Problem != null) return associations.Problem;
        if (members.Count > 2 && !associations.CoversProposedSet) return "This bodyguard already has two attached Leader/Support units.";
        // Duplicate restrictions remain binding even when a structural limit permits multiple characters.
        for (var i = 0; i < members.Count; i++)
        for (var j = 0; j < members.Count; j++)
            if (i != j)
            {
                AllowsPartner(members[i].Unit, members[i].Rules, members[j].Unit, members[j].Rules, bodyguardUnit, out var restriction);
                if (restriction != null) return restriction;
            }
        if (members.Count == 2)
        {
            var first = members[0]; var second = members[1];
            string? bodyguardRestriction = null;
            var bodyguardAllows = !first.Rules.IsSupport && !second.Rules.IsSupport
                && BodyguardAllowsPair(bodyguard, Semantics(bodyguardUnit.Profile), first.Unit, second.Unit, out bodyguardRestriction);
            if (bodyguardRestriction != null) return bodyguardRestriction;
            var firstAllows = AllowsPartner(first.Unit, first.Rules, second.Unit, second.Rules, bodyguardUnit, out var firstRestriction);
            var secondAllows = AllowsPartner(second.Unit, second.Rules, first.Unit, first.Rules, bodyguardUnit, out var secondRestriction);
            if (firstRestriction != null || secondRestriction != null) return firstRestriction ?? secondRestriction;
            var supportsAllowed = first.Rules.IsSupport != second.Rules.IsSupport
                && (first.Rules.AllowsLeaderAndSupport || second.Rules.AllowsLeaderAndSupport)
                || first.Rules.Retinues?.Count > 0 || second.Rules.Retinues?.Count > 0;
            if (!associations.CoversProposedSet && !bodyguardAllows && !supportsAllowed && !firstAllows && !secondAllows)
                return "No imported rule permits this combination of Leader/Support units on this bodyguard.";
        }
        // Preserve this boundary: effect evaluation and candidate discovery can skip
        // slot conflicts, but a committed attachment must still enforce them in Attach.
        // Eligibility extensions must not remove selections or relax enhancement slots.
        if (!checkEnhancements) return null;
        var proposed = army with { Entries = army.Entries.Select(e => attachmentIds.Contains(e.Id) ? e with { AttachedToId = bodyguardId }
            : e.AttachedToId == bodyguardId ? e with { AttachedToId = "" } : e).ToList() };
        foreach (var member in members)
            if (Enhancements.SlotProblem(proposed, member.Entry.Id) is { } problem) return problem;
        return null;
    }
    public static ArmyList Attach(ArmyList army, Dataset? data, string leaderId, string bodyguardId)
    {
        if (army.Entries.All(e => e.Id != leaderId)) throw new InvalidDataException("Leader is no longer in this roster.");
        if (bodyguardId.Length > 0 && Problem(army, data, leaderId, bodyguardId, checkEnhancements: false) is { } problem) throw new InvalidDataException(problem);
        var next = army with { Entries = army.Entries.Select(e => e.Id == leaderId ? e with { AttachedToId = bodyguardId } : e).ToList() };
        if (bodyguardId.Length > 0)
            foreach (var member in next.Entries.Where(e => e.AttachedToId == bodyguardId).OrderBy(e => e.Id, StringComparer.Ordinal))
                if (Enhancements.SlotProblem(next, member.Id) is { } slotProblem) throw Enhancements.Rejection(slotProblem);
        return next;
    }
    public static ArmyList Normalize(ArmyList army) => army with
    {
        Entries = army.Entries.Select(e => e.AttachedToId.Length > 0 && army.Entries.All(b => b.Id != e.AttachedToId)
            ? e with { AttachedToId = "" } : e).ToList()
    };
    public static List<string> Issues(ArmyList army, Dataset? data) => army.Entries.Where(e => e.AttachedToId.Length > 0)
        .Select(e => Problem(army, data, e.Id, e.AttachedToId) is { } problem ? $"{e.Name}: {problem}" : null)
        .OfType<string>().ToList();
}
