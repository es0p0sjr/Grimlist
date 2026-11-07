namespace GrimList.Core;

public static partial class EffectiveDatasheets
{
    // Shared dependency evaluator: choices, enhancements and legal attachments can
    // change effective datasheets. This is not a Stratagem/reference-card filter.
    // Preserve for attachment work: immutable snapshots, complete evaluation passes,
    // convergence handling and the exclusion of a selection's own eligibility grant.
    // Regression coverage: EffectiveDatasheetChecks and AttachmentRecipientChecks.
    // Evaluation is synchronous and scoped to one immutable proposed roster. Recursive
    // consumers (attachment constraints/eligibility) read the previous complete pass,
    // never a half-updated unit or a cached result from another user action.
    [ThreadStatic] private static RosterEvaluation? evaluation;
    // ArmySession publishes new records for every edit/undo/redo and new Dataset
    // snapshots for imports. Weak keys reuse work only within those immutable inputs.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ArmyList,
        System.Runtime.CompilerServices.ConditionalWeakTable<Dataset, Dictionary<string, RosterEvaluation>>> evaluations = new();

    private static RosterEvaluation EvaluateRoster(ArmyList army, Dataset data, string excludedEnhancement)
    {
        var cache = evaluations.GetOrCreateValue(army).GetOrCreateValue(data);
        lock (cache)
        {
            if (cache.TryGetValue(excludedEnhancement, out var ready)) return ready;
            var context = new RosterEvaluation(army, data);
            if (excludedEnhancement.Length == 0
#if PERFORMANCE_CHECKS
                && ControlledPerformance.ReuseEvaluations
#endif
                ) context.Reuse(Previous(army, data));
            var previous = evaluation;
            evaluation = context;
            try
            {
                context.Evaluate(excludedEnhancement);
                if (context.Work.Incremental && (context.GlobalDependencies || context.Failed))
                {
                    context = new RosterEvaluation(army, data); evaluation = context;
                    context.Evaluate(excludedEnhancement);
                }
                context.Complete(); cache.Add(excludedEnhancement, context);
                if (excludedEnhancement.Length == 0) Remember(army, data, context);
                return context;
            }
            finally { evaluation = previous; }
        }
    }

    public static UnitDefinition Preview(ArmyList army, Dataset? data, UnitDefinition unit)
        => Project(army, new ArmyEntry("effective-preview", unit.Key, unit.Name, unit.Options.FirstOrDefault()?.Models ?? 1), data)?.Unit ?? unit;

    public static EffectiveUnitState? Project(ArmyList army, ArmyEntry entry, Dataset? data, bool includeEnhancement = true)
    {
        if (data?.FindUnit(entry.UnitKey) == null) return null;
        if (evaluation != null) { evaluation.ObserveRead(entry.Id); return evaluation.States.GetValueOrDefault(entry.Id); }
        var proposed = army.Entries.Any(e => ReferenceEquals(e, entry)) ? army : army.Entries.Any(e => e.Id == entry.Id) ? army with {
            Entries = army.Entries.Select(e => e.Id == entry.Id ? entry : e).ToList() }
            : army with { Entries = army.Entries.Append(entry).ToList() };
        // Keep malformed saved rosters reviewable; validation reports duplicate IDs.
        if (proposed.Entries.Select(e => e.Id).Distinct().Count() != proposed.Entries.Count)
            proposed = proposed with { Entries = proposed.Entries.DistinctBy(e => e.Id).ToList() };
        return EvaluateRoster(proposed, data, includeEnhancement ? "" : entry.Id).States.GetValueOrDefault(entry.Id);
    }

    // null = no structured permission, so LeaderRules may use prose; false = source
    // permissions exist but none allow this recipient. Keep these outcomes distinct.
    internal static bool? AttachmentAllowed(ArmyList army, Dataset data, ArmyEntry owner, UnitDefinition ownerUnit,
        ArmyEntry target, UnitDefinition targetUnit)
    {
        var permissions = ownerUnit.Profile?.Semantics?.Attachments;
        if (permissions == null || permissions.Count == 0) return null;
        if (evaluation == null)
        {
            var context = EvaluateRoster(army, data, "");
            evaluation = context;
            try
            {
                return AttachmentAllowed(army, data, owner, context.States.GetValueOrDefault(owner.Id)?.Unit ?? ownerUnit,
                    target, context.States.GetValueOrDefault(target.Id)?.Unit ?? targetUnit);
            }
            finally { evaluation = null; }
        }
        bool? Check(EffectCondition condition)
        {
            if (condition.Kind == "owner") return Test(condition.Children?.SingleOrDefault(), army, owner, ownerUnit);
            if (condition.Kind is "and" or "or")
            {
                var values = (condition.Children ?? []).Select(Check).ToList();
                return condition.Kind == "and" ? values.Contains(false) ? false : values.Contains(null) ? null : true
                    : values.Contains(true) ? true : values.Contains(null) ? null : false;
            }
            if (condition.Kind == "not") return !Check(condition.Children!.Single());
            return Test(condition, army, target, targetUnit);
        }
        // Source association predicates describe the complete recipient set, including
        // both base and enhancement-granted branches; do not OR them with stale prose.
        return permissions.Any(p => Check(p.Condition) == true);
    }

