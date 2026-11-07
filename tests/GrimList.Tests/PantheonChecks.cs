using GrimList.Application;
using GrimList.Core;

internal static class PantheonChecks
{
    private const string Detachment = "Pantheon of Woe";
    private static readonly Dictionary<string, string> Bindings = new()
    {
        ["C'tan Shard of the Deceiver"] = "Lord of Deceit (Aura)",
        ["C'tan Shard of the Nightbringer"] = "Quantum Goad",
        ["C'tan Shard of the Void Dragon"] = "Animus Damper",
        ["Transcendent C'tan"] = "Reletavistic Tether"
    };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Dataset Scope(Dataset data) => data with
    {
        Units = data.Units.Where(u => u.Faction == "Necrons" && Bindings.ContainsKey(u.Name)).ToList(),
        Detachments = data.Detachments?.Where(d => d.Faction == "Necrons").ToList(),
        ReferenceCards = data.ReferenceCards?.Where(c => c.Faction == "Necrons").ToList()
    };
    private static ArmyList Army(Dataset data)
    {
        var units = data.Units.Concat(data.Units.Where(u => u.Name == "Transcendent C'tan"));
        return ArmyList.New("Necrons") with { Detachment = Detachment, Entries = units.Select((u, i) =>
            new ArmyEntry("ctan-" + i, u.Key, u.Name, 1, Loadout: Loadouts.Create(u, 1))).ToList() };
    }
    private static void Verify(ArmyList army, Dataset data, bool active)
    {
        foreach (var entry in army.Entries)
        {
            var state = EffectiveDatasheets.Project(army, entry, data)!;
            var abilities = state.EffectiveAbilities;
            Check(abilities.Count(a => a.Name == Bindings[entry.Name]) == (active ? 1 : 0), entry.Name + ": incorrect binding activation");
            Check(!abilities.Any(a => Bindings.Values.Contains(a.Name) && a.Name != Bindings[entry.Name]), "Binding leaked to another C'tan");
            Check(Loadouts.Abilities(state.Unit, entry.Loadout).Count(a => a.Name == Bindings[entry.Name]) == (active ? 1 : 0),
                entry.Name + ": play mode hides mandatory ability");
            Check(entry.Enhancement == null && (entry.Choices?.Count ?? 0) == 0, "Mandatory ability occupied a picker selection");
        }
        Check(Enhancements.Usage(army, data).Choices == 0, "Mandatory abilities consumed enhancement allowance");
    }
    public static void Run(Action<string, Action> test, string root)
    {
        Dataset? imported = null;
        Dataset Fresh()
        {
            if (imported != null) return imported;
            var path = "tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip";
            var bytes = File.ReadAllBytes(path);
            var docs = BsDataDocument.ReadArchive(bytes);
            foreach (var doc in docs.Where(d => d.Name != "Xenos - Necrons")) doc.Root["library"] = true;
            return imported = Scope(BsDataImporter.ImportAll(docs, new("BSData", "fixture", "", "", Edition: "11",
                ContentHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)))).Dataset);
        }
        test("Pantheon: all four mandatory bindings activate, including every Transcendent, without double charging", () =>
        {
            var data = Fresh(); var army = Army(data);
            Check(data.Units.Count == 4 && army.Entries.Count == 5, "Incomplete C'tan fixture");
            var libraryBefore = DataJson.Write(data); var armyBefore = DataJson.Write(army);
            Verify(army, data, true);
            var inactive = army with { Detachment = "Awakened Dynasty", Detachments = [] };
            Verify(inactive, data, false);
            Verify(army with { Detachment = "", Detachments = [Detachment] }, data, true);
            var expected = army.Entries.Sum(e => data.FindUnit(e.UnitKey)!.DetachmentCosts!.Single(c => c.Detachment == Detachment).Points);
            Check(ArmyCalculator.Calculate(army, data).Total - ArmyCalculator.Calculate(inactive, data).Total == expected, "Surcharge missing or doubled");
            var xml = YellowscribeExport.Roster(army, data);
            foreach (var name in Bindings.Values) Check(xml.Contains(name), "Export missing " + name);
            Check(DataJson.Write(data) == libraryBefore && DataJson.Write(army) == armyBefore, "Projection mutated source or saved selections");
        });
        test("Mandatory selections require exact source IDs and active detachments, and count only once", () =>
        {
            var data = Fresh(); var army = Army(data); var entry = army.Entries[0]; var unit = data.FindUnit(entry.UnitKey)!;
            var cost = unit.DetachmentCosts!.Single();
            Check(EffectiveDatasheets.Test(new("selection", cost.SourceId, "equalTo", 1), army, entry, unit) == true, "Automatic selection not counted once");
            var explicitEntry = entry with { Choices = [new("diagnostic", cost.SourceId)] };
            Check(EffectiveDatasheets.Test(new("selection", cost.SourceId, "equalTo", 1), army, explicitEntry, unit) == true, "Automatic selection doubled explicit selection");
            var missing = data with { Units = data.Units.Select(u => u with { DetachmentCosts = null }).ToList() };
            Verify(army, missing, false);
            var wrong = data with { Units = data.Units.Select(u => u with { DetachmentCosts = u.DetachmentCosts!
                .Select(c => c with { SourceId = "different-source-selection" }).ToList() }).ToList() };
            Verify(army, wrong, false);
            Check(EffectiveDatasheets.Test(new("selection", "unselected-optional-upgrade"), army, entry, unit) == false, "Unselected optional upgrade activated");
            var independentPrices = data with { Units = data.Units.Select(u => u with
            {
                PointsSource = new("Munitorum Field Manual", "fixture", "", "https://mfm.warhammer-community.com/", Edition: "11"),
                Options = [new(1, 100)], DetachmentCosts = u.DetachmentCosts!.Select(c => c with { Points = 0 }).ToList()
            }).ToList() };
            Verify(army, independentPrices, true);
        });
        test("Pantheon: bundled legacy recovery and save/reopen preserve abilities, IDs, loadouts and undo/redo", () =>
        {
            var legacy = Scope(BootstrapRelease.ReadBundled().Dataset); var army = Army(legacy);
            var recovered = EffectiveDatasheetRecovery.ApplyBundled(legacy);
            Verify(army, recovered, true);
            Check(ReferenceEquals(recovered, EffectiveDatasheetRecovery.ApplyBundled(recovered)), "Recovery not idempotent");
            var unknown = legacy with { Units = legacy.Units.Select(u => u with { ProfileSource = u.ProfileSource! with { ContentHash = "unknown" } }).ToList() };
            Verify(army, EffectiveDatasheetRecovery.ApplyBundled(unknown), false);
            var directory = Path.Combine(root, "pantheon-save");
            string saved;
            using (var store = WindowsServices.OpenStore(directory))
            {
                store.Import(legacy); store.Save(army);
                var session = new ArmySession(store); session.Open(army.Id);
                Verify(session.Army, session.Dataset!, true);
                Check(DataJson.Write(session.Army.Entries) == DataJson.Write(army.Entries), "Opening changed unit IDs or loadouts");
                session.Commit(session.Army with { Detachment = "Awakened Dynasty", Detachments = [] });
                Verify(session.Army, session.Dataset!, false);
                session.Undo(); Verify(session.Army, session.Dataset!, true);
                session.Redo(); Verify(session.Army, session.Dataset!, false);
                session.Undo(); saved = DataJson.Write(session.Army.Entries);
            }
            using (var store = WindowsServices.OpenStore(directory))
            {
                var reopened = new ArmySession(store); reopened.Open(army.Id);
                Verify(reopened.Army, reopened.Dataset!, true);
                Check(DataJson.Write(reopened.Army.Entries) == saved, "Reopening changed saved selections");
            }
        });
    }
}
