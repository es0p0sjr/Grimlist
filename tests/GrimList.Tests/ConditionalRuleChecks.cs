using GrimList.Application;
using GrimList.Core;

internal static class ConditionalRuleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        Dataset? imported = null;
        Dataset Data()
        {
            if (imported != null) return imported;
            var path = "tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip";
            var bytes = File.ReadAllBytes(path);
            var docs = BsDataDocument.ReadArchive(bytes);
            foreach (var doc in docs.Where(d => d.Name is not ("Xenos - Necrons" or "Xenos - Aeldari" or "Chaos - Thousand Sons" or "Imperium - Adeptus Mechanicus"))) doc.Root["library"] = true;
            imported = BsDataImporter.ImportAll(docs, new("BSData", "fixture", "", "", Edition: "11",
                ContentHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)))).Dataset;
            File.WriteAllText(Path.Combine(root, "conditional-import.json"), DataJson.Write(imported));
            return imported;
        }
        ArmyList Army(string faction, string detachment, params string[] units) => new("conditional", "Conditional regression", faction, 2000,
            units.Select((n, i) => { var u = Data().Units.Single(u => u.Faction == faction && u.Name == n); return new ArmyEntry("entry" + i, u.Key, u.Name, u.Options.FirstOrDefault()?.Models ?? 1); }).ToList(), Detachment: detachment);
        string Fnp(ArmyList army, int index, Dataset? data = null) => DatasheetStats.FeelNoPainLabel(DatasheetStats.FeelNoPain(army, data ?? Data(), null, army.Entries[index]));
        test("Conditional BSData: Murdermind unlocks attachment and FNP, removal reverses all dependent effects", () =>
        {
            var army = Army("Necrons", "Cursed Legion", "Technomancer", "Lokhust Destroyers");
            Check(Fnp(army, 1) == "", "Unattached destroyers have FNP");
            Check(LeaderRules.Problem(army, Data(), "entry0", "entry1") != null, "Attachment legal without enhancement");
            var card = Data().ReferenceCards!.Single(c => c.Faction == army.Faction && c.Name == "Murdermind" && c.Detachment == army.Detachment);
            Check(Enhancements.Eligible(army, Data().FindUnit(army.Entries[0].UnitKey), card, Data()), "Murdermind unavailable: " + DataJson.Write(card.Eligibility));
            army = Enhancements.Select(army, Data(), "entry0", new(card.Detachment, card.Name));
            var effective = EffectiveDatasheets.Project(army, army.Entries[0], Data())!;
            Check(effective.EffectiveKeywords.Contains("Destroyer Cult"), "Keyword not granted");
            Check(effective.EffectiveCharacteristics["M"] == "13\"", "Movement not modified exactly once");
            Check(LeaderRules.Problem(army, Data(), "entry0", "entry1") == null, "Enhanced attachment unavailable: " + LeaderRules.Problem(army, Data(), "entry0", "entry1"));
            army = LeaderRules.Attach(army, Data(), "entry0", "entry1");
            Check(Fnp(army, 1) == "5+++ FNP", "Attached destroyers lack FNP: " + Fnp(army, 1));
            Check(Fnp(LeaderRules.Attach(army, Data(), "entry0", ""), 1) == "", "Detached FNP remains");
            var reversed = army with { Entries = army.Entries.AsEnumerable().Reverse().ToList() };
            Check(Fnp(reversed, 0) == "5+++ FNP", "Entry order changes effects");
            var removed = Enhancements.Select(army, Data(), "entry0", null);
            Check(Fnp(removed, 1) == "", "Illegal attachment retains FNP");
            Check(LeaderRules.Issues(removed, Data()).Count > 0, "Illegal relationship not explained");
            Check(!EffectiveDatasheets.Project(removed, removed.Entries[0], Data())!.EffectiveKeywords.Contains("Destroyer Cult"), "Stale keyword");
            var switched = army with { Detachment = "Awakened Dynasty" };
            Check(Fnp(switched, 1) == "" && LeaderRules.Issues(switched, Data()).Count > 0, "Detachment switch retained enhancement permission/effect");
            var noPrerequisite = Data() with { Units = Data().Units.Select(u => u.Key == army.Entries[0].UnitKey
                ? u with { Profile = u.Profile! with { Keywords = u.Profile!.Keywords!.Where(k => k != "Cryptek").ToList() } } : u).ToList() };
            Check(Fnp(army, 1, noPrerequisite) == "" && LeaderRules.Issues(army, noPrerequisite).Count > 0, "Ineligible enhancement still grants attachment permission");
            var baseUnit = Data().FindUnit(army.Entries[0].UnitKey)!;
            Check(!baseUnit.Profile!.Keywords!.Contains("Destroyer Cult"), "Base mutated");
        });
        test("Conditional BSData: only the selected detachment ability is projected", () =>
        {
            var army = Army("Necrons", "Cursed Legion", "Lokhust Destroyers");
            var names = new[] { "Annihilation Protocol", "Command Protocols", "Hyperphasing", "Power Matrix", "Worthy Foes", "Technosorcerous Augmentations", "Cold Fervour", "Cosmic Distortion" };
            foreach (var pair in new[] { ("Cursed Legion", "Cold Fervour"), ("Awakened Dynasty", "Command Protocols"), ("Hypercrypt Legion", "Hyperphasing") })
            {
                var next = army with { Detachment = pair.Item1 };
                var shown = EffectiveDatasheets.Project(next, next.Entries[0], Data())!.EffectiveAbilities.Select(a => a.Name).Intersect(names).ToList();
                Check(shown.SequenceEqual([pair.Item2]), "Wrong detachment rules: " + string.Join(", ", shown));
            }
        });
        test("Conditional BSData: attached defensive rule persists through save/reopen and undo/redo", () =>
        {
            var army = Army("Necrons", "Cursed Legion", "Technomancer", "Lokhust Destroyers");
            using var store = WindowsServices.OpenStore(Path.Combine(root, "conditional-save"));
            store.Import(Data()); store.Save(army);
            var session = new ArmySession(store); session.Open(army.Id);
            session.Commit(Enhancements.Select(session.Army, session.Dataset, "entry0", new("Cursed Legion", "Murdermind")));
            session.Commit(LeaderRules.Attach(session.Army, session.Dataset, "entry0", "entry1"));
            File.WriteAllText(Path.Combine(root, "conditional-session.json"), DataJson.Write(session.Army));
            Check(Fnp(session.Army, 1, session.Dataset) == "5+++ FNP", "Session effect absent");
            session.Undo(); Check(Fnp(session.Army, 1, session.Dataset) == "", "Undo retained FNP");
            session.Redo(); Check(Fnp(session.Army, 1, session.Dataset) == "5+++ FNP", "Redo lost FNP");
            var reopened = new ArmySession(store); reopened.Open(army.Id);
            Check(Fnp(reopened.Army, 1, reopened.Dataset) == "5+++ FNP", "Reopen lost effect");
        });
        test("Non-Necron BSData: Bray Lord changes Infernal Master attachment permissions", () =>
        {
            var army = Army("Thousand Sons", "Warpmeld Pact", "Infernal Master", "Tzaangors");
            Check(LeaderRules.Problem(army, Data(), "entry0", "entry1") != null, "Unenhanced attachment accepted");
            army = Enhancements.Select(army, Data(), "entry0", new("Warpmeld Pact", "Bray Lord"));
            Check(LeaderRules.Problem(army, Data(), "entry0", "entry1") == null, "Bray Lord permission missing");
            army = LeaderRules.Attach(army, Data(), "entry0", "entry1");
            Check(EffectiveDatasheets.Project(army, army.Entries[1], Data())!.EffectiveWeaponProfiles.All(w => w.Keywords.Contains("SUSTAINED HITS 1")), "Attached weapon grant missing");
            army = Enhancements.Select(army, Data(), "entry0", null);
            Check(LeaderRules.Issues(army, Data()).Count > 0, "Removed enhancement leaves valid attachment");
            Check(!EffectiveDatasheets.Project(army, army.Entries[1], Data())!.EffectiveWeaponProfiles.Any(w => w.Keywords.Contains("SUSTAINED HITS 1")), "Stale weapon modifier");
        });
        test("Non-Necron BSData: Tech-Priest Dominus defensive grant needs a legal attachment", () =>
        {
            var army = Army("Adeptus Mechanicus", "", "Tech-Priest Dominus", "Kataphron Breachers");
            Check(Fnp(army, 1) == "", "Unattached Breachers have FNP");
            army = LeaderRules.Attach(army, Data(), "entry0", "entry1");
            Check(Fnp(army, 1) == "5+++ FNP", "Dominus attachment did not grant FNP: " + Fnp(army, 1));
            Check(Fnp(LeaderRules.Attach(army, Data(), "entry0", ""), 1) == "", "Detached Breachers retain FNP");
        });
        test("Non-Necron BSData: Autarch conditional abilities follow current bodyguard", () =>
        {
            var army = Army("Aeldari", "", "Autarch", "Howling Banshees", "Guardian Defenders");
            bool Fights(ArmyList a) => EffectiveDatasheets.Project(a, a.Entries[0], Data())!.EffectiveAbilities.Any(r => r.Name == "Fights First");
            Check(!Fights(army), "Unattached Autarch has conditional ability");
            army = LeaderRules.Attach(army, Data(), "entry0", "entry1"); Check(Fights(army), "Conditional ability missing");
            army = LeaderRules.Attach(army, Data(), "entry0", "entry2"); Check(!Fights(army), "Old bodyguard ability remains");
        });
        test("Conditional metadata: existing bundled libraries receive hash-matched rules without reimport", () =>
        {
            var baseline = BootstrapRelease.ReadBundled().Dataset;
            var data = EffectiveDatasheetRecovery.ApplyBundled(EnhancementLibraryRepair.ApplyBundled(baseline));
            var tech = data.Units.Single(u => u.Faction == "Necrons" && u.Name == "Technomancer");
            var destroyers = data.Units.Single(u => u.Faction == "Necrons" && u.Name == "Lokhust Destroyers");
            Check(tech.Profile?.Semantics?.Version >= 2 && destroyers.Profile?.Semantics?.Version >= 2, "Existing source not recovered");
            var army = new ArmyList("recovered", "Recovered", "Necrons", 2000,
                [new("tech", tech.Key, tech.Name, 1), new("destroyers", destroyers.Key, destroyers.Name, 1)], Detachment: "Cursed Legion");
            Check(Fnp(army, 1, data) == "", "Recovered library still has unconditional FNP");
            army = Enhancements.Select(army, data, "tech", new("Cursed Legion", "Murdermind"));
            army = LeaderRules.Attach(army, data, "tech", "destroyers");
            Check(Fnp(army, 1, data) == "5+++ FNP", "Recovered library lacks conditional FNP");
            Check(ReferenceEquals(data, EffectiveDatasheetRecovery.ApplyBundled(data)), "Recovery is not idempotent");
            Check(baseline.Units.Single(u => u.Key == tech.Key).Profile?.Semantics == null, "Baseline source was mutated");
        });
    }
}
