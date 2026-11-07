using GrimList.Core;

internal static class MultipleLeaderChecks
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid extra attachment accepted"); }
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var source = new SourceInfo("Multiple attachment fixtures", "1", "", "", Edition: "11");
        var body = new UnitDefinition("Adepta Sororitas", "Celestian Sacresants", [new(5, 100)], Profile: new(Keywords: ["Infantry"]));
        const string eligible = "This model can be attached to the following units:\n■ CELESTIAN SACRESANTS";
        const string shared = "Unless otherwise stated, each bodyguard unit can only have one\u00a0leader unit\nand one support unit attached to it.";
        var junith = new UnitDefinition(body.Faction, "Junith Eruita", [new(1, 100)], Profile: new(Keywords: ["Character", "Leader"], Abilities: [new("Leader", eligible), new("Leader", shared)]));
        var hospitaller = new UnitDefinition(body.Faction, "Hospitaller", [new(1, 50)], Profile: new(Keywords: ["Character"], Abilities: [new("Support", eligible)]));
        var canoness = junith with { Name = "Canoness" };
        var data = new Dataset(source, [body, junith, hospitaller, canoness]);
        ArmyList Army(Dataset rules) => ArmyList.New(rules.Units[0].Faction) with { Entries = rules.Units.Select((u, i) => new ArmyEntry(i.ToString(), u.Key, u.Name, u.Options[0].Models)).ToList() };
        test("Sacresants accept Junith and Hospitaller in either order, with Support ability fallback", () =>
        {
            var army = Army(data);
            Check(LeaderRules.BodyguardNames(junith).Single() == "CELESTIAN SACRESANTS", "Square bullet or list boundary corrupted eligible unit");
            Check(LeaderRules.IsSupport(hospitaller), "Support ability without keyword not recognized");
            foreach (var order in new[] { new[] { "1", "2" }, new[] { "2", "1" } })
            {
                var attached = LeaderRules.Attach(LeaderRules.Attach(army, data, order[0], "0"), data, order[1], "0");
                Check(LeaderRules.Issues(attached, data).Count == 0 && attached.Entries.Count(e => e.AttachedToId == "0") == 2, "Leader plus Support depended on attachment order");
                Reject(() => LeaderRules.Attach(attached, data, "3", "0"));
                Check(ArmyCalculator.Calculate(attached, data).Total == ArmyCalculator.Calculate(army, data).Total, "Attachment changed unit costs");
                var copy = attached.Duplicate();
                Check(copy.Entries.Count(e => e.AttachedToId == copy.Entries[0].Id) == 2, "Duplication lost one attachment");
            }
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(army, data, "1", "0"), data, "3", "0"));
            using var store = WindowsServices.OpenStore(Path.Combine(root, "multiple-leader-save")); store.Import(data);
            var both = LeaderRules.Attach(LeaderRules.Attach(army, data, "1", "0"), data, "2", "0"); store.Save(both);
            Check(store.LoadArmies().Single().Entries.Count(e => e.AttachedToId == "0") == 2, "SQLite lost attachment");
            var removed = data with { Units = data.Units.Select(u => u == junith ? u with { Profile = u.Profile! with { Abilities = [new("Leader", eligible)] } } : u).ToList() };
            Check(LeaderRules.Issues(both, removed).Count > 0, "Changed permission left invalid imported attachment unflagged");
        });
        test("Two-Leader bodyguards enforce the Command Squad limit", () =>
        {
            var guard = body with { Profile = new(Abilities: [new("Unit Composition", "*This unit can have up to two Leader units attached to it, provided no more\u00a0than one of those units is a Command Squad unit. If it does, the Leaders separate when the bodyguard is destroyed.*")]) };
            var command = junith with { Name = "Command A", Profile = new(Keywords: ["Leader", "Command Squad"], Abilities: [new("Leader", eligible)]) };
            var officer = command with { Name = "Officer", Profile = command.Profile! with { Keywords = ["Leader", "Officer"] } };
            var rules = data with { Units = [guard, command, officer, command with { Name = "Command B" }] };
            var army = Army(rules);
            foreach (var order in new[] { new[] { "1", "2" }, new[] { "2", "1" } })
                Check(LeaderRules.Issues(LeaderRules.Attach(LeaderRules.Attach(army, rules, order[0], "0"), rules, order[1], "0"), rules).Count == 0, "Explicit two-Leader permission ignored");
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(army, rules, "1", "0"), rules, "3", "0"));
        });
        test("Size-gated two-Leader rules reject duplicates and invalidate attachments after resizing", () =>
        {
            var guard = body with { Options = [new(10, 50), new(20, 100)], Profile = new(Abilities: [new("Bodyguard", "If this unit has a Starting Strength of 20, you can attach up to two Leader units to it instead of one, provided those Leaders are not duplicates (e.g. you cannot attach two WAR SHAPERS to this unit).")]) };
            var first = junith with { Profile = new(Keywords: ["Leader"], Abilities: [new("Leader", eligible)]) };
            var second = first with { Name = "Other Leader" };
            var rules = data with { Units = [guard, first, second] };
            var small = Army(rules); var large = small with { Entries = small.Entries.Select(e => e.Id == "0" ? e with { Models = 20 } : e).ToList() };
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(small, rules, "1", "0"), rules, "2", "0"));
            var attached = LeaderRules.Attach(LeaderRules.Attach(large, rules, "1", "0"), rules, "2", "0");
            Check(LeaderRules.Issues(attached, rules).Count == 0, "Valid 20-model bodyguard rejected");
            var resized = attached with { Entries = attached.Entries.Select(e => e.Id == "0" ? e with { Models = 10 } : e).ToList() };
            Check(LeaderRules.Issues(resized, rules).Any(i => i.Contains("20 models")), "Resizing did not flag two-Leader restriction");
            var duplicate = large with { Entries = [.. large.Entries, large.Entries[1] with { Id = "duplicate" }] };
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(duplicate, rules, "1", "0"), rules, "duplicate", "0"));
        });
        test("Named partner exceptions enforce both partner and bodyguard restrictions in either order", () =>
        {
            var allowed = body with { Name = "Acolyte Hybrids with Handflamers", Profile = new(Keywords: ["Acolyte Hybrids"]) };
            var excluded = body with { Name = "Hybrid Metamorphs" };
            const string list = "This model can be attached to the following units:\n■ ACOLYTE HYBRIDS\n■ HYBRID METAMORPHS\n\n";
            var primus = junith with { Name = "Primus", Profile = new(Keywords: ["Leader", "Primus"], Abilities: [new("Leader", list)]) };
            var biophagus = junith with { Name = "Biophagus", Profile = new(Keywords: ["Leader"], Abilities: [new("Leader", list + "You can attach this model to an ACOLYTE HYBRIDS or NEOPHYTE HYBRID unit, even if a PRIMUS, MAGUS, or ACOLYTE ICONWARD model has already been attached to it. If you do, the attached units separate later.")]) };
            var wrongPartner = primus with { Name = "Wrong partner", Profile = primus.Profile! with { Keywords = ["Leader"] } };
            var rules = data with { Units = [allowed, primus, biophagus, excluded, wrongPartner] }; var army = Army(rules);
            Check(LeaderRules.BodyguardNames(biophagus).Count == 2, "Exception prose leaked into bodyguard list");
            foreach (var order in new[] { new[] { "1", "2" }, new[] { "2", "1" } })
            {
                Check(LeaderRules.Issues(LeaderRules.Attach(LeaderRules.Attach(army, rules, order[0], "0"), rules, order[1], "0"), rules).Count == 0, "Named permission depended on order");
                Reject(() => LeaderRules.Attach(LeaderRules.Attach(army, rules, order[0], "3"), rules, order[1], "3"));
            }
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(army, rules, "4", "0"), rules, "2", "0"));
            var iconward = primus with { Name = "Acolyte Iconward", Profile = primus.Profile! with { Keywords = ["Leader", "Acolyte Iconward"] } };
            var finalPartner = rules with { Units = [allowed, iconward, biophagus] };
            var finalArmy = Army(finalPartner);
            Check(LeaderRules.Issues(LeaderRules.Attach(LeaderRules.Attach(finalArmy, finalPartner, "1", "0"), finalPartner, "2", "0"), finalPartner).Count == 0,
                "Final partner after a comma and 'or' was ignored");
            var unknown = rules with { Units = rules.Units.Select(u => u == biophagus ? u with { Profile = u.Profile! with { Abilities = [new("Leader", list + "You may sometimes attach an extra character if other conditions are met.")] } } : u).ToList() };
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(army, unknown, "1", "0"), unknown, "2", "0"));
        });
        test("Screenshot's other-Leader exception permits distinct partners in either order and rejects duplicates", () =>
        {
            var marines = body with { Faction = "Death Guard", Name = "Plague Marines" };
            const string list = "This model can be attached to the following units:\n\u25a0 Plague Marines\n\n";
            const string exception = "You can attach this model to a Plague Marines unit, even if one other Leader unit has already been attached to it (you cannot attach more than one of the same Leader to the same unit). If you do, and that Bodyguard unit is destroyed, the Leader units attached to it become separate units, with their original Starting Strengths.";
            var putrifier = junith with { Faction = marines.Faction, Name = "Biologus Putrifier", Profile = new(Abilities: [new("Leader", list + exception)]) };
            var partner = putrifier with { Name = "Malignant Plaguecaster", Profile = new(Abilities: [new("Leader", list)]) };
            var third = partner with { Name = "Another Leader" };
            var rules = data with { Units = [marines, putrifier, partner, third] };
            var army = Army(rules);
            Check(LeaderRules.BodyguardNames(putrifier).SequenceEqual(new[] { "Plague Marines" }), "Exception leaked into bodyguard list");
            foreach (var order in new[] { new[] { "1", "2" }, new[] { "2", "1" } })
            {
                var attached = LeaderRules.Attach(LeaderRules.Attach(army, rules, order[0], "0"), rules, order[1], "0");
                Check(LeaderRules.Issues(attached, rules).Count == 0, "Other-Leader exception depended on order or Leader keyword");
                Reject(() => LeaderRules.Attach(attached, rules, "3", "0"));
            }
            var duplicate = army with { Entries = [.. army.Entries, army.Entries[1] with { Id = "duplicate" }] };
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(duplicate, rules, "1", "0"), rules, "duplicate", "0"));
            // Neither an ordinary Leader nor an unrecognized condition may grant an extra slot.
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(army, rules, "2", "0"), rules, "3", "0"));
            var unknown = rules with { Units = rules.Units.Select(u => u == putrifier ? u with { Profile = u.Profile! with
                { Abilities = [new("Leader", list + exception.Replace("you cannot attach more than one of the same Leader to the same unit", "only if an unsupported condition is met"))] } } : u).ToList() };
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(army, unknown, "2", "0"), unknown, "1", "0"));
            // Eligibility alone cannot extend the exception to another bodyguard.
            var alternate = marines with { Name = "Other Bodyguard" };
            var expanded = rules with { Units = [.. rules.Units.Select(u => u.Profile?.Abilities?.Any(a => a.Name == "Leader") == true
                ? u with { Profile = u.Profile! with { Abilities = u.Profile.Abilities.Select(a => a with { Text = a.Text.Replace(list, list.Replace("\n\n", "\n\u25a0 Other Bodyguard\n\n")) }).ToList() } } : u), alternate] };
            var expandedArmy = Army(expanded);
            Reject(() => LeaderRules.Attach(LeaderRules.Attach(expandedArmy, expanded, "2", "4"), expanded, "1", "4"));
            var noParenthesis = rules with { Units = rules.Units.Select(u => u == putrifier ? u with { Profile = u.Profile! with
                { Abilities = [new("Leader", list + exception.Replace(" (you cannot attach more than one of the same Leader to the same unit)", ""))] } } : u).ToList() };
            Check(LeaderRules.Issues(LeaderRules.Attach(LeaderRules.Attach(army, noParenthesis, "1", "0"), noParenthesis, "2", "0"), noParenthesis).Count == 0,
                "Unconditional other-Leader wording was ignored");
        });
        File.WriteAllText(Path.Combine(root, "multiple-leader-fixture.json"), DataJson.Write(data));
        var index = Array.IndexOf(args, "--attachment-source");
        if (index >= 0 && index + 1 < args.Length) test("Current imported Sacresants, Junith and Hospitaller work in both orders", () =>
        {
            var imported = DataJson.Read<Dataset>(File.ReadAllText(args[index + 1]));
            var names = new[] { "Celestian Sacresants", "Junith Eruita", "Hospitaller" };
            var units = names.Select(n => imported.Units.Single(u => u.Name == n && u.Faction == "Adepta Sororitas")).ToList();
            var rules = imported with { Units = units }; var army = Army(rules);
            foreach (var order in new[] { new[] { "1", "2" }, new[] { "2", "1" } })
                Check(LeaderRules.Issues(LeaderRules.Attach(LeaderRules.Attach(army, rules, order[0], "0"), rules, order[1], "0"), rules).Count == 0, "Actual imported example failed");
        });
    }
}
