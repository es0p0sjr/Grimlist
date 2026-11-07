using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;

internal static class HomeWorkload
{
    internal static async Task Run(ArmySession session, IArmyRepository store, string output)
    {
        await AcceptanceWorkload.Run(session, _ => Task.CompletedTask, -1);
        var full = session.Army;
        List<object> samples = [];
        foreach (var size in new[] { 3, 20 })
        {
            foreach (var old in store.LoadArmies()) store.Delete(old.Id);
            var seed = full with { Entries = full.Entries.Take(size).ToList() };
            session.Open(seed);
            for (var i = 0; i < 9; i++) store.Save(seed.Duplicate());
            foreach (var cached in new[] { false, true })
            for (var i = -1; i < 20; i++)
            {
                var count = session.SummaryCalculationCount;
                var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
                if (cached) session.LoadArmySummaries();
                else foreach (var army in session.LoadArmies())
                    if (army.Id != session.Army.Id) ArmyCalculator.Calculate(army, session.Dataset);
                if (i >= 0) samples.Add(new { Entries = size, SavedArmies = 10, Cached = cached, Ms = clock.Elapsed.TotalMilliseconds,
                    AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, SummaryCalculations = session.SummaryCalculationCount - count });
            }
        }
        File.WriteAllText(Path.Combine(output, "home.json"), DataJson.Write(samples));
    }
}
