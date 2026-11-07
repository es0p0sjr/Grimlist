using GrimList.Core;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

internal static class CatalogueAttachmentChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string[] args, string root)
    {
        var index = Array.IndexOf(args, "--attachment-diagnostic");
        if (index < 0 || index + 1 >= args.Length) return;
        var output = Path.GetFullPath(args[index + 1]);
        var trace = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "source-trace.json")))!;
        // Preserve the historical trace bytes; resolve its filenames in the promoted fixture revision.
        var revisionDirectory = Path.GetFullPath(Path.Combine(output, ".."));
        var sourcePath = Path.Combine(revisionDirectory, Path.GetFileName(trace["SourceMetadata"]!.ToString()));
        var source = DataJson.Read<SourceInfo>(File.ReadAllText(sourcePath));
        var archive = Path.Combine(revisionDirectory, Path.GetFileName(trace["Archive"]!.ToString()));
        var bytes = File.ReadAllBytes(archive);
        var data = BsDataImporter.Import(BsDataDocument.ReadArchive(bytes), source).Dataset;
        UnitDefinition Unit(string name) => data.Units.Single(u => u.Faction == "Death Guard" && u.Name == name);
        var body = Unit("Plague Marines"); var caster = Unit("Malignant Plaguecaster");
        test("Real revision-17 enhancement text exposes a generic bearer FNP grant", () =>
        {
            var card = data.ReferenceCards!.Single(c => c.Section == "Enhancements" && c.Name == "Revolting Regeneration");
            Check(EnhancementDefences.FeelNoPain(card).Single().Name == "5+++ FNP", "RAW enhancement grant was not parsed");
        });
        test("Real catalogue makes Poxwalkers Battleline only with Shamblerot and doubles their muster limit", () =>
        {
            var pox = Unit("Poxwalkers"); var rhino = Unit("Chaos Rhino"); var landRaider = Unit("Chaos Land Raider");
            var plain = ArmyList.New("Death Guard"); var shamblerot = plain with { Detachments = ["Shamblerot Vectorium"] };
            Check(pox.Profile!.DetachmentKeywords!.Count > 0, "Conditional source category discarded");
            Check(DatasheetStats.FeelNoPain(pox.Profile).Single().Name == "5+++ FNP", "Real Poxwalker FNP ability did not supply the defensive stat");
            Check(MusterRules.Category(plain, pox, data) == "Others" && MusterRules.Limit(plain, pox, data) == 3, "Poxwalkers gained unconditional Battleline");
            Check(MusterRules.Category(shamblerot, pox, data) == "Battleline" && MusterRules.Limit(shamblerot, pox, data) == 6, "Shamblerot keyword/copy limit missing");
            Check(MusterRules.Limit(shamblerot with { Limit = 1000 }, pox, data) == 4, "Incursion did not use doubled base 2");
            Check(MusterRules.Category(plain, rhino, data) == "Dedicated Transports" && MusterRules.Limit(plain, rhino, data) == 6, "Dedicated Transport misclassified");
            Check(MusterRules.Category(plain, landRaider, data) == "Vehicles" && MusterRules.Limit(plain, landRaider, data) == 3, "Transport alone granted six copies");
            File.WriteAllText(Path.Combine(root, "muster-fixture.json"), DataJson.Write(data with { Units = [pox, rhino, landRaider, body, caster, Unit("Typhus")] }));
        });
        ArmyList Army(int size, string partner) => ArmyList.New("Death Guard") with { Entries =
            [new("body", body.Key, body.Name, size), new("caster", caster.Key, caster.Name, 1), new("partner", Unit(partner).Key, partner, 1)] };
        test("Real revision-17 catalogue preserves bodyguard association constraints and candidate-dependent modifiers", () =>
        {
            Check(Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase), "Wrong cached archive");
            var associations = body.Profile!.Associations!;
            Check(associations.SourceEntryId == "8467-b3a2-2597-de0d" && associations.Warnings.Count == 0, "Plague Marines association import incomplete");
            var limit = associations.Constraints.Single(c => c.Id == "7d52-af78-d77f-b4f3");
            Check(limit.Value == 1 && limit.ChildId == "1556-9b56-fba6-4370" && limit.Modifiers.Single().Operation == "set" && limit.Modifiers.Single().Value == 2,
                "Base constraint or conditional SET was discarded");
            Check(Unit("Biologus Putrifier").Profile!.AttachmentRules!.AdditionalAttachments.Count == 0, "Test must use RAW structural permission, not injected text");
            Check(caster.Profile!.AttachmentRules!.AdditionalAttachments.Count == 0, "Other Leader must not need an exception");
            foreach (var partner in new[] { "Biologus Putrifier", "Tallyman", "Foul Blightspawn" })
            foreach (var size in body.Options.Where(o => o.FromCopy == 1).Select(o => o.Models))
            foreach (var order in new[] { new[] { "caster", "partner" }, new[] { "partner", "caster" } })
            {
                var army = Army(size, partner);
                var both = LeaderRules.Attach(LeaderRules.Attach(army, data, order[0], "body"), data, order[1], "body");
                Check(LeaderRules.Issues(both, data).Count == 0, $"Real {partner} combination rejected at {size} models in {order[0]}-first order");
                var evaluation = AssociationRules.Evaluate(army, data, army.Entries[0], ["caster", "partner"]);
                Check(evaluation.Constraints.Single().Base == 1 && evaluation.Constraints.Single().Effective == 2 && evaluation.Constraints.Single().Actual == 2,
                    "Candidate-dependent association count not used");
            }
            var proposed = Army(10, "Biologus Putrifier");
            File.WriteAllText(Path.Combine(output, "bodyguard-association-diagnostic.txt"), AssociationRules.Describe(proposed, data, proposed.Entries[0], ["caster", "partner"]));
            File.WriteAllText(Path.Combine(output, "bodyguard-association-import.json"), DataJson.Write(body.Profile.Associations));
            // Actual imported source fixture used by native UI tests (no altered rule text).
            File.WriteAllText(Path.Combine(root, "real-association-fixture.json"), DataJson.Write(data with { Units = [body, caster, Unit("Biologus Putrifier"), Unit("Tallyman")] }));
        });
        test("Real catalogue does not grant two ordinary Leaders a global second slot; persistence retains structural rules", () =>
        {
            var army = Army(10, "Biologus Putrifier");
            var plain = army with { Entries = [army.Entries[0], army.Entries[1], army.Entries[1] with { Id = "ordinary-b" }] };
            foreach (var order in new[] { new[] { "caster", "ordinary-b" }, new[] { "ordinary-b", "caster" } })
            {
                var first = LeaderRules.Attach(plain, data, order[0], "body");
                Check(LeaderRules.Problem(first, data, order[1], "body")?.Contains("effective max = 1") == true, "Two plain Leaders bypassed the base maximum");
            }
            var initial = LeaderRules.Attach(army, data, "caster", "body");
            Check(AssociationRules.Evaluate(initial, data, initial.Entries[0], ["caster"]).Constraints.Single().Effective == 1, "Current context unexpectedly has a raised limit");
            Check(AssociationRules.Evaluate(initial, data, initial.Entries[0], ["caster", "partner"]).Constraints.Single().Effective == 2, "Proposed candidate did not raise limit");
            var pair = LeaderRules.Attach(initial, data, "partner", "body");
            Check(LeaderRules.Problem(pair with { Entries = [.. pair.Entries, pair.Entries[1] with { Id = "third", AttachedToId = "" }] }, data, "third", "body") != null, "Third Leader bypassed effective max 2");
            using var store = WindowsServices.OpenStore(Path.Combine(root, "real-association-persistence")); store.Import(data); store.Save(pair);
            Check(LeaderRules.Issues(store.LoadArmies().Single(), store.LoadDataset()).Count == 0, "Reload lost structural permission");
            Check(store.LoadDataset()!.FindUnit(body.Key)!.Profile!.Associations!.Constraints.Single().Value == 1, "Persisted global limit was increased");
        });
    }
}
