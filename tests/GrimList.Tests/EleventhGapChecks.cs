using GrimList.Application;
using GrimList.Core;
using System.Text.Json.Nodes;

internal static class EleventhGapChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Illegal selection accepted"); }
    private static readonly SourceInfo Source = new("11e regression fixture", "1", "", "", Edition: "11");
    private static Dataset References(string file) => BsDataImporter.ImportReferences(BsDataDocument.Parse(file,
        File.ReadAllText(Path.Combine("tests", "fixtures", "eleventh-gaps", file))), Source);

    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        test("11e source Upgrade semantics retain inline bearer restrictions and shared choice accounting", () =>
        {
            var refs = References("upgrades.json");
            var document = BsDataDocument.Parse("upgrades.json", File.ReadAllText(Path.Combine("tests", "fixtures", "eleventh-gaps", "upgrades.json")));
            var owner = document.Root["sharedSelectionEntries"]![0]!;
            var troop = new UnitDefinition(refs.Detachments![0].Faction, owner["name"]!.GetValue<string>(), [new(1, 50)],
                Profile: new(Keywords: ["Infantry", "Battleline"]), Aliases: ["bsdata:fixture:" + owner["id"]!.GetValue<string>()]);
            var character = troop with { Name = "Synthetic commander", Aliases = [], Profile = new(Keywords: ["Character", "Infantry"]) };
            var normal = new ReferenceCard(troop.Faction, "Enhancements", "Synthetic normal", "INFANTRY model only. Test.", refs.Detachments[0].Name, "10 pts");
            var data = refs with { Units = [troop, character], ReferenceOnly = false, ReferenceCards = [.. refs.ReferenceCards!, normal] };
            var upgrades = data.ReferenceCards!.Where(c => c.Section == "Enhancements" && c != normal).ToList();
            Check(upgrades.Count == 2 && upgrades.All(c => c.IsUpgrade), "Structural Upgrade semantics were not imported");
            var army = ArmyList.New(troop.Faction) with { Limit = 2000, Detachment = refs.Detachments[0].Name,
                Entries = [.. Enumerable.Range(0, 6).Select(i => new ArmyEntry("t" + i, troop.Key, troop.Name, 1)), new("c", character.Key, character.Name, 1)] };
            Check(Enhancements.Options(army, data, troop).Count == 2, "Non-Character cannot take source Upgrades");
            Check(!Enhancements.Options(army, data, character).Any(c => c.IsUpgrade), "Inline Upgrade escaped its source bearer");
            Reject(() => Enhancements.Select(army, data, "t0", new(normal.Detachment, normal.Name)));
            var basePoints = ArmyCalculator.Calculate(army, data).Total;
            for (int i = 0; i < 3; i++)
            {
                army = Enhancements.Select(army, data, "t" + i, new(upgrades[0].Detachment, upgrades[0].Name));
                Check(Enhancements.Usage(army, data) == (1, i + 1), "Copies 1/2/3 used extra choices");
                Check(ArmyCalculator.Calculate(army, data).Total == basePoints + 15 * (i + 1), "A copy's points were omitted");
            }
            Reject(() => Enhancements.Select(army, data, "t3", new(upgrades[0].Detachment, upgrades[0].Name)));
            Reject(() => Enhancements.Select(army, data, "t0", new(upgrades[1].Detachment, upgrades[1].Name)));
            for (int i = 3; i < 6; i++) army = Enhancements.Select(army, data, "t" + i, new(upgrades[1].Detachment, upgrades[1].Name));
            Check(Enhancements.Usage(army, data) == (2, 6), "Different Upgrades shared a choice or had a global copy cap");
            army = Enhancements.Select(army, data, "c", new(normal.Detachment, normal.Name));
            Check(Enhancements.Usage(army, data) == (3, 6), "Ordinary Enhancement accounting changed");
            Reject(() => Enhancements.Select(army, data, "c", new(upgrades[0].Detachment, upgrades[0].Name)));
            var path = Path.Combine(root, "eleventh-upgrades");
            using (var store = WindowsServices.OpenStore(path)) { store.Import(data); store.Save(army); }
            using (var store = WindowsServices.OpenStore(path))
            {
                var session = new ArmySession(store); session.Open(store.LoadArmies().Single());
                Check(Enhancements.Usage(session.Army, session.Dataset) == (3, 6), "Save/reopen lost Upgrade groups");
                session.SelectEnhancement("t0", null); session.Undo(); session.Redo();
                Check(Enhancements.Usage(session.Army, session.Dataset) == (3, 5), "Shared session history lost Upgrade accounting");
            }
        });

        test("Explicit Detachment exclusions are order independent and shared tags alone remain legal", () =>
        {
            var a = new DetachmentDefinition("Test", "First", "Reconnaissance", "1 DP", Tags: ["HEARTHGUARD"], CannotCombineWithTags: ["HEARTHGUARD"]);
            var b = new DetachmentDefinition("Test", "Second", "Disruption", "1 DP", Tags: ["HEARTHGUARD"]);
            var c = new DetachmentDefinition("Test", "Third", "Reconnaissance", "1 DP", Tags: ["HEARTHGUARD"]);
            var data = new Dataset(Source, [], Detachments: [a, b, c]);
            var army = ArmyList.New("Test");
            var ab = DetachmentRules.WithSelection(army, [a.Name, b.Name]);
            var ba = DetachmentRules.WithSelection(army, [b.Name, a.Name]);
            Check(DetachmentRules.CombinationIssues(ab, data).SequenceEqual(DetachmentRules.CombinationIssues(ba, data)), "Order changed exclusion result");
            Check(DetachmentRules.CombinationIssues(ab, data).Count == 1, "Explicit exclusion was ignored");
            Check(!DetachmentRules.CanAdd(DetachmentRules.WithSelection(army, [b.Name]), a.Name, data)
                && !DetachmentRules.CanAdd(DetachmentRules.WithSelection(army, [a.Name]), b.Name, data), "Picker depends on selection order");
            Check(DetachmentRules.Issues(DetachmentRules.WithSelection(army, [b.Name, c.Name]), data).Count == 0, "Shared descriptive tag inferred a prohibition");
            var card = new ReferenceCard("Test", "Detachments", "Restriction", "This Detachment has the HEARTHGUARD keyword. It cannot be taken with another Detachment with the HEARTHGUARD keyword.", a.Name);
            var textData = data with { Detachments = [a with { Tags = null, CannotCombineWithTags = null }, b], ReferenceCards = [card] };
            Check(DetachmentRules.CombinationIssues(ab, textData).Count == 1, "Explicit source prose was not interpreted");
            Check(DetachmentRules.SelectionMetadata(a, data).Contains("HEARTHGUARD") && DetachmentRules.SelectionMetadata(a, data).Contains("Reconnaissance"), "Picker metadata missing");
            Check(DetachmentRules.CombinationIssues(ab with { Limit = 1000 }, textData).Count == 1, "Restrictions depended on DP accounting");
        });

        foreach (var scenario in new[] { (File: "houndpack.json", Keyword: "War Dog", Min: 3, Max: 3), (File: "subterranean.json", Keyword: "Trygon", Min: 0, Max: 2) })
        test("Real BSData " + scenario.File + " nominations grant effective CHARACTER through shared editing and persistence", () =>
        {
            var refs = References(scenario.File); var detachment = refs.Detachments!.Single();
            var unit = new UnitDefinition(detachment.Faction, scenario.Keyword + " fixture", [new(1, 50)], Category: "Vehicles",
                Profile: new(Keywords: [scenario.Keyword, "Vehicle", "Battleline"]));
            var wrong = unit with { Name = "Unrelated fixture", Profile = new(Keywords: ["Vehicle"]) };
            var enhancement = new ReferenceCard(unit.Faction, "Enhancements", "Synthetic character gift", "CHARACTER model only. The bearer has the Feel No Pain 5+ ability.", detachment.Name, "15 pts");
            var data = refs with { ReferenceOnly = false, Units = [unit, wrong], ReferenceCards = [.. refs.ReferenceCards!, enhancement] };
            File.WriteAllText(Path.Combine(root, Path.GetFileNameWithoutExtension(scenario.File) + "-nomination-fixture.json"), DataJson.Write(data));
            var army = ArmyList.New(unit.Faction) with { Detachment = detachment.Name,
                Entries = [.. Enumerable.Range(0, 4).Select(i => new ArmyEntry("u" + i, unit.Key, unit.Name, 1)), new("wrong", wrong.Key, wrong.Name, 1)] };
            var rule = MusterEffects.Rules(army, data).Single();
            Check(rule.Rule.Minimum == scenario.Min && rule.Rule.Maximum == scenario.Max, "Required/optional source counts lost");
            Check(MusterEffects.Candidates(army, data, rule).Count == 4, "Ineligible unit offered");
            Check(MusterEffects.Issues(army, data).Any(i => i.Contains("missing required")) == (scenario.Min > 0), "Required nominations not explained");
            Check(WarlordRules.Candidates(army, data).Count == 0, "Base datasheet became Character");
            Reject(() => Enhancements.Select(army, data, "u0", new(detachment.Name, enhancement.Name)));
            var path = Path.Combine(root, scenario.File + "-session");
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(data); var session = new ArmySession(store); session.Open(army);
                data = session.Dataset!;
                var candidates = MusterEffects.Candidates(session.Army, data, rule);
                // Exercise the same command synchronously (Windows) and via RunAsync (Android).
                session.SelectMusterNomination(candidates[0], true);
                session.Undo(); Check(WarlordRules.Candidates(session.Army, data).Count == 0, "Undo left derived keyword");
                session.Redo();
                for (int i = 1; i < scenario.Max; i++) session.RunAsync(s => s.SelectMusterNomination(candidates[i], true)).GetAwaiter().GetResult();
                Reject(() => session.SelectMusterNomination(candidates[scenario.Max], true));
                Reject(() => session.SelectMusterNomination(new(detachment.Name, rule.Rule.Id, "wrong"), true));
                Check(MusterEffects.Issues(session.Army, data).Count == 0, "Completed nominations invalid");
                Check(WarlordRules.Candidates(session.Army, data).Count == scenario.Max, "Warlord eligibility ignored effective state");
                foreach (var sourceCard in refs.ReferenceCards!.Where(c => c.Section == "Enhancements"))
                    Check(sourceCard.Eligibility != null && Enhancements.Options(session.Army, data, MusterEffects.EffectiveUnit(session.Army, session.Army.Entries[0], data)).Any(c => c.Name == sourceCard.Name),
                        "Source membership predicate did not accept the nominated bearer");
                session.SelectWarlord("u0"); session.SelectEnhancement("u0", new(detachment.Name, enhancement.Name));
                Check(DatasheetStats.FeelNoPain(session.Army, data, data.FindUnit(session.Army.Entries[0].UnitKey), session.Army.Entries[0]).Count > 0,
                    "Derived Character did not receive enhancement defence");
                Check(Enhancements.Issues(session.Army, data, session.Army.Entries[0]).Count == 0, "Enhancement eligibility ignored effective state");
                Check(!unit.Profile!.Keywords!.Contains("Character"), "Base datasheet mutated");
                Check(RosterExport.Category(session.Army, data, session.Army.Entries[0]) == "Characters", "Export category ignored grant");
                Check(RosterExport.Text(session.Army, data).Contains("Muster keywords: Character"), "Text export omitted granted state");
                var copy = session.DuplicateArmy(session.Army);
                Check(copy.MusterSelections!.All(s => copy.Entries.Any(e => e.Id == s.EntryId)) && WarlordRules.Candidates(copy, data).Count == scenario.Max, "Army duplication failed to remap nominations");
                session.DuplicateUnit("u0");
                Check(!WarlordRules.IsCharacter(MusterEffects.EffectiveUnit(session.Army, session.Army.Entries[^1], data)) && session.Army.Entries[^1].Enhancement == null, "Unit duplication cloned a limited nomination");
                session.Undo();
                session.RemoveDetachment(detachment.Name);
                Check(WarlordRules.Candidates(session.Army, data).Count == 0 && session.Army.MusterSelections!.Count == 0, "Removal retained grant");
                Check(session.Army.WarlordEntryId == "" && session.Result.Issues.Any(i => i.Contains("no longer eligible")), "Removal failed to invalidate dependent choices");
                session.Undo(); Check(WarlordRules.Candidates(session.Army, data).Count == scenario.Max && session.Army.WarlordEntryId == "u0", "Undo did not restore grant and Warlord");
                session.Redo(); session.Undo();
                session.RemoveUnit("u1"); Check(session.Army.MusterSelections!.All(s => s.EntryId != "u1"), "Deleted unit kept nomination"); session.Undo();
            }
            using (var store = WindowsServices.OpenStore(path))
            {
                var session = new ArmySession(store); Check(session.Restore(), "Session failed to reopen");
                Check(WarlordRules.Candidates(session.Army, session.Dataset).Count == scenario.Max && session.Army.WarlordEntryId == "u0", "Reopen lost granted state");
                Check(session.Result.Entries.Single(p => p.Entry.Id == "u0").EnhancementPoints == 15, "Reopen lost Enhancement points");
            }
        });

        test("Generic muster effects support arbitrary keywords, models, attachments and malformed selections", () =>
        {
            var rule = new MusterNominationRule("generic", "Scouts", 1, 2, ["Infantry"], ["Character", "Scout"], SelectModels: true);
            var unit = new UnitDefinition("Test", "Squad", [new(2, 40)], Profile: new(Keywords: ["Infantry"]));
            var leader = new UnitDefinition("Test", "Leader", [new(1, 40)], Profile: new(Keywords: ["Character"], Abilities: [new("Leader", "This model can be attached to the following units: SCOUT.")]));
            var data = new Dataset(Source, [unit, leader], Detachments: [new("Test", "Host", Nominations: [rule])]);
            var army = ArmyList.New("Test") with { Detachment = "Host", Entries = [new("squad", unit.Key, unit.Name, 2), new("leader", leader.Key, leader.Name, 1)] };
            Check(LeaderRules.Problem(army, data, "leader", "squad") != null, "Attachment keyword restriction ignored");
            army = MusterEffects.Select(army, data, new("Host", rule.Id, "squad", 1), true);
            Check(LeaderRules.Problem(army, data, "leader", "squad") == null, "Attachment did not consume granted keyword");
            Check(MusterEffects.Candidates(army, data, MusterEffects.Rules(army, data).Single()).Count == 2, "Model nomination count wrong");
            Reject(() => MusterEffects.Select(army, data, new("Host", rule.Id, "squad", 2), true));
            var invalid = army with { MusterSelections = [new("Host", rule.Id, "squad", 9)] };
            Check(MusterEffects.Issues(invalid, data).Count > 0 && !WarlordRules.IsCharacter(MusterEffects.EffectiveUnit(invalid, invalid.Entries[0], data)), "Invalid model granted keyword");
            var inactive = army with { Detachment = "Other" };
            Check(!WarlordRules.IsCharacter(MusterEffects.EffectiveUnit(inactive, inactive.Entries[0], data)), "Inactive grant persisted");
        });
        var sourceIndex = Array.IndexOf(args, "--enhancement-catalogue");
        if (sourceIndex >= 0 && sourceIndex + 2 < args.Length)
        foreach (var scenario in new[] { (Faction: "Chaos Knights", Detachment: "Houndpack Lance", Keyword: "War Dog"), (Faction: "Tyranids", Detachment: "Subterranean Assault", Keyword: "Trygon") })
        test("Real saved " + scenario.Faction + " library nominations unlock source Enhancements and Warlord", () =>
        {
            var data = EnhancementLibraryRepair.ApplyBundled(DataJson.Read<Dataset>(File.ReadAllText(args[sourceIndex + 2])));
            var unit = data.Units.First(u => u.Faction == scenario.Faction && !WarlordRules.IsCharacter(u)
                && u.Profile?.Keywords?.Contains(scenario.Keyword) == true);
            var army = ArmyList.New(scenario.Faction) with { Detachment = scenario.Detachment };
            var rule = MusterEffects.Rules(army, data).Single();
            army = army with { Entries = Enumerable.Range(0, rule.Rule.Maximum).Select(i => new ArmyEntry("real-" + i, unit.Key, unit.Name, unit.Options[0].Models,
                Loadout: Loadouts.Create(unit, unit.Options[0].Models))).ToList() };
            Check(Enhancements.Options(army, data, unit).All(Enhancements.Upgrade), "Ordinary enhancements available before nomination");
            foreach (var selection in MusterEffects.Candidates(army, data, rule)) army = MusterEffects.Select(army, data, selection, true);
            Check(WarlordRules.Candidates(army, data).Count == rule.Rule.Maximum, "Real source Warlord eligibility missing");
            var effective = MusterEffects.EffectiveUnit(army, army.Entries[0], data)!;
            var enhancement = Enhancements.Options(army, data, effective).FirstOrDefault(c => !Enhancements.Upgrade(c));
            Check(enhancement != null, "Real source ordinary Enhancement unavailable after nomination");
            army = Enhancements.Select(army, data, army.Entries[0].Id, new(enhancement!.Detachment, enhancement.Name));
            army = WarlordRules.Select(army, data, army.Entries[0].Id);
            Check(Enhancements.Issues(army, data, army.Entries[0]).Count == 0, "Real source Enhancement invalid");
            Check(YellowscribeExport.Roster(army, data).Contains("Character"), "Yellowscribe export omitted effective Character");
        });
    }
}
