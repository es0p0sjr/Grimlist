using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;

internal static class ReferencePreparationWorkload
{
    internal static void Run(string output)
    {
        var data = BootstrapRelease.ReadBundled().Dataset;
        var pairs = data.Units.GroupBy(u => u.Faction).SelectMany(g => g.Take(3))
            .SelectMany(u => (u.Profile?.Weapons ?? []).Where(w => w.Keywords.Length > 0).Take(2).Select(w => (Unit: u, Weapon: w))).ToList();
        foreach (var (unit, weapon) in pairs)
            if (DataJson.Write(WeaponRuleReference.ForWeapon(weapon, unit, data)) != DataJson.Write(WeaponRuleReference.ForWeaponUncached(weapon, unit, data)))
                throw new InvalidDataException("Reference index changed " + unit.Name + "/" + weapon.Name);
        List<object> samples = [];
        for (var round = 0; round < 5; round++)
            foreach (var cached in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var bytes = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew(); var count = 0;
                foreach (var (unit, weapon) in pairs)
                    count += (cached ? WeaponRuleReference.ForWeapon(weapon, unit, data) : WeaponRuleReference.ForWeaponUncached(weapon, unit, data)).Count;
                samples.Add(new { Kind = "weapon-definitions", Round = round, Indexed = cached, Calls = pairs.Count, Definitions = count,
                    ElapsedMs = clock.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - bytes });
            }
        var abilities = data.Units.SelectMany(u => u.Profile?.Abilities ?? []).DistinctBy(a => (a.Name, a.Text)).ToList();
        foreach (var ability in abilities)
            if (DataJson.Write(AttachedEffects.CompileSource(ability)) != DataJson.Write(AttachedEffects.ParseSource(ability)))
                throw new InvalidDataException("Compiled attachment source changed " + ability.Name);
        for (var round = 0; round < 5; round++)
            foreach (var cached in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var bytes = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew(); var count = 0;
                foreach (var ability in abilities)
                    if ((cached ? AttachedEffects.CompileSource(ability) : AttachedEffects.ParseSource(ability)).Leading) count++;
                samples.Add(new { Kind = "attachment-source", Round = round, Indexed = cached, Calls = abilities.Count, Definitions = count,
                    ElapsedMs = clock.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - bytes });
            }
        File.WriteAllText(output, DataJson.Write(new { Pairs = pairs.Count, Factions = pairs.Select(p => p.Unit.Faction).Distinct().Count(), Samples = samples }));
        Console.WriteLine("Reference preparation equivalence and measurements saved: " + output);
    }
}
