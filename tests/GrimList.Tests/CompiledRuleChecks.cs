using GrimList.Application;
using GrimList.Core;

internal static class CompiledRuleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("Compiled rules match uncached source interpretation across bundled reference scopes", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset;
            var count = 0;
            foreach (var card in (data.ReferenceCards ?? []).Where(c => c.Section is "Enhancements" or "Detachments" or "Army Rules"))
                foreach (var roster in new[] { false, true })
                {
                    var expected = WeaponRules.WithTextFallback(card.Modifiers, card.Name, card.Text, roster).ToList();
                    Check(DataJson.Write(expected) == DataJson.Write(CompiledRuleText.ForCard(card, roster)), "Compilation changed " + card.Name);
                    count++;
                }
            Check(count > 100, "Bundled source comparison did not exercise multiple scopes");
            foreach (var ability in data.Units.SelectMany(u => u.Profile?.Abilities ?? []).Distinct())
            {
                Check(DataJson.Write(CompiledRuleText.Attached(ability)) == DataJson.Write(DatasheetRuleText.Attached(ability)), "Bundled recipient interpretation changed: " + ability.Name);
                Check(DataJson.Write(CompiledRuleText.Weapons(ability)) == DataJson.Write(DatasheetRuleText.WeaponAbilities(ability.Name, ability.Text)), "Bundled weapon interpretation changed: " + ability.Name);
            }
        });
        test("Compiled source identity separates same-ID revisions, grammar modes and concurrent consumers", () =>
        {
            var card = new ReferenceCard("Fixture", "Enhancements", "Repeated source", "Add 2 to the Objective Control characteristic of the bearer.",
                Modifiers: [new("unknown", "Fixture", "weapon", "M", "increment", "3", WeaponPredicate: new("unknown", "unrecognized-selector"))]);
            var before = CompiledRuleText.CompilationCount;
            var results = new IReadOnlyList<DatasheetModifier>[32];
            Parallel.For(0, results.Length, i => results[i] = CompiledRuleText.ForCard(card));
            Check(CompiledRuleText.CompilationCount == before + 1 && results.All(r => ReferenceEquals(r, results[0])), "Concurrent source compiled repeatedly");
            Check(results[0].Any(m => m.WeaponPredicate?.Kind == "unknown"), "Unknown selector lost");
            var changed = card with { Text = "Add 5 to the Objective Control characteristic of the bearer." };
            Check(CompiledRuleText.ForCard(changed).Any(m => m.Characteristic == "OC" && m.Value == "5"), "Changed same-identity text reused old rules");
            Check(CompiledRuleText.ForCard(card).Any(m => m.Characteristic == "OC" && m.Value == "2"), "Old revision changed");
            Check(DataJson.Write(CompiledRuleText.ForCard(card, true)) == DataJson.Write(WeaponRules.WithTextFallback(card.Modifiers, card.Name, card.Text, true).ToList()), "Roster grammar reused enhancement interpretation");
            Check(!ReferenceEquals(CompiledRuleText.ForCard(card), CompiledRuleText.ForCard(card, true)), "Grammar modes shared one cached result");
        });
        test("Compiled ability grants preserve recipient scope and invalidate changed ability records", () =>
        {
            AbilityProfile[] sources = [new("Leading", "While this model is leading a unit, add 1 to the Toughness characteristic of models in that unit."),
                new("Weapon grant", "Ranged weapons equipped by models in this unit have the [LETHAL HITS] ability."),
                new("Unsupported", "If an unknown event happens, apply a special benefit.")];
            foreach (var ability in sources)
            {
                Check(DataJson.Write(CompiledRuleText.Attached(ability)) == DataJson.Write(DatasheetRuleText.Attached(ability)), "Recipient semantics changed");
                Check(DataJson.Write(CompiledRuleText.Weapons(ability)) == DataJson.Write(DatasheetRuleText.WeaponAbilities(ability.Name, ability.Text)), "Weapon grant changed");
                Check(ReferenceEquals(CompiledRuleText.Attached(ability), CompiledRuleText.Attached(ability)), "Ability parsed twice");
            }
            var revised = sources[0] with { Text = "While this model is leading a unit, add 3 to the Toughness characteristic of models in that unit." };
            Check(CompiledRuleText.Attached(revised).Single().Value == "3" && CompiledRuleText.Attached(sources[0]).Single().Value == "1", "Ability revision leaked across snapshots");
        });
        test("Cached static interpretation does not cache enhancement selection or roster results", () =>
        {
            var unit = new UnitDefinition("Fixture", "Bearer", [new(1, 100)], Profile: new(new() { ["M"] = "6\"", ["OC"] = "1" }, Keywords: ["Character"]));
            var card = new ReferenceCard("Fixture", "Enhancements", "Movement", "Add 2\" to the Move characteristic of the bearer.", "Host", "10 pts", Eligibility: new("constant", "1"));
            var data = new Dataset(new("Fixture", "1", "", ""), [unit], ReferenceCards: [card], Detachments: [new("Fixture", "Host")]);
            var army = ArmyList.New("Fixture") with { Detachment = "Host", Entries = [new("bearer", unit.Key, unit.Name, 1, Enhancement: new("Host", card.Name))] };
            string Movement(ArmyList a, Dataset d) => EffectiveDatasheets.Project(a, a.Entries[0], d)!.EffectiveCharacteristics["M"];
            Check(Movement(army, data) == "8\"", "Initial selection failed");
            var count = CompiledRuleText.CompilationCount;
            Check(Movement(army with { Name = "Another immutable roster" }, data) == "8\"" && CompiledRuleText.CompilationCount == count, "Roster edit recompiled static card");
            var removed = army with { Entries = [army.Entries[0] with { Enhancement = null }] };
            Check(Movement(removed, data) == "6\"" && Movement(army, data) == "8\"", "Remove/undo retained wrong projection");
            var refreshed = data with { ReferenceCards = [card with { Text = "Add 4\" to the Move characteristic of the bearer." }] };
            Check(Movement(army, refreshed) == "10\"" && Movement(army, data) == "8\"", "Import reused old compiled text");
        });
        test("Presentation equality preserves changed model stats, weapon provenance and rule definitions", () =>
        {
            var profile = new UnitProfile(new() { ["M"] = "6\"", ["OC"] = "1" }, Models: [new("Model", new() { ["T"] = "4" })]);
            var copy = DataJson.Read<UnitProfile>(DataJson.Write(profile));
            Check(PresentationEquality.Stats(profile, copy), "Equivalent statlines invalidated");
            Check(!PresentationEquality.Stats(profile, copy with { Characteristics = new() { ["OC"] = "1", ["M"] = "6\"" } }), "Displayed field order change ignored");
            Check(!PresentationEquality.Stats(new(Characteristics: new() { ["Inv"] = "5+", ["InSv"] = "4+" }),
                new(Characteristics: new() { ["InSv"] = "4+", ["Inv"] = "5+" })), "First defensive-save alias change ignored");
            Check(!PresentationEquality.Stats(profile, copy with { Models = [new("Model", new() { ["T"] = "5" })] }), "Model stat change ignored");
            Check(!PresentationEquality.Stats(profile, copy with { Models = [new("Other model", new() { ["T"] = "4" })] }), "Model name change ignored");
            var weapon = new WeaponProfile("Gun", "Ranged", "24\"", "2", "3+", "4", "0", "1", "Rapid Fire 1", "source", ["category"], "grant");
            var cloned = DataJson.Read<WeaponProfile>(DataJson.Write(weapon));
            Check(PresentationEquality.Weapon(weapon, cloned), "Equivalent source weapon invalidated");
            WeaponProfile[] changes = [cloned with { Name = "Renamed" }, cloned with { Type = "Melee" }, cloned with { Range = "12\"" },
                cloned with { Attacks = "3" }, cloned with { Skill = "2+" }, cloned with { Strength = "5" }, cloned with { Ap = "-1" },
                cloned with { Damage = "2" }, cloned with { Keywords = "Lethal Hits" }, cloned with { SourceId = "other" },
                cloned with { SourceCategories = [] }, cloned with { GrantedBy = "other" }];
            Check(changes.All(w => !PresentationEquality.Weapon(weapon, w)), "Weapon change ignored");
            Check(!PresentationEquality.EffectiveWeapon(new(weapon, weapon, []), new(weapon, weapon, ["Conditional change"])), "Modifier notes ignored");
            Check(!PresentationEquality.Sequence<RuleDefinition>([new("Rule", "Old")], [new("Rule", "New")]), "Same-name new rule text ignored");
            Check(!PresentationEquality.Sequence<string>(null, []), "Absent and empty presentation inputs conflated");
        });
        test("Compiled rule caches release discarded source records", () =>
        {
            var references = CacheTransientSources();
            for (var i = 0; i < 3 && references.Any(r => r.IsAlive); i++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Check(references.All(r => !r.IsAlive), "Compilation cache retained an obsolete source object");
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] CacheTransientSources()
    {
        var card = new ReferenceCard("Transient", "Enhancements", "Source", "Add 1 to the Toughness characteristic of the bearer.");
        var ability = new AbilityProfile("Transient", "No supported static operation.");
        _ = CompiledRuleText.ForCard(card); _ = CompiledRuleText.Attached(ability); _ = CompiledRuleText.Weapons(ability);
        return [new(card), new(ability)];
    }
}
