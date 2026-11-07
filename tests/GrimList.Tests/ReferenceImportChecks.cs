using GrimList.Core;
using GrimList.Application;
using System.Text.Json.Nodes;

internal static class ReferenceImportChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var source = new SourceInfo("BSData reference fixture", "1", "", "", Edition: "11");
        var supplement = source with { Name = "Separate rules reference" };
        var document = BsDataDocument.Parse("references.json", """
        {"catalogue":{"name":"Imperium - Adepta Sororitas","id":"fixture","sharedRules":[
          {"id":"army","name":"Army rule","description":"Applies to the army."},
          {"id":"alpha-rule","name":"Alpha rule","description":"Only selected Alpha."},
          {"id":"beta-rule","name":"Beta rule","description":"Only selected Beta."},
          {"id":"keyword","name":"Feel No Pain","description":"Keyword explanation."}],
         "selectionEntryGroups":[{"name":"Detachment","id":"detachments","selectionEntries":[
          {"name":"Alpha","id":"alpha","type":"upgrade","infoLinks":[{"type":"rule","targetId":"alpha-rule"}]},
          {"name":"Beta","id":"beta","type":"upgrade","infoLinks":[{"type":"rule","targetId":"beta-rule"}]}]},
          {"name":"Enhancements","id":"enhancements","selectionEntries":[
           {"id":"enhancement","type":"upgrade","name":"Healing relic","costs":[{"name":"pts","value":30}],
            "profiles":[{"id":"relic-profile","name":"Healing relic","typeName":"Abilities","characteristics":[{"name":"Description","$text":"ADEPTA SORORITAS model only. The bearer has Feel No Pain 5+."}]}],
            "infoLinks":[{"type":"rule","targetId":"keyword"}],
            "modifiers":[{"field":"hidden","type":"set","value":true,"conditions":[{"childId":"alpha","type":"lessThan","value":1}]}]}]}]}}
        """);
        test("BSData references retain detachment scope and one selectable card per enhancement", () =>
        {
            var imported = BsDataImporter.ImportReferences(document, source);
            var cards = imported.ReferenceCards!;
            Check(cards.Count(c => c.Section == "Detachments") == 2, "Detachment rule lost");
            Check(!cards.Any(c => c.Section == "Army Rules" && c.Name is "Alpha rule" or "Beta rule"), "Scoped rule copied to global rules");
            var relic = cards.Single(c => c.Section == "Enhancements");
            Check(relic.Name == "Healing relic" && relic.Cost == "30 pts", "Linked keyword became an enhancement");
            Check(relic.Text.StartsWith("ADEPTA SORORITAS model only.") && relic.Text.Contains("Keyword explanation."), "Owner text or linked definition lost");
            var army = ArmyList.New("Adepta Sororitas") with { Detachment = "Alpha" };
            var visible = ArmyReferences.ForArmy(army, imported);
            Check(visible.Any(c => c.Name == "Alpha rule") && !visible.Any(c => c.Name == "Beta rule"), "Unselected rules leaked");
        });
        var owner = new ReferenceCard("Adepta Sororitas", "Enhancements", "Healing relic", "ADEPTA SORORITAS model only. The bearer has Feel No Pain 5+.", "Alpha", "30 pts", supplement);
        var upgrade = new ReferenceCard("Adepta Sororitas", "Enhancements", "Ancient relic UPGRADE", "ADEPTA SORORITAS unit only. Extra strength.", "Alpha", "15 pts", supplement, true);
        var duplicate = new ReferenceCard("Adepta Sororitas", "Enhancements", "Feel No Pain", "Keyword explanation.", "Alpha", "30 pts", source);
        var unit = new UnitDefinition("Adepta Sororitas", "Bearer", [new(1, 50)], Category: "Characters",
            Profile: new(Keywords: ["Adepta Sororitas", "Character"], Abilities: [new("Feel No Pain 5+", "Keyword explanation.")]), ProfileSource: source);
        var legacy = new Dataset(source, [unit], Detachments: [new("Adepta Sororitas", "Alpha", "Disruption"), new("Adepta Sororitas", "Beta", "Disruption"), new("Adepta Sororitas", "Gamma", "Reconnaissance")], ReferenceCards: [
            owner, upgrade, duplicate, upgrade with { Name = "Ancient relic", Text = "Extra strength.", Source = source, IsUpgrade = false },
            new("Adepta Sororitas", "Detachments", "Alpha rule", "Alpha text", "Alpha", Source: source),
            new("Adepta Sororitas", "Detachments", "Beta rule", "Beta text", "Beta", Source: source),
            new("Adepta Sororitas", "Army Rules", "Alpha rule", "Alpha text", Source: source),
            new("Adepta Sororitas", "Army Rules", "Beta rule", "Beta text", Source: source),
            new("Adepta Sororitas", "Army Rules", "Real global", "Always", Source: source),
            new("Adepta Sororitas", "Dispositions", "Disruption", "Available from Alpha and Beta", Source: source),
            new("Adepta Sororitas", "Dispositions", "Reconnaissance", "Available from Gamma", Source: source)]);
        test("Legacy reference cleanup is conservative, idempotent and preserves keyword definitions", () =>
        {
            var original = DataJson.Write(legacy);
            var clean = ReferenceData.Normalize(legacy);
            Check(clean.ReferenceCards!.Count == legacy.ReferenceCards!.Count - 4, "Expected four proven duplicates removed");
            Check(DataJson.Write(clean) == DataJson.Write(ReferenceData.Normalize(clean)), "Cleanup is not idempotent");
            Check(DataJson.Write(legacy) == original && ReferenceEquals(clean.Units, legacy.Units), "Cleanup mutated unit rules or input");
            var actualEnhancement = duplicate with { Text = "ADEPTA SORORITAS model only. A real named choice." };
            Check(ReferenceData.Normalize(legacy with { ReferenceCards = [actualEnhancement, owner] }).ReferenceCards!.Contains(actualEnhancement), "Removed a legitimate keyword-named enhancement");
            Check(ReferenceData.Normalize(legacy with { ReferenceCards = [duplicate] }).ReferenceCards!.Count == 1, "Removed an unproven orphan");
            Check(ReferenceData.Normalize(legacy with { ReferenceCards = [duplicate with { Source = supplement }, owner] }).ReferenceCards!.Count == 2, "Removed independent source data");
            Check(clean.Units.Single().Profile!.Abilities!.Single().Text == duplicate.Text, "Keyword popup definition lost");
        });
        test("Selected references and dispositions do not advertise other detachments; missing stratagems stay explicit", () =>
        {
            var army = ArmyList.New(unit.Faction) with { Detachment = "Alpha" };
            var refs = ArmyReferences.ForArmy(army, legacy);
            Check(!refs.Any(c => c.Name is "Beta rule" or "Reconnaissance" or "Feel No Pain"), "Unselected or spurious reference leaked");
            var disposition = refs.Single(c => c.Section == "Dispositions");
            Check(disposition.Text.Contains("Alpha") && !disposition.Text.Contains("Beta"), "Disposition includes unselected detachment");
            Check(ArmyReferences.MissingStratagemDetachments(army, legacy).SequenceEqual(["Alpha"]), "Missing stratagems not detected");
            var supplied = legacy with { ReferenceCards = [.. legacy.ReferenceCards!, new(unit.Faction, "Stratagems", "Valid card", "WHEN: Command phase. TARGET: Bearer. EFFECT: Fixture.", "Alpha", "1 CP", supplement)] };
            Check(ArmyReferences.MissingStratagemDetachments(army, supplied).Count == 0, "Imported stratagem not recognized");
            Check(ArmyReferences.ForArmy(army, supplied).Any(c => c.Name == "Valid card"), "Supplemental stratagem filtered out");
            Check(ArmyReferences.ForArmy(army with { Detachment = "Beta" }, supplied).All(c => c.Name != "Valid card"), "Stratagem leaked to other detachment");
        });
        test("Existing SQLite reference snapshots recover on load without changing saved armies or history", () =>
        {
            var path = Path.Combine(root, "legacy-reference-cleanup");
            using var store = WindowsServices.OpenStore(path);
            store.Import(legacy);
            var persisted = legacy with { Units = store.LoadDataset()!.Units };
            var army = ArmyList.New(unit.Faction) with { Detachment = "Alpha", Entries = [new("entry", persisted.Units.Single().Key, unit.Name, 1, Enhancement: new("Alpha", "Ancient relic"))] };
            store.Save(army, new(army.Id, "entry", Screen: "roster"));
            var before = DataJson.Write(store.LoadArmies()); var context = DataJson.Write(store.LoadContext());
            // Simulate a snapshot persisted by the previous application version.
            using (var raw = new GrimList.Platform.Windows.WindowsSqliteConnectionFactory().Open(Path.Combine(path, "grimlist.db")))
                raw.Run("UPDATE dataset_snapshots SET content=?", DataJson.Write(persisted));
            var recovered = store.LoadDataset()!;
            Check(recovered.ReferenceCards!.Count == legacy.ReferenceCards!.Count - 4, "Old snapshot not cleaned on read");
            Check(DataJson.Write(store.LoadArmies()) == before && DataJson.Write(store.LoadContext()) == context, "Saved selections or context changed");
            Check(Enhancements.Find(army, recovered, army.Entries[0].Enhancement)?.Name == upgrade.Name, "Saved upgrade spelling no longer resolves");
            var workflow = new LibraryWorkflow(store);
            var preview = workflow.Prepare(legacy, ImportLayer.Combined);
            workflow.Apply(preview);
            Check(store.LoadDataset()!.ReferenceCards!.Count == recovered.ReferenceCards.Count, "Reimport resurrected bad references");
            Check(DataJson.Write(store.LoadArmies()) == before, "Reimport changed saved army");
        });
        var index = Array.IndexOf(args, "--legacy-reference-library");
        if (index >= 0 && index + 1 < args.Length) test("Shipped real library cleans Sisters scope and Death Guard duplicate enhancements", () =>
        {
            var shipped = DataJson.Read<Dataset>(File.ReadAllText(args[index + 1]));
            var clean = ReferenceData.Normalize(shipped);
            var sisters = ArmyList.New("Adepta Sororitas") with { Detachment = "Hallowed Martyrs" };
            var cards = ArmyReferences.ForArmy(sisters, clean).Where(c => !ArmyReferences.IsCore(c)).ToList();
            Check(cards.Where(c => c.Section == "Army Rules").Select(c => c.Name).SequenceEqual(["Acts of Faith"]), "Sisters global rules polluted");
            Check(cards.Where(c => c.Section == "Detachments").Select(c => c.Name).SequenceEqual(["The Blood of Martyrs"]), "Wrong selected detachment rules");
            var dg = clean.ReferenceCards!.Where(c => c.Faction == "Death Guard" && c.Section == "Enhancements").ToList();
            Check(dg.Count == 30 && dg.All(c => c.Name is not ("Feel No Pain" or "Devastating Wounds")), "Death Guard spurious choices remain");
            Check(dg.Count(c => c.Detachment == "Virulent Vectorium") == 4, "Legitimate Vectorium enhancements lost");
            Check(clean.ReferenceCards!.Count(c => c.Faction == "Death Guard" && c.Section == "Stratagems") == 45, "Existing supplemental stratagems lost");
            Check(DataJson.Write(shipped.Units) == DataJson.Write(clean.Units), "Unit/loadout/pricing data changed");
        });
        index = Array.IndexOf(args, "--reference-import-archive");
        if (index >= 0 && index + 1 < args.Length) test("Full real BSData archive imports scoped Sisters rules and named Death Guard enhancements", () =>
        {
            var imported = BsDataImporter.ImportAll(BsDataDocument.ReadArchive(File.ReadAllBytes(args[index + 1])), source);
            var cards = imported.Dataset.ReferenceCards!;
            var sisters = cards.Where(c => c.Faction == "Adepta Sororitas").ToList();
            Check(sisters.Where(c => c.Section == "Army Rules").Select(c => c.Name).SequenceEqual(["Acts of Faith"]), "Real shared detachment rules leaked globally");
            var dg = cards.Where(c => c.Faction == "Death Guard" && c.Section == "Enhancements" && c.Detachment == "Virulent Vectorium").ToList();
            Check(dg.Count == 4 && dg.Any(c => c.Name == "Revolting Regeneration") && dg.Any(c => c.Name == "Furnace of Plagues"), "Wrong real enhancement identities");
            Check(dg.All(c => c.Name is not ("Feel No Pain" or "Devastating Wounds")), "Linked keyword promoted to real enhancement");
            Check(dg.Single(c => c.Name == "Revolting Regeneration").Text.Contains("Feel No Pain"), "Enhancement effect lost");
            Check(!sisters.Any(c => c.Section == "Stratagems") && imported.Warnings.Any(w => w.Contains("Adepta Sororitas") && w.Contains("no detachment stratagem cards")), "Missing source text was invented or hidden");
            File.WriteAllText(Path.Combine(root, "real-reference-import.json"), DataJson.Write(imported.Dataset with { Units = [], ReferenceOnly = true }));
        });
    }
}
