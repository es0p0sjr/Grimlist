using System.Xml.Linq;
using GrimList.Application;
using GrimList.Core;

internal static class EnhancementWeaponChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        var source = new SourceInfo("BSData · All armies", "0fb7e6ac9cb5289c4cfe519029787d34eca95032", "2026-10-14",
            "https://github.com/BSData/wh40k-11e/tree/0fb7e6ac9cb5289c4cfe519029787d34eca95032", Edition: "11",
            ContentHash: "76e61be4e86ea63030c93ba13fb57c6c67f76882328802ebb509f566ed27d270");
        var doc = BsDataDocument.Parse("imperiums-sword.json", File.ReadAllText("tests/fixtures/bsdata/imperiums-sword.json"));
        var card = BsDataImporter.ImportEnhancementReferences([doc], source).Single();
        var legacy = card with { Weapons = null, Details = null, Text = "Melee\n6\n2+\n7\n-3\n3\n" };
        var sword = new WeaponProfile("Imperium's Sword", "Melee", "Melee", "6", "2+", "7", "-3", "3");
        var unit = new UnitDefinition("Space Marines", "Captain", [new(1, 80)], Category: "Characters", Id: "captain",
            Profile: new(Characteristics: new() { ["M"] = "6", ["T"] = "4", ["Sv"] = "3+", ["W"] = "5", ["LD"] = "6+", ["OC"] = "1" },
                Weapons: [new("Chainsword", "Melee", "Melee", "5", "2+", "4", "-1", "1")], Keywords: ["Character", "Infantry"]));
        var loadout = new UnitLoadout([], [new("manual", "manual:Chainsword", 1)], true);
        var army = ArmyList.New(unit.Faction) with { Detachment = card.Detachment, Entries = [
            new("bearer", unit.Key, unit.Name, 1, Loadout: loadout), new("other", unit.Key, unit.Name, 1, Loadout: loadout)] };
        Dataset Data(ReferenceCard enhancement) => new(source, [unit], ReferenceCards: [enhancement]);
        static List<WeaponProfile> Weapons(ArmyList roster, Dataset data, string id = "bearer")
            => EffectiveDatasheets.Project(roster, roster.Entries.Single(e => e.Id == id), data)!.EffectiveWeaponProfiles;
        test("Imperium's Sword imports its actual melee profile and preserves source identity", () =>
        {
            Check(card.Weapons?.Count == 1 && card.Weapons[0] with { SourceId = null } == sword, "Source profile fields lost");
            Check(card.Weapons![0].SourceId == "6dc6-4f92-c8b4-ebda" && card.Details?.SourceCardId == "342a-2485-9ec0-b0a3", "Source identity lost");
            Check(card.DisplayText.Contains("in addition") && !card.DisplayText.Contains("\n6\n"), "Raw stats still displayed");
            Check(DataJson.Read<ReferenceCard>(DataJson.Write(card)).Weapons![0] == card.Weapons[0], "Library serialization lost profile");
            Check(ReferenceModel.ContentHash(card) != ReferenceModel.ContentHash(card with { Weapons = [sword with { Damage = "4" }] }), "Profile changes missing from content hash");
        });
        test("Fresh and already imported swords add one normal melee profile in build, play and export only to their bearer", () =>
        {
            foreach (var enhancement in new[] { card, legacy })
            {
                var data = Data(enhancement); var before = DataJson.Write(data);
                var selected = Enhancements.Select(army, data, "bearer", new(card.Detachment, card.Name));
                var state = EffectiveDatasheets.Project(selected, selected.Entries[0], data)!;
                Check(state.EffectiveWeaponProfiles.Count == 2 && state.EffectiveWeaponProfiles[0] == unit.Profile!.Weapons![0], "Original weapon replaced/changed");
                Check(state.EffectiveWeaponProfiles[1] with { SourceId = null, GrantedBy = null } == sword, "Wrong granted stats");
                var equipped = Loadouts.Weapons(state.Unit, loadout);
                Check(equipped.Count == 2 && equipped.Single(w => w.Profile.Name == sword.Name).Count == 1, "Play mode lacks one equipped sword");
                Check(Loadouts.Weapons(state.Unit, null).Single().Profile.Name == sword.Name, "Unconfigured loadout lost automatically equipped sword");
                Check(!Loadouts.Definition(state.Unit).UnitGroups.SelectMany(g => g.Options).Any(o => o.Weapons.Contains(sword.Name)), "Granted sword became editable equipment");
                Check(Weapons(selected, data, "other").Count == 1, "Grant leaked to another entry of same unit");
                var attached = selected with { Entries = [selected.Entries[0] with { AttachedToId = "other" }, selected.Entries[1]] };
                Check(Weapons(attached, data, "other").Count == 1, "Grant leaked to attached bodyguard");
                var xml = XDocument.Parse(YellowscribeExport.Roster(selected, data));
                var exported = xml.Descendants().Single(e => e.Name.LocalName == "profile" && (string?)e.Attribute("name") == sword.Name && (string?)e.Attribute("typeName") == "Melee Weapons");
                Check(exported.Descendants().Any(e => (string?)e.Attribute("name") == "D" && e.Value == "3"), "Export lacks normal weapon stats");
                Check(Weapons(Enhancements.Select(selected, data, "bearer", null), data).Count == 1, "Removal left sword");
                Check(Weapons(selected with { Detachment = "Other", Detachments = [] }, data).Count == 1, "Inactive detachment retained sword");
                Check(Weapons(selected, Data(enhancement with { Eligibility = new("unknown") })).Count == 1, "Unknown eligibility granted sword");
                Check(DataJson.Write(data) == before, "Projection mutated source library");
                Check(enhancement.DisplayText.Contains("in addition"), "Legacy enhancement still displays raw stats");
            }
        });
        test("Automatically configured loadouts retain the added sword and apply normal weapon modifiers", () =>
        {
            var automatic = unit with { Profile = unit.Profile! with { Loadout = new([
                new("captain", "Captain", new(1), new(1), [new("melee", "Melee", [new("chainsword", "Chainsword", ["Chainsword"], [], new(1), new(1))], new(1), new(1), "chainsword")])], [], []),
                Semantics = new([], [new("strength", "Test modifier", "weapon", "S", "increment", "1", WeaponType: "Melee")]) } };
            var data = Data(card) with { Units = [automatic] };
            var configured = army with { Entries = [army.Entries[0] with { Loadout = Loadouts.Create(automatic, 1) }] };
            var selected = Enhancements.Select(configured, data, "bearer", new(card.Detachment, card.Name));
            var state = EffectiveDatasheets.Project(selected, selected.Entries[0], data)!;
            var weapons = Loadouts.Weapons(state.Unit, selected.Entries[0].Loadout);
            Check(weapons.Count == 2 && weapons.Single(w => w.Profile.Name == sword.Name).Profile.Strength == "8", "Automatic loadout or modifier lost sword");
            Check(Loadouts.Issues(state.Unit, selected.Entries[0]).Count == 0, "Grant invalidated legal equipment");
            Check(YellowscribeExport.Roster(selected, data).Contains("Imperium's Sword"), "Automatic export lost sword");
        });
        test("Sword compatibility does not invent a weapon for older, unrelated or malformed enhancements", () =>
        {
            foreach (var unrelated in new[] { legacy with { Name = "The Imperium's Sword", Detachment = "1st Company Task Force", Text = "Add 1 to the Attacks characteristic of the bearer's melee weapons." },
                legacy with { Detachment = "Other" }, legacy with { Source = source with { Edition = "10" } },
                legacy with { Text = "Melee\n6\n2+\n7\n-3" }, legacy with { Text = "Melee\n6\n2+\n7\n-3\n3\n\nAdditional rule" },
                legacy with { Weapons = [] } })
                Check(EnhancementWeapons.Profiles(unrelated).Count == 0, "Guessed weapon from incompatible card");
        });
        test("Sword selection survives SQLite save, reopen, undo and redo without persisting projected equipment", () =>
        {
            var path = Path.Combine(root, "imperiums-sword");
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(Data(legacy)); var session = new ArmySession(store); session.Open(army);
                session.SelectEnhancement("bearer", new(card.Detachment, card.Name));
                Check(Weapons(session.Army, session.Dataset!).Count == 2, "Selected sword missing");
                session.Undo(); Check(Weapons(session.Army, session.Dataset!).Count == 1, "Undo retained sword");
                session.Redo(); Check(Weapons(session.Army, session.Dataset!).Count == 2, "Redo lost sword");
                Check(DataJson.Write(session.Army.Entries[0].Loadout) == DataJson.Write(loadout), "Selection rewrote saved loadout");
            }
            using (var store = WindowsServices.OpenStore(path))
            {
                var session = new ArmySession(store); session.Restore();
                Check(Weapons(session.Army, session.Dataset!).Count == 2, "Reopen lost sword");
                session.SelectEnhancement("bearer", null);
                Check(Weapons(session.Army, session.Dataset!).Count == 1, "Removal retained sword after reopen");
            }
        });
    }
}
