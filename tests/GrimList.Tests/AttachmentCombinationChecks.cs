using System.Xml.Linq;
using GrimList.Core;

internal static class AttachmentCombinationChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private const string Targets = "This model can be attached to the following units:\n\u25a0 Plague Marines\n\n";
    private const string ExceptionText = "You can attach this model to a Plague Marines unit, even if one other Leader unit has already been attached to it (you cannot attach more than one of the same Leader to the same unit). If you do, and that Bodyguard unit is destroyed, the Leader units attached to it become separate units, with their original Starting Strengths.";
    private static Dataset Fixture()
    {
        // The screenshot's explicit rule, not a correction to the user's installed 11e catalogue.
        XElement Unit(string id, string name, string? rule = null) => new("selectionEntry", new XAttribute("id", id), new XAttribute("name", name), new XAttribute("type", "model"),
            new XElement("costs", new XElement("cost", new XAttribute("name", "pts"), new XAttribute("typeId", "points"), new XAttribute("value", "50"))),
            new XElement("profiles", new XElement("profile", new XAttribute("name", name), new XAttribute("typeName", "Unit"), new XElement("characteristics", new XElement("characteristic", new XAttribute("name", "M"), "5"))),
                rule == null ? null : new XElement("profile", new XAttribute("name", "Leader"), new XAttribute("typeName", "Abilities"), new XElement("characteristics", new XElement("characteristic", new XAttribute("name", "Description"), rule)))));
        var root = new XElement("catalogue", new XAttribute("id", "dg"), new XAttribute("name", "Chaos - Death Guard"), new XElement("selectionEntries",
            Unit("body", "Plague Marines"), Unit("caster", "Malignant Plaguecaster", Targets), Unit("biologus", "Biologus Putrifier", Targets + ExceptionText),
            Unit("ordinary", "Ordinary Leader fixture", Targets), Unit("other-body", "Other Bodyguard fixture")));
        return BsDataImporter.Import([BsDataDocument.Parse("fixture.cat", root.ToString())], new("Attachment combination fixtures", "1", "", "", Edition: "10")).Dataset;
    }
    private static ArmyList Army(Dataset data) => ArmyList.New("Death Guard") with { Entries = data.Units.Select(u =>
        new ArmyEntry(u.Name, u.Key, u.Name, u.Options[0].Models)).ToList() };
    private const string Body = "Plague Marines", Caster = "Malignant Plaguecaster", Biologus = "Biologus Putrifier", Ordinary = "Ordinary Leader fixture";

    public static void Run(Action<string, Action> test, string root)
    {
        var uiFixture = Fixture();
        uiFixture = uiFixture with { Units = uiFixture.Units.Select(u => u.Name == Body ? u with { Options = [new(5, 90), new(10, 175)] } : u).ToList() };
        File.WriteAllText(Path.Combine(root, "attachment-combination-fixture.json"), DataJson.Write(uiFixture));
        test("Imported semantics grant Biologus's exception locally without increasing the Plague Marines Leader limit", () =>
        {
            var data = Fixture();
            AttachmentDefinition Rules(string name) => data.Units.Single(u => u.Name == name).Profile!.AttachmentRules!;
            Check(Rules(Body).BodyguardRules.Count == 0, "Character exception changed the body's global limit");
            Check(Rules(Caster).BodyguardTargets.SequenceEqual(new[] { Body }) && Rules(Caster).AdditionalAttachments.Count == 0, "Plain Leader acquired an exception");
            var exception = Rules(Biologus).AdditionalAttachments.Single();
            Check(exception.AnyLeader && exception.ForbidDuplicateLeaders && exception.BodyguardTargets.SequenceEqual(new[] { Body }), "Exception semantics lost its restrictions");
            Check(!Rules(Biologus).IsSupport && !Rules(Caster).IsSupport, "Test must exercise two Leaders, not a Support slot");
            var army = Army(data);
            foreach (var order in new[] { new[] { Caster, Biologus }, new[] { Biologus, Caster } })
            foreach (var entries in new[] { army.Entries, army.Entries.AsEnumerable().Reverse().ToList() })
            {
                var proposed = army with { Entries = entries };
                Check(LeaderRules.CombinationProblem(proposed, data, Body, order) == null, "Combination rejected based on selection or roster order");
                var attached = LeaderRules.Attach(LeaderRules.Attach(proposed, data, order[0], Body), data, order[1], Body);
                Check(LeaderRules.Issues(attached, data).Count == 0, "Asymmetric exception was not accepted from either member");
                Check(LeaderRules.Problem(attached, data, Ordinary, Body) != null, "Accepted a third attachment");
            }
            Check(LeaderRules.CombinationProblem(army, data, Body, new[] { Caster, Ordinary }) != null, "Two plain Leaders inherited the exception");
            var duplicate = army with { Entries = [.. army.Entries, army.Entries.Single(e => e.Id == Biologus) with { Id = "duplicate" }] };
            Check(LeaderRules.CombinationProblem(duplicate, data, Body, new[] { Biologus, "duplicate" })?.Contains("same Leader") == true, "Duplicate ban lost");
        });
        test("Combination validation checks every member's bodyguard eligibility and retains scoped partner restrictions", () =>
        {
            var data = Fixture(); var army = Army(data);
            var alreadyAttached = army with { Entries = army.Entries.Select(e => e.Id == Caster ? e with { AttachedToId = Body } : e).ToList() };
            var ineligible = data with { Units = data.Units.Select(u => u.Name == Caster ? u with { Profile = u.Profile! with
                { Abilities = [new("Leader", "This model can be attached to the following unit: Other Bodyguard fixture.")] } } : u).ToList() };
            Check(LeaderRules.Problem(alreadyAttached, ineligible, Biologus, Body) != null, "Existing ineligible Leader bypassed group validation");
            var expanded = data with { Units = data.Units.Select(u => u.Name is Caster or Biologus ? u with { Profile = u.Profile! with
                { Abilities = u.Profile.Abilities!.Select(a => a with { Text = a.Text.Replace(Targets, Targets.Replace("\n\n", "\n\u25a0 Other Bodyguard fixture\n\n")) }).ToList() } } : u).ToList() };
            Check(LeaderRules.CombinationProblem(army, expanded, "Other Bodyguard fixture", new[] { Caster, Biologus }) != null, "Permission escaped its Plague Marines scope");
            var named = data with { Units = data.Units.Select(u => u.Name == Biologus ? u with { Profile = u.Profile! with
                { Abilities = [new("Leader", Targets + ExceptionText.Replace("one other Leader unit", "a Malignant Plaguecaster model"))] } } : u).ToList() };
            Check(LeaderRules.CombinationProblem(army, named, Body, new[] { Biologus, Caster }) == null, "Named partner rejected");
            Check(LeaderRules.CombinationProblem(army, named, Body, new[] { Biologus, Ordinary }) != null, "Named partner restriction bypassed");
        });
        test("Structured attachment rules persist and refresh when source ability text changes", () =>
        {
            var data = Fixture();
            using var store = WindowsServices.OpenStore(Path.Combine(root, "semantic-attachments"));
            store.Import(data);
            var saved = store.LoadDataset()!; var army = Army(saved);
            Check(saved.Units.Single(u => u.Name == Biologus).Profile!.AttachmentRules!.AdditionalAttachments.Count == 1, "Structured exception did not persist");
            var attached = LeaderRules.Attach(LeaderRules.Attach(army, saved, Caster, Body), saved, Biologus, Body); store.Save(attached);
            Check(LeaderRules.Issues(store.LoadArmies().Single(), saved).Count == 0, "Reload changed combination validity");
            var revised = saved with { Units = saved.Units.Select(u => u.Name == Biologus ? u with { Profile = u.Profile! with { Abilities = [new("Leader", Targets)] } } : u).ToList() };
            // Copying a profile carries old semantic metadata; changed text must invalidate it.
            Check(LeaderRules.CombinationProblem(army, revised, Body, new[] { Caster, Biologus }) != null, "Stale semantics retained removed permission");
            store.Import(revised);
            Check(store.LoadDataset()!.Units.Single(u => u.Name == Biologus).Profile!.AttachmentRules!.AdditionalAttachments.Count == 0, "Reimport retained stale exception");
            Check(LeaderRules.Issues(store.LoadArmies().Single(), store.LoadDataset()).Count > 0, "Saved invalid combination not flagged");
            var legacy = data with { Units = data.Units.Select(u => u with { Profile = u.Profile! with { AttachmentRules = null } }).ToList() };
            Check(LeaderRules.CombinationProblem(Army(legacy), legacy, Body, new[] { Biologus, Caster }) == null, "Old library without structured fields became unusable");
        });
    }
}
