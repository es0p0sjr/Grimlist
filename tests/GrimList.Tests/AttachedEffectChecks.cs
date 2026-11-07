using GrimList.Core;

internal static class AttachedEffectChecks
{
    private const string Gift = "While this model is leading a unit, each time a model in that unit makes an attack which targets a unit which is Afflicted, that attack has the [Sustained Hits 1] ability.";
    private const string Fusillade = "If this unit has a Starting Strength of 5 or more, or if a Character is leading this unit, then each time a model in this unit makes a ranged attack that targets an Afflicted unit, improve the Strength and Armour Penetration characteristics of that attack by 1.";
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var gun = new WeaponProfile("Test rifle", "Ranged", "24\"", "2", "3+", "4", "-1", "1", "Lethal Hits");
        var blade = new WeaponProfile("Test blade", "Melee", "Melee", "3", "3+", "5", "-2", "1");
        var heavy = gun with { Name = "Test heavy weapon", Strength = "6", Keywords = "Sustained Hits 2" };
        var gear = new EquipmentGroup("gear", "Equipment", [new("standard", "Standard equipment", [gun.Name, blade.Name], [], new(0), new(1)),
            new("heavy", "Heavy equipment", [heavy.Name, blade.Name], [], new(0), new(1))], new(1), new(1), "standard");
        var squad = new UnitDefinition("Death Guard", "Effect squad", [new(3, 100), new(5, 150)], Category: "Battleline",
            Profile: new(Weapons: [gun, blade, heavy], Abilities: [new("Fusillade fixture", Fusillade)], Loadout: new([], [gear], [])));
        var leader = new UnitDefinition("Death Guard", "Effect leader", [new(1, 50)], Category: "Characters",
            Profile: new(Keywords: ["Character", "Leader"], Weapons: [gun, blade, heavy], Abilities: [new("Leader", "This model can be attached to the following unit: Effect squad"),
                new("Gift fixture", Gift), new("Defence fixture", "While this model is leading a unit, each time a melee attack targets that unit, subtract 1 from the Hit roll.")], Loadout: new([], [gear], [])));
        var data = new Dataset(new("Effect fixtures", "1", "", ""), [squad, leader]);
        var b = new ArmyEntry("bodyguard", squad.Key, squad.Name, 3, Loadout: Loadouts.Create(squad, 3));
        var l = new ArmyEntry("leader", leader.Key, leader.Name, 1, Loadout: Loadouts.Create(leader, 1));
        var army = ArmyList.New("Death Guard") with { Entries = [b, l] };
        var attached = LeaderRules.Attach(army, data, l.Id, b.Id);
        test("Attached bonuses respect target, starting size, weapon scope and detachment", () =>
        {
            var before = DataJson.Write(data);
            var effects = AttachedEffects.ForUnit(attached, data, b);
            Equal(3, effects.Count); Equal(0, AttachedEffects.ForUnit(army, data, b).Count);
            var unmodified = AttachedEffects.Weapons([gun, blade], effects, false);
            Equal(gun, unmodified[0].Profile); Equal(0, unmodified[0].Changes.Count);
            var modified = AttachedEffects.Weapons([gun, blade], effects, true);
            Equal("5", modified[0].Profile.Strength); Equal("-2", modified[0].Profile.Ap);
            Equal("Lethal Hits, Sustained Hits 1", modified[0].Profile.Keywords);
            Equal(blade.Strength, modified[1].Profile.Strength); Equal(blade.Ap, modified[1].Profile.Ap);
            Equal("Sustained Hits 1", modified[1].Profile.Keywords);
            Equal(gun.Skill, modified[0].Profile.Skill); Equal(gun.Attacks, modified[0].Profile.Attacks);
            var leaderEffects = AttachedEffects.ForUnit(attached, data, attached.Entries[1]);
            Equal(modified[0].Profile, AttachedEffects.Weapons([gun], leaderEffects, true)[0].Profile);
            Equal(gun, AttachedEffects.Weapons([gun], AttachedEffects.ForUnit(LeaderRules.Attach(attached, data, l.Id, ""), data, b), true)[0].Profile);
            var large = b with { Models = 5 }; var largerArmy = army with { Entries = [large, l] };
            var larger = AttachedEffects.Weapons([gun], AttachedEffects.ForUnit(largerArmy, data, large), true)[0].Profile;
            Equal("5", larger.Strength); Equal(gun.Keywords, larger.Keywords);
            Equal(before, DataJson.Write(data));
            Equal(modified[0].Profile, AttachedEffects.Weapons([gun], effects, true)[0].Profile);
        });
        test("Keywords do not stack, unknown conditions stay descriptive, and Play loadouts retain filtering", () =>
        {
            var effects = AttachedEffects.ForUnit(attached, data, b);
            Equal("Sustained Hits 2", AttachedEffects.Weapons([heavy], effects, true)[0].Profile.Keywords);
            Equal("Sustained Hits 1", AttachedEffects.Weapons([gun with { Keywords = "Sustained Hits 1" }], effects, true)[0].Profile.Keywords);
            var alternateLeader = leader with { Profile = leader.Profile! with { Abilities = [leader.Profile.Abilities![0], new("Conditional", Gift.Replace("While this model is leading a unit,", "While this model is leading a unit, if you roll a 6,"))] } };
            var conditionalEffects = AttachedEffects.ForUnit(attached, data with { Units = [squad, alternateLeader] }, b);
            Equal(null, conditionalEffects.Single(e => e.Ability.Name == "Conditional").Adjustment);
            Equal(gun.Keywords, AttachedEffects.Weapons([gun], conditionalEffects, true)[0].Profile.Keywords);
            var play = Loadouts.Weapons(squad, b.Loadout);
            var rendered = AttachedEffects.Weapons(play.Select(w => w.Profile), effects, true);
            Equal(2, rendered.Count); Equal(false, rendered.Any(w => w.Profile.Name == heavy.Name));
            var meleeLeader = leader with { Profile = leader.Profile! with { Abilities = [leader.Profile.Abilities![0], new("Melee gift", "While this model is leading a unit, melee weapons equipped by models in that unit have the [Sustained Hits 1] and [Lance] abilties.")] } };
            var meleeEffects = AttachedEffects.ForUnit(attached, data with { Units = [squad, meleeLeader] }, b);
            var meleeResult = AttachedEffects.Weapons([gun, blade], meleeEffects, false);
            Equal(gun, meleeResult[0].Profile); Equal("Sustained Hits 1, Lance", meleeResult[1].Profile.Keywords);
            Equal("D6", AttachedEffects.Weapons([gun with { Strength = "D6" }], effects, true)[0].Profile.Strength);
        });
        var index = Array.IndexOf(args, "--loadout-catalogue");
        if (index >= 0 && index + 1 < args.Length) test("Cached Death Guard leader effects use the imported rules and conditions", () =>
        {
            var catalogue = DataJson.Read<Dataset>(File.ReadAllText(args[index + 1]));
            ArmyEntry Entry(string name, string id, int models) { var u = catalogue.Units.Single(u => u.Name == name); return new(id, u.Key, u.Name, models, Loadout: Loadouts.Create(u, models)); }
            var caster = Entry("Malignant Plaguecaster", "caster", 1); var marines = Entry("Plague Marines", "marines", 5);
            var realArmy = ArmyList.New("Death Guard") with { Entries = [caster, marines] };
            realArmy = LeaderRules.Attach(realArmy, catalogue, caster.Id, marines.Id);
            var gifts = AttachedEffects.ForUnit(realArmy, catalogue, marines);
            Equal(true, gifts.Any(e => e.Ability.Name == "Gift of Contagion (Psychic)" && e.Adjustment?.RequiresAfflicted == true));
            var terminators = Entry("Blightlord Terminators", "terminators", 3); var typhus = Entry("Typhus", "typhus", 1);
            realArmy = LeaderRules.Attach(realArmy with { Entries = [terminators, typhus] }, catalogue, typhus.Id, terminators.Id);
            var fusillade = AttachedEffects.ForUnit(realArmy, catalogue, terminators).Single(e => e.Ability.Name == "Blistering Fusillade").Adjustment!;
            Equal(1, fusillade.Strength); Equal(-1, fusillade.Ap); Equal(true, fusillade.RequiresAfflicted);
            var putrifier = Entry("Biologus Putrifier", "putrifier", 1);
            realArmy = LeaderRules.Attach(realArmy with { Entries = [marines, putrifier] }, catalogue, putrifier.Id, marines.Id);
            Equal("Lethal Hits", AttachedEffects.ForUnit(realArmy, catalogue, marines).Single(e => e.Ability.Name == "Foul Infusion").Adjustment!.Keywords.Single());
        });
        File.WriteAllText(Path.Combine(root, "effect-fixture.json"), DataJson.Write(data));
    }
}
