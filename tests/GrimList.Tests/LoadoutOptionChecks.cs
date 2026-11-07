using GrimList.Core;

internal static class LoadoutOptionChecks
{
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid option count accepted"); }
    private static UnitDefinition Variants()
    {
        var original = LoadoutChecks.Fixture().Units[0];
        var blade = original.Profile!.Loadout!.Roles[0].Groups.First(g => g.Name == "Blade");
        EquipmentGroup Gear(string id, string name) => new("gear:" + id, name,
            [new(id, name, [name], [], new(1), new(1))], new(1), new(1), id);
        List<LoadoutRole> roles = [original.Profile.Loadout.Roles[0],
            new("standard", "Trooper with rifle", new(0), new(9), [Gear("rifle", "Rifle"), blade], true),
            new("launcher", "Trooper with launcher", new(0), new(2), [Gear("launcher", "Blight launcher"), blade]),
            new("spewer", "Trooper with spewer", new(0), new(2), [Gear("spewer", "Spewer"), blade])];
        return original with { Name = "Option counter fixture", Id = "", Aliases = [], Profile = original.Profile with
        {
            Weapons = [.. original.Profile.Weapons!, new("Blight launcher", "Ranged", "24\"", "D3", "3+", "6", "-1", "2"), new("Spewer", "Ranged", "12\"", "D6", "—", "5", "-1", "1")],
            Loadout = new(roles, [], [], [new("Special weapons", ["launcher", "spewer"], new(0), new(2)), new("Troopers", ["standard", "launcher", "spewer"], new(4), new(9))])
        } };
    }

    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var unit = Variants(); var section = Loadouts.Sections(unit).Single(s => s.RoleIds.Contains("launcher"));
        ArmyEntry Entry() => new("counter", unit.Key, unit.Name, 10, Loadout: Loadouts.Create(unit, 10));
        test("Option counters replace standard bundles, enforce caps, and preserve the champion", () =>
        {
            var entry = Entry(); var champion = DataJson.Write(entry.Loadout!.Models[0]);
            entry = entry with { Loadout = Loadouts.SetRoleCount(unit, entry, section.Id, "launcher", 2) };
            Equal(2, Loadouts.RoleCount(entry.Loadout!, "launcher")); Equal(7, Loadouts.RoleCount(entry.Loadout!, "standard"));
            Equal(10, entry.Loadout!.Models.Count); Equal(champion, DataJson.Write(entry.Loadout.Models[0]));
            Equal(2, Loadouts.Weapons(unit, entry.Loadout).Single(w => w.Profile.Name == "Blight launcher").Count);
            Reject(() => Loadouts.SetRoleCount(unit, entry, section.Id, "launcher", 3));
            Reject(() => Loadouts.SetRoleCount(unit, entry, section.Id, "spewer", 1));
            entry = entry with { Loadout = Loadouts.SetRoleCount(unit, entry, section.Id, "launcher", 1) };
            entry = entry with { Loadout = Loadouts.SetRoleCount(unit, entry, section.Id, "spewer", 1) };
            Equal(8, Loadouts.Weapons(unit, entry.Loadout).Single(w => w.Profile.Name == "Rifle").Count);
            Equal(0, Loadouts.Issues(unit, entry).Count);
            entry = entry with { Loadout = Loadouts.SetRoleCount(unit, entry, section.Id, "launcher", 0) };
            Equal(8, Loadouts.RoleCount(entry.Loadout!, "standard"));
            Reject(() => Loadouts.SetRoleCount(unit, entry, section.Id, "launcher", -1));
        });
        test("Grouped equipment counters respect shared limits and keep unrelated upgrades", () =>
        {
            var simple = LoadoutChecks.Fixture().Units[0];
            var entry = new ArmyEntry("group", simple.Key, simple.Name, 10, Loadout: Loadouts.Create(simple, 10));
            entry = entry with { Loadout = Loadouts.Choose(simple, entry, 0, "ranged", "heavy", 1) };
            entry = entry with { Loadout = Loadouts.SetOptionCount(simple, entry, "marine", "ranged", "heavy", 1) };
            Reject(() => Loadouts.SetOptionCount(simple, entry, "marine", "ranged", "heavy", 2));
            entry = entry with { Loadout = Loadouts.SetOptionCount(simple, entry, "marine", "ranged", "plasma", 8) };
            Reject(() => Loadouts.SetOptionCount(simple, entry, "marine", "ranged", "plasma", 9));
            entry = entry with { Loadout = Loadouts.SetOptionCount(simple, entry, "marine", "ranged", "heavy", 0) };
            Equal(8, Loadouts.OptionCount(entry.Loadout!, "marine", "ranged", "plasma"));
            Equal(1, Loadouts.OptionCount(entry.Loadout!, "marine", "ranged", "rifle"));
            Equal(1, Loadouts.Weapons(simple, entry.Loadout).Single(w => w.Profile.Name == "Heavy gun").Count);
            Equal(10, Loadouts.Weapons(simple, entry.Loadout).Single(w => w.Profile.Name == "Blade").Count);
        });
        var fixture = LoadoutChecks.Fixture(); fixture = fixture with { Units = [.. fixture.Units, unit] };
        var index = Array.IndexOf(args, "--loadout-catalogue");
        if (index >= 0 && index + 1 < args.Length)
        {
            var catalogue = DataJson.Read<Dataset>(File.ReadAllText(args[index + 1]));
            test("Imported Plague Marine session counters enforce size and shared special-weapon caps", () =>
            {
                var marines = catalogue.Units.Single(u => u.Faction == "Death Guard" && u.Name == "Plague Marines");
                var definition = Loadouts.Definition(marines);
                var launcher = definition.Roles.Single(r => r.Name.Contains("w/ blight launcher", StringComparison.OrdinalIgnoreCase));
                var section = Loadouts.Sections(marines).Single(s => s.RoleIds.Contains(launcher.Id));
                var pool = definition.ModelGroups!.Single(g => g.Name == "Special weapons");
                var options = definition.Roles.Where(r => pool.RoleIds.Contains(r.Id)).ToList();
                using var store = WindowsServices.OpenStore(Path.Combine(root, "plague-marine-counters"));
                store.Import(catalogue with { Units = [marines] });
                var session = new ArmySession(store); session.Create(marines.Faction, "Grouped equipment", 2000, []);
                session.AddUnit(marines.Key, 10); var id = session.Context.EntryId!;
                LoadoutCountAction Add(string role) => new(section.Id, role, "", "", 1);
                var champion = DataJson.Write(session.SelectedEntry!.Loadout!.Models.First(m => !section.RoleIds.Contains(m.RoleId)));
                session.AdjustEquipment(id, Add(launcher.Id)); session.AdjustEquipment(id, Add(launcher.Id));
                Equal(false, ArmySession.CanAdjust(marines, session.SelectedEntry, Add(launcher.Id)));
                var before = DataJson.Write(session.Army);
                Reject(() => session.AdjustEquipment(id, Add(launcher.Id)));
                Equal(before, DataJson.Write(session.Army));
                session.AdjustEquipment(id, Add(options[0].Id)); session.AdjustEquipment(id, Add(options[1].Id));
                foreach (var option in options) Equal(false, ArmySession.CanAdjust(marines, session.SelectedEntry, Add(option.Id)));
                Reject(() => session.AdjustEquipment(id, Add(options[2].Id)));
                Equal(2, session.SelectedEntry!.Loadout!.Models.Count(m => pool.RoleIds.Contains(m.RoleId)));
                Equal(champion, DataJson.Write(session.SelectedEntry.Loadout.Models.First(m => !section.RoleIds.Contains(m.RoleId))));
                Equal(0, Loadouts.Issues(marines, session.SelectedEntry).Count);
                session.Undo(); Equal(true, ArmySession.CanAdjust(marines, session.SelectedEntry, Add(options[2].Id)));
                session.Redo(); Equal(false, ArmySession.CanAdjust(marines, session.SelectedEntry, Add(options[2].Id)));
                var saved = DataJson.Write(session.SelectedEntry.Loadout); session.Save(); session.Open(session.Army.Id);
                Equal(saved, DataJson.Write(session.SelectedEntry!.Loadout));
                session.ResetLoadout(id); session.ResizeUnit(id, 5);
                session.AdjustEquipment(id, Add(launcher.Id));
                Equal(false, ArmySession.CanAdjust(marines, session.SelectedEntry, Add(launcher.Id)));
                session.AdjustEquipment(id, Add(options[0].Id));
                Equal(false, ArmySession.CanAdjust(marines, session.SelectedEntry, Add(options[1].Id)));
                Equal(0, Loadouts.Issues(marines, session.SelectedEntry!).Count);
            });
            var blightlords = catalogue.Units.Single(u => u.Name == "Blightlord Terminators");
            test("Imported Blightlord option counters stop at two Blight launchers", () =>
            {
                var definition = blightlords.Profile!.Loadout!;
                var role = definition.Roles.Single(r => r.Name.Contains("w/ blight launcher", StringComparison.OrdinalIgnoreCase));
                var pool = Loadouts.Sections(blightlords).Single(s => s.RoleIds.Contains(role.Id));
                var entry = new ArmyEntry("real", blightlords.Key, blightlords.Name, 10, Loadout: Loadouts.Create(blightlords, 10));
                entry = entry with { Loadout = Loadouts.SetRoleCount(blightlords, entry, pool.Id, role.Id, 2) };
                Equal(2, Loadouts.Weapons(blightlords, entry.Loadout).Single(w => w.Profile.Name == "Blight launcher").Count);
                Reject(() => Loadouts.SetRoleCount(blightlords, entry, pool.Id, role.Id, 3));
                Equal(0, Loadouts.Issues(blightlords, entry).Count);
                entry = entry with { Loadout = Loadouts.SetRoleCount(blightlords, entry, pool.Id, role.Id, 1) };
                Equal(8, Loadouts.RoleCount(entry.Loadout!, pool.DefaultRoleId));
            });
            fixture = fixture with { Units = [.. fixture.Units, blightlords] };
        }
        fixture.Validate();
        File.WriteAllText(Path.Combine(root, "loadout-fixture.json"), DataJson.Write(fixture));
    }
}
