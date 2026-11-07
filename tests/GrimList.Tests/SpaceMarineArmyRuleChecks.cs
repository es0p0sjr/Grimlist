using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class SpaceMarineArmyRuleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Dataset Fixture() => new(new("BSData fixture", "old", "2026-10-02", "https://example.test", Edition: "11"),
        [new("Space Marines", "Marine", [new(5, 100)], Profile: new(Abilities: [new("Oath of Moment", "Old unit text")]))],
        ReferenceCards: [new("Space Marines", "Army Rules", "Oath of Moment", "Old army rule"),
            new("Space Marines", "Army Rules", "Chapter rule", "Keep"),
            new("Space Marines", "Detachments", "Combat Doctrines", "Old Gladius rule", "Gladius Task Force")],
        FactionContexts: [new("Space Marines", "e0af-67df-9d63-8fb7", [])]);
    private static ReferenceCard Rule(Dataset data) => ArmyReferences.ForArmy(ArmyList.New("Space Marines"), data)
        .Single(c => c.Section == "Army Rules" && c.Name == "Combat Doctrines");
    public static void Run(Action<string, Action> test, string root)
    {
        test("Space Marine army rule replaces obsolete references while preserving unit data, Chapters and editions", () => {
            var original = Fixture(); var updated = ReferenceData.Normalize(original);
            var card = Rule(updated);
            Check(card.Text.Contains("Command phase") && card.Text.Contains("next Command Phase") && card.Text.Contains("[ASSAULT]")
                && card.Text.Contains("once per battle") && card.Text.Contains("Only one Combat Doctrine"), "Incomplete rule");
            Check(!WeaponRules.WithTextFallback(card.Modifiers, card.Name, card.Text, rosterRule: true).Any(), "Temporary doctrine became a permanent weapon bonus");
            Check(updated.ReferenceCards!.All(c => c.Name != "Oath of Moment" && !(c.Name == "Combat Doctrines" && c.Detachment == "Gladius Task Force"))
                && updated.ReferenceCards!.Any(c => c.Name == "Chapter rule"), "Wrong reference retirement");
            Check(ReferenceEquals(original.Units, updated.Units) && ReferenceEquals(updated, ReferenceData.Normalize(updated)), "Units changed or repair not idempotent");
            var ten = original with { Source = original.Source with { Edition = "10" } };
            Check(ReferenceEquals(ten, SpaceMarineArmyRule.Apply(ten)), "10e changed");
            var other = original with { Game = "Other" };
            Check(ReferenceEquals(other, SpaceMarineArmyRule.Apply(other)), "Other game changed");
            var unknown = original with { ReferenceCards = [], FactionContexts = [new("Space Marines", "unknown", [])] };
            Check(ReferenceEquals(unknown, SpaceMarineArmyRule.Apply(unknown)), "Unknown catalogue supplemented");
        });
        test("Newer unit rules, manual cards, tombstones and rules-pack ownership supersede the army-rule fallback", () => {
            var data = Fixture(); var future = data.Source with { Date = "2026-12-01" };
            var current = data with { Units = [data.Units[0] with { ProfileSource = future,
                Profile = new(Abilities: [new("Combat Doctrines", "Future source text")]) }] };
            Check(Rule(ReferenceData.Normalize(current)).Text == "Future source text", "Future unit rule pinned to bundled text");
            var manual = new ReferenceCard("Space Marines", "Army Rules", "Combat Doctrines", "Manual", Source: future with { Name = "Manual" });
            Check(Rule(ReferenceData.Normalize(data with { ReferenceCards = [manual] })).Text == "Manual", "Manual reference overwritten");
            var tombstone = ReferenceModel.Canonical(manual with { Source = future, Details = new(Status: "Removed") }, future);
            Check(!ArmyReferences.ForArmy(ArmyList.New("Space Marines"), ReferenceData.Normalize(data with { ReferenceCards = [tombstone] })).Any(c => c.Name == "Combat Doctrines"), "Tombstone resurrected");
            var pack = new RulesPack("fixture", 1, future, ["Space Marines"], [], [], [], RemoveCards: [new("Space Marines", "Army Rules", "Combat Doctrines")]);
            Check(!ReferenceData.Normalize(data with { RulesPacks = [pack] }).ReferenceCards!.Any(c => c.Name == "Combat Doctrines"), "Pack removal ignored");
        });
        test("Actual bundled Marine catalogues all display Combat Doctrines without a selected detachment", () => {
            var data = ReferenceData.Normalize(BootstrapRelease.ReadBundled().Dataset);
            var factions = data.ReferenceCards!.Where(c => c.Section == "Army Rules" && c.Name == "Combat Doctrines").Select(c => c.Faction).ToList();
            Check(factions.Count == 12, "Missing Marine Chapter rules");
            foreach (var faction in factions)
                Check(ArmyReferences.ForArmy(ArmyList.New(faction), data).Count(c => c.Section == "Army Rules" && c.Name == "Combat Doctrines") == 1, "Unmatched or duplicate army rule: " + faction);
        });
        test("Legacy storage, BSData reimport, Android recovery, export and restart retain the new army rule and saved armies", () => {
            var path = Path.Combine(root, "space-marine-army-rule"); var data = Fixture(); var army = ArmyList.New("Space Marines");
            using (var store = WindowsServices.OpenStore(path)) {
                store.ApplyImport(data); store.Save(army);
                using (var db = new WindowsSqliteConnectionFactory().Open(Path.Combine(path, "grimlist.db")))
                    db.Run("UPDATE dataset_snapshots SET content=? WHERE source_id=(SELECT value FROM settings WHERE key='active_source')", DataJson.Write(data));
                Check(Rule(store.LoadDataset()!).Text.Contains("[ASSAULT]"), "Existing database not repaired");
                var flow = new LibraryWorkflow(store);
                foreach (var layer in new[] { ImportLayer.Profiles, ImportLayer.Points, ImportLayer.Combined }) flow.Apply(flow.Prepare(data, layer));
                var recovered = ReferenceLibraryRecovery.Supplement(store.LoadDataset()!, data with { Units = [], ReferenceOnly = true });
                Check(Rule(recovered).Text.Contains("[ASSAULT]"), "Import/recovery lost new rule");
                Check(ArmyReferences.ForArmy(army, recovered).All(c => c.Name != "Oath of Moment"), "Recovery resurrected Oath");
                Check(DataJson.Write(store.LoadArmies().Single()) == DataJson.Write(army), "Saved army rewritten");
            }
            using var reopened = WindowsServices.OpenStore(path);
            Check(Rule(ReferenceModel.ExportDataset(reopened.LoadDataset()!)).Text.Contains("[ASSAULT]"), "Restart/export lost rule");
        });
    }
}
