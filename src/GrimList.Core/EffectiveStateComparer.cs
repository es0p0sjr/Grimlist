namespace GrimList.Core;

// Exactly the semantic fields previously serialized for convergence. States are
// immutable pass snapshots. Hash collisions always receive full structural equality;
// a hash alone must never establish convergence or suppress another evaluation.
internal sealed class EffectiveStateComparer : IEqualityComparer<Dictionary<string, EffectiveUnitState?>>
{
    internal static readonly EffectiveStateComparer Instance = new();
    public bool Equals(Dictionary<string, EffectiveUnitState?>? left, Dictionary<string, EffectiveUnitState?>? right)
        => ReferenceEquals(left, right) || left != null && right != null && left.Count == right.Count
            && left.All(p => right.TryGetValue(p.Key, out var value) && SameUnit(p.Value?.Unit, value?.Unit));
    public int GetHashCode(Dictionary<string, EffectiveUnitState?> values)
    {
        var result = values.Count;
        foreach (var pair in values) result ^= HashCode.Combine(pair.Key, UnitHash(pair.Value?.Unit));
        return result;
    }
    private static bool ListEqual<T>(List<T>? a, List<T>? b, Func<T, T, bool> same)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (!same(a[i], b[i])) return false;
        return true;
    }
    private static int ListHash<T>(List<T>? values, Func<T, int> hash)
    {
        if (values == null) return -1;
        var result = new HashCode(); result.Add(values.Count);
        foreach (var value in values) result.Add(hash(value));
        return result.ToHashCode();
    }
    private static bool MapEqual(Dictionary<string, string>? a, Dictionary<string, string>? b)
        => ReferenceEquals(a, b) || a != null && b != null && a.Count == b.Count
            && a.All(p => b.Keys.Contains(p.Key, StringComparer.Ordinal) && b.TryGetValue(p.Key, out var value) && p.Value == value);
    private static int MapHash(Dictionary<string, string>? values)
    {
        if (values == null) return -1;
        var result = values.Count;
        foreach (var pair in values) result ^= HashCode.Combine(pair.Key, pair.Value);
        return result;
    }
    private static bool Strings(List<string>? a, List<string>? b) => ListEqual(a, b, static (x, y) => x == y);
    private static int StringsHash(List<string>? a) => ListHash(a, static x => x?.GetHashCode(StringComparison.Ordinal) ?? 0);
    private static bool WeaponEqual(WeaponProfile a, WeaponProfile b) => a.Name == b.Name && a.Type == b.Type && a.Range == b.Range
        && a.Attacks == b.Attacks && a.Skill == b.Skill && a.Strength == b.Strength && a.Ap == b.Ap && a.Damage == b.Damage
        && a.Keywords == b.Keywords && a.SourceId == b.SourceId && a.GrantedBy == b.GrantedBy && Strings(a.SourceCategories, b.SourceCategories);
    private static int WeaponHash(WeaponProfile w) => HashCode.Combine(HashCode.Combine(w.Name, w.Type, w.Range, w.Attacks, w.Skill, w.Strength, w.Ap, w.Damage),
        w.Keywords, w.SourceId, w.GrantedBy, StringsHash(w.SourceCategories));
    private static bool SameUnit(UnitDefinition? a, UnitDefinition? b)
    {
        var x = a?.Profile; var y = b?.Profile;
        return a?.Category == b?.Category && Strings(x?.Keywords, y?.Keywords) && MapEqual(x?.Characteristics, y?.Characteristics)
            && ListEqual(x?.Models, y?.Models, static (m, n) => m.Name == n.Name && MapEqual(m.Characteristics, n.Characteristics))
            && ListEqual(x?.Weapons, y?.Weapons, WeaponEqual) && ListEqual(x?.Abilities, y?.Abilities, static (m, n) => m == n);
    }
    private static int UnitHash(UnitDefinition? unit)
    {
        var p = unit?.Profile;
        return HashCode.Combine(unit?.Category, StringsHash(p?.Keywords), MapHash(p?.Characteristics),
            ListHash(p?.Models, static m => HashCode.Combine(m.Name, MapHash(m.Characteristics))),
            ListHash(p?.Weapons, WeaponHash), ListHash(p?.Abilities, static a => a.GetHashCode()));
    }
}
