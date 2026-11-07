using GrimList.Application;
using GrimList.Core;

internal static class SourceWeaponGrantChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool Assault(WeaponProfile weapon) => WeaponRules.Tokens(weapon.Keywords).Any(k => k.Equals("ASSAULT", StringComparison.OrdinalIgnoreCase));
    public static void Run(Action<string, Action> test, string root)
    {
        test("Generic weapon prose handles unit attacks and independent conditional clauses without broadening targets", () =>
        {
            foreach (var subject in new[] { "This unit’s ranged attacks", "This unit's ranged weapons", "This model's ranged weapons" })
            {
                var mods = DatasheetRuleText.WeaponAbilities("Unrelated name", subject + " have [ASSAULT].");
                Check(mods.Count == 1 && mods[0].WeaponType == "Ranged" && mods[0].Condition == null, "Unrecognized unit grant: " + subject);
            }
            var mixed = DatasheetRuleText.WeaponAbilities("Another rule", "Ranged weapons equipped by EXAMPLE models from your army have the [ASSAULT] ability, and each time an attack made with such a weapon targets a unit within 6\", add 1 to the Strength characteristic of that attack.", true);
            Check(mixed.Count == 1 && mixed[0].Condition is { Kind: "keyword", Value: "EXAMPLE" }, "Independent conditional clause suppressed the permanent grant");
            var conditional = DatasheetRuleText.WeaponAbilities("Conditional", "While this unit is below Half-strength, this unit's ranged attacks have [LETHAL HITS].");
            Check(conditional.Count == 1 && conditional[0].Condition?.Kind == "battlefield", "Battlefield condition was lost");
            Check(DatasheetRuleText.WeaponAbilities("Conditional", "This unit's ranged attacks have [ASSAULT] only while it is leading a unit.").Count == 0, "Trailing condition became unconditional");
            Check(DatasheetRuleText.WeaponAbilities("Other target", "Enemy units' ranged attacks have [ASSAULT].").Count == 0, "Enemy weapon grant applied to owner");
        });
        test("Real imported and legacy weapon grants reach every applicable ranged profile, play loadouts and exports", () =>
        {
            var original = DataJson.Read<Dataset>(File.ReadAllText("tests/fixtures/libraries/GrimList-Library.json"));
            var legacy = DetachmentMetadataRecovery.ApplyBundled(EffectiveDatasheetRecovery.ApplyBundled(EnhancementLibraryRepair.ApplyBundled(original)));
            var docs = BsDataDocument.ReadArchive(File.ReadAllBytes("tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip"));
            foreach (var doc in docs.Where(d => d.Name is not ("Xenos - Necrons" or "Imperium - Adepta Sororitas"))) doc.Root["library"] = true;
            var fresh = BsDataImporter.ImportAll(docs, original.Source).Dataset;
            foreach (var (data, label) in new[] { (legacy, "legacy"), (fresh, "fresh") })
            {
                var sourceBefore = DataJson.Write(data);
                var unit = data.Units.Single(u => u.Faction == "Necrons" && u.Name == "Lokhust Destroyers");
                var gift = data.ReferenceCards!.Single(c => c.Name == "Deepening Madness" && c.Faction == unit.Faction);
                var army = ArmyList.New(unit.Faction) with { Detachment = gift.Detachment, Entries = [new("bearer", unit.Key, unit.Name, 3, Loadout: Loadouts.Create(unit, 3))] };
                army = Enhancements.Select(army, data, "bearer", new(gift.Detachment, gift.Name));
                var state = EffectiveDatasheets.Project(army, army.Entries[0], data)!;
                Check(state.EffectiveWeaponProfiles.Where(w => w.Type == "Ranged").All(Assault), label + ": upgrade missing from ranged weapons");
                Check(state.EffectiveWeaponProfiles.Where(w => w.Type == "Melee").All(w => !Assault(w)), label + ": upgrade leaked into melee");
                Check(Loadouts.Weapons(state.Unit, army.Entries[0].Loadout).Where(w => w.Profile.Type == "Ranged").All(w => Assault(w.Profile)), label + ": play loadout lost effective keywords");
                var xml = System.Xml.Linq.XDocument.Parse(YellowscribeExport.Roster(army, data));
                Check(xml.Descendants().Any(e => e.Attribute("name")?.Value == "Keywords" && e.Value.Contains("Assault", StringComparison.OrdinalIgnoreCase)), label + ": export lost grant");
                var removed = Enhancements.Select(army, data, "bearer", null);
                Check(EffectiveDatasheets.Project(removed, removed.Entries[0], data)!.EffectiveWeaponProfiles.All(w => !Assault(w)), label + ": upgrade removal left grant");
                var sororitas = data.Units.Where(u => u.Faction == "Adepta Sororitas" && u.Profile?.Weapons?.Any(w => w.Type == "Ranged") == true).ToList();
                Check(sororitas.Count > 10, "Fixture must cover more than one datasheet");
                foreach (var sister in sororitas)
                {
                    var roster = ArmyList.New(sister.Faction) with { Detachment = "Bringers of Flame", Entries = [new("unit", sister.Key, sister.Name, sister.Options.FirstOrDefault()?.Models ?? 1)] };
                    var baselineRoster = roster with { Detachment = "", Detachments = [] };
                    var baseline = EffectiveDatasheets.Project(baselineRoster, baselineRoster.Entries[0], data)!.EffectiveWeaponProfiles;
                    var projected = EffectiveDatasheets.Project(roster, roster.Entries[0], data)!;
                    Check(projected.EffectiveWeaponProfiles.Where(w => w.Type == "Ranged").All(Assault), label + ": detachment missing from " + sister.Name);
                    Check(projected.EffectiveWeaponProfiles.Zip(baseline).All(p => p.First.Strength == p.Second.Strength), label + ": range-dependent Strength became unconditional");
                    Check(projected.EffectiveWeaponProfiles.Where(w => w.Type == "Melee").SequenceEqual(baseline.Where(w => w.Type == "Melee")), label + ": detachment changed melee");
                    var preview = EffectiveDatasheets.Preview(roster, data, sister);
                    Check(preview.Profile!.Weapons!.Where(w => w.Type == "Ranged").All(Assault), label + ": library preview lost detachment grant");
                    roster = roster with { Detachment = "", Detachments = [] };
                    var cleared = EffectiveDatasheets.Project(roster, roster.Entries[0], data)!.EffectiveWeaponProfiles;
                    // Source weapon/loadout grants (including native Assault) survive.
                    Check(cleared.SequenceEqual(baseline), label + ": changing detachment left stale grants on " + sister.Name);
                }
                Check(DataJson.Write(data) == sourceBefore, label + ": modified base source data");
            }
            var fixtureUnits = legacy.Units.Where(u => u.Name is "Lokhust Destroyers" or "Battle Sisters Squad").ToList();
            File.WriteAllText(Path.Combine(root, "source-weapon-rules-ui.json"), DataJson.Write(legacy with { Units = fixtureUnits,
                ReferenceCards = legacy.ReferenceCards!.Where(c => c.Name is "Deepening Madness" or "Fervent Purgation").ToList(),
                Detachments = legacy.Detachments!.Where(d => d.Name is "Skyshroud Spearhead" or "Bringers of Flame").ToList() }));
        });
    }
}
