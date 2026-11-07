using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform.Windows;

internal static class WeaponMetadataChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Dataset Fixture(bool metadata)
    {
        var source = new SourceInfo("Fixture", "1", "", "", Edition: "11", ContentHash: "pinned-fixture");
        var weapon = new WeaponProfile("Psychic blade", "Melee", "Melee", "5", "2+", "8", "-2", "3", "Psychic",
            metadata ? "blade" : null, metadata ? ["psychic"] : null);
        return new(source, [new("Arbitrary faction", "Arbitrary bearer", [new(1, 100)], Profile: new(Weapons: [weapon,
            weapon with { Name = "Cannon", Keywords = "", SourceId = metadata ? "cannon" : null, SourceCategories = metadata ? [] : null }]),
            Aliases: ["bsdata:unit"], ProfileSource: source)]);
    }
    public static void Run(Action<string, Action> test, string root)
    {
        test("Weapon metadata recovery is generic, source-pinned, immutable and idempotent", () =>
        {
            var old = Fixture(false); var before = DataJson.Write(old); var evidence = WeaponMetadataRecovery.Create(Fixture(true));
            var recovered = WeaponMetadataRecovery.Apply(old, evidence);
            Check(DataJson.Write(recovered) == DataJson.Write(Fixture(true)), "Not all target metadata recovered");
            Check(DataJson.Write(old) == before && ReferenceEquals(WeaponMetadataRecovery.Apply(recovered, evidence), recovered), "Mutation or non-idempotent repair");
            Check(recovered.Units[0].Profile!.Weapons![1].SourceCategories!.Count == 0, "Known negative selector lost");
        });
        test("Weapon metadata rejects changed identities, visible profiles and conflicting evidence", () =>
        {
            var old = Fixture(false); var unit = old.Units[0]; var evidence = WeaponMetadataRecovery.Create(Fixture(true));
            foreach (var changed in new[] {
                unit with { Faction = "Other" }, unit with { Aliases = ["bsdata:other"] },
                unit with { ProfileSource = unit.ProfileSource! with { Edition = "10" } },
                unit with { ProfileSource = unit.ProfileSource! with { ContentHash = "other" } },
                unit with { Profile = unit.Profile! with { Weapons = [unit.Profile!.Weapons![0] with { Strength = "99" }, unit.Profile.Weapons[1]] } },
                unit with { Profile = unit.Profile! with { Weapons = [unit.Profile!.Weapons![0] with { SourceCategories = ["conflict"] }, unit.Profile.Weapons[1]] } } })
            {
                var edited = old with { Units = [changed] };
                Check(ReferenceEquals(edited, WeaponMetadataRecovery.Apply(edited, evidence)), "Unproven profile repaired");
            }
            var conflicting = evidence[0] with { Weapons = evidence[0].Weapons.Select(w => w with { SourceId = "conflict" }).ToList() };
            Check(ReferenceEquals(old, WeaponMetadataRecovery.Apply(old, [.. evidence, conflicting])), "Conflicting packs accepted");
            var duplicate = evidence[0] with { Weapons = [evidence[0].Weapons[0], evidence[0].Weapons[0] with { SourceId = "different" }] };
            var duplicated = old with { Units = [unit with { Profile = unit.Profile! with { Weapons = [unit.Profile!.Weapons![0], unit.Profile.Weapons[0]] } }] };
            Check(ReferenceEquals(duplicated, WeaponMetadataRecovery.Apply(duplicated, [duplicate])), "Ambiguous identical weapons repaired");
        });
        test("Weapon metadata preserves loadouts, prices and unrelated edited profile fields", () =>
        {
            var old = Fixture(false); var unit = old.Units[0];
            var profile = unit.Profile! with { Characteristics = new() { ["T"] = "17" }, Notes = "User note", Loadout = new([], [], []) };
            old = old with { Units = [unit with { Options = [new(1, 999)], Profile = profile }] };
            var repaired = WeaponMetadataRecovery.Apply(old, WeaponMetadataRecovery.Create(Fixture(true))).Units[0];
            Check(ReferenceEquals(repaired.Profile!.Loadout, profile.Loadout) && ReferenceEquals(repaired.Options, old.Units[0].Options)
                && repaired.Profile.Characteristics!["T"] == "17" && repaired.Profile.Notes == "User note", "Repair overwrote unrelated data");
        });
        test("Bundled weapon evidence restores Eldritch Vortex for both Princes and keeps non-Psychic weapons unchanged", () =>
        {
            var old = BootstrapRelease.ReadBundled().Dataset;
            var repaired = EffectiveDatasheetRecovery.ApplyBundled(old);
            Check(ReferenceEquals(repaired, EffectiveDatasheetRecovery.ApplyBundled(repaired)), "Composite semantic/weapon recovery needs a second launch");
            foreach (var unit in repaired.Units.Where(u => u.Faction == "Thousand Sons" && u.Name.StartsWith("Daemon Prince of Tzeentch")))
                AssertEldritch(repaired, unit);
            Check(repaired.Units.Count(u => u.Faction == "Thousand Sons" && u.Name.StartsWith("Daemon Prince of Tzeentch")) == 2, "Real fixture coverage missing");
        });
        test("Recovered weapon metadata survives cache hits, cache corruption and persisted session reopen", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset;
            data = data with { Units = data.Units.Where(u => u.Faction == "Thousand Sons").ToList() };
            var directory = Path.Combine(root, "weapon-recovery");
            var first = EffectiveDatasheetRecovery.ApplyBundled(data, directory);
            Check(DataJson.Write(first) == DataJson.Write(EffectiveDatasheetRecovery.ApplyBundled(data, directory)), "Cache dropped weapon metadata");
            foreach (var file in Directory.GetFiles(Path.Combine(directory, "derived-cache"))) File.WriteAllText(file, "corrupt");
            Check(DataJson.Write(first) == DataJson.Write(EffectiveDatasheetRecovery.ApplyBundled(data, directory)), "Corrupt cache changed recovery");
            using var store = new ArmyStore(Path.Combine(directory, "store"), new WindowsSqliteConnectionFactory()); store.Import(data);
            var session = new ArmySession(store);
            var prince = session.Dataset!.Units.Single(u => u.Name == "Daemon Prince of Tzeentch with wings");
            var army = ArmyList.New(prince.Faction) with { Detachment = "Grand Coven", Entries = [new("saved-bearer", prince.Key, prince.Name, 1)] };
            session.Open(army); session.SelectEnhancement("saved-bearer", new("Grand Coven", "Eldritch Vortex of E'taph"));
            var saved = DataJson.Write(session.Army); session.Undo(); session.Redo(); Check(DataJson.Write(session.Army) == saved, "Undo/redo changed army");
            var reopened = new ArmySession(store); reopened.Open(army.Id);
            Check(DataJson.Write(reopened.Army) == saved, "Saved army changed on recovery");
            AssertEldritch(reopened.Dataset!, reopened.Dataset!.FindUnit(prince.Key)!);
        });
        test("Unknown numeric weapon selectors stay unapplied and explain the unresolved target", () =>
        {
            var old = Fixture(false); var unit = old.Units[0];
            var modifier = new DatasheetModifier("test", "Source", "weapon", "S", "increment", "1", WeaponPredicate: new("source-category", "psychic"));
            unit = unit with { Profile = unit.Profile! with { Semantics = new([], [modifier]) } };
            old = old with { Units = [unit] };
            var army = ArmyList.New(unit.Faction) with { Entries = [new("unit", unit.Key, unit.Name, 1)] };
            var result = EffectiveDatasheets.Project(army, army.Entries[0], old)!;
            Check(result.EffectiveWeaponProfiles.All(w => w.Strength == "8") && result.AppliedModifierSources.Any(m => m.Conditional && m.Condition.Contains("Unresolved weapon selector")), "Unknown selector silently skipped or broadened");
        });
    }
    private static void AssertEldritch(Dataset data, UnitDefinition unit)
    {
        var army = ArmyList.New(unit.Faction) with { Detachment = "Grand Coven", Entries = [new("bearer", unit.Key, unit.Name, 1)] };
        army = Enhancements.Select(army, data, "bearer", new("Grand Coven", "Eldritch Vortex of E'taph"));
        var result = EffectiveDatasheets.Project(army, army.Entries[0], data)!;
        foreach (var weapon in result.EffectiveWeaponProfiles)
        {
            var original = unit.Profile!.Weapons!.Single(w => w.Name == weapon.Name);
            var psychic = WeaponRules.Tokens(original.Keywords).Contains("Psychic");
            Check(weapon.Strength == (int.Parse(original.Strength) + (psychic ? 1 : 0)).ToString()
                && weapon.Damage == (int.Parse(original.Damage) + (psychic ? 1 : 0)).ToString(), "Incorrect projected values for " + weapon.Name);
        }
        Check(result.AppliedModifierSources.Count(m => m.Source == "Eldritch Vortex of E'taph" && !m.Conditional) == 6, "Missing or doubled enhancement effect");
    }
}
