using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;

// Linked only into the opt-in Android performance build. Production never runs it.
public static class AcceptanceWorkload
{
    public record Sample(string Operation, int Entries, double CommandMs, double UsableMs, long AllocatedBytes, long ManagedBytes);
    public static async Task<List<Sample>> Run(ArmySession session, Func<string, Task> present, int iterations = 20,
        int? onlyEntries = null, int warmups = 1, Action<ArmySession, string, int>? completed = null)
    {
        var data = session.DatasetForArmy(ArmyList.New("Death Guard"))!;
        UnitDefinition Unit(string name) => data.Units.Single(u => u.Faction == "Death Guard" && u.Name == name);
        var marine = Unit("Plague Marines"); var guard = Unit("Deathshroud Terminators"); var leader = Unit("Lord of Contagion");
        ArmyEntry Entry(UnitDefinition u, int size, string id) => new(id, u.Key, u.Name, size, Loadout: Loadouts.Create(u, size));
        var small = ArmyList.New("Death Guard") with { Id = session.LoadArmies().FirstOrDefault(a => a.Name == "Disposable performance workload")?.Id ?? "performance-fixture",
            Name = "Disposable performance workload", Limit = 2000,
            Detachments = ["Virulent Vectorium"], Entries = [Entry(marine, 10, "marines"), Entry(guard, 3, "guard"), Entry(leader, 1, "leader")] };
        var extra = data.Units.Where(u => u.Faction == "Death Guard" && !u.Name.Contains("variant") && !PricingRepair.IsCrucible(u)
            && u.Options.Count > 0 && small.Entries.All(e => e.UnitKey != u.Key)).Take(17).Select((u, i) => Entry(u, u.Options[0].Models, "extra" + i)).ToList();
        var role = marine.Profile!.Loadout!.Roles.Single(r => r.Name.Contains("Blight launcher", StringComparison.OrdinalIgnoreCase));
        var section = Loadouts.Sections(marine).Single(s => s.RoleIds.Contains(role.Id));
        List<Sample> samples = [];
        foreach (var roster in new[] { small, small with { Entries = [.. small.Entries, .. extra] } })
        {
            if (onlyEntries != null && roster.Entries.Count != onlyEntries) continue;
            await session.RunAsync(s => s.Open(roster));
            var card = Enhancements.Options(session.Army, data, leader).First();
            var enhancement = new EnhancementSelection(card.Detachment, card.Name);
            foreach (var operation in new[] { "add", "equipment", "enhancement", "attachment", "detachment", "home" })
            {
                for (var i = -warmups; i < iterations; i++)
                {
                    await session.RunAsync(s => { s.Open(roster); s.SelectEntry(operation == "equipment" ? "marines" : "leader"); });
                    await present(operation);
                    // Precise sampling triggered a Mono thread-suspend assertion on
                    // the phone. Android allocation totals are approximate.
                    var allocated = GC.GetTotalAllocatedBytes(!OperatingSystem.IsAndroid()); var clock = Stopwatch.StartNew();
                    await session.RunAsync(s =>
                    {
                        switch (operation)
                        {
                            case "add": s.AddUnit(marine.Key, 5); break;
                            case "equipment": s.AdjustEquipment("marines", new(section.Id, role.Id, "", "", 1)); break;
                            case "enhancement": s.SelectEnhancement("leader", enhancement); break;
                            case "attachment": s.Attach("leader", "guard"); break;
                            case "detachment": s.RemoveDetachment("Virulent Vectorium"); break;
                        }
                    });
                    var command = clock.Elapsed.TotalMilliseconds;
                    await present(operation);
                    if (i >= 0) samples.Add(new(operation, roster.Entries.Count, command, clock.Elapsed.TotalMilliseconds,
                        GC.GetTotalAllocatedBytes(!OperatingSystem.IsAndroid()) - allocated, GC.GetTotalMemory(false)));
                    if (i >= 0) completed?.Invoke(session, operation, i);
                }
            }
        }
        return samples;
    }
}
