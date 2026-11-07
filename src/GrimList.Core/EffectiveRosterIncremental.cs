using System.Runtime.CompilerServices;

namespace GrimList.Core;

public static partial class EffectiveDatasheets
{
    private static readonly ConditionalWeakTable<Dataset, Dictionary<string, WeakReference<RosterEvaluation>>> recentEvaluations = new();
    internal sealed record EvaluationWork(int Projected, int Reused, bool Incremental);
    internal static EvaluationWork Work(ArmyList army, Dataset data) => EvaluateRoster(army, data, "").Work;
    internal static IReadOnlyDictionary<string, EffectiveUnitState?> EvaluateFull(ArmyList army, Dataset data, string excluded = "")
    {
        var context = new RosterEvaluation(army, data); var previous = evaluation; evaluation = context;
        try { context.Evaluate(excluded); return context.States; }
        finally { evaluation = previous; }
    }
    private static RosterEvaluation? Previous(ArmyList army, Dataset data)
    {
        var recent = recentEvaluations.GetOrCreateValue(data);
        lock (recent) return recent.TryGetValue(army.Id, out var reference) && reference.TryGetTarget(out var value) ? value : null;
    }
    private static void Remember(ArmyList army, Dataset data, RosterEvaluation context)
    {
        var recent = recentEvaluations.GetOrCreateValue(data);
        lock (recent)
        {
            if (recent.Count >= 8 && !recent.ContainsKey(army.Id)) recent.Clear();
            recent[army.Id] = new(context);
        }
    }
    private sealed partial class RosterEvaluation
    {
        private ArmyList Army => army;
        private RosterEvaluation? baseline;
        private HashSet<string>? dirty;
        private bool globalDependencies;
        private readonly HashSet<string> rosterReaders = [];
        private readonly Dictionary<string, HashSet<string>> readers = [];
        private string currentEntry = "";
        private int projected;
        private bool incremental;
        private readonly Dictionary<string, EffectiveUnitState?>[] phases = new Dictionary<string, EffectiveUnitState?>[2];
        private readonly HashSet<string>[] phaseEnhancements = new HashSet<string>[2], phaseLegal = new HashSet<string>[2];
        public EvaluationWork Work => new(projected, dirty == null ? 0 : army.Entries.Count - dirty.Count, incremental);
        public bool GlobalDependencies => globalDependencies;
        public void Complete() => baseline = null; // Never retain an edit-history chain.

