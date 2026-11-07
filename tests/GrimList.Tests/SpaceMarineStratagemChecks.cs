using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class SpaceMarineStratagemChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static List<ReferenceCard> Cards(Dataset data, string faction, params string[] detachments) =>
        ArmyReferences.ForArmy(ArmyList.New(faction) with { Detachments = detachments.ToList() }, data)
            .Where(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)).ToList();
    public static void Run(Action<string, Action> test, string root)
    {
        var baseline = ReferenceData.Normalize(BootstrapRelease.ReadBundled().Dataset);
        var legacy = ReferenceLibraryRecovery.ReadBundled();
        test("Wahapedia snapshot supplies current generic and Chapter stratagems with complete fields and provenance", () => {
            foreach (var faction in baseline.FactionContexts!.Where(c => c.Faction == "Space Marines" || c.Faction == "Deathwatch"
                || c.Faction == "Ultramarines" || c.Faction == "Blood Angels" || c.Faction == "Dark Angels" || c.Faction == "Black Templars"
                || c.Faction == "Space Wolves" || c.Faction == "Imperial Fists" || c.Faction == "Iron Hands" || c.Faction == "Raven Guard"
                || c.Faction == "Salamanders" || c.Faction == "White Scars").Select(c => c.Faction))
            foreach (var definition in DetachmentRules.Options(faction, baseline)) {
                var cards = Cards(baseline, faction, definition.Name);
                Check(cards.Count is 3 or 4 or 6, faction + "/" + definition.Name + ": missing current set");
                Check(cards.All(c => c.Source?.Date == "2026-10-14" && c.Source.Url.Contains("wahapedia.ru/wh40k11ed")
                    && c.Details!.Timing.Length > 0 && c.Details.Target.Length > 0 && c.Details.Effect.Length > 0
                    && !c.DisplayText.Contains("Errata") && !c.DisplayText.Contains('\ue200')), "Malformed/unattributed card");
            }
            Check(Cards(baseline, "Space Marines", "Gladius Task Force").Count == 6, "Gladius count");
            Check(Cards(baseline, "Space Marines", "Ironstorm Spearhead").Count == 3, "Old Ironstorm set survived");
            Check(Cards(baseline, "Dark Angels", "Wrath of the Rock").Any(c => c.Name == "IN SACRIFICE, VICTORY"), "New Dark Angels card missing");
            Check(Cards(baseline, "Dark Angels", "Wrath of the Rock").All(c => c.Name != "ARMOUR OF CONTEMPT" && c.Name != "LEONINE AGGRESSION"), "Old Dark Angels cards survived");
            Check(StratagemTiming.FromText(Cards(baseline, "Dark Angels", "Wrath of the Rock").Single(c => c.Name == "KNIGHTS OF IRON").DisplayText) == StratagemTurn.Yours, "Slash-separated phase timing lost its owner");
            Check(ReferenceEquals(baseline, ReferenceData.Normalize(baseline)), "Normalization not idempotent");
            baseline.Validate();
        });
        test("Selected detachments and Chapters control stratagem visibility, including repeated names", () => {
            Check(Cards(baseline, "Ultramarines").Count == 0, "Unselected cards leaked");
            Check(Cards(baseline, "Ultramarines", "Wrath of the Rock").Count == 0, "Another Chapter leaked");
            Check(Cards(baseline, "Ultramarines", "Blade of Ultramar").Count == 6, "Chapter cards absent");
            var multiple = Cards(baseline, "Space Marines", "Assault Brethren", "Tactical Brethren");
            Check(multiple.Count == 6 && multiple.Count(c => c.Name == "ARMOUR OF CONTEMPT") == 2, "Repeated names collapsed across detachments");
            Check(Cards(baseline, "Space Marines", "Anvil Siege Force").Count == 0, "Retired detachment restored");
        });
        test("Deathwatch Support receives four references without changing its BSData placeholder", () => {
            var placeholder = new DetachmentDefinition("Space Marines", "Deathwatch Support (ITS NOT READY)", Cost: "1 DP");
            var data = baseline with { Detachments = [.. baseline.Detachments!, placeholder] };
            var updated = SpaceMarineStratagems.Apply(data);
            Check(ReferenceEquals(data.Detachments, updated.Detachments) && updated.Detachments!.Contains(placeholder), "Placeholder semantics rewritten");
            Check(Cards(updated, "Space Marines", placeholder.Name).Count == 4, "Support references missing");
            Check(Cards(updated, "Space Marines", placeholder.Name).Any(c => c.Name == "BLACKSTAR EXTRACTION"), "Wrong Support set");
        });
        test("Legacy source retirement preserves units, detachment definitions, other factions and manual references", () => {
            var original = baseline with { ReferenceCards = [.. baseline.ReferenceCards!.Where(c => c.Section != "Stratagems"), .. legacy.ReferenceCards!], ReferenceCoverage = legacy.ReferenceCoverage };
            var manual = new ReferenceCard("Space Marines", "Stratagems", "Personal reference", "Keep", "Gladius Task Force", Source: new("Manual", "1", "", "", Edition: "11"));
            original = original with { ReferenceCards = [.. original.ReferenceCards!, manual] };
            var updated = SpaceMarineStratagems.Apply(original);
            Check(ReferenceEquals(original.Units, updated.Units) && ReferenceEquals(original.Detachments, updated.Detachments), "Army-building data modified");
            Check(updated.ReferenceCards!.Contains(manual), "Manual reference removed");
            Check(legacy.ReferenceCards!.Where(c => c.Faction == "Death Guard").All(updated.ReferenceCards.Contains), "Other faction changed");
            Check(Cards(updated, "Space Marines", "Gladius Task Force").Count == 7, "Mixed old/current Gladius");
            Check(ReferenceData.Normalize(legacy).ReferenceCards!.All(c => c.Faction != "Space Marines"), "Reference-only legacy import not scrubbed");
            var unresolved = legacy with { ReferenceCards = legacy.ReferenceCards!.Select(c => c with { Details = c.Details! with { Status = "Unresolved" } }).ToList() };
            Check(ReferenceData.Normalize(unresolved).ReferenceCards!.All(c => c.Faction != "Space Marines"), "Unresolved legacy cards blocked replacement");
            foreach (var value in new[] { baseline with { Game = "Other" }, baseline with { Source = baseline.Source with { Edition = "10" } },
                new Dataset(baseline.Source, baseline.Units, FactionContexts: [new("Space Marines", "unknown", [])]) })
                Check(ReferenceEquals(value, SpaceMarineStratagems.Apply(value)), "Unrelated game/edition/catalogue changed");
        });
        test("Manual overrides, tombstones, future complete sources and rules packs supersede bundled stratagems", () => {
            var card = Cards(baseline, "Space Marines", "Gladius Task Force").First();
            var stripped = baseline with { ReferenceCards = baseline.ReferenceCards!.Where(c => c.Section != "Stratagems").ToList(), ReferenceCoverage = [] };
            var manual = card with { Source = card.Source! with { Name = "Manual", Url = "", Date = "" }, Details = null, Text = "Manual" };
            var removed = card with { Details = card.Details! with { Status = "Removed" } };
            foreach (var supplied in new[] { manual, removed }) {
                var updated = SpaceMarineStratagems.Apply(stripped with { ReferenceCards = [.. stripped.ReferenceCards!, supplied] });
                Check(updated.ReferenceCards!.Count(c => c.Faction == card.Faction && c.Detachment == card.Detachment && c.Name == card.Name) == 1, "Override resurrected/duplicated");
                Check(updated.ReferenceCards!.Contains(supplied), "Explicit reference overwritten");
            }
            var future = card.Source! with { Date = "2026-12-01", Version = "future" };
            var complete = new ReferenceCoverage(card.Details!.SourceId, "11", Faction: card.Faction, Detachment: card.Detachment,
                State: "Complete", Mode: "CompleteForDetachment", Authoritative: true, Source: future);
            var newer = SpaceMarineStratagems.Apply(stripped with { ReferenceCards = [.. stripped.ReferenceCards!, card with { Source = future, Text = "Future" }], ReferenceCoverage = [complete] });
            Check(Cards(newer, card.Faction, card.Detachment).Count == 1, "Future complete source filled with old cards");
            var reimported = DatasetMerge.Prepare(newer, BootstrapRelease.ReadBundled().Dataset, ImportLayer.Profiles).Dataset;
            Check(Cards(reimported, card.Faction, card.Detachment).Count == 1
                && Cards(reimported, card.Faction, card.Detachment)[0].Text == "Future", "BSData reimport downgraded newer references");
            foreach (var scope in new[] { complete with { Detachment = "", Mode = "CompleteForFaction" },
                complete with { Faction = "", Detachment = "", Mode = "CompleteForEdition" } }) {
                var scoped = SpaceMarineStratagems.Apply(stripped with { ReferenceCards = [card with { Source = future }], ReferenceCoverage = [scope] });
                Check(Cards(scoped, card.Faction, card.Detachment).Count == 1, "Broader complete source was filled from the fallback");
            }
            var pack = new RulesPack("stratagem-override", 1, future, [card.Faction], [], [], [], RemoveCards: [new(card.Faction, card.Section, card.Name, card.Detachment)]);
            Check(SpaceMarineStratagems.Apply(stripped with { RulesPacks = [pack] }).ReferenceCards!.All(c => c.Faction != card.Faction
                || c.Detachment != card.Detachment || c.Name != card.Name), "Rules-pack removal ignored");
        });
        test("BSData and legacy reference imports, Android recovery, export and restart preserve current stratagems and rosters", () => {
            var directory = Path.Combine(root, "space-marine-stratagems");
            var army = ArmyList.New("Dark Angels") with { Name = "Preservation", Detachment = "Wrath of the Rock" };
            using (var store = WindowsServices.OpenStore(directory)) {
                store.ApplyImport(baseline); store.Save(army);
                var flow = new LibraryWorkflow(store);
                var marineFactions = baseline.ReferenceCards!.Where(c => c.Source?.Name == "Wahapedia - Space Marines").Select(c => c.Faction).ToHashSet();
                var oldMarineReferences = legacy with {
                    ReferenceCards = legacy.ReferenceCards!.Where(c => marineFactions.Contains(c.Faction)).ToList(),
                    ReferenceCoverage = legacy.ReferenceCoverage!.Where(c => marineFactions.Contains(c.Faction)).ToList(),
                    Detachments = legacy.Detachments!.Where(c => marineFactions.Contains(c.Faction)).ToList() };
                var oldPreview = flow.Prepare(oldMarineReferences, ImportLayer.Profiles);
                Check(oldPreview.CanApply, "Legacy reference conflict: " + string.Join("; ", oldPreview.ReferenceChanges.Where(c => c.Status == "Conflict").Take(5).Select(c => c.Faction + "/" + c.Detachment + "/" + c.Name + ": " + c.Message)));
                flow.Apply(oldPreview);
                var incoming = BootstrapRelease.ReadBundled().Dataset;
                foreach (var layer in new[] { ImportLayer.Points, ImportLayer.Profiles, ImportLayer.Combined }) {
                    var preview = flow.Prepare(incoming, layer);
                    Check(preview.CanApply, "BSData conflict: " + string.Join("; ", preview.ReferenceChanges.Where(c => c.Status == "Conflict").Take(5).Select(c => c.Faction + "/" + c.Detachment + "/" + c.Name + ": " + c.Message)));
                    flow.Apply(preview);
                }
                var recovered = ReferenceLibraryRecovery.Supplement(store.LoadDataset()!, legacy);
                Check(Cards(recovered, "Dark Angels", "Wrath of the Rock").Count == 6, "Reimport/restoration changed cards");
                Check(ReferenceEquals(recovered, ReferenceLibraryRecovery.Supplement(recovered, legacy)), "Recovery not idempotent");
                Check(DataJson.Write(store.LoadArmies().Single()) == DataJson.Write(army), "Saved roster changed");
            }
            using var reopened = WindowsServices.OpenStore(directory);
            var export = DataJson.Read<Dataset>(DataJson.Write(ReferenceModel.ExportDataset(reopened.LoadDataset()!)));
            Check(Cards(export, "Space Marines", "Ironstorm Spearhead").Count == 3, "Restart/export lost new set");
        });
    }
}
