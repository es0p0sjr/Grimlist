using GrimList.Core;

internal static class EffectiveComparisonChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("Effective convergence compares every semantic field structurally and ignores allocation identity", () =>
        {
            var unit = new UnitDefinition("Test", "Unit", [new(1, 1)], Category: "Infantry", Profile: new(
                Characteristics: new() { ["M"] = "6", ["T"] = "4" }, Keywords: ["Infantry"],
                Models: [new("Model", new() { ["T"] = "4" })], Abilities: [new("Rule", "Text")],
                Weapons: [new("Gun", "Ranged", "24", "2", "3+", "4", "0", "1", "Assault", "profile", ["ranged"])]));
            Dictionary<string, EffectiveUnitState?> State(UnitDefinition u) => new() { ["unit"] = new(u, u, []) };
            string Old(Dictionary<string, EffectiveUnitState?> state) => DataJson.Write(state.OrderBy(p => p.Key).Select(p => new {
                p.Key, p.Value?.Unit.Category, p.Value?.Unit.Profile?.Keywords, p.Value?.Unit.Profile?.Characteristics,
                p.Value?.Unit.Profile?.Models, p.Value?.Unit.Profile?.Weapons, p.Value?.Unit.Profile?.Abilities }));
            var original = State(unit); var copy = State(DataJson.Read<UnitDefinition>(DataJson.Write(unit)));
            var comparer = EffectiveStateComparer.Instance;
            if (!comparer.Equals(original, copy) || comparer.GetHashCode(original) != comparer.GetHashCode(copy)) throw new Exception("Copies compare differently");
            var p = unit.Profile!; var w = p.Weapons![0];
            foreach (var property in typeof(WeaponProfile).GetProperties().Where(p => p.CanWrite && p.PropertyType == typeof(string)))
            {
                var altered = w with { }; property.SetValue(altered, property.GetValue(w) + " changed");
                if (comparer.Equals(original, State(unit with { Profile = p with { Weapons = [altered] } })))
                    throw new Exception("Weapon semantic field omitted: " + property.Name);
            }
            foreach (var changed in new[] {
                unit with { Category = "Vehicle" }, unit with { Profile = p with { Keywords = [] } },
                unit with { Profile = p with { Characteristics = new() { ["M"] = "7", ["T"] = "4" } } },
                unit with { Profile = p with { Models = [new("Model", new() { ["T"] = "5" })] } },
                unit with { Profile = p with { Abilities = [new("Rule", "Different text")] } },
                unit with { Profile = p with { Weapons = [w with { SourceCategories = ["other"] }] } },
                unit with { Profile = p with { Weapons = [w with { Keywords = "Heavy" }] } },
                unit with { Profile = p with { Weapons = [w with { Strength = "5" }] } },
                unit with { Profile = p with { Weapons = null } }, unit with { Profile = p with { Keywords = null } },
                unit with { Profile = p with { Notes = "Not an effective field" } } })
            {
                var next = State(changed);
                if (comparer.Equals(original, next) != (Old(original) == Old(next))) throw new Exception("Comparison diverged from legacy semantic fields");
            }
            // Dictionaries are mappings; insertion/entry enumeration order cannot affect convergence.
            var reordered = State(unit with { Profile = p with { Characteristics = new() { ["T"] = "4", ["M"] = "6" } } });
            if (!comparer.Equals(original, reordered) || comparer.GetHashCode(original) != comparer.GetHashCode(reordered)) throw new Exception("Mapping order affects convergence");
        });
    }
}