    private sealed partial class RosterEvaluation(ArmyList army, Dataset data)
    {
        public Dataset Dataset => data;
        public bool Failed { get; private set; }
        public HashSet<string> ActiveEnhancements { get; private set; } = [];
        public Dictionary<string, EffectiveUnitState?> States { get; private set; } = army.Entries.ToDictionary(e => e.Id,
            e => data.FindUnit(e.UnitKey) is { } unit ? new EffectiveUnitState(unit, MusterEffects.MusterUnit(army, e, data) ?? unit, []) : null);
        private HashSet<string> legal = [];
        private bool relationships;
        public IEnumerable<DatasheetModifier> AttachedModifiers(ArmyEntry entry)
        {
            if (!relationships || Failed) return [];
            var root = legal.Contains(entry.Id) ? entry.AttachedToId : entry.Id;
            List<DatasheetModifier> modifiers = [];
            var members = army.Entries.Where(e => e.Id == root || legal.Contains(e.Id) && e.AttachedToId == root).ToList();
            // Unit-wide grants include the owner even without an attached bodyguard.
            if (members.Count > 0)
                foreach (var owner in members)
                {
                    var source = States.GetValueOrDefault(owner.Id)?.Unit;
                    var target = States.GetValueOrDefault(entry.Id)?.Unit;
                    if (source == null || target == null) continue;
                    var structural = (source.Profile?.Semantics?.Modifiers ?? []).Select(ModelStatModifiers.Normalize).Where(m => m.Recipient != null).ToList();
                    if (ActiveEnhancements.Contains(owner.Id) && Enhancements.Find(army, data, owner.Enhancement) is { } enhancement)
                        structural.AddRange(CompiledRuleText.ForCard(enhancement).Where(m => m.Recipient != null));
                    var prose = (States.GetValueOrDefault(owner.Id)?.EffectiveAbilities ?? []).DistinctBy(a => (a.Name, a.Text))
                        .SelectMany(CompiledRuleText.Attached).Where(p => !structural.Any(m => m.Target == p.Target
                            && Key(m.Value) == Key(p.Value) && m.Operation == p.Operation));
                    foreach (var modifier in structural.Concat(prose))
                    {
                        var recipient = modifier.Recipient!;
                        bool? inScope = recipient.Scope switch
                        {
                            "attached-unit" => true,
                            // The source's recipient predicate defines which attached
                            // models qualify. Support Characters also lead for these
                            // grants; the composition-slot label is not a recipient rule.
                            "led-unit" => legal.Contains(owner.Id),
                            "leaders" => owner.Id != entry.Id && legal.Contains(entry.Id),
                            "other-members" => owner.Id != entry.Id,
                            "bodyguard" => legal.Contains(owner.Id) && entry.Id == root,
                            "self" => owner.Id == entry.Id,
                            _ => null
                        };
                        var sourceActive = Test(modifier.Condition, army, owner, source);
                        var targetActive = Test(recipient.Predicate, army, entry, target);
                        if (inScope == false || sourceActive == false || targetActive == false) continue;
                        var active = inScope == true && sourceActive == true && targetActive == true;
                        modifiers.Add(modifier with { Id = "from/" + owner.Id + "/" + modifier.Id,
                            Source = LeaderRules.EntryLabel(army, owner.Id) + " — " + modifier.Source, Recipient = null,
                            Condition = active ? new("constant", "1") : new("unknown", Label:
                                "Unresolved attachment grant: " + recipient.SourceSelector + " " + modifier.Condition?.Label) });
                    }
                }
            // Include the existing source-driven size/leading clauses in the same
            // keyword merge. Native UIs only add explicitly selected target effects.
            foreach (var effect in AttachedEffects.ForUnit(army, data, entry).Where(e => e.Adjustment is { RequiresAfflicted: false, Keywords.Count: > 0 }))
                modifiers.Add(new("attached-keywords/" + Key(effect.Ability.Name), effect.Ability.Name, "weapon", "Keywords", "add",
                    string.Join(", ", effect.Adjustment!.Keywords), WeaponType: effect.Adjustment.Scope));
            return modifiers;
        }
        public UnitDefinition ConditionUnit(ArmyEntry entry, UnitDefinition current) => States.GetValueOrDefault(entry.Id)?.Unit ?? current;
        private bool Matches(UnitDefinition unit, string id, string label) => id is "any" or "unit"
            || (unit.Aliases ?? []).Any(a => a.EndsWith(":" + id, StringComparison.Ordinal))
            || label.Length > 0 && MatchesKeywords((unit.Profile?.Keywords ?? []).Append(unit.Name), label);
        public int? Count(EffectCondition condition, ArmyEntry entry, UnitDefinition unit)
        {
            if (condition.Kind == "catalogue") return (data.FactionContexts ?? []).Any(c => c.Faction == army.Faction && c.CatalogueId == condition.Value) ? 1 : 0;
            IEnumerable<ArmyEntry> members;
            if (condition.Kind.StartsWith("roster-", StringComparison.Ordinal)) members = army.Entries;
            else
            {
                if (!relationships) return 0;
                var root = legal.Contains(entry.Id) ? entry.AttachedToId : entry.Id;
                members = army.Entries.Where(e => e.Id == root || legal.Contains(e.Id) && e.AttachedToId == root);
                if (condition.Kind == "association") members = members.Where(e => e.Id != entry.Id);
            }
            if (condition.Kind == "attached-models") return members.Sum(e => e.Models);
            if (condition.Kind == "attached-enhancement") return members.Count(e => ActiveEnhancements.Contains(e.Id) && e.Enhancement is { } selected && Key(selected.Name) == Key(condition.Value));
            return members.Count(e => States.GetValueOrDefault(e.Id)?.Unit is { } member
                && Matches(member, condition.Value, condition.Label));
        }
        public void Evaluate(string excludedEnhancement)
        {
            // Establish permissions from roster selections before activating any attached
            // benefits. This prevents a circular benefit from legalising its own attachment.
            for (var phase = 0; phase < 2; phase++)
            {
                relationships = phase == 1;
                BeginPhase(phase);
                HashSet<Dictionary<string, EffectiveUnitState?>> seen = new(EffectiveStateComparer.Instance);
                var limit = Math.Max(16, army.Entries.Count * 4);
                var converged = false;
                for (var pass = 0; pass < limit; pass++)
                {
                    var before = States;
                    if (!seen.Add(before)) break;
                    // Test a bearer's selection without that selection's own effects.
                    // Otherwise an enhancement could grant the keyword that makes
                    // itself eligible, and attachment changes could keep it active.
                    ActiveEnhancements = army.Entries.Where(e => NeedsEvaluation(e) ? e.Id != excludedEnhancement && Enhancements.Find(army, data, e.Enhancement) is { } card
                        && ForEntry(e, () => Enhancements.Eligible(army, ProjectEntry(e, false)?.Unit, card, data)) : baseline!.phaseEnhancements[phase].Contains(e.Id)).Select(e => e.Id).ToHashSet();
                    if (relationships) legal = army.Entries.Where(e => NeedsEvaluation(e) ? e.AttachedToId.Length > 0
                        && ForEntry(e, () => LeaderRules.Problem(army, data, e.Id, e.AttachedToId, false)) == null : baseline!.phaseLegal[phase].Contains(e.Id)).Select(e => e.Id).ToHashSet();
                    var next = army.Entries.ToDictionary(e => e.Id, e => NeedsEvaluation(e) ? ProjectEntry(e, e.Id != excludedEnhancement) : before[e.Id]);
                    States = next;
                    if (EffectiveStateComparer.Instance.Equals(before, next)) { converged = true; break; }
                }
                if (!converged)
                {
                    Failed = true; legal.Clear();
                    States = army.Entries.ToDictionary(e => e.Id, e => ProjectCore(army, e, data, e.Id != excludedEnhancement));
                    States = States.ToDictionary(p => p.Key, p => p.Value?.Unit.Profile == null ? p.Value : p.Value with {
                        Unit = p.Value.Unit with { Profile = p.Value.Unit.Profile with { Warnings = (p.Value.Unit.Profile.Warnings ?? [])
                            .Append("Conditional rules could not converge; dependent effects are disabled. Review conflicting source conditions.").ToList() } } });
                    return;
                }
                EndPhase(phase);
            }
        }
    }
}
