using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;

// Diagnostic WPF workload matching the October 13 navigation/ten-army setup.
public static class WindowsWorkload
{
    public record Sample(string Operation, int Entries, double CommandMs, double UsableMs, long AllocatedBytes, long ManagedBytes);
    public static async Task<List<Sample>> Run(ArmySession session, Func<string, Task> present, int iterations = 20)
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
            await session.RunAsync(s => {
                foreach (var saved in s.LoadArmies()) s.DeleteArmy(saved.Id);
                s.Open(roster);
                for (var n = 0; n < 9; n++) s.DuplicateArmy(roster);
            });
            var card = Enhancements.Options(session.Army, data, leader).First();
            var enhancement = new EnhancementSelection(card.Detachment, card.Name);
            foreach (var operation in new[] { "open", "datasheet", "add", "equipment", "enhancement", "attachment", "detachment", "home" })
            {
                Console.WriteLine($"Measuring {roster.Entries.Count} entries: {operation}");
                for (var i = -1; i < iterations; i++) // One unrecorded warm-up per operation.
                {
                    await session.RunAsync(s => { s.Open(roster); s.SelectEntry(operation == "equipment" ? "marines" : "leader"); });
                    await present(operation == "open" ? "home" : operation is "home" or "datasheet" ? "add" : operation);
                    // Precise sampling triggered a Mono thread-suspend assertion on
                    // the phone. Android allocation totals are approximate.
                    var allocated = GC.GetTotalAllocatedBytes(!OperatingSystem.IsAndroid()); var clock = Stopwatch.StartNew();
                    await session.RunAsync(s =>
                    {
                        switch (operation)
                        {
                            case "open": s.Open(roster.Id); break;
                            case "datasheet": s.SelectEntry("marines"); break;
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
                }
            }
        }
        return samples;
    }
}
