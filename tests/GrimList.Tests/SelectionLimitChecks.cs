using System.Text.Json.Nodes;
using GrimList.Core;

internal static class SelectionLimitChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Illegal launcher count accepted"); }
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var index = Array.IndexOf(args, "--reference-catalogue");
        if (index < 0 || index + 1 >= args.Length) return;
        test("Plague Marine validation modifiers become enforced launcher limits for 5, 7 and 10 models", () =>
        {
            var doc = BsDataDocument.Parse(args[index + 1], File.ReadAllText(args[index + 1]));
            doc.Root.Remove("selectionEntries");
            doc.Root["entryLinks"] = new JsonArray(doc.Root["entryLinks"]!.AsArray().Single(n => n?["name"]?.ToString() == "Plague Marines")!.DeepClone());
            var data = BsDataImporter.Import([doc], new("Selection limit fixture", "1", "", "", Edition: "11")).Dataset;
            var unit = data.Units.Single(); var definition = unit.Profile!.Loadout!;
            var launcher = definition.Roles.Single(r => r.Name.Contains("w/ blight launcher", StringComparison.OrdinalIgnoreCase));
            var section = Loadouts.Sections(unit).Single(s => s.RoleIds.Contains(launcher.Id));
            foreach (var size in new[] { 5, 7, 10 })
            {
                var maximum = size / 5;
                Check(launcher.Maximum.At(size) == maximum, $"Incorrect limit for {size} models");
                var entry = new ArmyEntry("marines", unit.Key, unit.Name, size, Loadout: Loadouts.Create(unit, size));
                entry = entry with { Loadout = Loadouts.SetRoleCount(unit, entry, section.Id, launcher.Id, maximum) };
                Check(Loadouts.Weapons(unit, entry.Loadout).Single(w => w.Profile.Name == "Blight launcher").Count == maximum, "Legal launcher choice rejected");
                Reject(() => Loadouts.SetRoleCount(unit, entry, section.Id, launcher.Id, maximum + 1));
                Check(Loadouts.Issues(unit, entry).Count == 0, "Valid squad produces loadout errors");
                if (size == 10)
                {
                    var sorted = entry.Loadout! with { Models = entry.Loadout!.Models.OrderByDescending(m => m.RoleId == launcher.Id).ToList() };
                    entry = entry with { Models = 5, Loadout = Loadouts.Resize(unit, sorted, 5) };
                    Check(Loadouts.RoleCount(entry.Loadout!, launcher.Id) == 1 && Loadouts.Issues(unit, entry).Count == 0, "Shrinking squad retained too many launchers");
                }
            }
            var old = unit with { Profile = unit.Profile with { Loadout = definition with { Roles = definition.Roles.Select(r => r.Id == launcher.Id ? r with { Maximum = new(2) } : r).ToList() } } };
            var repaired = BsDataImporter.RepairRoleLimits(old, doc);
            Check(repaired.Profile!.Loadout!.Roles.Single(r => r.Id == launcher.Id).Maximum.At(5) == 1, "Existing library repair failed");
            Check(DataJson.Write(repaired) == DataJson.Write(BsDataImporter.RepairRoleLimits(repaired, doc)), "Repair is not idempotent");
            Check(repaired.Profile.Loadout.ModelGroups!.Single(g => g.Name == "Special weapons").Maximum.At(5) == 1, "Shared per-five selection cap was lost");
            var invalid = new ArmyEntry("old", old.Key, old.Name, 5, Loadout: Loadouts.Create(old, 5));
            invalid = invalid with { Loadout = Loadouts.SetRoleCount(old, invalid, section.Id, launcher.Id, 2) };
            Check(Loadouts.Issues(repaired, invalid).Count > 0, "Existing illegal selections are not flagged");
            invalid = invalid with { Loadout = Loadouts.SetRoleCount(repaired, invalid, section.Id, launcher.Id, 1) };
            Check(Loadouts.Issues(repaired, invalid).Count == 0, "Existing illegal selection cannot be repaired");
            File.WriteAllText(Path.Combine(root, "plague-marines-limit-fixture.json"), DataJson.Write(data));
        });
    }
}
