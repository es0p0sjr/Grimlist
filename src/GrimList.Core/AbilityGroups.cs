namespace GrimList.Core;

// A presentation tree; the imported abilities remain flat for loadouts and rule evaluation.
public sealed record AbilityCard : AbilityProfile
{
    public AbilityCard(AbilityProfile ability, List<AbilityCard> options) : base(ability.Name, ability.Text, ability.Section)
        => Options = options;
    public List<AbilityCard> Options { get; }
}

public static class AbilityGroups
{
    public static List<AbilityCard> Create(IEnumerable<AbilityProfile> abilities)
    {
        var items = abilities.ToList();
        var parents = new int?[items.Count];
        var generic = new[] { "Abilities", "Rules", "Equipment rules", "Core", "Faction", "Wargear Abilities" }
            .Select(UnitIdentity.Normalize).ToHashSet();
        for (var i = 0; i < items.Count; i++)
        {
            var section = UnitIdentity.Normalize(items[i].Section);
            if (section.Length == 0 || generic.Contains(section)) continue;
            var candidates = Enumerable.Range(0, items.Count).Where(j => j != i && UnitIdentity.Normalize(items[j].Name) == section).ToList();
            if (candidates.Count == 1) parents[i] = candidates[0];
        }
        // Malformed/ambiguous source relationships must never hide an ability.
        var cycles = new HashSet<int>();
        for (var i = 0; i < items.Count; i++)
        {
            var path = new HashSet<int>(); int? next = i;
            while (next is int index)
            {
                if (!path.Add(index)) { cycles.UnionWith(path); break; }
                next = parents[index];
            }
        }
        foreach (var index in cycles) parents[index] = null;
        AbilityCard Card(int index) => new(items[index], Enumerable.Range(0, items.Count)
            .Where(i => parents[i] == index).Select(Card).ToList());
        return Enumerable.Range(0, items.Count).Where(i => parents[i] == null).Select(Card).ToList();
    }
}
