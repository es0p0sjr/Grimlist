using GrimList.Core;

internal static class EnhancementChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        UnitDefinition Unit(string name, string category, params string[] keywords) => new("Death Guard", name, [new(1, 80)], Category: category,
            Profile: new(Keywords: ["Death Guard", .. keywords]));
        var caster = Unit("Enhancement Caster", "Characters", "Character", "Infantry", "Malignant Plaguecaster");
        var terminator = Unit("Enhancement Terminator", "Characters", "Character", "Infantry", "Terminator");
        var hero = Unit("Enhancement Epic Hero", "Epic Heroes", "Character", "Epic Hero", "Terminator");
        var troops = Unit("Enhancement Troops", "Battleline", "Infantry", "Terminator");
        ReferenceCard Card(string name, string text, string detachment = "Enhancement host", string cost = "15 pts") => new("Death Guard", "Enhancements", name, text, detachment, cost);
        var general = Card("General blessing", "DEATH GUARD model only. The bearer gains a rule.");
        var term = Card("Terminator blessing", "TERMINATOR model only. The bearer gains a rule.");
        var named = Card("Caster blessing", "MALIGNANT PLAGUECASTER only. The bearer gains a rule.");
        var exclusion = Card("Infantry blessing", "DEATH GUARD INFANTRY model only (excluding TERMINATOR models). The bearer gains a rule.");
        var locked = Card("Other blessing", "DEATH GUARD model only. The bearer gains a rule.", "Other host");
        var unknown = Card("Unclear blessing", "Consult a special eligibility table. The bearer gains a rule.");
        var data = new Dataset(new("Enhancement tests", "1", "", "", Edition: "11"), [caster, terminator, hero, troops],
            Detachments: [new("Death Guard", "Enhancement host", "Reconnaissance", "2 DP"), new("Death Guard", "Other host", "Disruption", "1 DP")],
            ReferenceCards: [general, term, named, exclusion, locked, unknown]);
        var army = ArmyList.New("Death Guard") with { Detachment = "Enhancement host", Entries = data.Units.Select((u, i) => new ArmyEntry($"enh-{i}", u.Key, u.Name, 1)).ToList() };
        test("Enhancements require non-Epic characters, active detachments and matching own-model restrictions", () =>
        {
            Check(Enhancements.Options(army, data, caster).Select(c => c.Name).Order().SequenceEqual(new[] { general.Name, named.Name, exclusion.Name }.Order()), "Non-Terminator character saw restricted options");
            Check(Enhancements.Options(army, data, terminator).Select(c => c.Name).Order().SequenceEqual(new[] { general.Name, term.Name }.Order()), "Terminator exclusions or named restrictions were ignored");
            Check(Enhancements.Options(army, data, hero).Count == 0 && Enhancements.Options(army, data, troops).Count == 0, "Epic heroes or non-characters saw enhancements");
            Check(!Enhancements.Character(hero with { Category = "Characters" }), "Epic Hero keyword must override Character category");
            Check(!Enhancements.Character(troops with { Profile = null }), "Missing metadata guessed character eligibility");
            Check(Enhancements.Options(army with { Detachment = "Other host" }, data, caster).Single().Name == locked.Name, "Inactive detachments leaked options");
            var attached = army with { Entries = army.Entries.Select(e => e.UnitKey == caster.Key ? e with { AttachedToId = army.Entries[1].Id } : e).ToList() };
            Check(!Enhancements.Options(attached, data, caster).Contains(term), "Attached units must not lend keywords to the enhancement bearer");
            Check(!Enhancements.Eligible(army, caster, general with { Faction = "Orks" }), "Cross-faction enhancement allowed");
            try { Enhancements.Select(army, data, army.Entries[0].Id, new(term.Detachment, term.Name)); throw new Exception("Invalid enhancement selection accepted"); }
            catch (InvalidDataException) { }
        });
        test("Enhancement points, selection, clearing and unavailable source handling are included in army calculation", () =>
        {
            var selected = Enhancements.Select(army, data, army.Entries[1].Id, new(term.Detachment, term.Name));
            Check(ArmyCalculator.Calculate(selected, data).Total == 335, "Enhancement points were not added exactly once");
            Check(ArmyCalculator.Calculate(Enhancements.Select(selected, data, army.Entries[1].Id, null), data).Total == 320, "Removing enhancement did not remove its cost");
            Check(ArmyCalculator.Calculate(selected with { Detachment = "Other host" }, data).Issues.Any(i => i.Contains("no longer eligible")), "Removing detachment failed to flag equipped enhancement");
            var changed = data with { ReferenceCards = [.. data.ReferenceCards!.Where(c => c != term), term with { Cost = "25 pts" }] };
            Check(ArmyCalculator.Calculate(selected, changed).Total == 345, "Updated enhancement cost was ignored");
            Check(!ArmyCalculator.Calculate(selected, data with { ReferenceCards = [] }).Complete, "Missing enhancement points appeared as a complete total");
            Check(ArmyCalculator.Export(selected, data).Contains("Enhancement: Terminator blessing"), "Export lost enhancement");
        });
        test("Enhancements persist across SQLite reopen, army duplication and JSON round trips", () =>
        {
            var selected = Enhancements.Select(army, data, army.Entries[1].Id, new(term.Detachment, term.Name));
            var path = Path.Combine(root, "enhancements");
            using (var store = WindowsServices.OpenStore(path)) { store.Import(data); store.Save(selected); }
            using (var store = WindowsServices.OpenStore(path))
            {
                var loaded = store.LoadArmies().Single();
                Check(loaded.Entries[1].Enhancement == selected.Entries[1].Enhancement, "Saved enhancement lost");
                Check(ArmyCalculator.Calculate(loaded, store.LoadDataset()).Total == 335, "Reloaded army lost enhancement points");
                Check(loaded.Duplicate().Entries[1].Enhancement == selected.Entries[1].Enhancement, "Army duplication lost enhancement");
                Check(DataJson.Read<ArmyList>(DataJson.Write(loaded)).Entries[1].Enhancement == selected.Entries[1].Enhancement, "JSON lost enhancement");
            }
        });
        var references = Path.Combine(root, "reference-fixture.json");
        if (File.Exists(references)) test("Real Death Guard enhancements honour Terminator, named-model and exclusion restrictions", () =>
        {
            var real = DataJson.Read<Dataset>(File.ReadAllText(references));
            var selected = DetachmentRules.WithSelection(army, real.Detachments!.Select(d => d.Name));
            var casterOptions = Enhancements.Options(selected, real, caster).Select(c => c.Name).ToHashSet();
            var termOptions = Enhancements.Options(selected, real, terminator).Select(c => c.Name).ToHashSet();
            Check(casterOptions.Contains("Bilemaw Blight") && !termOptions.Contains("Bilemaw Blight"), "Named-model restriction failed");
            Check(termOptions.Contains("Face of Death") && !casterOptions.Contains("Face of Death"), "Terminator restriction failed");
            Check(casterOptions.Contains("Rejuvenating Swarm") && !termOptions.Contains("Rejuvenating Swarm"), "Terminator exclusion failed");
            Check(termOptions.Contains("Host of the Hybridised Pox") && casterOptions.Contains("Host of the Hybridised Pox"), "Combined faction and infantry requirements failed");
            Check(!casterOptions.Any(n => n.Contains("UPGRADE")) && Enhancements.Options(selected, real, hero).Count == 0, "Non-character upgrades or Epic Hero options leaked");
        });
        File.WriteAllText(Path.Combine(root, "enhancement-fixture.json"), DataJson.Write(data));
    }
}
