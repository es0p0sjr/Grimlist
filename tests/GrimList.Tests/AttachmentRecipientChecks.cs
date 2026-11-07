using GrimList.Application;
using GrimList.Core;

internal static class AttachmentRecipientChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Fnp(ArmyList army, Dataset data, string id) => DatasheetStats.FeelNoPainLabel(
        DatasheetStats.FeelNoPain(army, data, null, army.Entries.Single(e => e.Id == id)));
    public static void Run(Action<string, Action> test, string root, bool freshOnly)
    {
        test("Generic recipients separate owner conditions from recipient predicates and preserve innate defences", () =>
        {
            var guard = new UnitDefinition("Test", "Guard", [new(5, 100)], Profile: new(Keywords: ["Shield"],
                Semantics: new([], [new("grant", "Protection", "ability", "From the bodyguard", "add", "Feel No Pain 4+",
                    Condition: new("keyword", "Shield"), Recipient: new("leaders", new("keyword", "Medic")))], 3)));
            var medic = new UnitDefinition("Test", "Medic", [new(1, 50)], Profile: new(Keywords: ["Character", "Medic"],
                Abilities: [new("Leader", "This model can be attached to the following unit: Guard."), new("Feel No Pain 6+", "Innate protection.")]));
            var data = new Dataset(new("Synthetic", "1", "", ""), [guard, medic]);
            var before = DataJson.Write(data);
            var army = ArmyList.New("Test") with { Entries = [new("guard", guard.Key, guard.Name, 5), new("medic", medic.Key, medic.Name, 1)] };
            var attached = LeaderRules.Attach(army, data, "medic", "guard");
            Check(Fnp(attached, data, "guard") == "" && Fnp(attached, data, "medic") == "4+++ FNP", "Recipient or owner condition evaluated on the wrong unit");
            Check(Fnp(army, data, "medic") == "6+++ FNP" && Fnp(army, data, "guard") == "", "Unattached grant leaked");
            var reversed = attached with { Entries = attached.Entries.AsEnumerable().Reverse().ToList() };
            Check(Fnp(reversed, data, "medic") == "4+++ FNP", "Entry ordering changes recipients");
            var unknown = data with { Units = [guard with { Profile = guard.Profile! with { Semantics = new([], [guard.Profile!.Semantics!.Modifiers[0] with { Recipient = new("unknown") }], 3) } }, medic] };
            Check(Fnp(attached, unknown, "medic") == "6+++ FNP", "Unknown recipient became an unconditional grant");
            var removedSource = data with { Units = [guard with { Profile = guard.Profile! with { Keywords = [] } }, medic] };
            Check(Fnp(attached, removedSource, "medic") == "6+++ FNP", "Source condition ignored");
            var changedRecipient = data with { Units = [guard, medic with { Profile = medic.Profile! with { Keywords = ["Character"] } }] };
            Check(Fnp(attached, changedRecipient, "medic") == "6+++ FNP", "Recipient keyword ignored");
            Check(Fnp(attached, DataJson.Read<Dataset>(DataJson.Write(data)), "medic") == "4+++ FNP", "JSON roundtrip lost recipients");
            Check(DataJson.Write(data) == before, "Projection changed source data");
        });
        test("Generic bodyguard prose directs abilities only to eligible leaders and shared-unit members", () =>
        {
            foreach (var text in new[] {
                "While a Character model is leading this unit, that Character model has the Feel No Pain 4+ ability.",
                "While a Medic model is leading this unit, that Medic has the Stealth ability.",
                "While this unit is in the same unit as a Specialist model, that Specialist model has the Feel No Pain 5+ ability." })
                Check(DatasheetRuleText.Attached(new("Unrelated rule name", text)).Single().Recipient != null, "Explicit recipient not parsed: " + text);
            Check(DatasheetRuleText.Attached(new("Rule", "While a Character model is leading this unit, that Character model has the Feel No Pain 4+ ability against mortal wounds.")).Count == 0, "Conditional defence became permanent");
        });
        Dataset? imported = null;
        Dataset Fresh()
        {
            if (imported != null) return imported;
            var bytes = File.ReadAllBytes("tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip");
            var docs = BsDataDocument.ReadArchive(bytes);
            foreach (var doc in docs.Where(d => d.Name is not ("Chaos - Death Guard" or "Xenos - Tyranids" or "Xenos - Necrons"))) doc.Root["library"] = true;
            return imported = BsDataImporter.ImportAll(docs, new("BSData", "fixture", "", "", Edition: "11", ContentHash:
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)))).Dataset;
        }
        test("Recipient metadata repairs version 2 libraries without changing source profiles", () =>
        {
            var data = Fresh();
            var pack = EffectiveDatasheetRecovery.Create(data);
            var old = data with { Units = data.Units.Select(u => u.Profile?.Semantics is not { } semantics ? u : u with {
                Profile = u.Profile with { Semantics = semantics with { Version = 2,
                    Modifiers = semantics.Modifiers.Select(m => m with { Recipient = null }).ToList() } } }).ToList() };
            var repaired = EffectiveDatasheetRecovery.Apply(old, [pack]);
            Check(repaired.Units.Any(u => u.Profile?.Semantics?.Modifiers.Any(m => m.Recipient != null) == true), "Old recipient metadata not repaired");
            Check(repaired.Units.All(u => u.Profile == null || EffectiveDatasheetRecovery.ProfileHash(u.Profile)
                == EffectiveDatasheetRecovery.ProfileHash(old.Units.Single(v => v.Key == u.Key).Profile!)), "Repair changed source profiles");
            Check(ReferenceEquals(repaired, EffectiveDatasheetRecovery.Apply(repaired, [pack])), "Repair is not idempotent");
            var grant = old.Units.First(u => data.Units.Single(v => v.Key == u.Key).Profile?.Semantics?.Modifiers.Any(m => m.Recipient != null) == true);
            var edited = grant with { Profile = grant.Profile! with { Abilities = [new("Edited", "User rule")] } };
            Check(EffectiveDatasheetRecovery.Apply(old with { Units = [edited] }, [pack]).Units[0].Profile!.Semantics!.Version == 2,
                "Repair overwrote edited source rules");
        });
        foreach (var legacy in freshOnly ? new[] { false } : new[] { false, true })
            test((legacy ? "Recovered libraries" : "Real BSData") + ": Deathshroud, Tyrant Guard and Cryptothralls grant defences to their recipients", () =>
            {
                var data = legacy ? EffectiveDatasheetRecovery.ApplyBundled(EnhancementLibraryRepair.ApplyBundled(
                    DataJson.Read<Dataset>(File.ReadAllText("tests/fixtures/libraries/GrimList-Library.json")))) : Fresh();
                foreach (var (faction, leaderName, guardName, roll) in new[] {
                    ("Death Guard", "Lord of Contagion", "Deathshroud Terminators", "4+++ FNP"),
                    ("Tyranids", "Hive Tyrant", "Tyrant Guard", "5+++ FNP") })
                {
                    var leader = data.Units.Single(u => u.Faction == faction && u.Name == leaderName);
                    var guard = data.Units.Single(u => u.Faction == faction && u.Name == guardName);
                    var army = ArmyList.New(faction) with { Entries = [new("guard", guard.Key, guard.Name, guard.Options.First().Models,
                        Loadout: Loadouts.Create(guard, guard.Options.First().Models)), new("leader", leader.Key, leader.Name, 1, Loadout: Loadouts.Create(leader, 1))] };
                    Check(Fnp(army, data, "guard") == "", guardName + " has its recipient's FNP while unattached");
                    var originalLeader = Fnp(army, data, "leader");
                    var attached = LeaderRules.Attach(army, data, "leader", "guard");
                    Check(Fnp(attached, data, "guard") == "" && Fnp(attached, data, "leader") == roll, guardName + " has incorrect FNP recipients");
                    Check(Fnp(LeaderRules.Attach(attached, data, "leader", ""), data, "leader") == originalLeader, "Detach retained a grant");
                    Check(Fnp(attached with { Entries = attached.Entries.AsEnumerable().Reverse().ToList() }, data, "leader") == roll, "Order changed recipient");
                    var xml = System.Xml.Linq.XDocument.Parse(YellowscribeExport.Roster(attached, data));
                    var fnpName = "Feel No Pain " + roll[0] + "+";
                    var exportedLeader = xml.Descendants().Single(e => e.Name.LocalName == "selection" && e.Attribute("id")?.Value == "leader");
                    var exportedGuard = xml.Descendants().Single(e => e.Name.LocalName == "selection" && e.Attribute("id")?.Value == "guard");
                    Check(exportedLeader.Descendants().Any(e => e.Name.LocalName == "profile" && e.Attribute("name")?.Value == fnpName)
                        && !exportedGuard.Descendants().Any(e => e.Name.LocalName == "profile" && e.Attribute("name")?.Value == fnpName), "Effective export put the ability on the wrong recipient");
                    using var store = WindowsServices.OpenStore(Path.Combine(root, "recipient-" + legacy + "-" + EffectiveDatasheets.Key(faction)));
                    store.Import(data with { Units = [guard, leader] }); var session = new ArmySession(store); session.Open(army);
                    session.Commit(LeaderRules.Attach(session.Army, session.Dataset, "leader", "guard"));
                    Check(Fnp(session.Army, session.Dataset!, "leader") == roll, "Shared session lost recipient");
                    session.Undo(); Check(Fnp(session.Army, session.Dataset!, "guard") == "" && Fnp(session.Army, session.Dataset!, "leader") == originalLeader, "Undo retained effect");
                    session.Redo(); var reopened = new ArmySession(store); reopened.Open(session.Army.Id);
                    Check(Fnp(reopened.Army, reopened.Dataset!, "leader") == roll && Fnp(reopened.Army, reopened.Dataset!, "guard") == "", "Reopen changed recipient");
                }
                var names = new[] { "Necron Warriors", "Technomancer", "Cryptothralls" };
                var units = names.Select(name => data.Units.Single(u => u.Faction == "Necrons" && u.Name == name)).ToList();
                var roster = ArmyList.New("Necrons") with { Entries = units.Select((u, i) => new ArmyEntry("member" + i, u.Key, u.Name, u.Options.First().Models)).ToList() };
                Check(Fnp(roster, data, "member2") == "", "Cryptothralls received Cryptek-only defence while alone");
                Check(LeaderRules.Problem(roster, data, "member2", "member0") != null, "Retinue allowed without its required leader");
                roster = LeaderRules.Attach(roster, data, "member1", "member0");
                Check(LeaderRules.Problem(roster, data, "member2", "member0") == null, "Retinue permission: "
                    + LeaderRules.Problem(roster, data, "member2", "member0") + DataJson.Write(new { Rules = LeaderRules.Semantics(units[2].Profile),
                        Permissions = units[2].Profile!.Semantics!.Attachments, Leader = EffectiveDatasheets.Project(roster, roster.Entries[1], data)!.EffectiveKeywords }));
                roster = LeaderRules.Attach(roster, data, "member2", "member0");
                var duplicate = roster with { Entries = [.. roster.Entries, roster.Entries[2] with { Id = "duplicate", AttachedToId = "" }] };
                Check(LeaderRules.Problem(duplicate, data, "duplicate", "member0") != null, "Duplicate retinue allowed");
                Check(Fnp(roster, data, "member1") == "4+++ FNP", "Cryptek missing support grant");
                Check(Fnp(roster, data, "member0") == "5+++ FNP" && Fnp(roster, data, "member2") == "5+++ FNP", "Cryptek-only 4+ leaked to other members or leader's whole-unit 5+ lost");
                roster = LeaderRules.Attach(roster, data, "member2", "");
                Check(Fnp(roster, data, "member1") == "5+++ FNP" && Fnp(roster, data, "member2") == "", "Removing support lost independent grant or retained support grant");
                if (legacy)
                    File.WriteAllText(Path.Combine(root, "recipient-ui.json"), DataJson.Write(data with {
                        Units = data.Units.Where(u => u.Faction == "Death Guard" && u.Name is "Deathshroud Terminators" or "Lord of Contagion").ToList(), ReferenceCards = [] }));
            });
    }
}
