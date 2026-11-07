using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;

// Same correctness/measurement workload on Windows and the isolated Android app.
public static class FactionLoadingWorkload
{
    public record Measurement(string Mode, string Operation, double Milliseconds, long AllocatedBytes, int LoadedUnits, int FullReads, int ShardReads);
    public record Report(int Checks, int Factions, int TotalUnits, double CacheOpenMs, bool CacheBuilt, List<Measurement> Measurements);
    public static Report Run(IArmyRepository store, Func<IArmyRepository, LoadedLibrary>? loader = null, string variant = "")
    {
        ArmySession Session(bool lazy) => new(store, useFactionLoading: lazy, libraryLoader: loader, libraryVariant: variant);
        var checks = 0;
        void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); checks++; }
        var eager = Session(false); var full = eager.Dataset!;
        var watch = Stopwatch.StartNew(); var lazy = Session(true); var firstCache = watch.Elapsed.TotalMilliseconds;
        var cacheBuilt = lazy.FactionLoading!.FullReads > 0;
        Check(lazy.Dataset!.Units.Count == 0, "Lazy startup loads no unit profiles");
        Check(lazy.Factions.SequenceEqual(eager.Factions), "Faction names match");
        foreach (var faction in eager.Factions)
        {
            var army = ArmyList.New(faction) with { Game = full.Game };
            var view = lazy.DatasetForArmy(army)!;
            var detachments = (full.Detachments ?? []).Where(d => d.Faction == faction).Select(d => d.Name).Distinct().ToArray();
            foreach (var selected in new[] { Array.Empty<string>(), detachments }.Concat(detachments.Select(d => new[] { d })))
            {
                var draft = army with { Detachments = selected.ToList() };
                var expected = AlliedRules.Library(draft, full); var actual = AlliedRules.Library(draft, view);
                Check(expected.Select(u => u.Key).SequenceEqual(actual.Select(u => u.Key)), "Library candidates: " + faction + "/" + string.Join(",", selected));
                Check(DataJson.Write(DetachmentRules.Options(draft, full)) == DataJson.Write(DetachmentRules.Options(draft, view)), "Detachment options: " + faction);
                Check(DataJson.Write(ArmyReferences.ForArmy(draft, full)) == DataJson.Write(ArmyReferences.ForArmy(draft, view)), "Army references: " + faction);
            }
            foreach (var unit in AlliedRules.Library(army, full))
                Check(DataJson.Write(unit) == DataJson.Write(view.FindUnit(unit.Key)), "Unit profile: " + unit.Name);
            var roster = army with { Entries = full.Units.Where(u => u.Faction == faction && u.Options.Count > 0).Take(5)
                .Select((u, i) => new ArmyEntry("parity-" + i, u.Key, u.Name, u.Options[0].Models, Loadout: Loadouts.Create(u, u.Options[0].Models))).ToList() };
            Check(DataJson.Write(ArmyCalculator.Calculate(roster, full)) == DataJson.Write(ArmyCalculator.Calculate(roster, view)), "Roster calculations: " + faction);
            var first = roster.Entries.FirstOrDefault();
            if (first != null && full.FindUnit(first.UnitKey) is { } firstUnit)
                foreach (var weapon in firstUnit.Profile?.Weapons ?? [])
                    Check(DataJson.Write(WeaponRuleReference.ForWeapon(weapon, firstUnit, full)) == DataJson.Write(WeaponRuleReference.ForWeapon(weapon, view.FindUnit(first.UnitKey), view)), "Shared weapon rules: " + faction);
        }
        Check(DataJson.Write(lazy.LoadFullDataset()) == DataJson.Write(full), "Full export parity");
        var fixtures = new[] { "Death Guard", "Orks", "Adeptus Mechanicus" }.Select((faction, index) =>
            ArmyList.New(faction) with { Id = "faction-benchmark-" + index, Name = "Faction benchmark " + index,
                Detachments = (full.Detachments ?? []).Where(d => d.Faction == faction).Take(1).Select(d => d.Name).ToList(),
                Entries = full.Units.Where(u => u.Faction == faction && u.Options.Count > 0 && !PricingRepair.IsReference(u)).Take(5)
                    .Select((u, i) => new ArmyEntry("faction-benchmark-" + index + "-" + i, u.Key, u.Name, u.Options[0].Models, Loadout: Loadouts.Create(u, u.Options[0].Models))).ToList() }).ToArray();
        foreach (var army in fixtures) store.Save(army);
        var summaries = lazy.LoadArmySummaries();
        foreach (var summary in summaries)
        {
            var result = ArmyCalculator.Calculate(summary.Army, full);
            Check(summary.Result == new ArmySummary(result.Total, result.Complete, result.Validated), "Home summary parity");
        }
        var warm = Session(true); warm.LoadArmySummaries();
        Check(warm.FactionLoading!.FullReads == 0 && warm.FactionLoading.ShardReads == 0 && warm.SummaryCalculationCount == 0, "Warm Home must not load factions");
        var factions = eager.Factions.Count; var units = full.Units.Count;
        // Baseline/full datasets are not held across the measurement loop.
        eager = null!; lazy = null!; warm = null!; full = null!;
        List<Measurement> measurements = [];
        for (var i = -1; i < 5; i++)
            foreach (var mode in i % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                ArmySession? session = null;
                void Measure(string operation, Action action)
                {
                    var allocated = GC.GetTotalAllocatedBytes(!OperatingSystem.IsAndroid()); var clock = Stopwatch.StartNew();
                    action(); var ms = clock.Elapsed.TotalMilliseconds;
                    if (i >= 0) measurements.Add(new(mode ? "faction" : "full", operation, ms,
                        GC.GetTotalAllocatedBytes(!OperatingSystem.IsAndroid()) - allocated, session!.Dataset?.Units.Count ?? 0,
                        session.FactionLoading?.FullReads ?? 1, session.FactionLoading?.ShardReads ?? 0));
                }
                Measure("startup-home", () => { session = Session(mode); session.LoadArmySummaries(); });
                Measure("open-army", () => session!.Open(fixtures[0]));
                Measure("switch-faction", () => session!.Open(fixtures[1]));
                Measure("return-cached-faction", () => session!.Open(fixtures[0]));
                session = null;
            }
        return new(checks, factions, units, firstCache, cacheBuilt, measurements);
    }
}