        public void Observe(EffectCondition condition)
        {
            if (!RuleDependencies.Known(condition)) globalDependencies = true;
            if (condition.Kind is "roster-keyword" or "roster-unit")
            {
                if (currentEntry.Length == 0) globalDependencies = true;
                else rosterReaders.Add(currentEntry);
            }
        }
        public void ObserveRead(string id)
        {
            if (currentEntry.Length == 0 || currentEntry == id) return;
            if (!readers.TryGetValue(id, out var dependants)) readers[id] = dependants = [];
            dependants.Add(currentEntry);
        }
        private T ForEntry<T>(ArmyEntry entry, Func<T> action)
        {
            var previous = currentEntry; currentEntry = entry.Id;
            try { return action(); } finally { currentEntry = previous; }
        }
        private EffectiveUnitState? ProjectEntry(ArmyEntry entry, bool includeEnhancement)
        {
            var previous = currentEntry; currentEntry = entry.Id; projected++;
            try { return ProjectCore(army, entry, data, includeEnhancement); }
            finally { currentEntry = previous; }
        }
        public void Reuse(RosterEvaluation? previous)
        {
            PrepareDependencies();
            if (previous == null || previous.Failed || previous.globalDependencies || globalDependencies || data.SchemaVersion is < 1 or > 4) return;
            var before = previous.Army;
            // These presentation fields do not participate in effective rules. Muster
            // nominations are per-entry; every other context field remains a full guard.
            if ((army with { Entries = before.Entries, Name = before.Name, Disposition = before.Disposition,
                MusterSelections = before.MusterSelections }) != before
                || army.Entries.Select(e => e.Id).Distinct().Count() != army.Entries.Count) return;
            var oldEntries = before.Entries.ToDictionary(e => e.Id);
            var newEntries = army.Entries.ToDictionary(e => e.Id);
            dirty = army.Entries.Where(e => !oldEntries.TryGetValue(e.Id, out var old) || e != old).Select(e => e.Id)
                .Concat(before.Entries.Where(e => !newEntries.ContainsKey(e.Id)).Select(e => e.Id)).ToHashSet();
            foreach (var selection in (army.MusterSelections ?? []).Except(before.MusterSelections ?? [])
                .Concat((before.MusterSelections ?? []).Except(army.MusterSelections ?? []))) dirty.Add(selection.EntryId);
            // Copy labels enter modifier provenance. Adding/removing/reordering copies
            // invalidates their sources even if their selections did not change.
            foreach (var entry in army.Entries)
                if (oldEntries.ContainsKey(entry.Id) && LeaderRules.EntryLabel(before, entry.Id) != LeaderRules.EntryLabel(army, entry.Id)) dirty.Add(entry.Id);
            rosterReaders.UnionWith(previous.rosterReaders.Where(newEntries.ContainsKey));
            foreach (var (id, dependants) in previous.readers)
                readers[id] = dependants.Where(newEntries.ContainsKey).ToHashSet();
            // Any changed contributor may alter a roster predicate, even if it did not
            // match previously. Do not filter on old keywords: grants can change them.
            if (dirty.Count > 0) dirty.UnionWith(rosterReaders);
            var edges = army.Entries.Concat(before.Entries).Where(e => e.AttachedToId.Length > 0).ToArray();
            bool changed;
            do
            {
                changed = false;
                foreach (var edge in edges)
                    if (dirty.Contains(edge.Id) || dirty.Contains(edge.AttachedToId))
                    { changed |= dirty.Add(edge.Id); changed |= dirty.Add(edge.AttachedToId); }
                foreach (var (source, dependants) in readers)
                    if (dirty.Contains(source)) foreach (var id in dependants) changed |= dirty.Add(id);
            } while (changed);
            dirty.IntersectWith(army.Entries.Select(e => e.Id));
            foreach (var id in readers.Keys.Where(id => !newEntries.ContainsKey(id)).ToArray()) readers.Remove(id);
            if (dirty.Count == army.Entries.Count) { dirty = null; return; }
            baseline = previous;
            incremental = true;
        }
        private void PrepareDependencies()
        {
            var shared = ArmyReferences.ForArmy(army, data).Where(c => c.Section is "Army Rules" or "Detachments")
                .Select(c => (Card: c, Plan: RuleDependencies.For(c))).ToArray();
            foreach (var entry in army.Entries)
            {
                void Add(RuleDependencies plan)
                { globalDependencies |= plan.Unknown; if (plan.Roster) rosterReaders.Add(entry.Id); }
                if (data.FindUnit(entry.UnitKey) is { } unit)
                {
                    Add(RuleDependencies.For(unit));
                    foreach (var (card, plan) in shared)
                        if (ArmyReferences.IsCore(card) || Key(card.Faction) == Key(unit.Faction)) Add(plan);
                }
                if (Enhancements.Find(army, data, entry.Enhancement) is { } enhancement) Add(RuleDependencies.For(enhancement));
            }
        }
        private bool NeedsEvaluation(ArmyEntry entry) => dirty == null || dirty.Contains(entry.Id);
        private void BeginPhase(int phase)
        {
            if (baseline == null) return;
            States = States.ToDictionary(p => p.Key, p => dirty!.Contains(p.Key) ? p.Value : baseline.phases[phase][p.Key]);
        }
        private void EndPhase(int phase)
        {
            phases[phase] = States;
            phaseEnhancements[phase] = ActiveEnhancements;
            phaseLegal[phase] = legal;
        }
    }
}
