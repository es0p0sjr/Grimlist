using GrimList.Application;
using GrimList.Core;

internal static class EffectiveDatasheetChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid change accepted"); }
    public static void Run(Action<string, Action> test, string root)
    {
        var unit = new UnitDefinition("Test", "Generic bearer", [new(1, 100)], Profile: new(new() { ["M"] = "6\"", ["T"] = "5", ["Sv"] = "3+", ["W"] = "6", ["LD"] = "6+", ["OC"] = "2", ["InSv"] = "5+" },
            [new("Blade", "Melee", "Melee", "6", "2+", "8", "-2", "3"), new("Gun", "Ranged", "24\"", "3", "3+", "5", "-1", "2")],
            Keywords: ["Character", "Test", "Infantry"]));
        var card = new ReferenceCard("Test", "Enhancements", "Generic blades", "CHARACTER model only. Add 1 to the Attacks and Strength characteristics of the bearer's melee weapons. While the bearer is within your army's Shadow of Chaos, add 2 to the Attacks and Strength characteristics of the bearer's melee weapons instead.", "Host", "10 pts");
        var data = new Dataset(new("Synthetic", "1", "", ""), [unit], Detachments: [new("Test", "Host")], ReferenceCards: [card]);
        var army = ArmyList.New("Test") with { Detachment = "Host", Entries = [new("bearer", unit.Key, unit.Name, 1)] };
        test("Generic enhancement projection changes melee A/S, retains conditional alternative and immutable base", () =>
        {
            var before = DataJson.Write(data);
            var selected = Enhancements.Select(army, data, "bearer", new(card.Detachment, card.Name));
            var state = EffectiveDatasheets.Project(selected, selected.Entries[0], data)!;
            Check(state.EffectiveWeaponProfiles[0].Attacks == "7" && state.EffectiveWeaponProfiles[0].Strength == "9", "Unconditional melee changes missing");
            Check(state.EffectiveWeaponProfiles[1] == unit.Profile!.Weapons![1], "Ranged weapon changed");
            Check(state.AppliedModifierSources.Any(s => s.Conditional && s.Characteristic == "S" && s.EffectiveValue == "10"), "Stronger conditional replacement missing");
            Check(DataJson.Write(data) == before, "Imported base was mutated");
            selected = Enhancements.Select(selected, data, "bearer", null);
            Check(EffectiveDatasheets.Project(selected, selected.Entries[0], data)!.EffectiveWeaponProfiles[0].Strength == "8", "Removal left stale change");
        });
        test("Generic modifiers cover every model and weapon stat, keywords and abilities", () =>
        {
            var modifiers = unit.Profile!.Characteristics!.Select(k => new DatasheetModifier("model/" + k.Key, "Choice source", "model", k.Key, "increment", "1"))
                .Concat(new[] { "A", "BS", "WS", "S", "AP", "D", "Range" }.Select(k => new DatasheetModifier("weapon/" + k, "Choice source", "weapon", k, "increment", "1")))
                .Concat([new("key", "Choice source", "keyword", "", "add", "New Keyword"), new("ability", "Choice source", "ability", "", "add", "Stealth")]).ToList();
            var changed = unit with { Profile = unit.Profile with { Semantics = new([], modifiers) } };
            var state = EffectiveDatasheets.Project(army, army.Entries[0], data with { Units = [changed] })!;
            Check(state.EffectiveCharacteristics["M"] == "7\"" && state.EffectiveCharacteristics["T"] == "6" && state.EffectiveCharacteristics["Sv"] == "4+"
                && state.EffectiveCharacteristics["W"] == "7" && state.EffectiveCharacteristics["LD"] == "7+" && state.EffectiveCharacteristics["OC"] == "3" && state.EffectiveCharacteristics["InSv"] == "6+", "Model fields missing");
            var gun = state.EffectiveWeaponProfiles[1];
            Check(gun.Range == "25\"" && gun.Attacks == "4" && gun.Skill == "4+" && gun.Strength == "6" && gun.Ap == "0" && gun.Damage == "3", "Weapon fields missing");
            Check(state.EffectiveKeywords.Contains("New Keyword") && state.EffectiveAbilities.Any(a => a.Name == "Stealth"), "Keyword/ability grant missing");
        });
        test("Equipment removal and unresolved battlefield conditions do not leave active modifiers", () =>
        {
            var changed = unit with { Profile = unit.Profile! with { Semantics = new([], [
                new("equipment", "Shield", "model", "T", "increment", "1", Condition: new("selection", "shield")),
                new("unknown", "Battlefield effect", "model", "M", "increment", "2", Condition: new("unknown"))]) } };
            var source = data with { Units = [changed] };
            var equipped = army.Entries[0] with { Loadout = new([], [new("gear", "shield")]) };
            var state = EffectiveDatasheets.Project(army, equipped, source)!;
            Check(state.EffectiveCharacteristics["T"] == "6" && state.EffectiveCharacteristics["M"] == "6\"", "Equipment or conditional state incorrect");
            Check(state.AppliedModifierSources.Any(m => m.Conditional && m.Characteristic == "M"), "Unknown condition hidden");
            Check(EffectiveDatasheets.Project(army, equipped with { Loadout = new([], []) }, source)!.EffectiveCharacteristics["T"] == "5", "Removed equipment left stale effect");
        });
        test("Complete proposed choice/enhancement state is order independent and detachment changes recalculate", () =>
        {
            var bearer = unit with { Profile = unit.Profile! with { Semantics = new([
                new("patron", "Patron", 0, [new("red", "Red", ["Red"], []), new("green", "Green", ["Green"], [])])], []) } };
            var restricted = card with { Text = "RED CHARACTER model only. Add 1 to the Strength characteristic of the bearer's melee weapons." };
            using var store = WindowsServices.OpenStore(Path.Combine(root, "complete-effective-state"));
            store.Import(data with { Units = [bearer], ReferenceCards = [restricted], Detachments = [new("Test", "Host"), new("Test", "Other")] });
            var session = new ArmySession(store); session.Open(army);
            var selected = army.Entries[0] with { Choices = [new("patron", "red")], Enhancement = new("Host", restricted.Name) };
            session.Commit(army with { Entries = [selected] });
            Check(EffectiveDatasheets.Project(session.Army, session.Army.Entries[0], session.Dataset)!.EffectiveWeaponProfiles[0].Strength == "9", "Complete proposed state rejected or enhancement not applied");
            session.Commit(DetachmentRules.WithSelection(session.Army, ["Other"]));
            Check(session.InvalidEnhancements(session.Army).Count == 1, "Detachment change did not invalidate enhancement");
            Check(EffectiveDatasheets.Project(session.Army, session.Army.Entries[0], session.Dataset)!.EffectiveWeaponProfiles[0].Strength == "8", "Detachment change retained effect");
            session.Undo();
            Check(EffectiveDatasheets.Project(session.Army, session.Army.Entries[0], session.Dataset)!.EffectiveWeaponProfiles[0].Strength == "9", "Undo did not restore effect");
            var unavailable = session.Army with { Entries = [session.Army.Entries[0] with { Choices = [new("patron", "green")] }] };
            Reject(() => session.Commit(unavailable));
        });
        test("Enhancement eligibility includes deterministic self-granted keywords without granting its own eligibility", () =>
        {
            var bearer = unit with { Profile = unit.Profile! with { Abilities = [new("Patron (Aura)", "While a friendly TEST INFANTRY unit is within 6\" of this model, models in that unit gain the RED keyword.")] } };
            var restricted = card with { Text = "RED CHARACTER model only. Add 1 to the Strength characteristic of the bearer's melee weapons." };
            var source = data with { Units = [bearer], ReferenceCards = [restricted] };
            var selected = Enhancements.Select(army, source, "bearer", new(card.Detachment, card.Name));
            Check(EffectiveDatasheets.Project(selected, selected.Entries[0], source)!.EffectiveWeaponProfiles[0].Strength == "9", "Eligible self-keyword enhancement lost its effects");
            restricted = restricted with { Modifiers = [new("red", "Enhancement", "keyword", "", "add", "RED")] };
            Reject(() => Enhancements.Select(army, data with { ReferenceCards = [restricted] }, "bearer", new(card.Detachment, card.Name)));
        });
        foreach (var (text, applies) in new[] {
            ("While a friendly TEST INFANTRY unit is within 6\" of this model, add 1 to the Toughness characteristic of models in that unit.", true),
            ("While a friendly TEST VEHICLE unit is within 6\" of this model, add 1 to the Toughness characteristic of models in that unit.", false),
            ("While another friendly TEST INFANTRY unit is within 6\" of this model, add 1 to the Toughness characteristic of models in that unit.", false),
            ("While a friendly TEST INFANTRY unit is within 6\" of this model, other models in that unit add 1 to their Toughness characteristic.", false) })
            test("Self Aura target predicate and exclusions: " + text, () =>
            {
                var aura = new AbilityProfile("Generic protection (Aura)", text);
                var bearer = unit with { Profile = unit.Profile! with { Abilities = [aura, aura] } };
                var state = EffectiveDatasheets.Project(army, army.Entries[0], data with { Units = [bearer] })!;
                Check(state.EffectiveCharacteristics["T"] == (applies ? "6" : "5"), "Self qualification or non-stacking failed");
            });
        test("Actual BSData: all four princes import choices, change profiles and recalculate enhancements", () =>
        {
            var archive = "tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip";
            var docs = BsDataDocument.ReadArchive(File.ReadAllBytes(archive));
            foreach (var doc in docs.Where(d => d.Name is not ("Chaos - Chaos Daemons" or "Chaos - Chaos Space Marines"))) doc.Root["library"] = true;
            var imported = BsDataImporter.ImportAll(docs, new("BSData fixture", "1", "", "", Edition: "11")).Dataset;
            File.WriteAllText(Path.Combine(root, "effective-import.json"), DataJson.Write(imported));
            var princes = imported.Units.Where(u => u.Name.Contains("Daemon Prince", StringComparison.OrdinalIgnoreCase)).ToList();
            Check(princes.Count == 4, "Expected four princes, got " + princes.Count);
            foreach (var prince in princes)
            {
                var argath = imported.ReferenceCards!.FirstOrDefault(c => c.Faction == prince.Faction && c.Name == "A'rgath, the King of Blades");
                var roster = ArmyList.New(prince.Faction) with { Detachment = argath?.Detachment ?? imported.Detachments!.First(d => d.Faction == prince.Faction).Name,
                    Entries = [new("p", prince.Key, prince.Name, 1)] };
                var groups = EffectiveDatasheets.Choices(roster, roster.Entries[0], imported);
                var group = groups.Single(g => g.Options.Any(o => o.Name == "Khorne"));
                Check(group.Options.Select(o => o.Name).Order().SequenceEqual(new[] { "Khorne", "Nurgle", "Slaanesh", "Tzeentch" }), "Wrong allegiance options for " + prince.Name);
                if (argath != null) Check(!Enhancements.Options(roster, imported, MusterEffects.EffectiveUnit(roster, roster.Entries[0], imported)).Contains(argath), "Khorne enhancement available without choice");
                foreach (var option in group.Options)
                {
                    var chosen = roster with { Entries = [roster.Entries[0] with { Choices = [new(group.Id, option.Id)] }] };
                    var state = EffectiveDatasheets.Project(chosen, chosen.Entries[0], imported)!;
                    Check(state.EffectiveKeywords.Any(k => k.Equals(option.Name, StringComparison.OrdinalIgnoreCase)), "Missing granted keyword " + option.Name);
                    var original = prince.Profile!;
                    if (option.Name == "Nurgle") Check(int.Parse(state.EffectiveCharacteristics["T"]) == int.Parse(original.Characteristics!["T"]) + 1, "Nurgle T incorrect");
                    if (option.Name == "Slaanesh") Check(state.EffectiveCharacteristics["M"] == (int.Parse(original.Characteristics!["M"].Trim('"')) + 2) + "\"", "Slaanesh M incorrect");
                    if (option.Name == "Tzeentch") Check(state.EffectiveWeaponProfiles.Single(w => w.Name == "Infernal cannon").Attacks == "6", "Tzeentch A incorrect");
                    if (option.Name == "Khorne")
                    {
                        Check(state.EffectiveWeaponProfiles.Single(w => w.Name.EndsWith("strike")).Strength == "10", "Khorne S incorrect");
                        if (argath != null)
                        {
                            Check(Enhancements.Options(chosen, imported, state.Unit).Contains(argath), "Khorne did not unlock A'rgath");
                            chosen = Enhancements.Select(chosen, imported, "p", new(argath.Detachment, argath.Name));
                            var enhanced = EffectiveDatasheets.Project(chosen, chosen.Entries[0], imported)!;
                            Check(enhanced.EffectiveWeaponProfiles.Single(w => w.Name.EndsWith("strike")).Strength == "11", "A'rgath does not stack after allegiance");
                            Check(enhanced.EffectiveWeaponProfiles.Single(w => w.Name.EndsWith("strike")).Attacks == "7", "A'rgath A missing");
                            Check(enhanced.AppliedModifierSources.Any(m => m.Conditional && m.Condition.Contains("Shadow of Chaos")), "Conditional shadow modifier missing");
                        }
                    }
                }
                using var store = WindowsServices.OpenStore(Path.Combine(root, "effective-state-" + Guid.NewGuid()));
                store.Import(imported); store.Save(roster);
                var session = new ArmySession(store); session.Open(roster);
                var khorne = group.Options.Single(o => o.Name == "Khorne"); var nurgle = group.Options.Single(o => o.Name == "Nurgle");
                session.SelectChoice("p", group.Id, khorne.Id);
                Check(store.LoadArmies().Single().Entries[0].Choices!.Single().OptionId == khorne.Id, "SQLite save lost choice");
                session.Undo(); Check(session.Army.Entries[0].Choices == null, "Undo failed");
                session.Redo(); Check(session.Army.Entries[0].Choices!.Single().OptionId == khorne.Id, "Redo failed");
                if (argath != null)
                {
                    session.SelectEnhancement("p", new(argath.Detachment, argath.Name));
                    Reject(() => session.SelectChoice("p", group.Id, nurgle.Id));
                    Check(session.Army.Entries[0].Choices!.Single().OptionId == khorne.Id, "Rejected edit changed state");
                    session.SelectChoice("p", group.Id, nurgle.Id, true);
                    Check(session.Army.Entries[0].Enhancement == null && !MusterEffects.EffectiveUnit(session.Army, session.Army.Entries[0], session.Dataset)!.Profile!.Keywords!.Contains("Khorne"), "Stale enhancement or keyword");
                    session.Undo(); Check(session.Army.Entries[0].Enhancement != null, "Undo failed to restore removed enhancement");
                }
                session.DuplicateUnit("p"); Check(session.Army.Entries[^1].Choices!.Single().OptionId == khorne.Id, "Duplicate lost allegiance");
                using var reopenedStore = WindowsServices.OpenStore(store.DirectoryPath);
                var reopened = new ArmySession(reopenedStore); reopened.Open(reopenedStore.LoadArmies().Single());
                Check(reopened.Army.Entries[0].Choices!.Single().OptionId == khorne.Id, "Reopen lost choice");
            }
        });
        test("Bundled semantic recovery is source/profile matched and never mutates the imported library", () =>
        {
            var original = DataJson.Read<Dataset>(File.ReadAllText("tests/fixtures/libraries/GrimList-Library.json"));
            var before = DataJson.Write(original);
            var recovered = EffectiveDatasheetRecovery.ApplyBundled(original);
            var princes = recovered.Units.Where(u => u.Faction is "Chaos Daemons" or "Chaos Space Marines" && u.Name.Contains("Daemon Prince")).ToList();
            Check(princes.Count == 4 && princes.All(u => u.Profile?.Semantics?.Choices.Any(g => g.Options.Any(o => o.Name == "Khorne")) == true), "Existing-library choice recovery failed");
            Check(DataJson.Write(original) == before, "Recovery mutated source library");
            Check(ReferenceEquals(recovered, EffectiveDatasheetRecovery.ApplyBundled(recovered)), "Recovery is not idempotent");
            var prince = original.Units.First(u => u.Name == "Daemon Prince of Chaos");
            var wrong = prince with { ProfileSource = prince.ProfileSource! with { ContentHash = "wrong" } };
            Check(EffectiveDatasheetRecovery.ApplyBundled(original with { Units = [wrong] }).Units.Single().Profile!.Semantics == null, "Wrong source accepted");
            var altered = prince with { Profile = prince.Profile! with { Characteristics = new(prince.Profile!.Characteristics!) { ["T"] = "123" } } };
            Check(EffectiveDatasheetRecovery.ApplyBundled(original with { Units = [altered] }).Units.Single().Profile!.Semantics == null, "Changed profile accepted");
        });
    }
}
