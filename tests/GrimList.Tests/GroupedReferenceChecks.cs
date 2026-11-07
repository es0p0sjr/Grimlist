using GrimList.Core;

internal static class GroupedReferenceChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static IEnumerable<AbilityCard> Flatten(IEnumerable<AbilityCard> cards) => cards.SelectMany(c => new[] { c }.Concat(Flatten(c.Options)));
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var source = new SourceInfo("Fixture", "1", "", "", Edition: "11");
        var unit = new UnitDefinition("Death Guard", "Fixture", [new(1, 10)]);
        var data = new Dataset(source, [unit]);
        test("Core stratagems include complete operative sections, restrictions and optional modes offline", () =>
        {
            foreach (var edition in new[] { "10", "11" })
            {
                var cards = CoreStratagems.ForEdition(edition);
                Check(cards.All(c => c.Text.StartsWith("WHEN:") && c.Text.Contains("TARGET:") && c.Text.Contains("EFFECT:")
                    && !c.Text.Contains("REMINDER:") && !c.Text.Contains("\ue200")
                    && !System.Text.RegularExpressions.Regex.IsMatch(c.Text, @"\bL\d+:")), "Core cards contain summaries or incomplete source text");
                Check(cards.Single(c => c.Name == "Insane Bravery").Text.Contains("RESTRICTIONS:"), "Once-per-battle restriction missing");
                Check(cards.Single(c => c.Name == "Rapid Ingress").Text.Contains("RESTRICTIONS:"), "Arrival restriction missing");
                var overwatch = cards.Single(c => c.Name == "Fire Overwatch");
                Check(StratagemTiming.FromText(overwatch.Text) == StratagemTurn.Opponents, "Overwatch timing has wrong owner");
                Check(overwatch.Text.Contains("TITANIC") && overwatch.Text.Contains("24\"") && overwatch.Text.Contains("6"), "Overwatch targeting/hit restrictions missing");
                if (edition == "11")
                {
                    Check(overwatch.Text.Contains("SNAP SHOOTING") && overwatch.Text.Contains("cannot re-roll hit rolls")
                        && overwatch.Text.Contains("AFTER SHOOTING:"), "Snap shooting procedure incomplete");
                    var intervention = cards.Single(c => c.Name == "Heroic Intervention").Text;
                    Check(intervention.Contains("Leap to Defend") && intervention.Contains("Into the Fray") && intervention.Contains("+1CP")
                        && intervention.Contains("greater than 6"), "Optional intervention mode/cost/cap missing");
                }
                else Check(overwatch.Text.Contains("once per turn"), "10e Overwatch use limit missing");
                cards.Clear();
                Check(CoreStratagems.ForEdition(edition).Count > 0, "Caller can mutate bundled cards");
            }
        });
        test("Universal stratagems follow editions for every faction without a detachment", () =>
        {
            foreach (var faction in new[] { "Death Guard", "Orks", "Space Marines", "New faction" })
            foreach (var edition in new[] { "10", "11" })
            {
                var cards = ArmyReferences.ForArmy(ArmyList.New(faction), data with { Source = source with { Edition = edition } });
                Check(cards.Count == (edition == "11" ? 10 : 11), "Core set missing");
                Check(cards.All(c => ArmyReferences.IsCore(c) && c.Source?.Edition == edition && c.Source.Url.StartsWith("https://")), "Edition/source lost");
                Check(cards.All(c => StratagemTiming.FromText(c.Text) != StratagemTurn.Unknown), "Timing colour missing");
                Check(cards.Any(c => c.Name == "Explosives") == (edition == "11"), "Mixed core editions");
                Check(StratagemTiming.FromText(cards.Single(c => c.Name.StartsWith("Counter")).Text) ==
                    (edition == "11" ? StratagemTurn.Opponents : StratagemTurn.Either), "Counteroffensive has wrong turn");
            }
            Check(ArmyReferences.ForArmy(ArmyList.New("Orks"), data with { Source = source with { Edition = "" } }).Count == 0, "Unknown edition guessed");
            Check(ArmyReferences.ForArmy(ArmyList.New("Orks") with { Game = "Other" }, data with { Game = "Other" }).Count == 0, "Core rules leaked to another game");
        });
        test("Imported core overrides deduplicate; factions, editions and detachments stay scoped", () =>
        {
            var army = ArmyList.New("Death Guard") with { Detachment = "Selected" };
            var cards = data with { Detachments = [new("Death Guard", "Selected"), new("Death Guard", "Other")], ReferenceCards = [
                new("core", "Stratagems", "COMMAND RE-ROLL", "Full imported rule", Source: source),
                new("Core", "Stratagems", "Old edition", "Must not display", Source: source with { Edition = "10" }),
                new("Death Guard", "Stratagems", "Army wide", "Always"),
                new("Death Guard", "Stratagems", "Selected", "Visible", "Selected"),
                new("Death Guard", "Stratagems", "Other detachment", "Hidden", "Other"),
                new("Orks", "Stratagems", "Other faction", "Hidden")
            ] };
            var before = DataJson.Write(cards);
            var result = ArmyReferences.ForArmy(army, cards);
            Check(result.Count == 12 && result.Count(ArmyReferences.IsCore) == 10, "Wrong reference scope/count");
            Check(result.Single(c => UnitIdentity.Normalize(c.Name) == "COMMANDREROLL").Text == "Full imported rule", "Bundled rule replaced import");
            Check(RosterExport.Rules(army, cards).SequenceEqual(result), "Export differs from army rules");
            Check(DataJson.Write(cards) == before, "Display mutated stored library");
            var imported = new Dataset(source, [], ReferenceCards: [new("Core", "Stratagems", "Custom core", "Custom rule")], ReferenceOnly: true);
            using var store = WindowsServices.OpenStore(Path.Combine(root, "core-stratagem-import"));
            store.Import(data); store.Save(army);
            store.ApplyImport(store.PreviewImport(imported, ImportLayer.Profiles).Dataset);
            Check(ArmyReferences.ForArmy(army, store.LoadDataset()).Count == 11, "Core import did not persist");
            Check(store.LoadArmies().Single().Id == army.Id, "Core import changed saved army");
        });
        test("Ability options group by imported section and retain order, text and unrelated rules", () =>
        {
            List<AbilityProfile> abilities = [new("First option", "One", "Command"), new("Command", "Choose one once per turn."),
                new("Independent", "Keep"), new("Second option", "Two", "command"), new("Third option", "Three", "Command")];
            var before = DataJson.Write(abilities);
            var cards = AbilityGroups.Create(abilities);
            Check(cards.Count == 2 && cards[0].Name == "Command" && cards[0].Text == abilities[1].Text, "Parent rule missing");
            Check(cards[0].Options.Select(c => c.Name).SequenceEqual(["First option", "Second option", "Third option"]), "Options detached or reordered");
            Check(Flatten(cards).Count() == abilities.Count && DataJson.Write(abilities) == before, "Abilities lost or mutated");
            Check(AbilityGroups.Create(abilities.Where(a => a.Name != "Command")).Count == 4, "Orphan options disappeared");
        });
        test("Ambiguous and cyclic ability relationships keep every rule accessible", () =>
        {
            foreach (List<AbilityProfile> abilities in new List<AbilityProfile>[] {
                [new("A", "One", "B"), new("B", "Two", "A")],
                [new("Parent", "One"), new("Parent", "Two"), new("Option", "Three", "Parent")],
                [new("Abilities", "Named like generic section"), new("Other", "Keep standalone")],
                [new("A", "One"), new("B", "Two", "A"), new("C", "Three", "B")]
            }) Check(Flatten(AbilityGroups.Create(abilities)).Count() == abilities.Count, "Relationship lost a rule");
        });
        var index = Array.IndexOf(args, "--reference-catalogue");
        if (index >= 0 && index + 1 < args.Length) test("Real Mortarion import nests all three Lord of the Death Guard abilities", () =>
        {
            var path = args[index + 1]; var document = BsDataDocument.Parse(path, File.ReadAllText(path));
            document.Root.Remove("selectionEntries");
            document.Root["entryLinks"] = new System.Text.Json.Nodes.JsonArray(document.Root["entryLinks"]!.AsArray()
                .Single(n => n?["name"]?.ToString() == "Mortarion")!.DeepClone());
            var imported = BsDataImporter.Import([document], source).Dataset;
            var mortarion = imported.Units.Single(u => u.Name == "Mortarion");
            var abilities = mortarion.Profile!.Abilities!;
            var cards = AbilityGroups.Create(abilities);
            var parent = cards.Single(c => c.Name == "Lord of the Death Guard");
            Check(parent.Options.Select(c => c.Name).SequenceEqual(["Diseased Influence", "Boon of Death", "Inflamed Reprisal"]), "Mortarion options missing");
            Check(Flatten(cards).Count() == abilities.Count, "Real import lost rules");
            File.WriteAllText(Path.Combine(root, "ability-groups-fixture.json"), DataJson.Write(imported with { Units = [mortarion] }));
        });
    }
}
