using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class EnhancementStatChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        var source = new SourceInfo("Synthetic stat test", "1", "", "", Edition: "11");
        var bearer = new UnitDefinition("Test", "Bearer", [new(1, 100)], Profile: new(new() { ["M"] = "6\"", ["OC"] = "2" },
            [new("Blade", "Melee", "Melee", "4", "2+", "5", "-2", "2")], Keywords: ["Character"],
            Abilities: [new("Leader", "This model can be attached to the following unit: Guard.")]));
        var guard = new UnitDefinition("Test", "Guard", [new(5, 100)], Profile: new(new() { ["M"] = "5\"", ["OC"] = "1" },
            [new("Blade", "Melee", "Melee", "2", "3+", "4", "0", "1")], Keywords: ["Infantry"],
            Models: [new("Guard", new() { ["M"] = "5\"", ["OC"] = "1" })]));
        var text = "The bearer has the Destroyer Cult keyword. Add 2\" to the Move characteristic of models in the bearer’s unit and add 2 to the Attacks characteristic of melee weapons equipped by the bearer.";
        var legacy = new ReferenceCard("Test", "Enhancements", "Unit movement", text, "Host", "20 pts", source,
            Eligibility: new("constant", "1"), Modifiers: [new("movement", "Unit movement", "weapon", "M", "increment", "2",
                WeaponPredicate: new("unknown", ModelStatModifiers.UnitGroupSelector)),
                new("attacks", "Unit movement", "weapon", "A", "increment", "2", WeaponType: "Melee"),
                new("keyword", "Unit movement", "keyword", "", "add", "Destroyer Cult")]);
        var data = new Dataset(source, [bearer, guard], Detachments: [new("Test", "Host", Cost: "1 DP")], ReferenceCards: [legacy]);
        var army = ArmyList.New("Test") with { Detachment = "Host", Entries = [new("bearer", bearer.Key, bearer.Name, 1), new("guard", guard.Key, guard.Name, 5), new("other", guard.Key, guard.Name, 5)] };
        army = Enhancements.Select(army, data, "bearer", new("Host", legacy.Name));
        EffectiveUnitState State(ArmyList a, Dataset d, string id) => EffectiveDatasheets.Project(a, a.Entries.Single(e => e.Id == id), d)!;
        test("Legacy group Unit modifiers apply to a lone bearer and legal attached models exactly once", () =>
        {
            var before = DataJson.Write(data);
            Check(State(army, data, "bearer").EffectiveCharacteristics["M"] == "8\"", "Lone bearer M");
            var attached = LeaderRules.Attach(army, data, "bearer", "guard");
            Check(State(attached, data, "bearer").EffectiveCharacteristics["M"] == "8\"", "Attached bearer M");
            var recipient = State(attached, data, "guard");
            Check(recipient.EffectiveCharacteristics["M"] == "7\"" && recipient.Unit.Profile!.Models![0].Characteristics["M"] == "7\"", "Bodyguard/model M");
            Check(State(attached, data, "other").EffectiveCharacteristics["M"] == "5\"", "Unrelated grant");
            Check(State(attached, data, "bearer").EffectiveWeaponProfiles[0].Attacks == "6" && recipient.EffectiveWeaponProfiles[0].Attacks == "2", "Attacks escaped bearer");
            Check(!recipient.EffectiveKeywords.Contains("Destroyer Cult"), "Bearer keyword escaped");
            var support = bearer with { Name = "Support", Profile = bearer.Profile! with { Abilities = [new("Leader",
                "This model can be attached to the following unit: Guard. You can attach this model to one of the above units even if one other Leader unit has already been attached to it.")] } };
            var shared = data with { Units = [bearer, guard, support] };
            var withSupport = attached with { Entries = [.. attached.Entries, new("support", support.Key, support.Name, 1)] };
            withSupport = LeaderRules.Attach(withSupport, shared, "support", "guard");
            Check(State(withSupport, shared, "support").EffectiveCharacteristics["M"] == "8\""
                && State(withSupport, shared, "support").EffectiveWeaponProfiles[0].Attacks == "4", "Co-leader recipient scope");
            var removed = Enhancements.Select(attached, data, "bearer", null);
            Check(State(removed, data, "guard").EffectiveCharacteristics["M"] == "5\"", "Removed grant persisted");
            Check(State(army, data, "guard").EffectiveCharacteristics["M"] == "5\"", "Detached grant persisted");
            var invalid = attached with { Entries = attached.Entries.Select(e => e.Id == "bearer" ? e with { AttachedToId = "missing" } : e).ToList() };
            Check(State(invalid, data, "guard").EffectiveCharacteristics["M"] == "5\"", "Invalid attachment granted movement");
            Check(DataJson.Write(data) == before, "Base library mutated");
        });
        test("Compound numeric prose keeps bearer weapons separate from unit movement and condition scope", () =>
        {
            var prose = data with { ReferenceCards = [legacy with { Modifiers = null }] };
            var attached = LeaderRules.Attach(army, prose, "bearer", "guard");
            Check(State(attached, prose, "guard").EffectiveCharacteristics["M"] == "7\"" && State(attached, prose, "bearer").EffectiveWeaponProfiles[0].Attacks == "6", "Compound prose failed");
            var conditional = DatasheetRuleText.Enhancement("Conditional", "While the bearer is Battle-shocked, add 2\" to the Move characteristic of models in the bearer's unit and add 2 to the Attacks characteristic of melee weapons equipped by the bearer.");
            Check(conditional.Count == 2 && conditional.All(m => m.Condition?.Kind == "battlefield"), "Condition lost across clauses");
            Check(DatasheetRuleText.Enhancement("Unsupported", "If something happens, add 2 to the Objective Control characteristic of the bearer.").Count == 0, "Unknown conditional became permanent");
        });
        test("Group OC works for bearer and bodyguard; unknown selectors and conditions never become permanent", () =>
        {
            var card = legacy with { Text = "This unit has +1 OC.", Modifiers = [legacy.Modifiers![0] with { Characteristic = "OC", Value = "1" }] };
            var oc = data with { ReferenceCards = [card] };
            var attached = LeaderRules.Attach(army, oc, "bearer", "guard");
            Check(State(attached, oc, "bearer").EffectiveCharacteristics["OC"] == "3" && State(attached, oc, "guard").EffectiveCharacteristics["OC"] == "2", "Group OC");
            var conditional = oc with { ReferenceCards = [card with { Modifiers = [card.Modifiers![0] with { Condition = new("battlefield", Label: "While Battle-shocked") }] }] };
            Check(State(attached, conditional, "guard").EffectiveCharacteristics["OC"] == "1", "Conditional OC applied");
            var unknown = oc with { ReferenceCards = [card with { Modifiers = [card.Modifiers![0] with { WeaponPredicate = new("unknown", "self.entries.filtered.profiles.Unit") }] }] };
            Check(State(attached, unknown, "bearer").EffectiveCharacteristics["OC"] == "2", "Unsupported selector widened");
        });
        test("Unit-stat legacy repair survives ArmySession reopen, undo/redo and shared display projection", () =>
        {
            var path = Path.Combine(root, "unit-stat-recovery");
            using (var store = WindowsServices.OpenStore(path))
            {
                store.ApplyImport(data); store.Save(army);
                var session = new ArmySession(store); session.Open(army);
                session.SelectEnhancement("bearer", null); session.Undo();
                Check(State(session.Army, session.Dataset!, "bearer").EffectiveCharacteristics["M"] == "8\"", "Undo");
                session.Redo(); Check(State(session.Army, session.Dataset!, "bearer").EffectiveCharacteristics["M"] == "6\"", "Redo");
                session.Undo();
            }
            using var reopened = WindowsServices.OpenStore(path);
            var loaded = new ArmySession(reopened); loaded.Open(reopened.LoadArmies().Single());
            Check(State(loaded.Army, loaded.Dataset!, "bearer").EffectiveCharacteristics["M"] == "8\"", "Reopen");
            Check(EffectiveDatasheetDisplay.Statlines(bearer.Profile, State(loaded.Army, loaded.Dataset!, "bearer").Unit.Profile)
                .Any(s => s.Effective.GetValueOrDefault("M") == "8\""), "Shared display missing effective M");
        });
        test("Pinned BSData reimport classifies Destroyer Ankh and Writ of Compunction as model recipients", () =>
        {
            var dir = Path.Combine("tests", "fixtures", "bsdata", "11e", "cc1830fbfead059f8059ed6c7f8e73cd4c390908");
            var pinned = DataJson.Read<SourceInfo>(File.ReadAllText(Path.Combine(dir, "source.json")));
            var cards = BsDataImporter.ImportEnhancementReferences(BsDataDocument.ReadArchive(File.ReadAllBytes(Path.Combine(dir, "repository.zip"))), pinned);
            foreach (var (name, stat) in new[] { ("Destroyer Ankh", "M"), ("Writ of Compunction", "OC") })
            {
                var card = cards.Single(c => c.Name == name);
                Check(card.Modifiers!.Count(m => m.Target == "model" && m.Characteristic == stat && m.Recipient?.Scope == "attached-unit") == 1, name + " missing or doubled");
                Check(!card.Modifiers!.Any(m => m.Target == "weapon" && m.Characteristic == stat), name + " still weapon stat");
            }
        });
        test("Real bundled Destroyer Ankh survives legacy recovery and grants Movement to Overlord and Lychguard", () =>
        {
            var bundled = BootstrapRelease.ReadBundled().Dataset;
            var raw = bundled with { Units = bundled.Units.Where(u => u.Faction == "Necrons" && u.Name is "Overlord" or "Lychguard").ToList() };
            var recovered = EffectiveDatasheetRecovery.ApplyBundled(ReferenceData.Normalize(raw));
            var overlord = recovered.Units.Single(u => u.Name == "Overlord");
            var lychguard = recovered.Units.Single(u => u.Name == "Lychguard");
            var roster = ArmyList.New("Necrons") with { Detachment = "Cursed Legion", Entries = [
                new("overlord", overlord.Key, overlord.Name, 1), new("lychguard", lychguard.Key, lychguard.Name, 5)] };
            roster = Enhancements.Select(roster, recovered, "overlord", new("Cursed Legion", "Destroyer Ankh"));
            Check(State(roster, recovered, "overlord").EffectiveCharacteristics["M"] == "7\"", "Real lone Overlord M");
            roster = LeaderRules.Attach(roster, recovered, "overlord", "lychguard");
            Check(State(roster, recovered, "lychguard").EffectiveCharacteristics["M"] == "7\"", "Real Lychguard M");
            Check(State(roster, recovered, "overlord").EffectiveCharacteristics["M"] == "7\"", "Real attached Overlord M");
        });
        test("ROS export writes effective model Movement and OC without changing source or saved quantities", () =>
        {
            var full = bearer with { Profile = bearer.Profile! with { Characteristics = new() {
                ["M"] = "6\"", ["T"] = "4", ["Sv"] = "3+", ["W"] = "5", ["LD"] = "6+", ["OC"] = "2" } } };
            var card = legacy with { Modifiers = [.. legacy.Modifiers!, legacy.Modifiers![0] with { Id = "oc", Characteristic = "OC", Value = "1" }] };
            var exportedData = data with { Units = [full], ReferenceCards = [card] };
            var exportedArmy = army with { Entries = [army.Entries[0] with { Loadout = new([], [new("manual", "manual:Blade", 1)], true) }] };
            var before = DataJson.Write(exportedArmy);
            var xml = System.Xml.Linq.XDocument.Parse(YellowscribeExport.Roster(exportedArmy, exportedData));
            var stats = xml.Descendants().Where(e => e.Name.LocalName == "characteristic").ToList();
            Check(stats.Any(e => (string?)e.Attribute("name") == "M" && e.Value == "8\"")
                && stats.Any(e => (string?)e.Attribute("name") == "OC" && e.Value == "3"), "ROS model stats");
            Check(DataJson.Write(exportedArmy) == before && full.Profile!.Characteristics!["M"] == "6\"", "Export mutated roster or baseline");
        });
    }
}
